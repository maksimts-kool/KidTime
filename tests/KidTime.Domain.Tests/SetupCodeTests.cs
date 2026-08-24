using KidTime.Domain.Contracts;

namespace KidTime.Domain.Tests;

public sealed class SetupCodeTests
{
    private const string Token = "abcdefghijklmnopqrstuvwxyz0123456789ABCDEFG";
    private const string Pin = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void Create_and_parse_preserve_the_server_url_and_enrollment_code()
    {
        var enrollmentCode = EnrollmentCode.Create(Token, Pin);

        var setupCode = SetupCode.Create("https://parent-pc:5081/", enrollmentCode);

        Assert.True(SetupCode.TryParse(setupCode, out var serverUrl, out var parsedEnrollment));
        Assert.Equal("https://parent-pc:5081", serverUrl);
        Assert.Equal(enrollmentCode, parsedEnrollment);
        Assert.True(EnrollmentCode.TryParse(parsedEnrollment, out var parsedToken, out var parsedPin));
        Assert.Equal(Token, parsedToken);
        Assert.Equal(Pin.ToUpperInvariant(), parsedPin);
    }

    [Fact]
    public void Plain_enrollment_codes_are_not_setup_codes()
    {
        Assert.False(SetupCode.TryParse(EnrollmentCode.Create(Token, Pin), out _, out _));
        Assert.False(SetupCode.TryParse(Token, out _, out _));
    }

    [Fact]
    public void Insecure_server_urls_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => SetupCode.Create("http://parent-pc:5081", EnrollmentCode.Create(Token, Pin)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("KTS1.notbase64!!.KT1")]
    [InlineData("KTS1.aHR0cHM6Ly9wYXJlbnQtcGM6NTA4MQ.KT1.short.pin")]
    public void Invalid_setup_codes_are_rejected(string value)
    {
        Assert.False(SetupCode.TryParse(value, out _, out _));
    }
}
