using System.Globalization;
using Sleepyshark.Officina.Core.Events;
using Sleepyshark.Officina.Core.Messages;

namespace Sleepyshark.Officina.Cli.Tests;

/// <summary>What <c>sof run</c> shows of a run's events (UX-01).</summary>
public class StatusViewTests
{
    // The console's costs read the same in every culture, so a program reading them, such as the benchmark's runner, reads them right.
    [Fact]
    public void Costs_are_written_the_same_in_every_culture()
    {
        var before = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            using var output = new StringWriter();
            new StatusView(output).Apply(new CoreEvent("run", "dev", null, 1, DateTimeOffset.UnixEpoch, new ModelCallEnded(StopReason.Finished, new Usage(10, 0, 0, 0), 12.34m)));

            Assert.Equal("[dev] model call: 10 tokens, $12.34; cost so far $12.34\n", output.ToString().ReplaceLineEndings("\n"));
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }
}
