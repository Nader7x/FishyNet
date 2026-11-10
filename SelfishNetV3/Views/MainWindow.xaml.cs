using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Resources;
using SelfishNetv3.ViewModels;

namespace SelfishNetv3.Views;

public partial class MainWindow : Window
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMSBT_TRANSIENTWINDOW = 3; // Desktop Acrylic Frosted Glass

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int attrValue, int attrSize);

    private readonly MainViewModel _viewModel;
    private bool _isExplicitExit;
    private bool _hasShownTrayNotice;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        TryLoadWindowIcon();
        _viewModel = viewModel;
        DataContext = _viewModel;

        _viewModel.RequestShowWindow += RestoreFromTray;
        _viewModel.RequestExplicitExit += PerformExplicitExit;

        SourceInitialized += OnWindowSourceInitialized;
        Loaded += OnWindowLoaded;
    }

    private void OnWindowSourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            nint hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == nint.Zero) return;

            // Enable dark mode titlebar on Windows 10/11
            int darkMode = 1;
            _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

            // Deep obsidian caption color (COLORREF 0x00BBGGRR -> #080C12 = 0x00120C08)
            int captionColor = 0x00120C08;
            _ = DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref captionColor, sizeof(int));

            // Crisp text color (#F8FAFC = 0x00FCFAF8)
            int textColor = 0x00FCFAF8;
            _ = DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref textColor, sizeof(int));

            // Subtle slate-cyan border (#1E2C42 = 0x00422C1E)
            int borderColor = 0x00422C1E;
            _ = DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref borderColor, sizeof(int));

            // Enable DWM Acrylic Frosted Glass backdrop on Windows 11 22H2+
            int backdropType = DWMSBT_TRANSIENTWINDOW;
            _ = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));
        }
        catch
        {
            // Ignore on older OS builds
        }
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
        _viewModel.Dispose();
        Application.Current.Shutdown();
    }
}
