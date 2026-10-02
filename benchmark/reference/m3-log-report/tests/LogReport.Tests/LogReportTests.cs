using LogReport.Core;
using Xunit;

namespace LogReport.Tests;

public class LogReportTests
{
    [Fact]
    public void Parses_a_line_without_its_query_string()
    {
        var request = CommonLog.Parse("""10.0.0.3 - - [10/Oct/2030:16:30:00 +0200] "GET /missing?x=1 HTTP/1.1" 404 -""");
        Assert.Equal(new Request("10.0.0.3", new DateTimeOffset(2030, 10, 10, 14, 30, 0, TimeSpan.Zero), "/missing", 404, 0), request);
    }

    [Fact]
    public void Counts_a_line_that_is_not_a_log_line_as_malformed()
    {
        var report = new Report(new Filter(), 10);
        report.Add("not a log line");
        Assert.Equal((0L, 1L), (report.Requests, report.Malformed));
    }

    [Fact]
    public void Keeps_a_status_class() =>
        Assert.True(new Filter(Status: "4xx").Keeps(new Request("h", DateTimeOffset.UnixEpoch, "/", 404, 0)));
}
