using System.Text;
using WordCount;
using Xunit;

namespace WordCount.Tests;

public class CountsTests
{
    [Fact]
    public void Counts_lines_words_and_bytes() =>
        Assert.Equal(new Counts(2, 4, 23), Counts.Of(Encoding.UTF8.GetBytes("  four\tfive  six\n\nseven")));

    [Fact]
    public void Formats_only_the_chosen_counts_in_order() =>
        Assert.Equal("2 23", new Counts(2, 4, 23).Format(lines: true, words: false, bytes: true));
}
