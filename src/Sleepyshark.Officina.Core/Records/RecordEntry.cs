using System.Reflection;
using System.Text.Json.Serialization;

namespace Sleepyshark.Officina.Core.Records;

/// <summary>
/// One accepted update of a run record (REC-01, REC-02). Entries are only ever added, never changed or removed, so the
/// record is never trimmed (REC-05).
/// </summary>
/// <param name="RunId">The run.</param>
/// <param name="Revision">The record's revision after this update: 1 for the first, and one more for each after it.</param>
/// <param name="Agent">The agent that proposed it; a decision's author.</param>
/// <param name="Time">When it was accepted; a decision's or a citation's date.</param>
/// <param name="Item">What was added.</param>
public sealed record RecordEntry(string RunId, long Revision, string Agent, DateTimeOffset Time, RecordItem Item);

/// <summary>What an entry adds. Each kind has a name, which configuration uses to choose what an agent sees (REC-06).</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(Fact), "fact")]
[JsonDerivedType(typeof(Finding), "finding")]
[JsonDerivedType(typeof(Decision), "decision")]
[JsonDerivedType(typeof(Citation), "citation")]
public abstract record RecordItem
{
    private static readonly Dictionary<Type, string> Names = typeof(RecordItem).GetCustomAttributes<JsonDerivedTypeAttribute>()
        .ToDictionary(kind => kind.DerivedType, kind => (string)kind.TypeDiscriminator!);

    /// <summary>The names of every kind.</summary>
    public static IReadOnlyCollection<string> Kinds => Names.Values;

    [JsonIgnore]
    public string Kind => Names[GetType()];
}

/// <summary>A value, where it comes from, and when it was true.</summary>
/// <param name="Subject">What the value is of, such as <c>invoice.total</c>. Facts with the same subject and different values conflict (REC-03).</param>
/// <param name="Value">The value.</param>
/// <param name="Source">Where it comes from, such as a file, a tool or a cited document.</param>
/// <param name="AsOf">When it was true; null for when it was recorded.</param>
public sealed record Fact(string Subject, string Value, string Source, DateTimeOffset? AsOf) : RecordItem;

/// <summary>Something learned that is not a single value.</summary>
public sealed record Finding(string Text) : RecordItem;

/// <summary>A choice and its reason. Its author and date are its entry's agent and time.</summary>
/// <param name="Subject">What is decided. Current decisions with the same subject and different choices conflict (REC-03).</param>
/// <param name="Choice">What was chosen.</param>
/// <param name="Reason">Why.</param>
/// <param name="Replaces">The revision of the decision this one replaces, which is then no longer current.</param>
public sealed record Decision(string Subject, string Choice, string Reason, long? Replaces) : RecordItem;

/// <summary>A source that answers and other entries cite as <c>[cite:id]</c>. Its date is its entry's time.</summary>
/// <param name="Id">The short id it is cited by, unique in the record.</param>
/// <param name="Document">The document.</param>
/// <param name="Location">Where in the document, such as a section or a line.</param>
/// <param name="Quote">The words cited.</param>
public sealed record Citation(string Id, string Document, string Location, string Quote) : RecordItem;
