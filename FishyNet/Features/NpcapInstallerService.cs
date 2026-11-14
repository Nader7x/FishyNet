using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace FishyNet.Features;

public sealed record NpcapInstallResult(bool Success, string Message, bool OpenedBrowserFallback = false);

/// <summary>
/// Downloads and launches the official Npcap installer when wpcap.dll is not present on the system.
/// </summary>
public static partial class NpcapInstallerService
{
    private const string NpcapDownloadPageUrl = "https://npcap.com/#download";
    private const string NpcapBaseUrl = "https://npcap.com/";

    [GeneratedRegex(@"dist/npcap-[0-9]+\.[0-9]+(?:\.[0-9]+)*\.exe", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NpcapExePathRegex();

    public static async Task<NpcapInstallResult> DownloadAndInstallAsync(
        Action<string>? onStatus = null,
        CancellationToken ct = default)
    {
        string? tempInstallerPath = null;
        try
        {
            onStatus?.Invoke("Checking latest Npcap installer...");
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("FishyNet/1.0");

            string html = await http.GetStringAsync(NpcapDownloadPageUrl, ct).ConfigureAwait(false);
            Match match = NpcapExePathRegex().Match(html);

            if (match.Success)
            {
                string relativePath = match.Value;
                string downloadUrl = NpcapBaseUrl + relativePath.TrimStart('/');
                string fileName = Path.GetFileName(relativePath);
                string tempDir = Path.Combine(Path.GetTempPath(), "FishyNet");
                Directory.CreateDirectory(tempDir);
                tempInstallerPath = Path.Combine(tempDir, fileName);

                onStatus?.Invoke($"Downloading {fileName} from npcap.com...");
                using HttpResponseMessage response = await http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                await using (FileStream fs = new(tempInstallerPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await response.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
                }

                onStatus?.Invoke("Launching Npcap installer (complete the setup wizard)...");
                using Process? proc = Process.Start(new ProcessStartInfo
                {
                    FileName = tempInstallerPath,
                    UseShellExecute = true
                });

                if (proc is null)
                {
                    return new NpcapInstallResult(false, "Failed to launch the Npcap installer process.");
                }

                await proc.WaitForExitAsync(ct).ConfigureAwait(false);
                return proc.ExitCode == 0
                    ? new NpcapInstallResult(true, "Npcap installer finished.")
                    : new NpcapInstallResult(false, $"Npcap installer exited with code {proc.ExitCode}.");
            }
        }
        catch (OperationCanceledException)
        {
            return new NpcapInstallResult(false, "Npcap installation was cancelled.");
        }
        catch
        {
            // Fall through to winget / browser fallback
        }
        finally
        {
            if (!string.IsNullOrEmpty(tempInstallerPath))
            {
                try
                {
                    if (File.Exists(tempInstallerPath))
                    {
                        File.Delete(tempInstallerPath);
                    }
                }
                catch
                {
                    // Ignore temp cleanup errors
                }
            }
        }

        // Fallback 1: winget
        try
        {
            onStatus?.Invoke("Attempting Npcap installation via winget...");
            using Process? winget = Process.Start(new ProcessStartInfo
            {
                FileName = "winget",
                Arguments = "install --id Insecure.Npcap -e --accept-package-agreements --accept-source-agreements",
                UseShellExecute = true
            });

            if (winget is not null)
            {
                await winget.WaitForExitAsync(ct).ConfigureAwait(false);
                if (winget.ExitCode == 0)
                {
                    return new NpcapInstallResult(true, "Npcap installed via winget.");
                }
            }
        }
        catch
        {
            // Fall through to browser fallback
        }

        // Fallback 2: open official download page
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = NpcapDownloadPageUrl,
                UseShellExecute = true
            });
            return new NpcapInstallResult(
                false,
                "Could not download Npcap automatically. Opened npcap.com in your browser.",
                OpenedBrowserFallback: true);
        }
        catch (Exception ex)
        {
            return new NpcapInstallResult(false, $"Failed to install Npcap: {ex.Message}");
        }
    }
}
