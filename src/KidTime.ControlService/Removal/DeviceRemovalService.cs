using KidTime.Domain.Contracts;
using KidTime.Domain.Localization;

namespace KidTime.ControlService.Removal;

public interface IParentDeviceRemovalClient
{
    Task<bool> RemoveDeviceWithParentCredentialsAsync(
        string email,
        string password,
        CancellationToken cancellationToken);
}

public sealed class DeviceRemovalService(
    IParentDeviceRemovalClient apiClient,
    ISystemUninstaller uninstaller,
    ILogger<DeviceRemovalService> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _removalScheduled;

    /// <summary>
    /// Verifies the parent login and, on success, schedules removal. Every answer is phrased in
    /// <paramref name="text"/>, because the person reading it is standing at the child's PC.
    /// </summary>
    public async Task<DeviceRemovalResult> AuthorizeAndScheduleAsync(
        ParentRemovalRequest request,
        CancellationToken cancellationToken,
        AgentStrings? text = null)
    {
        text ??= AgentStrings.English;
        var email = request.Email.Trim();
        if (email.Length is 0 or > 320 || request.Password.Length is 0 or > 1024)
            return Rejected(text.RemovalEnterCredentials);
        if (!await _gate.WaitAsync(0, cancellationToken))
            return Rejected(text.RemovalAlreadyChecking);

        try
        {
            if (_removalScheduled)
                return new DeviceRemovalResult(true, text.RemovalAlreadyInProgress);

            var authorized = await apiClient.RemoveDeviceWithParentCredentialsAsync(
                email,
                request.Password,
                cancellationToken);
            if (!authorized)
                return Rejected(text.RemovalCredentialsIncorrect);

            await uninstaller.ScheduleAsync(cancellationToken);
            _removalScheduled = true;
            logger.LogInformation("Parent-authorized KidTime removal was scheduled.");
            return new DeviceRemovalResult(true, text.RemovalAccepted);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidDataException)
        {
            logger.LogWarning(exception, "Parent-authorized KidTime removal could not contact the server.");
            return Rejected(text.RemovalServerUnreachable);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            logger.LogError(exception, "Windows could not schedule parent-authorized KidTime removal.");
            return Rejected(text.RemovalWindowsFailed);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static DeviceRemovalResult Rejected(string message) => new(false, message);
}
