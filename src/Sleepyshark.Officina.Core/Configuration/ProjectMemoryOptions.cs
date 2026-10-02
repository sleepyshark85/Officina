using System.ComponentModel.DataAnnotations;

namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>Project memory (MEM): durable project knowledge shared by runs.</summary>
public sealed record ProjectMemoryOptions
{
    [Setting("Whether project memory is on: every agent's stable prefix then holds the memory, and agents propose changes with the `memory.*` tools.",
        Example = "true")]
    public bool Enabled { get; init; }

    [Setting("Whose memory it is: `project` (one per project name), `owner` (one per caller) or `tenant` (one per tenant). Agents of one definition share a prefix only within one scope.",
        Example = "\"project\"")]
    public MemoryScope Scope { get; init; } = MemoryScope.Project;

    [Setting("The size limit, in tokens, counted as one token per four characters. A change that would take memory past it is not applied: agents propose a condensed version instead, which only the owner approves.",
        Example = "20000")]
    [Range(1, int.MaxValue, ErrorMessage = "must be at least 1.")]
    public int MaxTokens { get; init; } = 20_000;

    [Setting("Who approves an agent's proposed change: `lead`, through a `builtin:memory.review` tool, which you give only to the lead's tool sets (nothing checks that yet), or `owner`, who is asked at the proposal and needs `humanInteraction`. Both memory tools are write tools, so each needs `gates` or a `gateExemption`, and under `permissionMode: ask` each call also asks the owner unless a permission rule allows it.",
        Example = "\"lead\"")]
    public MemoryApprover ApproveBy { get; init; } = MemoryApprover.Lead;
}

/// <summary>Whose memory it is (MEM-04).</summary>
public enum MemoryScope
{
    Project,
    Owner,
    Tenant,
}

/// <summary>Who approves a proposed change (MEM-03).</summary>
public enum MemoryApprover
{
    Lead,
    Owner,
}
