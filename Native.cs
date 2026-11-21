using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ImageSorter;

internal static class Native
{
    /// <summary>Explorer's "natural" ordering, so IMG_2 sorts before IMG_10.</summary>
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    public static extern int StrCmpLogicalW(string a, string b);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr handle);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHCreateItemFromParsingName(
        string path, IntPtr bindContext, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? factory);

    [StructLayout(LayoutKind.Sequential)]
    public struct SIZE
    {
        public int cx;
        public int cy;
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, int flags, out IntPtr hBitmap);
    }

    public static void UseDarkTitleBar(Window window)
    {
        const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        int on = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
    }
}
