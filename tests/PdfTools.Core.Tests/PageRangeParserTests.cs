namespace PdfTools.Core.Tests;

public class PageRangeParserTests
{
    [Fact]
    public void Parses_mixed_ranges_and_single_pages()
    {
        var ranges = PageRangeParser.Parse("1-3, 5, 7-10", 10);

        Assert.Equal(new[] { new PageRange(1, 3), new PageRange(5, 5), new PageRange(7, 10) }, ranges);
    }

    [Theory]
    [InlineData(" 1 - 3 ;4", 2)]
    [InlineData("1–3, 4", 2)]   // длинное тире
    [InlineData("1—3,4,", 2)]   // em dash и лишняя запятая в конце
    [InlineData(",,1-3,,4", 2)]
    public void Tolerates_spaces_dashes_and_extra_separators(string input, int expectedCount)
    {
        var ranges = PageRangeParser.Parse(input, 10);

        Assert.Equal(expectedCount, ranges.Count);
        Assert.Equal(new PageRange(1, 3), ranges[0]);
        Assert.Equal(new PageRange(4, 4), ranges[1]);
    }

    [Fact]
    public void Single_page_range_is_allowed()
    {
        Assert.Equal(new[] { new PageRange(3, 3) }, PageRangeParser.Parse("3-3", 5));
    }

    [Fact]
    public void Overlapping_ranges_are_allowed()
    {
        Assert.Equal(2, PageRangeParser.Parse("1-5, 3-7", 10).Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData(",,,")]
    [InlineData("abc")]
    [InlineData("1-a")]
    [InlineData("0")]
    [InlineData("0-3")]
    [InlineData("5-3")]
    [InlineData("1-")]
    [InlineData("-3")]
    [InlineData("1-3-5")]
    [InlineData("11")]
    [InlineData("8-11")]
    [InlineData("+2")]
    [InlineData("2.5")]
    [InlineData("99999999999")]
    public void Rejects_invalid_input(string? input)
    {
        var ex = Assert.Throws<PdfToolException>(() => PageRangeParser.Parse(input, 10));

        Assert.Equal(PdfErrorKind.InvalidInput, ex.Kind);
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }

    [Fact]
    public void Error_mentions_page_count_when_out_of_range()
    {
        var ex = Assert.Throws<PdfToolException>(() => PageRangeParser.Parse("1-20", 10));

        Assert.Contains("10", ex.Message);
    }

    [Fact]
    public void TryParse_returns_error_text_instead_of_throwing()
    {
        Assert.False(PageRangeParser.TryParse("5-3", 10, out var ranges, out var error));
        Assert.Empty(ranges);
        Assert.NotEmpty(error);

        Assert.True(PageRangeParser.TryParse("2", 10, out ranges, out error));
        Assert.Single(ranges);
        Assert.Equal("", error);
    }

    [Fact]
    public void ToString_formats_for_file_names()
    {
        Assert.Equal("1-3", new PageRange(1, 3).ToString());
        Assert.Equal("5", new PageRange(5, 5).ToString());
    }
}
