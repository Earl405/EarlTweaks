using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LiveBoost;

/// <summary>
/// Game artwork for the My Games tiles, all read locally:
/// 1) your own cover image in %LocalAppData%\LiveBoost\covers\&lt;GameName&gt;.png/.jpg (full-tile cover), else
/// 2) the game's own program icon, saved once to %LocalAppData%\LiveBoost\icons when the game is detected, else
/// 3) the caller falls back to the letter tile. Nothing is downloaded.
/// </summary>
public static class GameIcons
{
    static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiveBoost");
    static string Safe(string game) => string.Concat(game.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
    static string IconFile(string game) => Path.Combine(Root, "icons", Safe(game) + ".png");

    /// <summary>Called when a game is first detected. Returns the exe path if Windows allows reading it.</summary>
    public static string? Capture(int pid, string game)
    {
        string? exe = null;
        try { using var p = Process.GetProcessById(pid); exe = p.MainModule?.FileName; } catch { }
        if (string.IsNullOrEmpty(exe)) return null;
        try
        {
            var f = IconFile(game);
            if (!File.Exists(f))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(f)!);
                var fi = new SHFILEINFO();
                SHGetFileInfo(exe, 0, ref fi, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_LARGEICON);
                if (fi.hIcon != IntPtr.Zero)
                {
                    try
                    {
                        var src = Imaging.CreateBitmapSourceFromHIcon(fi.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        var enc = new PngBitmapEncoder();
                        enc.Frames.Add(BitmapFrame.Create(src));
                        using var fs = File.Create(f);
                        enc.Save(fs);
                    }
                    finally { DestroyIcon(fi.hIcon); }
                }
            }
        }
        catch { }
        return exe;
    }

    static ImageSource? Open(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            var b = new BitmapImage();
            b.BeginInit(); b.CacheOption = BitmapCacheOption.OnLoad; b.UriSource = new Uri(file); b.EndInit(); b.Freeze();
            return b;
        }
        catch { return null; }
    }

    /// <summary>Your own full-size cover image, if you put one in the covers folder.</summary>
    public static ImageSource? LoadCover(string game)
    {
        foreach (var ext in new[] { ".png", ".jpg", ".jpeg" })
            if (Open(Path.Combine(Root, "covers", Safe(game) + ext)) is ImageSource s) return s;
        return null;
    }

    /// <summary>The game's own program icon (saved the first time the game was detected).</summary>
    public static ImageSource? LoadIcon(string game) => Open(IconFile(game));

    // Windows shell API (no extra packages needed)
    const uint SHGFI_ICON = 0x100, SHGFI_LARGEICON = 0;
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEINFO
    {
        public IntPtr hIcon; public int iIcon; public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);
    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr hIcon);
}
