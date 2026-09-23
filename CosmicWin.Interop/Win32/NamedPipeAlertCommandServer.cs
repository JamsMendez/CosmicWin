using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.Security.Authorization;
using Windows.Win32.Storage.FileSystem;
using Windows.Win32.System.Pipes;

namespace CosmicWin.Interop.Win32;

/// <summary>
/// The real <see cref="IAlertCommandServer"/>: a per-user named pipe, one client at a time,
/// message-framed so the server never has to guess where a command ends without a delimiter.
/// </summary>
/// <remarks>
/// <para>
/// Decided 2026-09-23 (plan &#167;4, task file T4 entry): <c>CosmicWin.App.exe</c> runs elevated, so
/// the pipe's security descriptor must let the SAME user's unelevated processes (a plain shell, or
/// WSL through <c>/mnt/c/...</c> interop) connect and write, while refusing every OTHER account and
/// every remote client. Built here from an SDDL string rather than <see
/// cref="System.IO.Pipes.PipeSecurity"/>: the DACL half (grant this user, nobody else) is easy
/// either way, but the MEDIUM MANDATORY LABEL (<c>S:(ML;;NW;;;ME)</c>) is a System ACL entry, and
/// <see cref="System.IO.Pipes.PipeSecurity"/>/<see
/// cref="System.Security.AccessControl.ObjectSecurity"/> has no supported way to add a mandatory
/// label ACE -- only <c>ConvertStringSecurityDescriptorToSecurityDescriptor</c>, called through
/// CsWin32 like the rest of this project's Win32 surface, builds BOTH halves from one SDDL string.
/// </para>
/// <para>
/// Message-mode (<c>PIPE_TYPE_MESSAGE | PIPE_READMODE_MESSAGE</c>), not byte-mode: a Windows named
/// pipe treats each <c>WriteFile</c> call as exactly one discrete message, so the client's single
/// write IS the frame -- no length prefix, no end-of-message delimiter, and no "did the writer
/// close yet" ambiguity a byte-mode pipe would need one of. <see
/// cref="ServeOneConnectionAsync"/> rejects an oversized message two ways: a read that returns
/// fewer bytes than the whole message sets <see cref="PipeStream.IsMessageComplete"/> to <see
/// langword="false"/>, catching a message far larger than <see cref="ReadBufferSize"/> without
/// ever reading (or handing to the command handler) the rest of it; a message that fits under
/// <see cref="ReadBufferSize"/> but still exceeds the wire limit is caught by the plain length
/// check next to it, since <c>IsMessageComplete</c> alone would let that one through.
/// </para>
/// <para>
/// One pipe INSTANCE per connection (<c>nMaxInstances = 1</c> at creation, plus a fresh
/// <see cref="CreateInstance"/> call for every iteration of <see cref="RunLoop"/>) rather than
/// reusing one handle across clients: this is a low-traffic control channel, and creating a fresh,
/// freshly-ACLed instance for each wait removes any question of stale connection state surviving
/// between clients.
/// </para>
/// </remarks>
public sealed class NamedPipeAlertCommandServer : IAlertCommandServer
{
    /// <summary>
    /// How long a connected client has to finish sending its one message before the server gives
    /// up on it and moves on to the next connection (plan &#167;6, T4: "per-connection read timeout
    /// ~2s").
    /// </summary>
    public static readonly TimeSpan ConnectionReadTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Larger than <see cref="AlertPipeProtocol.MaxMessageBytes"/> on purpose: reading a moderately
    /// oversized message fully (rather than the theoretical minimum of one byte past the limit)
    /// lets <see cref="AlertPipeProtocol.OversizedReply"/> report the real size, while still
    /// bounding memory for a pathologically large one -- <see cref="PipeStream.IsMessageComplete"/>
    /// still reports <see langword="false"/> and the rest of an oversized message is simply
    /// discarded when the pipe instance is disposed, never read into memory.
    /// </summary>
    private const int ReadBufferSize = AlertPipeProtocol.MaxMessageBytes * 4;

    private readonly string _pipeName;
    private readonly Func<string, string> _handleCommand;
    private readonly Action<string> _onDiagnostic;
    private readonly CancellationTokenSource _stopping = new();
    private Thread? _thread;

    /// <param name="pipeName">The pipe name to listen on, e.g. from <see cref="AlertPipeName.Resolve()"/>.</param>
    /// <param name="handleCommand">
    /// Called with the raw command text once a full message is read; its return value is sent back
    /// verbatim as the reply line (<see cref="AlertPipeProtocol.OkReply"/> or an <c>"error: ..."</c>
    /// line). Never called for an oversized message. A throw from this delegate is caught and
    /// reported as <c>"error: internal error"</c> -- a caller's bug must not take the pipe down.
    /// </param>
    /// <param name="onDiagnostic">Told about every per-connection failure. Defaults to a no-op, the same convention <see cref="AlertQueue"/> and <c>AppComposition</c>'s <c>persistX</c> parameters use.</param>
    public NamedPipeAlertCommandServer(string pipeName, Func<string, string> handleCommand, Action<string>? onDiagnostic = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        _pipeName = pipeName;
        _handleCommand = handleCommand ?? throw new ArgumentNullException(nameof(handleCommand));
        _onDiagnostic = onDiagnostic ?? (_ => { });
    }

    public void Start()
    {
        if (_thread is not null)
        {
            return;
        }

        _thread = new Thread(RunLoop) { IsBackground = true, Name = "CosmicWin.AlertPipe" };
        _thread.Start();
    }

    /// <summary>
    /// Runs forever until <see cref="Dispose"/> cancels <see cref="_stopping"/>. A single
    /// connection's failure -- a broken pipe, a Win32 error creating the next instance -- is caught
    /// and reported, never allowed to end the loop: the constraint from the task file is "errors on
    /// one connection never stop the server."
    /// </summary>
    private void RunLoop()
    {
        while (!_stopping.IsCancellationRequested)
        {
            try
            {
                ServeOneConnectionAsync().GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                // Dispose was called, either while waiting for a connection or while reading one.
                // The while condition above ends the loop; nothing else to do.
            }
            catch (Exception error)
            {
                _onDiagnostic($"alert pipe: {error.GetType().Name}: {error.Message}");
            }
        }
    }

    private async Task ServeOneConnectionAsync()
    {
        using var pipe = CreateInstance();

        await pipe.WaitForConnectionAsync(_stopping.Token).ConfigureAwait(false);

        using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
        readTimeout.CancelAfter(ConnectionReadTimeout);

        var buffer = new byte[ReadBufferSize];
        int read;
        try
        {
            read = await pipe.ReadAsync(buffer, readTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!_stopping.IsCancellationRequested)
        {
            // The client connected but never finished sending within ConnectionReadTimeout. Closed
            // without a reply -- the constraint is that the SERVER keeps serving the next client,
            // not that this one gets a considered answer.
            _onDiagnostic("alert pipe: a connection timed out waiting for a command");
            return;
        }

        // Two distinct oversized shapes, both rejected without ever calling the handler --
        // "never partially apply" (task file, T4): !IsMessageComplete means the message was
        // larger than ReadBufferSize and got truncated (the pathological case, caught cheaply
        // without reading the rest); read > MaxMessageBytes means the WHOLE message arrived (it
        // fit under ReadBufferSize) but is still past the wire limit (the everyday case, e.g. a
        // stray extra token) -- that one needs BOTH the read to complete and this length check,
        // since IsMessageComplete alone would let it straight through to the handler.
        var reply = !pipe.IsMessageComplete || read > AlertPipeProtocol.MaxMessageBytes
            ? AlertPipeProtocol.OversizedReply(read)
            : InvokeHandler(Encoding.UTF8.GetString(buffer, 0, read));

        await WriteReplyAsync(pipe, reply).ConfigureAwait(false);
    }

    private string InvokeHandler(string text)
    {
        try
        {
            return _handleCommand(text);
        }
        catch (Exception error)
        {
            _onDiagnostic($"alert pipe: the command handler threw {error.GetType().Name}: {error.Message}");
            return AlertPipeProtocol.FormatError("internal error");
        }
    }

    private async Task WriteReplyAsync(NamedPipeServerStream pipe, string reply)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(reply);
            await pipe.WriteAsync(bytes, _stopping.Token).ConfigureAwait(false);
            await pipe.FlushAsync(_stopping.Token).ConfigureAwait(false);
            pipe.WaitForPipeDrain();
        }
        catch (Exception error)
        {
            _onDiagnostic($"alert pipe: failed to write the reply: {error.GetType().Name}: {error.Message}");
        }
    }

    /// <summary>
    /// Creates one fresh, per-user-ACLed named pipe instance, wrapped as a <see
    /// cref="NamedPipeServerStream"/> for the async read/write/timeout ergonomics the rest of this
    /// class relies on. The native <c>CreateNamedPipe</c> call is the only part of this class that
    /// needs to be Win32 at all -- the .NET pipe type has no constructor that both supplies a
    /// custom security descriptor AND lets the mandatory label be set, so the two are combined by
    /// creating the handle natively and handing it to the standard managed wrapper.
    /// </summary>
    private NamedPipeServerStream CreateInstance()
    {
        var handle = CreateSecuredHandle(_pipeName);
        var pipe = new NamedPipeServerStream(
            PipeDirection.InOut, isAsync: true, isConnected: false, handle);

        // The raw-handle constructor does not learn the pipe's native PIPE_READMODE_MESSAGE from
        // the handle -- PipeStream tracks its own ReadMode independently of what CreateNamedPipe
        // was actually called with, and defaults to Byte, which would make every
        // IsMessageComplete read below throw InvalidOperationException. Setting it here just
        // updates .NET's own bookkeeping to match what the pipe already natively is; it does not
        // change the underlying pipe's type (that was fixed for good at CreateNamedPipe).
        pipe.ReadMode = PipeTransmissionMode.Message;
        return pipe;
    }

    /// <summary>
    /// SDDL: an ACE granting the current user full pipe access, and nobody else -- the DACL's
    /// implicit default (no matching ACE) denies every other account, which is what keeps this out
    /// of the way of "grant nothing, deny nothing else" being spelled out as an explicit deny; a
    /// mandatory label ACE (<c>ML</c>) with the "no write-up" flag (<c>NW</c>) at the well-known
    /// Medium alias (<c>ME</c>), matching the task file's <c>S:(ML;;NW;;;ME)</c> exactly, so an
    /// unelevated (Medium-integrity) client can write to a pipe an elevated (High-integrity) server
    /// created.
    /// </summary>
    internal static unsafe SafePipeHandle CreateSecuredHandle(string pipeName)
    {
        var userSid = System.Security.Principal.WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The current Windows identity has no user SID.");
        var sddl = $"D:(A;;GA;;;{userSid.Value})S:(ML;;NW;;;ME)";

        if (!PInvoke.ConvertStringSecurityDescriptorToSecurityDescriptor(
                sddl, PInvoke.SDDL_REVISION_1, out var securityDescriptor))
        {
            throw LastWin32Error("ConvertStringSecurityDescriptorToSecurityDescriptor");
        }

        try
        {
            var securityAttributes = new SECURITY_ATTRIBUTES
            {
                nLength = (uint)sizeof(SECURITY_ATTRIBUTES),
                lpSecurityDescriptor = securityDescriptor.Value,
                bInheritHandle = false,
            };

            // The bare AlertPipeName.Resolve() name, not this full \\.\pipe\ path: .NET's
            // NamedPipeServerStream/NamedPipeClientStream add that prefix themselves, but the raw
            // Win32 CreateNamedPipe this class calls directly does not.
            using var handle = PInvoke.CreateNamedPipe(
                @"\\.\pipe\" + pipeName,
                FILE_FLAGS_AND_ATTRIBUTES.PIPE_ACCESS_DUPLEX | FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_OVERLAPPED,
                NAMED_PIPE_MODE.PIPE_TYPE_MESSAGE | NAMED_PIPE_MODE.PIPE_READMODE_MESSAGE | NAMED_PIPE_MODE.PIPE_REJECT_REMOTE_CLIENTS,
                nMaxInstances: 1u,
                nOutBufferSize: (uint)ReadBufferSize,
                nInBufferSize: (uint)ReadBufferSize,
                nDefaultTimeOut: 0u,
                securityAttributes);

            if (handle.IsInvalid)
            {
                throw LastWin32Error("CreateNamedPipe");
            }

            // NamedPipeServerStream wants System.IO.Pipes.SafePipeHandle, not the SafeFileHandle
            // CsWin32's CreateNamedPipe binding returns; both are thin wrappers around the same
            // raw HANDLE, so ownership is transferred by re-wrapping the raw value and marking the
            // original invalid rather than closed -- letting it Dispose too would double-close.
            var raw = handle.DangerousGetHandle();
            handle.SetHandleAsInvalid();
            return new SafePipeHandle(raw, ownsHandle: true);
        }
        finally
        {
            PInvoke.LocalFree((HLOCAL)securityDescriptor.Value);
        }
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(5));
        _stopping.Dispose();
    }

    /// <summary>
    /// <see cref="Win32Exception(int, string)"/> uses the supplied message VERBATIM, unlike the
    /// single-<c>int</c> constructor, which looks up the system's own text for the error code --
    /// this keeps both: the caller's operation name, and the real reason (e.g. "The system cannot
    /// find the file specified") that name alone would otherwise discard.
    /// </summary>
    private static Win32Exception LastWin32Error(string operation)
    {
        var code = Marshal.GetLastWin32Error();
        return new Win32Exception(code, $"{operation} failed: {new Win32Exception(code).Message}");
    }

    /// <summary>
    /// Reads a live pipe handle's owner, DACL and mandatory label back as an SDDL string --
    /// test-only, used by <c>NamedPipeAlertCommandServerAclTests</c> to prove
    /// <see cref="CreateSecuredHandle"/> actually attached the security descriptor it built, rather
    /// than trusting the SDDL string that went IN. Walking the DACL/SACL by hand
    /// (<c>GetAce</c>, <c>SYSTEM_MANDATORY_LABEL_ACE</c>) would prove the same thing with far more
    /// native code; round-tripping through the OS's own string form keeps the test a plain
    /// substring check.
    /// </summary>
    internal static unsafe string DescribeSecurity(SafeHandle handle)
    {
        var result = PInvoke.GetSecurityInfo(
            handle, SE_OBJECT_TYPE.SE_KERNEL_OBJECT,
            OBJECT_SECURITY_INFORMATION.OWNER_SECURITY_INFORMATION
                | OBJECT_SECURITY_INFORMATION.DACL_SECURITY_INFORMATION
                | OBJECT_SECURITY_INFORMATION.LABEL_SECURITY_INFORMATION,
            out _, out _, out _, out _, out var securityDescriptor);

        if (result != WIN32_ERROR.ERROR_SUCCESS)
        {
            throw new Win32Exception(
                (int)result, $"GetSecurityInfo failed: {new Win32Exception((int)result).Message}");
        }

        try
        {
            if (!PInvoke.ConvertSecurityDescriptorToStringSecurityDescriptor(
                    securityDescriptor, PInvoke.SDDL_REVISION_1,
                    OBJECT_SECURITY_INFORMATION.OWNER_SECURITY_INFORMATION
                        | OBJECT_SECURITY_INFORMATION.DACL_SECURITY_INFORMATION
                        | OBJECT_SECURITY_INFORMATION.LABEL_SECURITY_INFORMATION,
                    out var sddlPointer))
            {
                throw LastWin32Error("ConvertSecurityDescriptorToStringSecurityDescriptor");
            }

            try
            {
                return sddlPointer.ToString();
            }
            finally
            {
                PInvoke.LocalFree((HLOCAL)(void*)sddlPointer.Value);
            }
        }
        finally
        {
            PInvoke.LocalFree((HLOCAL)securityDescriptor.Value);
        }
    }
}
