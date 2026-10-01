using System.Security.Cryptography;
using System.Text;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Configuration.Model;

namespace Sleepyshark.Officina.Core.Running;

/// <summary>
/// The exact configuration a run used, in canonical form, so its behaviour can be reproduced and
/// explained later (CFG-07). Defaults are written out, so a later core with other defaults reads it the same.
/// </summary>
/// <param name="CoreVersion">The core that ran it.</param>
/// <param name="Json">Every setting, in canonical order.</param>
/// <param name="Sha256">The hash of <paramref name="Json"/>, to tell configurations apart at a glance.</param>
public sealed record RunConfiguration(string CoreVersion, string Json, string Sha256)
{
    public static RunConfiguration Capture(OfficinaOptions options, SettingsModel model)
    {
        var json = OptionsWriter.WriteText(options, model);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        return new RunConfiguration(Core.CoreVersion.Value, json, hash);
    }
}

/// <summary>What is stored when a run starts.</summary>
/// <param name="RunId">The run's id.</param>
/// <param name="Agent">The agent definition the run starts with.</param>
/// <param name="Configuration">The resolved configuration.</param>
public sealed record RunStarted(string RunId, string Agent, RunConfiguration Configuration);
