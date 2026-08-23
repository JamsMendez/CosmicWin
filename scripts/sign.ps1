<#
.SYNOPSIS
    Authenticode-signs the built CosmicWin binaries.

.DESCRIPTION
    Signing is what turns UAC's orange "Unknown publisher" prompt into a named one, and what lets
    SmartScreen build a reputation for the binary instead of warning every person who downloads it.

    It needs a REAL certificate from a public CA. A self-signed certificate does nothing here: it is
    only trusted on machines where its root was installed by hand, so it changes nothing for anyone
    else and is worth less than not signing at all, because it looks like protection and is not.

    Two sources are supported:

      -PfxPath      A .pfx file. Note that since June 2023 the CA/Browser Forum requires code-signing
                    private keys to live on FIPS 140-2 hardware, so a plain .pfx is only realistic
                    for a test certificate or one exported before that rule.
      -Thumbprint   A certificate already installed in the current user's store, which is how a
                    hardware token or a smart card presents itself.

    Always timestamped. Without a timestamp the signature stops being valid the day the certificate
    expires; with one it stays valid for the life of the timestamp, which is the whole point.

.PARAMETER Configuration
    Build configuration to sign. Defaults to Release, because a Debug build is not what ships.

.PARAMETER PfxPath
    Path to a .pfx certificate file.

.PARAMETER PfxPassword
    Password for the .pfx. Prompted for if omitted, so it never lands in shell history.

.PARAMETER Thumbprint
    SHA1 thumbprint of a certificate in Cert:\CurrentUser\My.

.EXAMPLE
    ./scripts/sign.ps1 -Thumbprint A1B2C3...
    Sign the Release build with a certificate held on a hardware token.
#>
[CmdletBinding(DefaultParameterSetName = 'Thumbprint')]
param(
    [string]$Configuration = 'Release',

    [Parameter(ParameterSetName = 'Pfx', Mandatory = $true)]
    [string]$PfxPath,

    [Parameter(ParameterSetName = 'Pfx')]
    [System.Security.SecureString]$PfxPassword,

    [Parameter(ParameterSetName = 'Thumbprint', Mandatory = $true)]
    [string]$Thumbprint,

    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$framework = 'net10.0-windows10.0.19041.0'

# Only OUR binaries. Signing a dependency would put this project's name on code it did not write.
$targets = @(
    "CosmicWin.App\bin\$Configuration\$framework\CosmicWin.App.exe",
    "CosmicWin.App\bin\$Configuration\$framework\CosmicWin.App.dll",
    "CosmicWin.App\bin\$Configuration\$framework\CosmicWin.Interop.dll",
    "CosmicWin.App\bin\$Configuration\$framework\CosmicWin.Layout.dll",
    "CosmicWin.Launcher\bin\$Configuration\$framework\CosmicWin.Launcher.exe"
) | ForEach-Object { Join-Path $repo $_ } | Where-Object { Test-Path $_ }

if (-not $targets) {
    Write-Error "Nothing to sign. Build $Configuration first: dotnet build -c $Configuration"
}

$signtool = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match '\\x64\\' } |
    Sort-Object FullName -Descending |
    Select-Object -First 1

if (-not $signtool) {
    Write-Error 'signtool.exe not found. Install the Windows SDK.'
}

$arguments = @('sign', '/fd', 'SHA256', '/td', 'SHA256', '/tr', $TimestampUrl, '/v')

if ($PSCmdlet.ParameterSetName -eq 'Pfx') {
    if (-not (Test-Path $PfxPath)) { Write-Error "Certificate not found: $PfxPath" }
    if (-not $PfxPassword) { $PfxPassword = Read-Host -AsSecureString 'Certificate password' }
    $plain = [System.Runtime.InteropServices.Marshal]::PtrToStringAuto(
        [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($PfxPassword))
    $arguments += @('/f', $PfxPath, '/p', $plain)
}
else {
    $arguments += @('/sha1', $Thumbprint)
}

Write-Host "Signing $($targets.Count) file(s) with $($signtool.FullName)" -ForegroundColor Cyan
& $signtool.FullName @arguments @targets
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Verified, not assumed: signtool reporting success is not the same as Windows accepting the chain.
Write-Host 'Verifying...' -ForegroundColor Cyan
& $signtool.FullName verify /pa /v @targets
if ($LASTEXITCODE -ne 0) {
    Write-Error 'Signed, but verification failed -- the chain is not trusted on this machine.'
}

Write-Host 'Signed and verified.' -ForegroundColor Green
Write-Host 'A brand-new certificate still has no SmartScreen reputation: the download warning fades'
Write-Host 'as installs accumulate. EV certificates and Azure Artifact Signing skip that wait.'
