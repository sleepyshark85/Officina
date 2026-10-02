namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>
/// Optional capabilities, each off until it is enabled (CAP-01). One that is off adds no tools, storage or required
/// settings: its settings are not checked, and using it is an error (CAP-02). The slices that add the other
/// capabilities add their switches and dependencies here.
/// </summary>
public sealed record CapabilitiesOptions
{
    /// <summary>What each capability needs on as well, checked at validation (CAP-03).</summary>
    public static IReadOnlyList<(string Capability, string Requires)> Dependencies { get; } = [("sandbox", "workspace")];

    [Setting("The conversation store: each agent's conversation with each caller is kept, anonymous callers sharing one, so a history strategy other than `none` continues it across requests and restarts.",
        Example = """{ "enabled": true }""")]
    public CapabilityOptions ConversationStore { get; init; } = new();

    [Setting("Knowledge retrieval: the sources in `knowledge`, searched before a turn or through `knowledge:` tools.",
        Example = """{ "enabled": true }""")]
    public CapabilityOptions Knowledge { get; init; } = new();

    [Setting("Human interaction: the `builtin:human.ask_owner` tool, and sign-offs where the run waits for the owner.",
        Example = """{ "enabled": true, "signOffs": ["runBudgetExceeded"] }""")]
    public HumanInteractionOptions HumanInteraction { get; init; } = new();

    [Setting("The git workspace: a working copy per agent, and an integration queue into the baseline.",
        Example = """{ "enabled": true, "protectedPaths": [{ "path": "secrets/**", "access": "hidden" }] }""")]
    public WorkspaceOptions Workspace { get; init; } = new();

    [Setting("The sandbox that commands run in: no network unless allowed, and command rules. It needs the workspace.",
        Example = """{ "enabled": true, "allowedHosts": ["api.nuget.org"] }""")]
    public SandboxOptions Sandbox { get; init; } = new();

    [Setting("The task board: tasks with dependencies, verification checks and review, which agents change through the `tasks.*` tools and the owner at any time.",
        Example = """{ "enabled": true, "maxAttempts": 2 }""")]
    public TaskBoardOptions TaskBoard { get; init; } = new();

    [Setting("Project memory: durable instructions, conventions and decisions in every agent's stable prefix, which agents change through the `memory.*` tools once the lead or the owner approves.",
        Example = """{ "enabled": true, "scope": "project", "approveBy": "lead" }""")]
    public ProjectMemoryOptions ProjectMemory { get; init; } = new();

    /// <summary>Every capability, by its name in configuration, and whether it is on.</summary>
    public IReadOnlyDictionary<string, bool> Switches() => new Dictionary<string, bool>
    {
        ["conversationStore"] = ConversationStore.Enabled,
        ["knowledge"] = Knowledge.Enabled,
        ["humanInteraction"] = HumanInteraction.Enabled,
        ["workspace"] = Workspace.Enabled,
        ["sandbox"] = Sandbox.Enabled,
        ["taskBoard"] = TaskBoard.Enabled,
        ["projectMemory"] = ProjectMemory.Enabled,
    };
}

/// <summary>A capability whose only setting is its switch.</summary>
public sealed record CapabilityOptions
{
    [Setting("Whether the capability is on.", Example = "true")]
    public bool Enabled { get; init; }
}
