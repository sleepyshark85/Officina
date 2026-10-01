using Sleepyshark.Officina.Core.Configuration;

namespace Sleepyshark.Officina.Core.Running;

/// <summary>
/// How a run started: the agent, the owner, and the exact configuration it used, defaults included, so its behaviour
/// can be reproduced and explained later (CFG-07).
/// </summary>
/// <param name="RunId">The run's id.</param>
/// <param name="Agent">The agent definition the run starts with.</param>
/// <param name="Owner">Who the run acts for, or null for an anonymous caller (PRIV-02).</param>
/// <param name="Time">When it started.</param>
/// <param name="CoreVersion">The core that ran it.</param>
/// <param name="Configuration">The resolved configuration. Durable storage writes it as text.</param>
public sealed record RunStarted(string RunId, string Agent, string? Owner, DateTimeOffset Time, string CoreVersion, OfficinaOptions Configuration);
