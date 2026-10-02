using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Messages;
using Sleepyshark.Officina.Core.Running;
using Sleepyshark.Officina.Core.Tools;
using Sleepyshark.Officina.Testing;
using static Sleepyshark.Officina.Core.Tests.Tools.ToolSetup;

namespace Sleepyshark.Officina.Core.Tests.Tools;

/// <summary>The <c>workspace.*</c> tools, on the test kit's in-memory workspace (TEST-01, WS-06, WS-07).</summary>
public class WorkspaceToolsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_agent_reads_searches_and_edits_its_working_copy_and_an_edit_needs_a_fresh_read()
    {
        var workspace = new InMemoryWorkspace();
        workspace.Files["src/a.cs"] = "one\ntwo\nthree";
        var copy = (InMemoryWorkspace.Copy)await workspace.OpenWorkingCopyAsync("t1", Agent, Ct);
        var tools = new WorkspaceTools(_ => Task.FromResult<IWorkingCopy>(copy)).Tools;
        var kit = new TestKit(
            Options(
                ("read", Extension(WorkspaceTools.Read)), ("search", Extension(WorkspaceTools.Search)),
                ("edit", Extension(WorkspaceTools.Edit) with { GateExemption = "Tests only." }), ("write", Extension(WorkspaceTools.Write) with { GateExemption = "Tests only." })),
            tools);
        kit.Model.CallTools(("edit", """{ "path": "src/a.cs", "oldText": "two", "newText": "2" }"""))
            .CallTools(("search", """{ "pattern": "^t" }"""), ("read", """{ "path": "src/a.cs", "firstLine": 2, "lineCount": 1 }"""))
            .CallTools(("edit", """{ "path": "src/a.cs", "oldText": "two", "newText": "2" }"""), ("write", """{ "path": "b.cs", "content": "new" }"""))
            .Reply("Done.");

        var result = await kit.RunAsync(Agent, "work", Ct);

        Assert.Equal(AgentOutcome.Completed, result.Outcome);
        Assert.Equal(
            [
                "failed: src/a.cs changed since you last read it, or you have not read it. Read it again first.",
                "src/a.cs:2: two\nsrc/a.cs:3: three", "two", "Edited.", "Written.",
            ],
            result.Transcript.SelectMany(message => message.Content).OfType<ToolResultContent>().Select(content => Unlabel(content.Text)));
        Assert.Equal(("one\n2\nthree", "new"), (copy.Files["src/a.cs"], copy.Files["b.cs"]));
        Assert.Equal(["src/a.cs"], workspace.Files.Keys); // the baseline is untouched until integration
    }

    [Fact]
    public async Task An_agent_moves_and_deletes_files_it_has_read_in_its_own_working_copy()
    {
        var workspace = new InMemoryWorkspace();
        workspace.Files["a.cs"] = "one";
        workspace.Files["b.cs"] = "two";
        var copy = (InMemoryWorkspace.Copy)await workspace.OpenWorkingCopyAsync("t1", Agent, Ct);
        var kit = new TestKit(
            Options(
                ("read", Extension(WorkspaceTools.Read)), ("move", Extension(WorkspaceTools.Move) with { GateExemption = "Tests only." }),
                ("delete", Extension(WorkspaceTools.Delete) with { GateExemption = "Tests only." })),
            new WorkspaceTools(_ => Task.FromResult<IWorkingCopy>(copy)).Tools);
        kit.Model.CallTools(("move", """{ "from": "a.cs", "to": "c.cs" }"""))
            .CallTools(("read", """{ "path": "a.cs" }"""), ("read", """{ "path": "b.cs" }"""))
            .CallTools(("move", """{ "from": "a.cs", "to": "c.cs" }"""), ("delete", """{ "path": "b.cs" }"""), ("move", """{ "from": "b.cs", "to": "a.cs" }"""))
            .Reply("Done.");

        var result = await kit.RunAsync(Agent, "work", Ct);

        Assert.Equal(AgentOutcome.Completed, result.Outcome);
        Assert.Equal(
            [
                "failed: a.cs changed since you last read it, or you have not read it. Read it again first.",
                "one", "two", "Moved.", "Deleted.", "failed: b.cs does not exist.",
            ],
            result.Transcript.SelectMany(message => message.Content).OfType<ToolResultContent>().Select(content => Unlabel(content.Text)));
        Assert.Equal(["c.cs"], copy.Files.Keys);
        Assert.Equal(["a.cs", "b.cs"], workspace.Files.Keys.Order()); // the baseline is untouched until integration
    }

    private static string Unlabel(string text) => text.Split('\n', 2)[1][..^"\n</data>".Length];
}
