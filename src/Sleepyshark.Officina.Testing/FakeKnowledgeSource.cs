using System.Collections.Concurrent;
using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Testing;

/// <summary>A knowledge source that returns what a test gives it, whatever the question. It remembers every query.</summary>
/// <param name="coverage">Whether the passages cover the question.</param>
/// <param name="passages">The passages, as citation and text.</param>
public sealed class FakeKnowledgeSource(Coverage coverage, params (string Citation, string Text)[] passages) : IKnowledgeSource
{
    private readonly ConcurrentQueue<RetrievalQuery> queries = new();

    /// <summary>The queries received so far, in order.</summary>
    public IReadOnlyList<RetrievalQuery> Queries => [.. queries];

    public ValueTask<Retrieval> RetrieveAsync(RetrievalQuery query, CancellationToken ct)
    {
        queries.Enqueue(query);
        return ValueTask.FromResult(new Retrieval([.. passages.Take(query.MaxPassages).Select(passage => new Passage(passage.Citation, passage.Text))], coverage));
    }
}
