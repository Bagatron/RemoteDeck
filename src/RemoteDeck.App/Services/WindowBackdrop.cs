using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace RemoteDeck.App.Services;

/// <summary>
/// Applies the theme's "backdrop" (Mica, acrylic, tabbed or none) to a window through the Windows 11
/// compositor. Needs Windows 11 22H2 (build 22621) or newer; on anything older it quietly does nothing.
/// The material only shows where the theme's colors are translucent (#RRGGBBAA).
/// </summary>
internal static class WindowBackdrop
{
    private const int DwmwaSystemBackdropType = 38;
    private const int MinimumBuild = 22621;

    public static bool IsSupported => Environment.OSVersion.Version.Build >= MinimumBuild;

    /// <summary>The DWM value for a theme backdrop name; null for unknown names.</summary>
    public static int? ToDwmValue(string backdrop) => backdrop switch
    {
        "none" => 1,
        "mica" => 2,
        "acrylic" => 3,
        "tabbed" => 4,
        _ => null,
    };

    public static void Apply(Window window, string backdrop)
    {
        if (!IsSupported || ToDwmValue(backdrop) is not { } value)
        {
            return;
        }

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || HwndSource.FromHwnd(handle) is not { } source)
        {
            return;
        }

        var translucent = value != 1;

        try
        {
            // WPF paints the window's back buffer opaque unless told otherwise.
            if (source.CompositionTarget is { } target)
            {
                target.BackgroundColor = translucent ? Colors.Transparent : SystemColors.WindowColor;
            }

            var margins = translucent ? new Margins(-1) : new Margins(0);
            DwmExtendFrameIntoClientArea(handle, ref margins);
            DwmSetWindowAttribute(handle, DwmwaSystemBackdropType, ref value, sizeof(int));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // No compositor support: the theme simply stays opaque.
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;

        public Margins(int all) => Left = Right = Top = Bottom = all;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);
}
