using Sleepyshark.Officina.Core.Configuration;

namespace Sleepyshark.Officina.Core.Running;

/// <summary>
/// How a run started: the agent, and the exact configuration it used, defaults included, so its behaviour can
/// be reproduced and explained later (CFG-07).
/// </summary>
/// <param name="RunId">The run's id.</param>
/// <param name="Agent">The agent definition the run starts with.</param>
/// <param name="CoreVersion">The core that ran it.</param>
/// <param name="Configuration">Every setting, as JSON in the file format.</param>
public sealed record RunStarted(string RunId, string Agent, string CoreVersion, string Configuration);
