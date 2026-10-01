using System.Collections.Immutable;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Messages;
using Sleepyshark.Officina.Core.Tools;

namespace Sleepyshark.Officina.Core.Extensibility;

/// <summary>One model call. S05 splits it into the stable prefix, history and volatile context.</summary>
/// <param name="Profile">The model profile.</param>
/// <param name="Instructions">The agent's instructions.</param>
/// <param name="Messages">The conversation so far.</param>
/// <param name="Tools">The tools the agent is offered, sorted by name (TOOL-03).</param>
public sealed record ModelRequest(ModelProfile Profile, string Instructions, ImmutableArray<Message> Messages, ImmutableArray<ToolDefinition> Tools);
