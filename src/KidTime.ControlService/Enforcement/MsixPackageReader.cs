using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;

namespace KidTime.ControlService.Enforcement;

/// <summary>
/// Reads what a Windows package actually is out of its own manifest.
///
/// <c>Get-AppxPackage</c> answers with the package identity - "Microsoft.BingNews",
/// "38833FF26BA1D.UnigramPreview" - and a parent's applications page filled up with strings like
/// those beside a shelf of shell components nobody installed. The manifest carries both missing
/// facts: the display name Windows itself shows, and whether the package has a Start entry at all.
///
/// The second is the important one. A packaged component is a packaged application by every
/// mechanical test, so no name filter separates the widget feed or the handwriting dictionary from
/// Photos. <c>AppListEntry="none"</c> does: it is Windows' own statement that this package is not
/// something a person opens, and every in-box component sets it.
/// </summary>
public static class MsixPackageReader
{
    private static readonly XNamespace Appx = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";

    public sealed record PackageManifest(string? DisplayName, bool HasApplicationEntry);

    /// <summary>
    /// Returns what the package says about itself, or <c>null</c> when the manifest cannot be read
    /// - an encrypted or partially staged package, or one whose install location has gone. The
    /// caller keeps such a package on its identity name rather than losing it.
    /// </summary>
    public static PackageManifest? Read(string packageName, string packageFullName, string installLocation)
    {
        if (string.IsNullOrWhiteSpace(installLocation)) return null;
        XDocument document;
        try
        {
            document = XDocument.Load(Path.Combine(installLocation, "AppxManifest.xml"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or System.Xml.XmlException)
        {
            return null;
        }

        var application = document.Root?.Element(Appx + "Applications")?.Elements(Appx + "Application").FirstOrDefault();
        // VisualElements is namespaced under one of several uap revisions, and the revision changes
        // with the manifest's target. Matching on the local name keeps this working across all of
        // them rather than pinning a schema version that a Windows update moves.
        var visualElements = application?.Elements().FirstOrDefault(element => element.Name.LocalName == "VisualElements");
        var appListEntry = visualElements?.Attribute("AppListEntry")?.Value;
        var hasApplicationEntry = application is not null
                                  && !string.Equals(appListEntry, "none", StringComparison.OrdinalIgnoreCase);

        var displayName = visualElements?.Attribute("DisplayName")?.Value;
        if (string.IsNullOrWhiteSpace(displayName))
            displayName = document.Root?.Element(Appx + "Properties")?.Element(Appx + "DisplayName")?.Value;
        return new PackageManifest(
            ResolveIndirectString(displayName, packageName, packageFullName, installLocation),
            hasApplicationEntry);
    }

    /// <summary>
    /// A manifest usually names the application indirectly, as <c>ms-resource:AppName</c>, and the
    /// string itself lives in the package's compiled resources. Windows resolves those through
    /// SHLoadIndirectString, which needs the reference wrapped in the package it belongs to.
    ///
    /// Two wrappings are tried because neither covers everything. The package's own resources.pri
    /// on disk works from LocalSystem, which has none of these packages registered to it. The
    /// short <c>ms-resource:Key</c> form resolves only for packages that keep their strings in the
    /// default map, so the expanded <c>ms-resource://Name/Resources/Key</c> form follows it -
    /// that is the one Calculator, Terminal and Photos need. Failing everything, the caller falls
    /// back to the identity name, which is what it had before.
    /// </summary>
    private static string? ResolveIndirectString(string? value, string packageName, string packageFullName, string installLocation)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var reference = value.Trim();
        if (!reference.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)) return reference;

        var key = reference["ms-resource:".Length..].TrimStart('/');
        var expanded = $"ms-resource://{packageName}/Resources/{key}";
        var resourcePri = Path.Combine(installLocation, "resources.pri");
        string?[] candidates =
        [
            Load($"@{{{resourcePri}?{reference}}}"),
            Load($"@{{{resourcePri}?{expanded}}}"),
            Load($"@{{{packageFullName}?{reference}}}"),
            Load($"@{{{packageFullName}?{expanded}}}")
        ];
        return candidates.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate));
    }

    private static string? Load(string reference)
    {
        var buffer = new StringBuilder(512);
        return SHLoadIndirectString(reference, buffer, buffer.Capacity, IntPtr.Zero) == 0
            ? buffer.ToString()
            : null;
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHLoadIndirectString(string source, StringBuilder output, int outputLength, IntPtr reserved);
}
