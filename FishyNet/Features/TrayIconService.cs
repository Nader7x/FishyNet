using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace FishyNet.Features;

/// <summary>
/// Pure Win32 Shell_NotifyIconW system tray integration for WPF with live speed tooltips,
/// toast/balloon notifications, and a dark-themed quick-action context menu.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private const int WM_APP = 0x8000;
    private const int WM_TRAYMESSAGE = WM_APP + 0x42;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_CONTEXTMENU = 0x007B;

    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000002;

    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;
    private const uint NIF_INFO = 0x00000010;

    private const uint NIIF_INFO = 0x00000001;
    private const uint NIIF_WARNING = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint ExtractAssociatedIconW(nint hInst, string lpIconPath, ref ushort lpiIcon);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadImageW(nint hinst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

    [DllImport("user32.dll")]
    private static extern nint LoadIconW(nint hInstance, nint lpIconName);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint hIcon);

    private HwndSource? _hwndSource;
    private nint _hIcon;
    private bool _ownsIcon;
    private bool _isAdded;
    private bool _disposed;

    private ContextMenu? _contextMenu;
    private MenuItem? _headerStatusItem;
    private MenuItem? _toggleRedirectItem;

    public event Action? RestoreRequested;
    public event Action? ToggleRedirectionRequested;
    public event Action? ScanSubnetRequested;
    public event Action<BandwidthPresetProfile>? ApplyPresetRequested;
    public event Action? MonitorAllRequested;
    public event Action? UnspoofAllRequested;
    public event Action? ExitRequested;

    public void Initialize()
    {
        if (_isAdded) return;

        var parameters = new HwndSourceParameters("FishyNet_TraySink")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0
        };
        _hwndSource = new HwndSource(parameters);
        _hwndSource.AddHook(WndProc);

        _hIcon = LoadApplicationIcon(out _ownsIcon);

        var data = CreateBaseData();
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        data.szTip = "FishyNet — Ready";

        _isAdded = Shell_NotifyIconW(NIM_ADD, ref data);
        BuildContextMenu();
    }

    public void UpdateLiveStatus(string tooltipText, string menuHeaderText, bool isRedirecting)
    {
        if (!_isAdded || _hwndSource is null) return;

        var data = CreateBaseData();
        data.uFlags = NIF_TIP;
        data.szTip = tooltipText.Length > 120 ? tooltipText[..120] : tooltipText;
        Shell_NotifyIconW(NIM_MODIFY, ref data);

        if (_headerStatusItem is not null)
        {
            _headerStatusItem.Header = menuHeaderText;
        }

        if (_toggleRedirectItem is not null)
        {
            _toggleRedirectItem.Header = isRedirecting
                ? "Stop Packet Redirection"
                : "Start Packet Redirection";
        }
    }

    public void ShowNotification(string title, string message, bool isWarning = false)
    {
        if (!_isAdded || _hwndSource is null) return;

        var data = CreateBaseData();
        data.uFlags = NIF_INFO;
        data.szInfoTitle = title.Length > 60 ? title[..60] : title;
        data.szInfo = message.Length > 240 ? message[..240] : message;
        data.dwInfoFlags = isWarning ? NIIF_WARNING : NIIF_INFO;
        data.uTimeoutOrVersion = 4000;
        Shell_NotifyIconW(NIM_MODIFY, ref data);
    }

    private NOTIFYICONDATAW CreateBaseData()
    {
        return new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _hwndSource?.Handle ?? nint.Zero,
            uID = 1001,
            uCallbackMessage = WM_TRAYMESSAGE,
            hIcon = _hIcon,
            szTip = string.Empty,
            szInfo = string.Empty,
            szInfoTitle = string.Empty
        };
    }

    private static nint LoadApplicationIcon(out bool ownsIcon)
    {
        ownsIcon = false;
        try
        {
            string icoFile = Path.Combine(AppContext.BaseDirectory, "app.ico");
            if (File.Exists(icoFile))
            {
                const uint IMAGE_ICON = 1;
                const uint LR_LOADFROMFILE = 0x00000010;
                nint loaded = LoadImageW(nint.Zero, icoFile, IMAGE_ICON, 32, 32, LR_LOADFROMFILE);
                if (loaded != nint.Zero)
                {
                    ownsIcon = true;
                    return loaded;
                }
            }

            string exePath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "FishyNet.exe");
            if (File.Exists(exePath))
            {
                ushort idx = 0;
                nint extracted = ExtractAssociatedIconW(nint.Zero, exePath, ref idx);
                if (extracted != nint.Zero)
                {
                    ownsIcon = true;
                    return extracted;
                }
            }
        }
        catch
        {
            // Fallback to system application icon
        }

        return LoadIconW(nint.Zero, 32512); // IDI_APPLICATION
    }

    private void BuildContextMenu()
    {
        var emeraldBrush = new SolidColorBrush(Color.FromRgb(16, 185, 129));
        var dangerBrush = new SolidColorBrush(Color.FromRgb(248, 113, 113));

        _contextMenu = new ContextMenu();

        _headerStatusItem = new MenuItem
        {
            Header = "FishyNet — Idle",
            IsHitTestVisible = false,
            Focusable = false,
            FontWeight = FontWeights.SemiBold,
            Foreground = emeraldBrush
        };
        _contextMenu.Items.Add(_headerStatusItem);
        _contextMenu.Items.Add(new Separator());

        var openItem = new MenuItem { Header = "Open Dashboard Window", FontWeight = FontWeights.SemiBold };
        openItem.Click += (_, _) => RestoreRequested?.Invoke();
        _contextMenu.Items.Add(openItem);

        _toggleRedirectItem = new MenuItem { Header = "Start Packet Redirection" };
        _toggleRedirectItem.Click += (_, _) => ToggleRedirectionRequested?.Invoke();
        _contextMenu.Items.Add(_toggleRedirectItem);

        var scanItem = new MenuItem { Header = "Scan Subnet for Devices" };
        scanItem.Click += (_, _) => ScanSubnetRequested?.Invoke();
        _contextMenu.Items.Add(scanItem);

        var profilesMenu = new MenuItem
        {
            Header = "Quick Bandwidth Profile"
        };

        foreach (BandwidthPresetProfile preset in DeviceProfileStore.BuiltInPresets)
        {
            BandwidthPresetProfile captured = preset;
            var presetItem = new MenuItem
            {
                Header = $"{captured.Name} ({captured.Description})"
            };
            presetItem.Click += (_, _) => ApplyPresetRequested?.Invoke(captured);
            profilesMenu.Items.Add(presetItem);
        }

        _contextMenu.Items.Add(profilesMenu);

        var monitorAllItem = new MenuItem { Header = "Monitor All LAN Hosts" };
        monitorAllItem.Click += (_, _) => MonitorAllRequested?.Invoke();
        _contextMenu.Items.Add(monitorAllItem);

        var unspoofItem = new MenuItem { Header = "Bypass / Unspoof All Hosts" };
        unspoofItem.Click += (_, _) => UnspoofAllRequested?.Invoke();
        _contextMenu.Items.Add(unspoofItem);

        _contextMenu.Items.Add(new Separator());

        var exitItem = new MenuItem
        {
            Header = "Exit & Restore ARP Tables",
            Foreground = dangerBrush
        };
        exitItem.Click += (_, _) => ExitRequested?.Invoke();
        _contextMenu.Items.Add(exitItem);
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WM_TRAYMESSAGE)
        {
            int eventId = (int)(lParam & 0xFFFF);
            if (eventId == WM_LBUTTONDBLCLK)
            {
                handled = true;
                Application.Current?.Dispatcher.BeginInvoke(() => RestoreRequested?.Invoke());
            }
            else if (eventId == WM_RBUTTONUP || eventId == WM_CONTEXTMENU)
            {
                handled = true;
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    if (_contextMenu is not null)
                    {
                        _contextMenu.IsOpen = true;
                    }
                });
            }
        }
        return nint.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_isAdded && _hwndSource is not null)
        {
            var data = CreateBaseData();
            Shell_NotifyIconW(NIM_DELETE, ref data);
            _isAdded = false;
        }

        _hwndSource?.Dispose();
        _hwndSource = null;

        if (_ownsIcon && _hIcon != nint.Zero)
        {
            DestroyIcon(_hIcon);
            _hIcon = nint.Zero;
        }
    }
}
