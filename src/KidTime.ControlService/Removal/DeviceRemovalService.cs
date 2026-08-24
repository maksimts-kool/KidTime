using KidTime.Domain.Contracts;

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

    public async Task<DeviceRemovalResult> AuthorizeAndScheduleAsync(
        ParentRemovalRequest request,
        CancellationToken cancellationToken)
    {
        var email = request.Email.Trim();
        if (email.Length is 0 or > 320 || request.Password.Length is 0 or > 1024)
            return Rejected("Enter the parent email address and password.");
        if (!await _gate.WaitAsync(0, cancellationToken))
            return Rejected("KidTime is already checking a removal request.");

        try
        {
            if (_removalScheduled)
                return new DeviceRemovalResult(true, "KidTime removal is already in progress.");

            var authorized = await apiClient.RemoveDeviceWithParentCredentialsAsync(
                email,
                request.Password,
                cancellationToken);
            if (!authorized)
                return Rejected("The parent email address or password is incorrect.");

            await uninstaller.ScheduleAsync(cancellationToken);
            _removalScheduled = true;
            logger.LogInformation("Parent-authorized KidTime removal was scheduled.");
            return new DeviceRemovalResult(
                true,
                "Parent account verified. KidTime is being removed from this PC.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidDataException)
        {
            logger.LogWarning(exception, "Parent-authorized KidTime removal could not contact the server.");
            return Rejected("KidTime could not verify the parent login with the server. Check the connection and try again.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            logger.LogError(exception, "Windows could not schedule parent-authorized KidTime removal.");
            return Rejected("The parent login was accepted, but Windows could not start KidTime removal. Try again.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private static DeviceRemovalResult Rejected(string message) => new(false, message);
}
