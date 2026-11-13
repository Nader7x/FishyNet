using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace FishyNet.Features.AppMonitor;

public sealed record AppFirewallResult(bool Success, string RulePrefix, string Message);

public interface IAppFirewallController
{
    bool IsAppBlocked(string exePath);
    IReadOnlyCollection<string> BlockedExePaths { get; }
    Task<AppFirewallResult> BlockAppAsync(string exePath, string? appDisplayName = null, CancellationToken ct = default);
    Task<AppFirewallResult> UnblockAppAsync(string exePath, CancellationToken ct = default);
    Task<int> ClearAllFishyNetRulesAsync(CancellationToken ct = default);
}

/// <summary>
/// Manages per-application Windows Firewall block rules via netsh advfirewall using
/// ArgumentList (injection-safe) and deterministic FishyNet_Block_&lt;id&gt; rule names.
/// </summary>
public sealed class AppFirewallService : IAppFirewallController
{
    public const string RulePrefix = "FishyNet_Block_";

    public delegate Task<(int ExitCode, string Output)> NetshCommandRunner(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken);

    private static readonly HashSet<string> ProtectedSystemBinaries = new(StringComparer.OrdinalIgnoreCase)
    {
        "system",
        "svchost.exe",
        "lsass.exe",
        "csrss.exe",
        "wininit.exe",
        "services.exe",
        "smss.exe",
        "winlogon.exe",
        "dwm.exe",
        "spoolsv.exe"
    };

    private readonly NetshCommandRunner _commandRunner;
    private readonly bool _requireExistingFile;
    private readonly ConcurrentDictionary<string, string> _blockedRulePrefixes = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> BlockedExePaths => (IReadOnlyCollection<string>)_blockedRulePrefixes.Keys;

    public AppFirewallService()
        : this(RunNetshProcessAsync, requireExistingFile: true)
    {
    }

    public AppFirewallService(NetshCommandRunner commandRunner, bool requireExistingFile = false)
    {
        _commandRunner = commandRunner ?? throw new ArgumentNullException(nameof(commandRunner));
        _requireExistingFile = requireExistingFile;
    }

    public bool IsAppBlocked(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return false;
        }

        return TryNormalizeExecutablePath(exePath, out string normalized, out _)
               && _blockedRulePrefixes.ContainsKey(normalized);
    }

    public async Task<AppFirewallResult> BlockAppAsync(
        string exePath,
        string? appDisplayName = null,
        CancellationToken ct = default)
    {
        if (!TryValidateExecutableForFirewall(exePath, out string normalizedPath, out string error))
        {
            return new AppFirewallResult(false, string.Empty, error);
        }

        string ruleBase = BuildRulePrefix(normalizedPath);
        string outRule = $"{ruleBase}_Out";
        string inRule = $"{ruleBase}_In";
        string label = string.IsNullOrWhiteSpace(appDisplayName)
            ? Path.GetFileName(normalizedPath)
            : appDisplayName.Trim();

        // Remove any stale rules with same name first for idempotency
        _ = await DeleteRuleAsync(outRule, ct).ConfigureAwait(false);
        _ = await DeleteRuleAsync(inRule, ct).ConfigureAwait(false);

        (int outExit, string outMsg) = await _commandRunner(
            [
                "advfirewall", "firewall", "add", "rule",
                $"name={outRule}",
                "dir=out",
                "action=block",
                $"program={normalizedPath}",
                "enable=yes",
                "profile=any",
                $"description=FishyNet App Monitor block for {SanitizeDescription(label)}"
            ],
            ct).ConfigureAwait(false);

        if (outExit != 0)
        {
            return new AppFirewallResult(false, ruleBase, $"Firewall outbound block failed (exit {outExit}): {outMsg}");
        }

        (int inExit, string inMsg) = await _commandRunner(
            [
                "advfirewall", "firewall", "add", "rule",
                $"name={inRule}",
                "dir=in",
                "action=block",
                $"program={normalizedPath}",
                "enable=yes",
                "profile=any",
                $"description=FishyNet App Monitor block for {SanitizeDescription(label)}"
            ],
            ct).ConfigureAwait(false);

        if (inExit != 0)
        {
            _ = await DeleteRuleAsync(outRule, ct).ConfigureAwait(false);
            return new AppFirewallResult(false, ruleBase, $"Firewall inbound block failed (exit {inExit}): {inMsg}");
        }

        _blockedRulePrefixes[normalizedPath] = ruleBase;
        return new AppFirewallResult(true, ruleBase, $"Blocked {Path.GetFileName(normalizedPath)} via Windows Firewall.");
    }

    public async Task<AppFirewallResult> UnblockAppAsync(string exePath, CancellationToken ct = default)
    {
        if (!TryNormalizeExecutablePath(exePath, out string normalizedPath, out string error))
        {
            return new AppFirewallResult(false, string.Empty, error);
        }

        string ruleBase = _blockedRulePrefixes.TryGetValue(normalizedPath, out string? existing)
            ? existing
            : BuildRulePrefix(normalizedPath);

        bool deletedOut = await DeleteRuleAsync($"{ruleBase}_Out", ct).ConfigureAwait(false);
        bool deletedIn = await DeleteRuleAsync($"{ruleBase}_In", ct).ConfigureAwait(false);

        _blockedRulePrefixes.TryRemove(normalizedPath, out _);

        if (!deletedOut && !deletedIn)
        {
            return new AppFirewallResult(false, ruleBase, "No matching FishyNet firewall rules were found to remove.");
        }

        return new AppFirewallResult(true, ruleBase, $"Unblocked {Path.GetFileName(normalizedPath)}.");
    }

    public async Task<int> ClearAllFishyNetRulesAsync(CancellationToken ct = default)
    {
        int removed = 0;
        foreach (KeyValuePair<string, string> kv in _blockedRulePrefixes.ToArray())
        {
            if (ct.IsCancellationRequested) break;
            if (await DeleteRuleAsync($"{kv.Value}_Out", ct).ConfigureAwait(false)) removed++;
            if (await DeleteRuleAsync($"{kv.Value}_In", ct).ConfigureAwait(false)) removed++;
            _blockedRulePrefixes.TryRemove(kv.Key, out _);
        }
        return removed;
    }

    public static string BuildRulePrefix(string normalizedExePath)
    {
        byte[] hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedExePath.ToLowerInvariant()));
        string shortHash = Convert.ToHexString(hashBytes.AsSpan(0, 6));

        string fileStem = Path.GetFileNameWithoutExtension(normalizedExePath);
        Span<char> safeStem = stackalloc char[24];
        int len = 0;
        for (int i = 0; i < fileStem.Length && len < safeStem.Length; i++)
        {
            char c = fileStem[i];
            if (char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_')
            {
                safeStem[len++] = c;
            }
            else if (c == ' ' || c == '.')
            {
                safeStem[len++] = '_';
            }
        }

        string stemPart = len > 0 ? new string(safeStem.Slice(0, len)) : "app";
        return $"{RulePrefix}{shortHash}_{stemPart}";
    }

    private bool TryValidateExecutableForFirewall(string exePath, out string normalizedPath, out string error)
    {
        if (!TryNormalizeExecutablePath(exePath, out normalizedPath, out error))
        {
            return false;
        }

        if (!normalizedPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            error = "Only .exe executable files can be targeted by Windows Firewall rules.";
            return false;
        }

        string fileName = Path.GetFileName(normalizedPath);
        if (ProtectedSystemBinaries.Contains(fileName))
        {
            error = $"Refusing to block critical Windows system binary '{fileName}'.";
            return false;
        }

        if (_requireExistingFile && !File.Exists(normalizedPath))
        {
            error = $"Executable file not found: {normalizedPath}";
            return false;
        }

        return true;
    }

    private static bool TryNormalizeExecutablePath(string exePath, out string normalizedPath, out string error)
    {
        normalizedPath = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(exePath))
        {
            error = "Executable path is required.";
            return false;
        }

        string trimmed = exePath.Trim();
        if (trimmed.IndexOfAny(['"', '\r', '\n', '\0', '|', '&', ';', '<', '>']) >= 0)
        {
            error = "Executable path contains disallowed characters.";
            return false;
        }

        if (!Path.IsPathRooted(trimmed))
        {
            error = "Executable path must be an absolute path.";
            return false;
        }

        try
        {
            normalizedPath = Path.GetFullPath(trimmed);
            return true;
        }
        catch (Exception ex)
        {
            error = $"Invalid executable path: {ex.Message}";
            return false;
        }
    }

    private static string SanitizeDescription(string text)
    {
        var sb = new StringBuilder(Math.Min(text.Length, 64));
        for (int i = 0; i < text.Length && sb.Length < 64; i++)
        {
            char c = text[i];
            if (char.IsAsciiLetterOrDigit(c) || c is ' ' or '.' or '-' or '_')
            {
                sb.Append(c);
            }
        }
        return sb.Length > 0 ? sb.ToString() : "Application";
    }

    private async Task<bool> DeleteRuleAsync(string ruleName, CancellationToken ct)
    {
        (int exitCode, _) = await _commandRunner(
            ["advfirewall", "firewall", "delete", "rule", $"name={ruleName}"],
            ct).ConfigureAwait(false);
        return exitCode == 0;
    }

    private static async Task<(int ExitCode, string Output)> RunNetshProcessAsync(
        IReadOnlyList<string> arguments,
        CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            return (-1, "Windows Firewall is only supported on Windows.");
        }

        var psi = new ProcessStartInfo
        {
            FileName = "netsh.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (string arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return (-1, "Failed to launch netsh.exe.");
            }

            Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            Task<string> stderrTask = proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);

            string stdout = await stdoutTask.ConfigureAwait(false);
            string stderr = await stderrTask.ConfigureAwait(false);
            string combined = string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : $"{stdout} {stderr}".Trim();
            return (proc.ExitCode, combined);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}
