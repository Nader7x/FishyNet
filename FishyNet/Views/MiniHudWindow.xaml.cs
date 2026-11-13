using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FishyNet.Core.Native;
using FishyNet.Themes;
using FishyNet.ViewModels;

namespace FishyNet.Views;

public partial class MiniHudWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MiniHudWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;

        PositionAtTopRight();

        SourceInitialized += OnWindowSourceInitialized;
        Loaded += OnWindowLoaded;
        Closed += OnWindowClosed;
    }

    private void PositionAtTopRight()
    {
        Rect workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 30;
        Top = workArea.Top + 30;
    }

    private void OnWindowSourceInitialized(object? sender, EventArgs e)
    {
        ApplyBackdropAndTransparency();
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
        ThemeManager.PaletteChanged += OnThemePaletteChanged;
        ApplyBackdropAndTransparency();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
        ThemeManager.PaletteChanged -= OnThemePaletteChanged;
    }

    private void OnSystemParametersChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SystemParameters.HighContrast))
        {
            Dispatcher.InvokeAsync(ApplyBackdropAndTransparency);
        }
    }

    private void OnThemePaletteChanged(string _)
    {
        Dispatcher.InvokeAsync(ApplyBackdropAndTransparency);
    }

    private void ApplyBackdropAndTransparency()
    {
        _ = DwmBackdropHelper.ApplyBackdrop(this);

        if (DwmBackdropHelper.IsTransparencyEnabled())
        {
            CapsuleBorder.SetResourceReference(Border.BackgroundProperty, "GlassSurfaceElevatedBrush");
            CapsuleBorder.SetResourceReference(Border.BorderBrushProperty, "GlassSpecularRimBrush");
        }
        else
        {
            CapsuleBorder.SetResourceReference(Border.BackgroundProperty, "WindowSolidFallbackBrush");
            CapsuleBorder.SetResourceReference(Border.BorderBrushProperty, "BorderEdgeBrush");
        }
    }

    private void OnHeaderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _viewModel.IsMiniHudActive = false;
        base.OnClosing(e);
    }

    private void OnRestoreFullWindowClicked(object sender, RoutedEventArgs e)
    {
        _viewModel.IsMiniHudActive = false;
    }

    private void OnCloseHudClicked(object sender, RoutedEventArgs e)
    {
        _viewModel.IsMiniHudActive = false;
    }
}
