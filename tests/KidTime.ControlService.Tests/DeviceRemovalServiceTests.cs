using KidTime.ControlService.Removal;
using KidTime.Domain.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace KidTime.ControlService.Tests;

public sealed class DeviceRemovalServiceTests
{
    [Fact]
    public async Task Valid_parent_credentials_schedule_removal_once()
    {
        var client = new FakeRemovalClient { Authorized = true };
        var uninstaller = new FakeUninstaller();
        var service = CreateService(client, uninstaller);

        var first = await service.AuthorizeAndScheduleAsync(
            new ParentRemovalRequest("parent@example.com", "correct password"),
            CancellationToken.None);
        var second = await service.AuthorizeAndScheduleAsync(
            new ParentRemovalRequest("parent@example.com", "correct password"),
            CancellationToken.None);

        Assert.True(first.Accepted);
        Assert.True(second.Accepted);
        Assert.Equal(1, client.Calls);
        Assert.Equal(1, uninstaller.Calls);
    }

    [Fact]
    public async Task Invalid_parent_credentials_do_not_schedule_removal()
    {
        var client = new FakeRemovalClient { Authorized = false };
        var uninstaller = new FakeUninstaller();
        var service = CreateService(client, uninstaller);

        var result = await service.AuthorizeAndScheduleAsync(
            new ParentRemovalRequest("parent@example.com", "wrong password"),
            CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Contains("incorrect", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, client.Calls);
        Assert.Equal(0, uninstaller.Calls);
    }

    [Theory]
    [InlineData("", "password")]
    [InlineData("parent@example.com", "")]
    public async Task Missing_credentials_are_rejected_before_server_contact(string email, string password)
    {
        var client = new FakeRemovalClient { Authorized = true };
        var uninstaller = new FakeUninstaller();
        var service = CreateService(client, uninstaller);

        var result = await service.AuthorizeAndScheduleAsync(
            new ParentRemovalRequest(email, password),
            CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Equal(0, client.Calls);
        Assert.Equal(0, uninstaller.Calls);
    }

    [Fact]
    public async Task Server_failure_does_not_schedule_removal()
    {
        var client = new FakeRemovalClient { Exception = new HttpRequestException("offline") };
        var uninstaller = new FakeUninstaller();
        var service = CreateService(client, uninstaller);

        var result = await service.AuthorizeAndScheduleAsync(
            new ParentRemovalRequest("parent@example.com", "password"),
            CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Contains("could not verify", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, uninstaller.Calls);
    }

    private static DeviceRemovalService CreateService(
        IParentDeviceRemovalClient client,
        ISystemUninstaller uninstaller) =>
        new(client, uninstaller, NullLogger<DeviceRemovalService>.Instance);

    private sealed class FakeRemovalClient : IParentDeviceRemovalClient
    {
        public bool Authorized { get; init; }
        public Exception? Exception { get; init; }
        public int Calls { get; private set; }

        public Task<bool> RemoveDeviceWithParentCredentialsAsync(
            string email,
            string password,
            CancellationToken cancellationToken)
        {
            Calls++;
            if (Exception is not null) throw Exception;
            return Task.FromResult(Authorized);
        }
    }

    private sealed class FakeUninstaller : ISystemUninstaller
    {
        public int Calls { get; private set; }

        public Task ScheduleAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }
}
