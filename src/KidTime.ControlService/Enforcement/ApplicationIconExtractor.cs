using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Xml.Linq;
using KidTime.Domain.Applications;

namespace KidTime.ControlService.Enforcement;

public sealed class ApplicationIconExtractor(ILogger<ApplicationIconExtractor> logger)
{
    private const int IconSize = 64;
    private const int IconContentSize = 50;
    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);

    public ApplicationDescriptor AddIcon(ApplicationDescriptor descriptor)
    {
        if (!string.IsNullOrWhiteSpace(descriptor.IconPngBase64))
        {
            return descriptor;
        }

        var source = descriptor.ExecutablePath.Trim();
        if (source.Length == 0)
        {
            return descriptor;
        }

        var encoded = _cache.GetOrAdd(source, path => Extract(path) ?? string.Empty);
        return encoded.Length == 0 ? descriptor : CopyWithIcon(descriptor, encoded);
    }

    private string? Extract(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                using var icon = Icon.ExtractAssociatedIcon(path);
                if (icon is null) return null;
                using var source = icon.ToBitmap();
                return EncodePng(source);
            }

            if (!Directory.Exists(path)) return null;
            var packageIcon = FindPackageIcon(path);
            if (packageIcon is null) return null;
            using var sourceImage = Image.FromFile(packageIcon);
            return EncodePng(sourceImage);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException
                                           or System.Runtime.InteropServices.ExternalException)
        {
            logger.LogDebug(exception, "Could not extract an application icon from {Path}.", path);
            return null;
        }
    }

    private static string? FindPackageIcon(string installDirectory)
    {
        var manifestPath = Path.Combine(installDirectory, "AppxManifest.xml");
        if (!File.Exists(manifestPath)) return null;
        var document = XDocument.Load(manifestPath, LoadOptions.None);
        var visualElements = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "VisualElements");
        var relative = visualElements?.Attributes().FirstOrDefault(attribute =>
            attribute.Name.LocalName is "Square44x44Logo" or "Square150x150Logo")?.Value;
        if (string.IsNullOrWhiteSpace(relative) || relative.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
            return null;

        var requested = Path.Combine(installDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(requested)) return requested;
        var directory = Path.GetDirectoryName(requested);
        if (directory is null || !Directory.Exists(directory)) return null;
        var stem = Path.GetFileNameWithoutExtension(requested);
        return Directory.GetFiles(directory, $"{stem}*.png", SearchOption.TopDirectoryOnly)
            .OrderByDescending(file => new FileInfo(file).Length)
            .FirstOrDefault();
    }

    private static string EncodePng(Image source)
    {
        using var pixels = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using (var sourceGraphics = Graphics.FromImage(pixels)) sourceGraphics.DrawImageUnscaled(source, 0, 0);
        var visible = FindVisibleBounds(pixels);
        var scale = Math.Min((double)IconContentSize / visible.Width, (double)IconContentSize / visible.Height);
        var width = Math.Max(1, (int)Math.Round(visible.Width * scale));
        var height = Math.Max(1, (int)Math.Round(visible.Height * scale));
        var x = (IconSize - width) / 2;
        var y = (IconSize - height) / 2;
        using var target = new Bitmap(IconSize, IconSize, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Transparent);
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.SmoothingMode = SmoothingMode.HighQuality;
            graphics.DrawImage(pixels, new Rectangle(x, y, width, height), visible, GraphicsUnit.Pixel);
        }

        using var stream = new MemoryStream();
        target.Save(stream, ImageFormat.Png);
        return Convert.ToBase64String(stream.ToArray());
    }

    private static Rectangle FindVisibleBounds(Bitmap image)
    {
        var left = image.Width;
        var top = image.Height;
        var right = -1;
        var bottom = -1;
        for (var y = 0; y < image.Height; y++)
        for (var x = 0; x < image.Width; x++)
        {
            if (image.GetPixel(x, y).A <= 8) continue;
            left = Math.Min(left, x);
            top = Math.Min(top, y);
            right = Math.Max(right, x);
            bottom = Math.Max(bottom, y);
        }

        return right < left || bottom < top
            ? new Rectangle(0, 0, image.Width, image.Height)
            : Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
    }

    private static ApplicationDescriptor CopyWithIcon(ApplicationDescriptor descriptor, string iconPngBase64) => new()
    {
        DisplayName = descriptor.DisplayName,
        ExecutableName = descriptor.ExecutableName,
        ExecutablePath = descriptor.ExecutablePath,
        ProductName = descriptor.ProductName,
        OriginalFilename = descriptor.OriginalFilename,
        Company = descriptor.Company,
        SignaturePublisher = descriptor.SignaturePublisher,
        FileVersion = descriptor.FileVersion,
        PackageFamilyName = descriptor.PackageFamilyName,
        Sha256 = descriptor.Sha256,
        IconPngBase64 = iconPngBase64
    };
}
