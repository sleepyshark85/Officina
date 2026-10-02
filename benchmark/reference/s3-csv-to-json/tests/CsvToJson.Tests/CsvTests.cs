using CsvToJson;
using Xunit;

namespace CsvToJson.Tests;

public class CsvTests
{
    [Fact]
    public void Quoted_fields_keep_commas_line_breaks_and_quotes()
    {
        var records = Csv.Parse("a,b\r\n\"x, y\",\"say \"\"hi\"\"\"\r\n");
        Assert.Equal(["x, y", "say \"hi\""], records[1].Fields);
    }

    [Fact]
    public void A_ragged_record_names_its_line()
    {
        var error = Assert.Throws<CsvException>(() => Csv.ToJson(Csv.Parse("a,b\n1,2\n3\n"), types: false));
        Assert.StartsWith("line 3", error.Message);
    }
}
