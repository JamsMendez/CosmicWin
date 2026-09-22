using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Shell;

namespace SlideshowSpike;

// CLSID_DesktopWallpaper, documented in shobjidl.h / Microsoft Learn.
internal static class Clsid
{
    public static readonly Guid DesktopWallpaper = new("C2CF3110-460E-4fc1-B9D0-8A1C0C9CC4BD");
}

internal static unsafe class Program
{
    private static void Main(string[] args)
    {
        HRESULT coHr = PInvoke.CoInitializeEx(null, COINIT.COINIT_APARTMENTTHREADED);
        Console.WriteLine($"CoInitializeEx -> 0x{coHr.Value:X8}");

        Type? type = Type.GetTypeFromCLSID(Clsid.DesktopWallpaper);
        var wallpaper = (IDesktopWallpaper)Activator.CreateInstance(type!)!;
        Console.WriteLine("IDesktopWallpaper created.");

        if (args.Length > 0 && args[0] == "restore")
        {
            string restorePath = args[1];
            wallpaper.SetWallpaper(null, restorePath);
            Console.WriteLine($"Restored single wallpaper: {restorePath}");
            return;
        }

        PWSTR currentPtr;
        wallpaper.GetWallpaper(null, &currentPtr);
        string current = currentPtr.ToString();
        Console.WriteLine($"Current wallpaper (save this to restore): {current}");

        string folderPath = @"C:\Windows\Web\Wallpaper\Windows";
        Console.WriteLine($"Slideshow folder: {folderPath}, exists={Directory.Exists(folderPath)}");

        PInvoke.SHCreateItemFromParsingName(folderPath, null, out IShellItem folderItem);
        Console.WriteLine("SHCreateItemFromParsingName -> IShellItem created.");

        PInvoke.SHCreateShellItemArrayFromShellItem(folderItem, out IShellItemArray array);
        Console.WriteLine("SHCreateShellItemArrayFromShellItem -> IShellItemArray created.");

        wallpaper.SetSlideshow(array);
        Console.WriteLine("SetSlideshow called.");

        DESKTOP_SLIDESHOW_OPTIONS options;
        wallpaper.GetSlideshowOptions(&options, out uint tick);
        Console.WriteLine($"GetSlideshowOptions -> options={options}, tickMs={tick}");

        wallpaper.GetStatus(out DESKTOP_SLIDESHOW_STATE state);
        Console.WriteLine($"GetStatus -> {state} (DSS_SLIDESHOW bit = active)");

        Console.WriteLine();
        Console.WriteLine("Slideshow should now be active. Check whether DefView draws transparent.");
        Console.WriteLine($"To restore afterward: dotnet run -- restore \"{current}\"");
    }
}
