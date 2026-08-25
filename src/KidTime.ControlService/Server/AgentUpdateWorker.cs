using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using KidTime.ControlService.Enforcement;
using KidTime.ControlService.Infrastructure;
using KidTime.Domain.Contracts;

namespace KidTime.ControlService.Server;

public sealed class AgentUpdateWorker(
    AgentApiClient api,
    AgentUpdateState state,
    EnforcementCoordinator coordinator,
    IHostApplicationLifetime lifetime,
    ILogger<AgentUpdateWorker> logger) : BackgroundService
{
    private const string ServiceName = "KidTimeControl";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        AnnounceCompletedUpdate();
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await CheckAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException
                                               or UnauthorizedAccessException or InvalidOperationException
                                               or System.ComponentModel.Win32Exception)
            {
                state.Set("Failed", exception.Message);
                logger.LogWarning(exception, "Automatic agent update check failed.");
            }

            await Task.Delay(
                TimeSpan.FromSeconds(Math.Clamp(api.Options.UpdateCheckIntervalSeconds, 60, 86_400)),
                stoppingToken);
        }
    }

    private async Task CheckAsync(CancellationToken cancellationToken)
    {
        state.Set("Checking");
        var manifest = await api.GetUpdateAsync(cancellationToken);
        if (manifest is null)
        {
            state.Set("NoRelease");
            return;
        }
        if (!IsNewer(manifest.Version, state.CurrentVersion))
        {
            state.Set("UpToDate");
            return;
        }

        logger.LogInformation("Agent update {Version} is available; installed version is {InstalledVersion}.",
            manifest.Version, state.CurrentVersion);
        state.Set("Downloading");
        var packagePath = Path.Combine(AgentPaths.UpdateDirectory, $"kidtime-agent-{manifest.Version}.zip");
        await api.DownloadUpdateAsync(manifest.Version, packagePath, cancellationToken);
        var file = new FileInfo(packagePath);
        if (file.Length != manifest.SizeBytes) throw new InvalidDataException("Downloaded update size does not match its manifest.");
        await using (var stream = File.OpenRead(packagePath))
        {
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
            if (!string.Equals(hash, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Downloaded update hash does not match its manifest.");
        }

        var stagingPath = Path.Combine(AgentPaths.UpdateDirectory, $"staged-{manifest.Version}");
        if (Directory.Exists(stagingPath)) Directory.Delete(stagingPath, recursive: true);
        Directory.CreateDirectory(stagingPath);
        ZipFile.ExtractToDirectory(packagePath, stagingPath, overwriteFiles: true);
        if (!File.Exists(Path.Combine(stagingPath, "KidTime.ControlService.exe"))
            || !File.Exists(Path.Combine(stagingPath, "SessionAgent", "KidTime.SessionAgent.exe")))
            throw new InvalidDataException("Downloaded update is missing required executables.");

        await File.WriteAllTextAsync(AgentPaths.UpdateScriptFile, UpdateScript, cancellationToken);
        state.Set("Installing");
        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(AgentPaths.UpdateScriptFile);
        startInfo.ArgumentList.Add("-ServiceName");
        startInfo.ArgumentList.Add(ServiceName);
        startInfo.ArgumentList.Add("-InstallPath");
        startInfo.ArgumentList.Add(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        startInfo.ArgumentList.Add("-StagePath");
        startInfo.ArgumentList.Add(stagingPath);
        startInfo.ArgumentList.Add("-ParentProcessId");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
        startInfo.ArgumentList.Add("-StatusFile");
        startInfo.ArgumentList.Add(AgentPaths.UpdateStatusFile);
        startInfo.ArgumentList.Add("-Version");
        startInfo.ArgumentList.Add(manifest.Version);
        _ = Process.Start(startInfo) ?? throw new InvalidOperationException("Automatic updater process could not be started.");
        logger.LogInformation("Automatic agent update {Version} staged; service restart requested.", manifest.Version);
        await Task.Delay(500, cancellationToken);
        lifetime.StopApplication();
    }

    /// <summary>
    /// The updater script writes its outcome and restarts the service, so the first run after an
    /// update is where the result is known. A success becomes one ordinary notification for the
    /// child; a failure becomes an error the parent sees in the panel.
    /// </summary>
    private void AnnounceCompletedUpdate()
    {
        try
        {
            if (!File.Exists(AgentPaths.UpdateStatusFile)) return;
            var outcome = JsonSerializer.Deserialize<UpdateOutcome>(
                File.ReadAllText(AgentPaths.UpdateStatusFile), JsonOptions);
            File.Delete(AgentPaths.UpdateStatusFile);
            if (outcome is null) return;
            if (!string.Equals(outcome.Status, "Installed", StringComparison.OrdinalIgnoreCase))
            {
                state.Set("Failed", outcome.Error);
                logger.LogError("Automatic update to {Version} failed and was rolled back: {Error}",
                    outcome.Version ?? "unknown", outcome.Error ?? "no detail reported");
                return;
            }

            logger.LogInformation("Automatic update to {Version} completed.", state.CurrentVersion);
            coordinator.NotifyServicesUpdated(state.CurrentVersion);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning(exception, "The recorded update outcome could not be read.");
        }
    }

    private sealed record UpdateOutcome(string? Status, string? Version, string? Error);

    public static bool IsNewer(string availableVersion, string installedVersion) =>
        Version.TryParse(availableVersion, out var available)
        && Version.TryParse(installedVersion, out var installed)
        && available > installed;

    private const string UpdateScript = """
        param(
            [Parameter(Mandatory)][string]$ServiceName,
            [Parameter(Mandatory)][string]$InstallPath,
            [Parameter(Mandatory)][string]$StagePath,
            [Parameter(Mandatory)][int]$ParentProcessId,
            [Parameter(Mandatory)][string]$StatusFile,
            [Parameter(Mandatory)][string]$Version
        )
        $ErrorActionPreference = 'Stop'
        $backupPath = Join-Path (Split-Path -Parent $StatusFile) 'backup'
        function Write-UpdateStatus([string]$Status, [string]$ErrorMessage) {
            [pscustomobject]@{ status=$Status; version=$Version; error=$ErrorMessage; updatedAtUtc=[DateTimeOffset]::UtcNow } |
                ConvertTo-Json -Compress | Set-Content -LiteralPath $StatusFile -Encoding utf8
        }
        try {
            Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
            Wait-Process -Id $ParentProcessId -Timeout 60 -ErrorAction SilentlyContinue
            $agents = @(Get-Process -Name 'KidTime.SessionAgent' -ErrorAction SilentlyContinue)
            $agents | Stop-Process -Force
            if ($agents.Count -gt 0) { $agents | Wait-Process -Timeout 15 -ErrorAction SilentlyContinue }
            if (Test-Path -LiteralPath $backupPath) { Remove-Item -LiteralPath $backupPath -Recurse -Force }
            New-Item -ItemType Directory -Path $backupPath -Force | Out-Null
            Copy-Item -Path (Join-Path $InstallPath '*') -Destination $backupPath -Recurse -Force
            $copied = $false
            for ($attempt = 1; $attempt -le 5 -and -not $copied; $attempt++) {
                try {
                    Copy-Item -Path (Join-Path $StagePath '*') -Destination $InstallPath -Recurse -Force
                    $copied = $true
                }
                catch [System.IO.IOException] {
                    if ($attempt -eq 5) { throw }
                    Start-Sleep -Milliseconds 750
                }
            }
            Write-UpdateStatus 'Installed' $null
            Start-Service -Name $ServiceName
        }
        catch {
            $failure = $_.Exception.Message
            try {
                if (Test-Path -LiteralPath $backupPath) {
                    Copy-Item -Path (Join-Path $backupPath '*') -Destination $InstallPath -Recurse -Force
                }
                Start-Service -Name $ServiceName -ErrorAction SilentlyContinue
            } catch {}
            Write-UpdateStatus 'Failed' $failure
            exit 1
        }
        """;
}
