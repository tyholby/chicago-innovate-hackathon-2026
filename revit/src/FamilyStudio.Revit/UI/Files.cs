using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FamilyStudio.Core.Pipeline;

namespace FamilyStudio.Revit.UI;

/// <summary>Image loading and shell helpers shared by the windows.</summary>
internal static class Files
{
    /// <summary>
    /// Loads an image fully into memory, so the file is never locked while shown. Large images are
    /// decoded at display size: a 40-megapixel photo would otherwise cost about 160 MB each time.
    /// </summary>
    public static ImageSource? LoadImage(string path, int maxWidth)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            if (ReferenceImages.Measure(bytes) is { } size && size.Width > maxWidth) image.DecodePixelWidth = maxWidth;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Opens a URL, file or folder with its default program.</summary>
    public static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception) { /* the path is shown in the window */ }
    }

    /// <summary>Opens Explorer with the file selected.</summary>
    public static void Reveal(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch (Exception) { }
    }

    /// <summary>The user's Downloads folder, wherever it has been moved to.</summary>
    public static string DownloadsFolder()
    {
        try
        {
            if (SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero, out var path) == 0 && Directory.Exists(path))
                return path;
        }
        catch (Exception) { }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    /// <summary>A file name without the characters Windows does not allow.</summary>
    public static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => invalid.Contains(c) ? ' ' : c).ToArray());
        return string.Join(" ", safe.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim(' ', '.');
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid folder, uint flags, IntPtr token, out string path);
}
