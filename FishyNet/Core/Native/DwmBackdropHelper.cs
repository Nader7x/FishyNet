using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace FishyNet.Core.Native;

/// <summary>
/// Applies native Windows 11 DWM Acrylic backdrop, rounded window corners, and immersive dark mode,
/// with a Windows 10 SetWindowCompositionAttribute Acrylic fallback and clean solid-color fallback
/// when Windows transparency is disabled or High Contrast mode is active.
/// </summary>
public static partial class DwmBackdropHelper
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    private const int DWMWCP_ROUND = 2;

    private const int DWMSBT_NONE = 1;
    private const int DWMSBT_TRANSIENTWINDOW = 3; // Desktop Acrylic Frosted Glass

    private const int WCA_ACCENT_POLICY = 19;
    private const int ACCENT_DISABLED = 0;
    private const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;

    private const int Windows11MinBuild = 22000;
    private const int Windows11BackdropBuild = 22523;

    private const string PersonalizeRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string EnableTransparencyValueName = "EnableTransparency";

    public static readonly Color DefaultFallbackColor = Color.FromArgb(0xFF, 0x07, 0x09, 0x0D);
    private static readonly Color DefaultWin10AcrylicTint = Color.FromArgb(0x40, 0x07, 0x09, 0x0D);

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int LeftWidth;
        public int RightWidth;
        public int TopHeight;
        public int BottomHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public uint GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public nint Data;
        public int SizeOfData;
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmExtendFrameIntoClientArea(nint hwnd, ref Margins pMarInset);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowCompositionAttribute(nint hwnd, ref WindowCompositionAttributeData data);

    /// <summary>
    /// Checks Windows Personalization EnableTransparency setting and SystemParameters.HighContrast.
    /// </summary>
    public static bool IsTransparencyEnabled()
    {
        if (SystemParameters.HighContrast)
        {
            return false;
        }

        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(PersonalizeRegistryPath, writable: false);
            if (key?.GetValue(EnableTransparencyValueName) is int enabled)
            {
                return enabled != 0;
            }
        }
        catch
        {
            // Fall through to enabled default if registry lookup is unavailable.
        }

        return true;
    }

    /// <summary>
    /// Resolves the active palette solid fallback color from WPF resources or returns DefaultFallbackColor.
    /// </summary>
    public static Color ResolveFallbackColor(FrameworkElement? element = null)
    {
        object? resource = element?.TryFindResource("WindowSolidFallbackColor")
            ?? Application.Current?.TryFindResource("WindowSolidFallbackColor");

        if (resource is Color color)
        {
            return Color.FromArgb(0xFF, color.R, color.G, color.B);
        }

        object? brushResource = element?.TryFindResource("WindowSolidFallbackBrush")
            ?? Application.Current?.TryFindResource("WindowSolidFallbackBrush");

        if (brushResource is SolidColorBrush scb)
        {
            Color c = scb.Color;
            return Color.FromArgb(0xFF, c.R, c.G, c.B);
        }

        return DefaultFallbackColor;
    }

    /// <summary>
    /// Applies Windows 11 DWM Acrylic or Windows 10 Acrylic blur-behind to the target window,
    /// or applies a clean solid fallback when transparency is disabled or High Contrast is active.
    /// </summary>
    public static bool ApplyBackdrop(Window? window, Color? fallbackColor = null, Color? win10TintColor = null)
    {
        if (window is null)
        {
            return false;
        }

        nint hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == nint.Zero)
        {
            return false;
        }

        bool transparencyAllowed = IsTransparencyEnabled();
        Color resolvedFallback = fallbackColor ?? ResolveFallbackColor(window);

        try
        {
            // Enable dark mode non-client styling; never set DWMWA_CAPTION_COLOR.
            int darkMode = 1;
            _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

            int build = Environment.OSVersion.Version.Build;
            if (build >= Windows11MinBuild)
            {
                int cornerPref = DWMWCP_ROUND;
                _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPref, sizeof(int));
            }

            if (!transparencyAllowed)
            {
                ApplySolidFallbackInternal(window, hwnd, build, resolvedFallback);
                return false;
            }

            // Layered windows (AllowsTransparency=True) manage alpha directly in WPF and do not use DWM frame extension.
            if (window.AllowsTransparency)
            {
                return true;
            }

            if (HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } compositionTarget)
            {
                compositionTarget.BackgroundColor = Colors.Transparent;
            }

            var margins = new Margins
            {
                LeftWidth = -1,
                RightWidth = -1,
                TopHeight = -1,
                BottomHeight = -1
            };
            _ = DwmExtendFrameIntoClientArea(hwnd, ref margins);

            if (build >= Windows11BackdropBuild)
            {
                int backdropType = DWMSBT_TRANSIENTWINDOW;
                if (DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int)) == 0)
                {
                    return true;
                }
            }

            // Windows 10 (or Win11 < 22523) Acrylic fallback
            Color tint = win10TintColor ?? Color.FromArgb(0x40, resolvedFallback.R, resolvedFallback.G, resolvedFallback.B);
            if (tint.A == 0)
            {
                tint = DefaultWin10AcrylicTint;
            }

            return ApplyWin10Acrylic(hwnd, tint);
        }
        catch
        {
            ApplySolidFallbackInternal(window, hwnd, Environment.OSVersion.Version.Build, resolvedFallback);
            return false;
        }
    }

    private static void ApplySolidFallbackInternal(Window window, nint hwnd, int osBuild, Color fallbackColor)
    {
        Color opaque = Color.FromArgb(0xFF, fallbackColor.R, fallbackColor.G, fallbackColor.B);

        try
        {
            if (osBuild >= Windows11BackdropBuild)
            {
                int none = DWMSBT_NONE;
                _ = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref none, sizeof(int));
            }

            DisableWin10Accent(hwnd);

            if (!window.AllowsTransparency)
            {
                var zeroMargins = new Margins();
                _ = DwmExtendFrameIntoClientArea(hwnd, ref zeroMargins);

                if (HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } compositionTarget)
                {
                    compositionTarget.BackgroundColor = opaque;
                }

                var solidBrush = new SolidColorBrush(opaque);
                solidBrush.Freeze();
                window.Background = solidBrush;
            }
        }
        catch
        {
            // Keep safe default if window handle is tearing down.
        }
    }

    private static unsafe bool ApplyWin10Acrylic(nint hwnd, Color tintColor)
    {
        var accent = new AccentPolicy
        {
            AccentState = ACCENT_ENABLE_ACRYLICBLURBEHIND,
            AccentFlags = 2,
            GradientColor = ToAbgr(tintColor),
            AnimationId = 0
        };

        var data = new WindowCompositionAttributeData
        {
            Attribute = WCA_ACCENT_POLICY,
            Data = (nint)(&accent),
            SizeOfData = sizeof(AccentPolicy)
        };

        return SetWindowCompositionAttribute(hwnd, ref data);
    }

    private static unsafe void DisableWin10Accent(nint hwnd)
    {
        var accent = new AccentPolicy
        {
            AccentState = ACCENT_DISABLED,
            AccentFlags = 0,
            GradientColor = 0,
            AnimationId = 0
        };

        var data = new WindowCompositionAttributeData
        {
            Attribute = WCA_ACCENT_POLICY,
            Data = (nint)(&accent),
            SizeOfData = sizeof(AccentPolicy)
        };

        _ = SetWindowCompositionAttribute(hwnd, ref data);
    }

    private static uint ToAbgr(Color color)
    {
        return ((uint)color.A << 24) | ((uint)color.B << 16) | ((uint)color.G << 8) | color.R;
    }
}
