using KidTime.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace KidTime.Server.Controllers;

[ApiController]
[Authorize]
[Route("api/installer")]
public sealed class InstallerController(AgentUpdateCatalog updates) : ControllerBase
{
    [HttpGet]
    public IActionResult Download()
    {
        var path = updates.GetInstallerPath();
        return path is null
            ? NotFound(new { message = "The Windows setup file has not been published yet." })
            : PhysicalFile(path, "application/vnd.microsoft.portable-executable", "KidTimeSetup.exe", enableRangeProcessing: true);
    }
}
