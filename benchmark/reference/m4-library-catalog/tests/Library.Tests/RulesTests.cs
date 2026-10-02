using Library;
using Xunit;

namespace Library.Tests;

public class RulesTests
{
    [Theory]
    [InlineData("9780306406157", true)]
    [InlineData("9780306406158", false)]
    [InlineData("978030640615", false)]
    public void An_isbn_13_needs_its_check_digit(string isbn, bool valid) => Assert.Equal(valid, Rules.IsIsbn13(isbn));

    [Fact]
    public void An_email_has_a_domain() => Assert.False(Rules.IsEmail("not-an-email"));
}
