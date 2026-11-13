using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;

namespace FishyNet.Themes;

/// <summary>
/// Manages runtime frosted-glass palette switching across the application ResourceDictionary
/// and exposes a lightweight rounded-rectangle geometry clip attached property for containers.
/// </summary>
public static class ThemeManager
{
    public const string DefaultPaletteId = "obsidian_cyan";

    private const string PaletteUriPrefix = "Themes/Palettes/";

    private static readonly Dictionary<string, string> PaletteFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["obsidian_cyan"] = "ObsidianCyan.xaml",
        ["graphite_mint"] = "GraphiteMint.xaml",
        ["carbon_cobalt"] = "CarbonCobalt.xaml",
        ["smoked_onyx"] = "SmokedOnyx.xaml"
    };

    public static IReadOnlyList<string> AvailablePaletteIds { get; } =
    [
        "obsidian_cyan",
        "graphite_mint",
        "carbon_cobalt",
        "smoked_onyx"
    ];

    public static string CurrentPaletteId { get; private set; } = DefaultPaletteId;

    public static event Action<string>? PaletteChanged;

    /// <summary>
    /// Swaps the active palette ResourceDictionary in Application.Current.Resources.MergedDictionaries.
    /// Safely no-ops when Application.Current is null (such as in headless xUnit test runs).
    /// </summary>
    public static bool ApplyPalette(string? paletteId)
    {
        string normalizedId = NormalizePaletteId(paletteId);
        CurrentPaletteId = normalizedId;

        Application? app = Application.Current;
        if (app is null)
        {
            return false;
        }

        if (!app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.Invoke(() => ApplyPaletteOnUiThread(app, normalizedId));
        }
        else
        {
            ApplyPaletteOnUiThread(app, normalizedId);
        }

        return true;
    }

    public static string NormalizePaletteId(string? paletteId)
    {
        if (!string.IsNullOrWhiteSpace(paletteId) && PaletteFiles.ContainsKey(paletteId.Trim()))
        {
            return paletteId.Trim().ToLowerInvariant();
        }

        return DefaultPaletteId;
    }

    private static void ApplyPaletteOnUiThread(Application app, string normalizedId)
    {
        string fileName = PaletteFiles[normalizedId];
        var paletteUri = new Uri($"pack://application:,,,/FishyNet;component/{PaletteUriPrefix}{fileName}", UriKind.Absolute);
        var newPalette = new ResourceDictionary { Source = paletteUri };

        Collection<ResourceDictionary> appMerged = app.Resources.MergedDictionaries;
        ResourceDictionary? darkThemeDict = FindDarkThemeDictionary(appMerged);

        if (darkThemeDict is not null)
        {
            ReplaceOrAppendPalette(darkThemeDict.MergedDictionaries, newPalette);
            RemovePaletteDictionaries(appMerged);
        }
        else
        {
            ReplaceOrAppendPalette(appMerged, newPalette);
        }

        PaletteChanged?.Invoke(normalizedId);
    }

    private static ResourceDictionary? FindDarkThemeDictionary(Collection<ResourceDictionary> dictionaries)
    {
        for (int i = 0; i < dictionaries.Count; i++)
        {
            ResourceDictionary dict = dictionaries[i];
            string? src = dict.Source?.OriginalString;
            if (!string.IsNullOrEmpty(src) && src.Contains("DarkTheme.xaml", StringComparison.OrdinalIgnoreCase))
            {
                return dict;
            }
        }

        return null;
    }

    private static void ReplaceOrAppendPalette(Collection<ResourceDictionary> dictionaries, ResourceDictionary newPalette)
    {
        int firstPaletteIndex = -1;
        for (int i = dictionaries.Count - 1; i >= 0; i--)
        {
            string? src = dictionaries[i].Source?.OriginalString;
            if (!string.IsNullOrEmpty(src) && src.Contains("Palettes/", StringComparison.OrdinalIgnoreCase))
            {
                firstPaletteIndex = i;
                dictionaries.RemoveAt(i);
            }
        }

        if (firstPaletteIndex >= 0 && firstPaletteIndex <= dictionaries.Count)
        {
            dictionaries.Insert(firstPaletteIndex, newPalette);
        }
        else
        {
            dictionaries.Add(newPalette);
        }
    }

    private static void RemovePaletteDictionaries(Collection<ResourceDictionary> dictionaries)
    {
        for (int i = dictionaries.Count - 1; i >= 0; i--)
        {
            string? src = dictionaries[i].Source?.OriginalString;
            if (!string.IsNullOrEmpty(src) && src.Contains("Palettes/", StringComparison.OrdinalIgnoreCase))
            {
                dictionaries.RemoveAt(i);
            }
        }
    }

    /// <summary>
    /// Attached property that clips a FrameworkElement's bounds to a frozen rounded RectangleGeometry,
    /// preventing square children (such as DataGrid headers/rows) from poking outside rounded borders.
    /// </summary>
    public static readonly DependencyProperty ClipCornerRadiusProperty =
        DependencyProperty.RegisterAttached(
            "ClipCornerRadius",
            typeof(double),
            typeof(ThemeManager),
            new PropertyMetadata(0.0, OnClipCornerRadiusChanged));

    public static double GetClipCornerRadius(DependencyObject obj) => (double)obj.GetValue(ClipCornerRadiusProperty);

    public static void SetClipCornerRadius(DependencyObject obj, double value) => obj.SetValue(ClipCornerRadiusProperty, value);

    private static void OnClipCornerRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        element.SizeChanged -= OnElementSizeChangedForClip;
        double radius = (double)e.NewValue;

        if (radius > 0.0)
        {
            element.SizeChanged += OnElementSizeChangedForClip;
            UpdateElementRoundedClip(element, radius);
        }
        else
        {
            element.Clip = null;
        }
    }

    private static void OnElementSizeChangedForClip(object sender, SizeChangedEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            UpdateElementRoundedClip(element, GetClipCornerRadius(element));
        }
    }

    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.RegisterAttached(
            "IsActive",
            typeof(bool),
            typeof(ThemeManager),
            new PropertyMetadata(false));

    public static bool GetIsActive(DependencyObject obj) =>
        (bool)obj.GetValue(IsActiveProperty);

    public static void SetIsActive(DependencyObject obj, bool value) =>
        obj.SetValue(IsActiveProperty, value);

    private static void UpdateElementRoundedClip(FrameworkElement element, double radius)
    {
        double width = element.ActualWidth;
        double height = element.ActualHeight;
        if (width <= 0.0 || height <= 0.0)
        {
            return;
        }

        var rect = new RectangleGeometry(new Rect(0, 0, width, height), radius, radius);
        rect.Freeze();
        element.Clip = rect;
    }
}
