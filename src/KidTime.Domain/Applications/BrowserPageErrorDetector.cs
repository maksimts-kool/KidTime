using KidTime.Domain.Contracts;

namespace KidTime.Domain.Applications;

/// <summary>
/// What a browser's own error page says went wrong, read from nothing but the shape of its window
/// title.
///
/// A child whose home network refuses a site is shown "Server Not Found" and told nothing else, so
/// the household's filtering reads to them as the computer being broken. KidTime can explain the
/// rule - it holds the DNS server's configuration already - but only once it knows a page failed
/// to load at all.
///
/// The one thing it deliberately does not learn is <em>which</em> site. That is the address the
/// child typed, it is browsing history by any other name, and it sits on the far side of the
/// privacy boundary the rest of KidTime keeps. Firefox makes that easy to honour: its error page
/// title is a fixed translated sentence and never contains the host, so matching the sentence
/// answers "a page failed" without answering "which page". Chromium puts the host in the title
/// instead, which is why no Chromium browser is listed below - detecting it there would mean
/// reading the address, and that is not a trade this feature is worth.
///
/// Nothing here travels: <see cref="Detect"/> runs in the child's own session and only its answer,
/// one of three values, crosses the pipe to the service.
/// </summary>
public static class BrowserPageErrorDetector
{
    /// <summary>
    /// Gecko browsers, whose error pages are named rather than addressed. Matched on the
    /// executable name because that is the one part of a descriptor a rename cannot quietly
    /// change, and it is what the catalog identifies an application by anyway.
    /// </summary>
    private static readonly HashSet<string> SupportedBrowsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "firefox", "librewolf", "waterfox", "floorp", "zen", "palemoon", "basilisk", "mercury"
    };

    /// <summary>
    /// The error page titles, in the languages a KidTime household reads. They come from Firefox's
    /// own <c>netError.ftl</c>: <c>neterror-dns-not-found-title</c> is the name that did not
    /// resolve, and <c>neterror-page-title</c> is the connection that did not open. A browser
    /// showing its interface in a third language simply produces no match, which costs the child
    /// an explanation and nothing else - so the list can grow without anything else changing.
    /// </summary>
    private static readonly (string Title, BrowserPageError Error)[] ErrorTitles =
    [
        ("Server Not Found", BrowserPageError.NameNotResolved),
        ("Сервер не найден", BrowserPageError.NameNotResolved),
        ("Problem loading page", BrowserPageError.ConnectionFailed),
        ("Проблема при загрузке страницы", BrowserPageError.ConnectionFailed),
        ("Unable to connect", BrowserPageError.ConnectionFailed),
        ("Не удалось установить соединение", BrowserPageError.ConnectionFailed)
    ];

    public static BrowserPageError Detect(ApplicationDescriptor? foreground, string? windowTitle)
    {
        if (foreground is null || string.IsNullOrWhiteSpace(windowTitle)) return BrowserPageError.None;
        var executable = foreground.ExecutableName;
        if (string.IsNullOrWhiteSpace(executable)) return BrowserPageError.None;
        var name = Path.GetFileNameWithoutExtension(executable);
        if (!SupportedBrowsers.Contains(name)) return BrowserPageError.None;

        // A window title is "<document title> - Mozilla Firefox", so the error page's own sentence
        // is at the front. Matching the start rather than the whole avoids having to know every
        // suffix a Firefox build and a private window can add.
        var title = windowTitle.TrimStart();
        foreach (var (candidate, error) in ErrorTitles)
            if (title.StartsWith(candidate, StringComparison.OrdinalIgnoreCase)) return error;
        return BrowserPageError.None;
    }
}
