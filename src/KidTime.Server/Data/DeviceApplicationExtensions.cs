using KidTime.Domain.Applications;

namespace KidTime.Server.Data;

public static class DeviceApplicationExtensions
{
    /// <summary>
    /// The descriptor the catalog policy decides from. Needs <see cref="DeviceApplication.Application"/>
    /// loaded; every caller that filters, names or classifies an application reads it through here,
    /// so the panel, the snapshot and the reconciler cannot disagree about which fields count.
    /// </summary>
    public static ApplicationDescriptor ToDescriptor(this DeviceApplication item) => new()
    {
        DisplayName = item.Application.DisplayName,
        ExecutableName = item.Application.ExecutableName,
        ExecutablePath = item.ExecutablePath,
        ProductName = item.Application.ProductName,
        OriginalFilename = item.Application.OriginalFilename,
        Company = item.Application.Company,
        SignaturePublisher = item.Application.SignaturePublisher,
        FileVersion = item.FileVersion,
        PackageFamilyName = item.Application.PackageFamilyName,
        Sha256 = item.Sha256
    };
}
