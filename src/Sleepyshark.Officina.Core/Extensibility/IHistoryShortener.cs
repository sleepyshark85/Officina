using System.Collections.Immutable;
using Sleepyshark.Officina.Core.Messages;

namespace Sleepyshark.Officina.Core.Extensibility;

/// <summary>
/// Shortens a conversation's history when the model reports it too long (HIST-01, HIST-04). A model provider with its
/// own mechanism implements it as well; an application registers its own by id. It sees only the request, so it cannot
/// change the run record, tasks or memory (HIST-03).
/// </summary>
public interface IHistoryShortener
{
    /// <param name="request">The request the model reported too long.</param>
    /// <param name="ct">Cancels the shortening.</param>
    /// <returns>
    /// The shortened history. The core checks it before use (HIST-02): it starts with a user message, every tool request
    /// still has its result, and it ends with the current turn, unchanged.
    /// </returns>
    ValueTask<ImmutableArray<Message>> ShortenAsync(ModelRequest request, CancellationToken ct);
}
