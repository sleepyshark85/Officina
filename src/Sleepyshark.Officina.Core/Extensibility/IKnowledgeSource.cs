namespace Sleepyshark.Officina.Core.Extensibility;

/// <summary>
/// Searches the application's own data (CTX-04). The core calls it before a turn, or when the agent calls a knowledge
/// tool, and labels what it returns as data (INV-08).
/// </summary>
public interface IKnowledgeSource
{
    ValueTask<Retrieval> RetrieveAsync(RetrievalQuery query, CancellationToken ct);
}

/// <summary>What to search for.</summary>
/// <param name="Question">The question: the turn's work before the turn, or the agent's question to a knowledge tool.</param>
/// <param name="Caller">Who the search is for. A source returns only what the caller's tenant may see (SEC-02).</param>
/// <param name="MaxPassages">The most passages to return.</param>
public sealed record RetrievalQuery(string Question, Caller Caller, int MaxPassages);

/// <summary>What a knowledge source found (CTX-05).</summary>
/// <param name="Passages">The passages, best first.</param>
/// <param name="Coverage">Whether they answer the question.</param>
public sealed record Retrieval(IReadOnlyList<Passage> Passages, Coverage Coverage);

/// <summary>A passage and where it comes from.</summary>
/// <param name="Citation">The id an answer cites it by, such as a document and section.</param>
/// <param name="Text">The passage.</param>
public sealed record Passage(string Citation, string Text);

/// <summary>Whether the passages answer the question (CTX-05).</summary>
public enum Coverage
{
    Covered,
    PartlyCovered,
    NotCovered,
}
