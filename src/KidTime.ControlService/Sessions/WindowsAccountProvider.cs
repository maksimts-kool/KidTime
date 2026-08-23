using System.Diagnostics;
using System.Text.Json;
using KidTime.Domain.Contracts;

namespace KidTime.ControlService.Sessions;

public sealed class WindowsAccountProvider(ILogger<WindowsAccountProvider> logger)
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(1);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<WindowsUserAccount> _cached = [];
    private DateTimeOffset _cachedAtUtc = DateTimeOffset.MinValue;

    public async Task<IReadOnlyList<WindowsUserAccount>> GetAccountsAsync(CancellationToken cancellationToken)
    {
        if (DateTimeOffset.UtcNow - _cachedAtUtc < CacheDuration) return _cached;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (DateTimeOffset.UtcNow - _cachedAtUtc < CacheDuration) return _cached;
            _cached = await ReadAccountsAsync(cancellationToken);
            _cachedAtUtc = DateTimeOffset.UtcNow;
            return _cached;
        }
        finally { _gate.Release(); }
    }

    private async Task<IReadOnlyList<WindowsUserAccount>> ReadAccountsAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) return [];
        const string command = "$admins=@(Get-LocalGroup -SID 'S-1-5-32-544' | Get-LocalGroupMember -ErrorAction SilentlyContinue | ForEach-Object {$_.SID.Value}); Get-LocalUser | Select-Object @{n='Sid';e={$_.SID.Value}},@{n='AccountName';e={$env:COMPUTERNAME+'\\'+$_.Name}},@{n='DisplayName';e={if ([string]::IsNullOrWhiteSpace($_.FullName)) {$_.Name} else {$_.FullName}}},@{n='IsEnabled';e={$_.Enabled}},@{n='IsAdministrator';e={$admins -contains $_.SID.Value}} | ConvertTo-Json -Compress";
        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(command);
        try
        {
            using var process = Process.Start(startInfo);
            if (process is null) return [];
            var json = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(json))
            {
                logger.LogWarning("Windows account discovery failed: {Message}", error.Trim());
                return [];
            }

            using var document = JsonDocument.Parse(json);
            var entries = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().ToArray()
                : [document.RootElement];
            return entries.Select(entry => new WindowsUserAccount(
                    entry.GetProperty("Sid").GetString() ?? string.Empty,
                    entry.GetProperty("AccountName").GetString() ?? string.Empty,
                    entry.GetProperty("DisplayName").GetString() ?? string.Empty,
                    entry.GetProperty("IsEnabled").GetBoolean(),
                    entry.GetProperty("IsAdministrator").GetBoolean()))
                .Where(account => account.Sid.Length > 0 && account.AccountName.Length > 0)
                .OrderBy(account => account.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or JsonException)
        {
            logger.LogWarning(exception, "Windows account discovery failed.");
            return [];
        }
    }
}
