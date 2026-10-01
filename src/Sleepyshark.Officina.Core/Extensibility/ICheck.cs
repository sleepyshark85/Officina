namespace Sleepyshark.Officina.Core.Extensibility;

/// <summary>
/// A check of work, such as a build or the tests. Only its result decides; nothing an agent says overrides it (INV-09).
/// The integration queue runs the baseline checks (WS-02), and a turn its output checks (OUT-03); task checks use it
/// from S18.
/// </summary>
public interface ICheck
{
    ValueTask<CheckResult> RunAsync(CheckContext context, CancellationToken ct);
}

/// <summary>What a check looks at.</summary>
/// <param name="Directory">
/// The files to check, if any. For a baseline check, the change applied to the baseline as it is now (WS-09). Commands
/// run there in the sandbox (S15).
/// </param>
/// <param name="Output">For an output check, the output.</param>
/// <param name="Artifacts">For an output check, the artifacts the turn produced (OUT-05).</param>
public sealed record CheckContext(string? Directory, string? Output, IReadOnlyList<Artifact> Artifacts);

/// <summary>The result of a check.</summary>
/// <param name="Passed">Whether the work passed.</param>
/// <param name="Findings">What the check found, such as the failing tests.</param>
public sealed record CheckResult(bool Passed, IReadOnlyList<string> Findings);
