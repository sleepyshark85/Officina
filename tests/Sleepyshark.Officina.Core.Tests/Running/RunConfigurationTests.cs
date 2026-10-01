using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Configuration.Model;
using Sleepyshark.Officina.Core.Configuration.Validation;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Running;
using Sleepyshark.Officina.Testing;

namespace Sleepyshark.Officina.Core.Tests.Running;

/// <summary>Each run stores its resolved configuration (CFG-07), and nothing runs on an invalid one (CFG-06).</summary>
public class RunConfigurationTests
{
    private static readonly OfficinaOptions Options = new()
    {
        Project = new ProjectOptions { Name = "invoice-api" },
        Agents = new Dictionary<string, AgentDefinition> { ["extractor"] = new() { Instructions = "Extract the total of {{project.name}} invoices." } },
    };

    [Fact]
    public async Task Each_run_records_the_configuration_it_used()
    {
        var kit = new TestKit(Options);
        kit.Model.Reply("one").Reply("two");
        var ct = TestContext.Current.CancellationToken;

        await kit.RunAsync("extractor", "a", ct);
        await kit.RunAsync("extractor", "b", ct);

        Assert.Equal(2, kit.Runs.Runs.Count);
        Assert.NotEqual(kit.Runs.Runs[0].RunId, kit.Runs.Runs[1].RunId);
        var run = kit.Runs.Runs[0];
        Assert.Equal("extractor", run.Agent);
        Assert.Equal(CoreVersion.Value, run.Configuration.CoreVersion);
        Assert.Equal(OptionsWriter.WriteText(Options, SettingsModel.Default), run.Configuration.Json);
        Assert.Contains("Extract the total of {{project.name}} invoices.", run.Configuration.Json, StringComparison.Ordinal);
        Assert.Contains("\"cost\": 25", run.Configuration.Json, StringComparison.Ordinal);
    }

    [Fact]
    public void The_same_configuration_has_the_same_hash_and_a_change_changes_it()
    {
        var same = Options with { Project = new ProjectOptions { Name = "invoice-api" } };
        var changed = Options with { Run = new RunDefaults { PermissionMode = PermissionMode.Auto } };

        Assert.Equal(Capture(Options).Sha256, Capture(same).Sha256);
        Assert.NotEqual(Capture(Options).Sha256, Capture(changed).Sha256);
    }

    [Fact]
    public async Task An_agent_run_directly_is_recorded_as_part_of_the_configuration()
    {
        var kit = new TestKit();
        kit.Model.Reply("done");

        await kit.RunAsync(new AgentDefinition { Instructions = "Be brief." }, "hi", TestContext.Current.CancellationToken);

        var run = Assert.Single(kit.Runs.Runs);
        Assert.Equal(AgentRunner.InlineAgentName, run.Agent);
        Assert.Contains("Be brief.", run.Configuration.Json, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_runs_on_a_configuration_with_errors_and_every_error_is_named()
    {
        var invalid = Options with
        {
            Agents = new Dictionary<string, AgentDefinition>
            {
                ["a"] = new() { Instructions = "", Model = "missing" },
                ["b"] = new() { Instructions = "Today is {{now:date}}." },
            },
        };

        var error = Assert.Throws<ConfigurationException>(() => new AgentRunner(invalid, new Dictionary<string, IModelProvider>()));

        Assert.Equal(["agents.a.instructions", "agents.a.model", "agents.b.instructions"], error.Errors.Select(item => item.Path));
        Assert.Contains("agents.b.instructions: placeholder {{now:date}} is not allowed in the stable prefix.", error.Message, StringComparison.Ordinal);
    }

    private static RunConfiguration Capture(OfficinaOptions options) => RunConfiguration.Capture(options, SettingsModel.Default);
}
