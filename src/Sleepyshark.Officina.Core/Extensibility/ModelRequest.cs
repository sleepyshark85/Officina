using System.Collections.Immutable;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Messages;

namespace Sleepyshark.Officina.Core.Extensibility;

/// <summary>One model call. S05 splits it into the stable prefix, history and volatile context.</summary>
public sealed record ModelRequest(ModelProfile Profile, string Instructions, ImmutableArray<Message> Messages);
