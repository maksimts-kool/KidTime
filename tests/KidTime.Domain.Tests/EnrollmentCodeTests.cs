using KidTime.Domain.Contracts;

namespace KidTime.Domain.Tests;

public sealed class EnrollmentCodeTests
{
    private const string Token = "abcdefghijklmnopqrstuvwxyz0123456789ABCDEFG";
    private const string Pin = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void Create_and_parse_preserve_token_and_normalize_certificate_pin()
    {
        var code = EnrollmentCode.Create(Token, Pin);

        Assert.True(EnrollmentCode.TryParse(code, out var parsedToken, out var parsedPin));
        Assert.Equal(Token, parsedToken);
        Assert.Equal(Pin.ToUpperInvariant(), parsedPin);
    }

    [Fact]
    public void Legacy_raw_tokens_remain_valid()
    {
        Assert.True(EnrollmentCode.TryParse(Token, out var parsedToken, out var parsedPin));
        Assert.Equal(Token, parsedToken);
        Assert.Null(parsedPin);
    }

    [Theory]
    [InlineData("")]
    [InlineData("KT1.too-short.not-a-pin")]
    [InlineData("KT1.abcdefghijklmnopqrstuvwxyz0123456789.INVALID")]
    public void Invalid_codes_are_rejected(string value)
    {
        Assert.False(EnrollmentCode.TryParse(value, out _, out _));
    }
}
