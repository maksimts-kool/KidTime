using KidTime.Domain.Applications;
using KidTime.Domain.Contracts;

namespace KidTime.Domain.Tests;

/// <summary>
/// The detector answers "a page failed to open" and must never answer "which page". These tests
/// hold both halves: the error pages a household's DNS filter produces are recognized, and a
/// browser that puts the address in its title is not read at all.
/// </summary>
public class BrowserPageErrorDetectorTests
{
    [Theory]
    [InlineData("Server Not Found — Mozilla Firefox", BrowserPageError.NameNotResolved)]
    [InlineData("Сервер не найден — Mozilla Firefox", BrowserPageError.NameNotResolved)]
    [InlineData("Problem loading page — Mozilla Firefox", BrowserPageError.ConnectionFailed)]
    [InlineData("Проблема при загрузке страницы — Mozilla Firefox", BrowserPageError.ConnectionFailed)]
    public void FirefoxErrorPagesAreRecognized(string title, BrowserPageError expected) =>
        Assert.Equal(expected, BrowserPageErrorDetector.Detect(Firefox(), title));

    /// <summary>
    /// What a DNS server that answers blocked names with an address of its own actually produces,
    /// which is the commonest way a household runs one. The block page cannot present a
    /// certificate for the site that was asked for, so the child is stopped by a security warning
    /// and never sees "Server Not Found" at all - the case this feature originally missed.
    ///
    /// Both generations of Firefox's error page are here because the newer one is behind a pref,
    /// so which wording a child sees is not something KidTime gets to decide.
    /// </summary>
    [Theory]
    [InlineData("Warning: Security Risk — Mozilla Firefox")]
    [InlineData("Предупреждение: Риск безопасности — Mozilla Firefox")]
    [InlineData("Warning: Potential Security Risk Ahead — Mozilla Firefox")]
    [InlineData("Предупреждение: Вероятная угроза безопасности — Mozilla Firefox")]
    [InlineData("Did Not Connect: Potential Security Issue — Mozilla Firefox")]
    [InlineData("Соединение не установлено: Вероятная угроза безопасности — Mozilla Firefox")]
    public void ABlockPageWithTheWrongCertificateIsRecognized(string title) => Assert.Equal(
        BrowserPageError.SecureConnectionFailed,
        BrowserPageErrorDetector.Detect(Firefox(), title));

    /// <summary>
    /// The headings Firefox draws in the body of an error page, which never reach a window title.
    /// Listing one as if it were a page title would look right and match nothing, so the table is
    /// checked against the mistake rather than only against the titles that do work.
    /// </summary>
    [Theory]
    [InlineData("Unable to connect — Mozilla Firefox")]
    [InlineData("Secure Connection Failed — Mozilla Firefox")]
    public void ABodyHeadingIsNotAPageTitle(string title) =>
        Assert.Equal(BrowserPageError.None, BrowserPageErrorDetector.Detect(Firefox(), title));

    /// <summary>A private window adds a suffix, and the sentence the match needs is still in front.</summary>
    [Fact]
    public void APrivateWindowIsStillRecognized() => Assert.Equal(
        BrowserPageError.NameNotResolved,
        BrowserPageErrorDetector.Detect(Firefox(), "Server Not Found — Mozilla Firefox Private Browsing"));

    [Theory]
    [InlineData("Roblox — Mozilla Firefox")]
    [InlineData("YouTube — Mozilla Firefox")]
    [InlineData("")]
    [InlineData(null)]
    public void AnOrdinaryPageIsNotAnError(string? title) =>
        Assert.Equal(BrowserPageError.None, BrowserPageErrorDetector.Detect(Firefox(), title));

    /// <summary>
    /// Chromium's error page is titled with the host that failed, so recognizing it would mean
    /// reading the address the child typed. That is browsing history, it is outside the privacy
    /// boundary, and the answer is to detect nothing rather than to look.
    /// </summary>
    [Theory]
    [InlineData("chrome")]
    [InlineData("msedge")]
    [InlineData("brave")]
    public void ChromiumBrowsersAreNeverRead(string executable) => Assert.Equal(
        BrowserPageError.None,
        BrowserPageErrorDetector.Detect(Browser(executable), "example.com"));

    /// <summary>
    /// A window that happens to be titled like an error page is not one unless a browser drew it.
    /// </summary>
    [Fact]
    public void ANonBrowserIsNeverRead() => Assert.Equal(
        BrowserPageError.None,
        BrowserPageErrorDetector.Detect(Browser("notepad"), "Server Not Found"));

    [Fact]
    public void AMissingDescriptorIsNotAnError() =>
        Assert.Equal(BrowserPageError.None, BrowserPageErrorDetector.Detect(null, "Server Not Found"));

    private static ApplicationDescriptor Firefox() => Browser("firefox");

    private static ApplicationDescriptor Browser(string executable) => new()
    {
        DisplayName = executable,
        ExecutableName = $"{executable}.exe",
        ExecutablePath = $@"C:\Program Files\{executable}\{executable}.exe"
    };
}
