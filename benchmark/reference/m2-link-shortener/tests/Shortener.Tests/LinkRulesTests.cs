using Shortener;
using Xunit;

namespace Shortener.Tests;

public class LinkRulesTests
{
    [Theory]
    [InlineData("https://example.com/a?b=c", true)]
    [InlineData("ftp://example.com", false)]
    [InlineData("/relative", false)]
    [InlineData("javascript:alert(1)", false)]
    public void Only_absolute_http_urls(string url, bool valid) => Assert.Equal(valid, LinkRules.IsUrl(url));

    [Fact]
    public void A_generated_code_is_seven_letters_and_digits() =>
        Assert.Matches("^[A-Za-z0-9]{7}$", LinkRules.NewCode());
}
