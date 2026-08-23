using System.Security.Claims;
using KidTime.Server.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace KidTime.Server.Hubs;

[Authorize(AuthenticationSchemes = DeviceAuthenticationDefaults.Scheme)]
public sealed class DeviceHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        var deviceId = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (deviceId is not null)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(Guid.Parse(deviceId)));
        }

        await base.OnConnectedAsync();
    }

    public static string GroupName(Guid deviceId) => $"device:{deviceId:N}";
}
