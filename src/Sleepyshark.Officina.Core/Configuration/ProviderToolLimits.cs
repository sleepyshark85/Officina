using System.ComponentModel.DataAnnotations;

namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>The provider's own limits on a tool it runs itself (TOOL-13).</summary>
public sealed record ProviderToolLimits
{
    [Setting("The most calls of the tool in one model call. Unset leaves it to the provider.", Example = "5")]
    [Range(1, int.MaxValue, ErrorMessage = "must be at least 1.")]
    public int? MaxUses { get; init; }

    [Setting("The only domains the tool may reach, such as for a web search or fetch. Empty allows any.", Example = """["learn.microsoft.com", "github.com"]""")]
    public IReadOnlyList<string> AllowedDomains { get; init; } = [];
}
