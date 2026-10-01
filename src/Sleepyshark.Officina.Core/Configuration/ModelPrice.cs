using System.ComponentModel.DataAnnotations;
using Sleepyshark.Officina.Core.Messages;

namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>A model's prices, in USD per million tokens (MDL-09).</summary>
public sealed record ModelPrice
{
    private const string NotNegative = "cannot be negative.";

    [Setting("USD per million input tokens.", Example = "4")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = NotNegative)]
    public decimal Input { get; init; }

    [Setting("USD per million output tokens.", Example = "20")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = NotNegative)]
    public decimal Output { get; init; }

    [Setting("USD per million tokens read from the cache.", Example = "0.2")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = NotNegative)]
    public decimal CacheRead { get; init; }

    [Setting("USD per million tokens written to the cache.", Example = "5")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = NotNegative)]
    public decimal CacheWrite { get; init; }

    /// <summary>What the usage costs, in USD (MSG-06).</summary>
    public decimal Cost(Usage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        return ((usage.Input * Input) + (usage.Output * Output) + (usage.CacheRead * CacheRead) + (usage.CacheWrite * CacheWrite)) / 1_000_000m;
    }
}
