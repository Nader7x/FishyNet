using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Resources;
using FishyNet.Core.Native;
using FishyNet.Themes;
using FishyNet.ViewModels;
using Microsoft.Win32;

namespace FishyNet.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private MiniHudWindow? _miniHudWindow;
    private bool _isExplicitExit;
    private bool _hasShownTrayNotice;
    private bool _backdropEventsHooked;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        TryLoadWindowIcon();
        _viewModel = viewModel;
        DataContext = _viewModel;

        _viewModel.RequestShowWindow += RestoreFromTray;
        _viewModel.RequestExplicitExit += PerformExplicitExit;
        _viewModel.RequestMinimize += () => WindowState = WindowState.Minimized;
        _viewModel.RequestMaximizeRestore += () => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        _viewModel.RequestClose += Close;
        _viewModel.RequestMiniHudVisibilityChanged += OnMiniHudVisibilityChanged;

        SourceInitialized += OnWindowSourceInitialized;
        Loaded += OnWindowLoaded;
    }

    private void OnMiniHudVisibilityChanged(bool show)
    {
        if (show)
        {
            if (_miniHudWindow is null)
            {
                _miniHudWindow = new MiniHudWindow(_viewModel);
                _miniHudWindow.Closed += (_, _) => _miniHudWindow = null;
            }
            _miniHudWindow.Show();
            _miniHudWindow.Activate();
            WindowState = WindowState.Minimized;
        }
        else
        {
            _miniHudWindow?.Hide();
            RestoreFromTray();
        }
    }

    private void OnWindowSourceInitialized(object? sender, EventArgs e)
    {
        HookBackdropEvents();
        RefreshBackdrop();
    }

    private void HookBackdropEvents()
    {
        if (_backdropEventsHooked)
        {
            return;
        }

        _backdropEventsHooked = true;
        ThemeManager.PaletteChanged += OnPaletteChanged;
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    private void UnhookBackdropEvents()
    {
        if (!_backdropEventsHooked)
        {
            return;
        }

        _backdropEventsHooked = false;
        ThemeManager.PaletteChanged -= OnPaletteChanged;
        SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
    }

    private void OnPaletteChanged(string paletteId)
    {
        QueueBackdropRefresh();
    }

    private void OnSystemParametersChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName)
            || e.PropertyName == nameof(SystemParameters.HighContrast))
        {
            QueueBackdropRefresh();
        }
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General
            or UserPreferenceCategory.Color
            or UserPreferenceCategory.VisualStyle
            or UserPreferenceCategory.Accessibility)
        {
            QueueBackdropRefresh();
        }
    }

    private void QueueBackdropRefresh()
    {
        if (Dispatcher.CheckAccess())
        {
            RefreshBackdrop();
        }
        else
        {
            _ = Dispatcher.InvokeAsync(RefreshBackdrop);
        }
    }

    private void RefreshBackdrop()
    {
        if (new WindowInteropHelper(this).Handle == nint.Zero)
        {
            return;
        }

        SetResourceReference(BackgroundProperty, "WindowBackdropBrush");
        DwmBackdropHelper.ApplyBackdrop(this);
    }

    private void TryLoadWindowIcon()
    {
        try
        {
            StreamResourceInfo? res = Application.GetResourceStream(new Uri("pack://application:,,,/app.ico", UriKind.Absolute));
            if (res?.Stream is not null)
            {
                using Stream s = res.Stream;
                BitmapFrame frame = BitmapFrame.Create(s, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                Icon = frame;
                BrandLogoImage.Source = frame;
                return;
            }

            string icoPath = Path.Combine(AppContext.BaseDirectory, "app.ico");
            if (File.Exists(icoPath))
            {
                using FileStream fs = File.OpenRead(icoPath);
                BitmapFrame frame = BitmapFrame.Create(fs, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                Icon = frame;
                BrandLogoImage.Source = frame;
            }
        }
        catch
        {
            // Never block window creation if icon decoding fails
        }
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        _viewModel.Initialize();
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Minimized && _viewModel.CloseToTray)
        {
            Hide();
            if (!_hasShownTrayNotice)
            {
                _hasShownTrayNotice = true;
                _viewModel.NotifyMinimizedToTray();
            }
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_isExplicitExit && _viewModel.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            if (!_hasShownTrayNotice)
            {
                _hasShownTrayNotice = true;
                _viewModel.NotifyMinimizedToTray();
            }
            return;
        }

        _viewModel.Dispose();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        UnhookBackdropEvents();
        base.OnClosed(e);
    }

    public void RestoreFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    public void PerformExplicitExit()
    {
        _isExplicitExit = true;
        _miniHudWindow?.Close();
        _viewModel.Dispose();
        Application.Current.Shutdown();
    }

    private void OnInspectDeviceClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is DeviceViewModel vm)
        {
            _viewModel.SelectedDevice = vm;
            _viewModel.IsInspectorOpen = true;
        }
    }

    private void OnDismissTopologyHudClicked(object sender, RoutedEventArgs e)
    {
        _viewModel.SelectedDevice = null;
    }

    private void CapTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            CommitNumericTextBox(tb);
            tb.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            BindingExpression? binding = tb.GetBindingExpression(TextBox.TextProperty);
            binding?.UpdateTarget();
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    private void CapTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb)
        {
            CommitNumericTextBox(tb);
        }
    }

    private static void CommitNumericTextBox(TextBox tb)
    {
        if (string.IsNullOrWhiteSpace(tb.Text))
        {
            tb.Text = "0";
        }

        BindingExpression? binding = tb.GetBindingExpression(TextBox.TextProperty);
        binding?.UpdateSource();
    }
}
