using System.Diagnostics;
using System.IO;
using System.Windows;
using FishyNet.ViewModels;
using FishyNet.Views;

namespace FishyNet;

public partial class App : Application
{
    private MainViewModel? _mainViewModel;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        CleanupLegacyProcessesAndBackups();

        _mainViewModel = new MainViewModel();
        var mainWindow = new MainWindow(_mainViewModel);
        MainWindow = mainWindow;

        bool startMinimized = e.Args.Any(a =>
            string.Equals(a, "minimize", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, "-m", StringComparison.OrdinalIgnoreCase));

        mainWindow.Show();
        if (startMinimized)
        {
            mainWindow.WindowState = WindowState.Minimized;
            mainWindow.Hide();
        }
    }

    private static void CleanupLegacyProcessesAndBackups()
    {
        try
        {
            int currentPid = Environment.ProcessId;
            string[] processNames = ["FishyNet", "SelfishNetv3"];
            foreach (string name in processNames)
            {
                foreach (Process proc in Process.GetProcessesByName(name))
                {
                    using (proc)
                    {
                        if (proc.Id != currentPid)
                        {
                            try
                            {
                                proc.Kill();
                                proc.WaitForExit(1500);
                            }
                            catch
                            {
                                // Ignore if already exited
                            }
                        }
                    }
                }
            }

            string legacyBackupDir = Path.Combine(AppContext.BaseDirectory, "_legacy_locked");
            if (Directory.Exists(legacyBackupDir))
            {
                Directory.Delete(legacyBackupDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        _mainViewModel?.Dispose();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mainViewModel?.Dispose();
        base.OnExit(e);
    }
}
