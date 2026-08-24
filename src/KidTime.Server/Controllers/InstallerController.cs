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
    public IActionResult GetInformation()
    {
        var installer = updates.GetInstaller();
        return Ok(new
        {
            available = installer is not null,
            fileName = installer?.FileName,
            version = installer?.Version,
            sizeBytes = installer?.SizeBytes,
            sha256 = installer?.Sha256
        });
    }

    [HttpGet("download")]
    public IActionResult Download()
    {
        var installer = updates.GetInstaller();
        return installer is null
            ? NotFound(new { message = "The Windows setup file has not been published yet." })
            : PhysicalFile(
                installer.Path,
                "application/vnd.microsoft.portable-executable",
                installer.FileName,
                enableRangeProcessing: true);
    }
}
