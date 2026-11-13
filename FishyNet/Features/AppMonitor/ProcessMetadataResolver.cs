using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace FishyNet.Features.AppMonitor;

public interface IProcessMetadataResolver
{
    ProcessMetadata Resolve(int processId);
}

/// <summary>
/// Thread-safe, cached Windows PID -> process metadata resolver (Name, ExePath, Publisher via FileVersionInfo,
/// and optional frozen WPF BitmapSource icon via SHGetFileInfoW).
/// Safe in headless xUnit tests when Application.Current is null.
/// </summary>
public sealed unsafe partial class ProcessMetadataResolver : IProcessMetadataResolver
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiSmallIcon = 0x000000001;
    private const int MaxCacheEntries = 2048;

    private readonly bool? _iconExtractionOverride;
    private readonly long _pidCacheTtlTicks;
    private readonly ConcurrentDictionary<int, (ProcessMetadata Metadata, long TimestampTicks)> _pidCache = new();
    private readonly ConcurrentDictionary<string, CachedExeInfo> _exeCache = new(StringComparer.OrdinalIgnoreCase);

    private sealed record CachedExeInfo(string DisplayName, string Publisher, BitmapSource? Icon);

    [StructLayout(LayoutKind.Sequential)]
    private struct ShFileInfoW
    {
        public nint hIcon;
        public int iIcon;
        public uint dwAttributes;
        public fixed char szDisplayName[260];
        public fixed char szTypeName[80];
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint dwDesiredAccess, int bInheritHandle, int dwProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int CloseHandle(nint hObject);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    private static partial int QueryFullProcessImageName(
        nint hProcess,
        uint dwFlags,
        char* lpExeName,
        ref uint lpdwSize);

    [LibraryImport("shell32.dll", EntryPoint = "SHGetFileInfoW")]
    private static partial nint SHGetFileInfo(
        char* pszPath,
        uint dwFileAttributes,
        ref ShFileInfoW psfi,
        uint cbFileInfo,
        uint uFlags);

    [LibraryImport("user32.dll")]
    private static partial int DestroyIcon(nint hIcon);

    public ProcessMetadataResolver(bool? enableIconExtraction = null, TimeSpan? pidCacheTtl = null)
    {
        _iconExtractionOverride = enableIconExtraction;
        _pidCacheTtlTicks = (long)((pidCacheTtl ?? TimeSpan.FromSeconds(15)).TotalSeconds * Stopwatch.Frequency);
    }

    public ProcessMetadata Resolve(int processId)
    {
        if (processId == 0)
        {
            return new ProcessMetadata(0, "System Idle", "System Idle Process", string.Empty, "Microsoft Corporation", null);
        }

        if (processId == 4)
        {
            return new ProcessMetadata(4, "System", "Windows Kernel / System", "System", "Microsoft Corporation", null);
        }

        if (processId < 0)
        {
            return new ProcessMetadata(processId, "Unknown", "System / Unattributed", string.Empty, string.Empty, null);
        }

        long now = Stopwatch.GetTimestamp();
        if (_pidCache.TryGetValue(processId, out var cached) &&
            (now - cached.TimestampTicks) < _pidCacheTtlTicks)
        {
            return cached.Metadata;
        }

        ProcessMetadata resolved = ResolveUncached(processId);

        if (_pidCache.Count >= MaxCacheEntries)
        {
            _pidCache.Clear();
        }
        _pidCache[processId] = (resolved, now);
        return resolved;
    }

    public void InvalidatePid(int processId)
    {
        _pidCache.TryRemove(processId, out _);
    }

    private ProcessMetadata ResolveUncached(int processId)
    {
        string exePath = TryQueryProcessImagePath(processId);
        string processName = !string.IsNullOrEmpty(exePath)
            ? Path.GetFileName(exePath)
            : TryQueryProcessNameFallback(processId);

        if (string.IsNullOrWhiteSpace(processName))
        {
            processName = $"pid-{processId}";
        }

        if (string.IsNullOrEmpty(exePath))
        {
            string fallbackDisplay = Path.GetFileNameWithoutExtension(processName);
            if (string.IsNullOrWhiteSpace(fallbackDisplay)) fallbackDisplay = processName;
            return new ProcessMetadata(processId, processName, fallbackDisplay, string.Empty, string.Empty, null);
        }

        if (_exeCache.TryGetValue(exePath, out CachedExeInfo? cachedExe))
        {
            return new ProcessMetadata(
                processId,
                processName,
                cachedExe.DisplayName,
                exePath,
                cachedExe.Publisher,
                cachedExe.Icon);
        }

        string displayName = Path.GetFileNameWithoutExtension(processName);
        string publisher = string.Empty;

        try
        {
            if (File.Exists(exePath))
            {
                FileVersionInfo fvi = FileVersionInfo.GetVersionInfo(exePath);
                if (!string.IsNullOrWhiteSpace(fvi.FileDescription))
                {
                    displayName = fvi.FileDescription.Trim();
                }
                if (!string.IsNullOrWhiteSpace(fvi.CompanyName))
                {
                    publisher = fvi.CompanyName.Trim();
                }
            }
        }
        catch
        {
            // Ignore inaccessible binary metadata
        }

        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = processName;
        }

        BitmapSource? icon = ShouldExtractIcons() ? TryExtractSmallIcon(exePath) : null;

        if (_exeCache.Count >= MaxCacheEntries)
        {
            _exeCache.Clear();
        }

        // Only cache icon once icon extraction is enabled (or cache metadata cleanly)
        _exeCache[exePath] = new CachedExeInfo(displayName, publisher, icon);

        return new ProcessMetadata(processId, processName, displayName, exePath, publisher, icon);
    }

    private bool ShouldExtractIcons()
    {
        if (_iconExtractionOverride.HasValue)
        {
            return _iconExtractionOverride.Value;
        }

        return Application.Current is not null;
    }

    private static string TryQueryProcessImagePath(int processId)
    {
        if (!OperatingSystem.IsWindows())
        {
            return string.Empty;
        }

        nint handle = nint.Zero;
        try
        {
            handle = OpenProcess(ProcessQueryLimitedInformation, 0, processId);
            if (handle == nint.Zero)
            {
                return string.Empty;
            }

            char* buffer = stackalloc char[1024];
            uint size = 1024;
            if (QueryFullProcessImageName(handle, 0, buffer, ref size) != 0 && size > 0)
            {
                return new string(buffer, 0, (int)size);
            }
        }
        catch
        {
            // Ignore protected/exited process errors
        }
        finally
        {
            if (handle != nint.Zero)
            {
                _ = CloseHandle(handle);
            }
        }

        return string.Empty;
    }

    private static string TryQueryProcessNameFallback(int processId)
    {
        try
        {
            using Process proc = Process.GetProcessById(processId);
            return proc.ProcessName;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static BitmapSource? TryExtractSmallIcon(string exePath)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(exePath))
        {
            return null;
        }

        nint hIcon = nint.Zero;
        try
        {
            var shfi = new ShFileInfoW();
            fixed (char* pathPtr = exePath)
            {
                nint res = SHGetFileInfo(
                    pathPtr,
                    0,
                    ref shfi,
                    (uint)sizeof(ShFileInfoW),
                    ShgfiIcon | ShgfiSmallIcon);
                if (res == nint.Zero || shfi.hIcon == nint.Zero)
                {
                    return null;
                }
                hIcon = shfi.hIcon;
            }

            BitmapSource bmp = Imaging.CreateBitmapSourceFromHIcon(
                hIcon,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hIcon != nint.Zero)
            {
                _ = DestroyIcon(hIcon);
            }
        }
    }
}
