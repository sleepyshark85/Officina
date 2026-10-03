using Sleepyshark.Officina.Testing;

namespace Sleepyshark.Officina.Cli.Tests;

/// <summary>
/// Results saved as files in the project: after a reply that is a document, a chat session at a terminal offers to save it,
/// <c>/save</c> saves the last output at any time, and a team's plan is offered once the owner approves it. Only the console,
/// the model and the clock are stand-ins; the project is a real folder.
/// </summary>
public sealed class SaveOutputTests : IDisposable
{
    private const string Configuration = """
        {
          "providers": { "claude": { "prices": { "claude-opus-5-5": { "input": 1 } } } },
          "agents": { "dev": { "instructions": "Work." } }
        }
        """;

    private const string Requirements = "# Login requirements\n\nThe owner signs in with a passkey.";

    private readonly Sof sof = new();
    private readonly ScriptedModelProvider model = new();

    public SaveOutputTests()
    {
        sof.Write("sof.json", Configuration);
        sof.Providers["claude"] = model;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => sof.Dispose();

    private string PathOf(string relative) => Path.Combine(sof.Directory, relative);

    private string Read(string relative) => File.ReadAllText(PathOf(relative)).ReplaceLineEndings("\n");

    // At a terminal, a reply with a Markdown heading is offered at docs/, named by the heading; Enter saves it there.
    [Fact]
    public async Task A_document_is_offered_under_its_heading_and_Enter_saves_it()
    {
        sof.Interactive = true;
        model.Reply(Requirements);

        var chat = sof.RunAsync("chat");
        sof.In.Type("Write the login requirements.");
        await sof.Out.WaitForAsync("Save this as docs/login-requirements.md? [Enter = yes, n = no, or type another path]", Ct);
        sof.In.Type("");
        await sof.Out.WaitForAsync("Saved docs/login-requirements.md. It is not committed.", Ct);
        sof.In.Type("/quit");
        var (exitCode, output, _) = await sof.EndedAsync(chat);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("[dev] # Login requirements", output, StringComparison.Ordinal); // printed as ever
        Assert.Contains("note: masking is on", output, StringComparison.Ordinal); // on by default, so the prompt says so
        Assert.Equal(Requirements + "\n", Read("docs/login-requirements.md"));
        Assert.Empty(Directory.GetFiles(PathOf("docs"), "*.tmp", SearchOption.AllDirectories)); // written atomically, nothing left
    }

    // n saves nothing; /save then saves it at the suggestion, and /save <path> where the owner says.
    [Fact]
    public async Task No_saves_nothing_and_save_saves_it_later_at_the_suggestion_or_a_path()
    {
        sof.Interactive = true;
        model.Reply(Requirements);

        var chat = sof.RunAsync("chat");
        sof.In.Type("Write the login requirements.");
        await sof.Out.WaitForAsync("Save this as docs/login-requirements.md?", Ct);
        sof.In.Type("n");
        await sof.Out.WaitForAsync("Not saved; /save saves it later.", Ct);
        Assert.False(Directory.Exists(PathOf("docs")));
        sof.In.Type("/save");
        await sof.Out.WaitForAsync("Saved docs/login-requirements.md. It is not committed.", Ct);
        sof.In.Type("/save notes/login.md");
        await sof.Out.WaitForAsync("Saved notes/login.md. It is not committed.", Ct);
        sof.In.Type("/quit");
        Assert.Equal(ExitCodes.Success, (await sof.EndedAsync(chat)).ExitCode);

        Assert.Equal(Requirements + "\n", Read("docs/login-requirements.md"));
        Assert.Equal(Requirements + "\n", Read("notes/login.md"));
    }

    // The owner types another path, or a folder, which gets the suggested name; the folders are created.
    [Theory]
    [InlineData("specs/auth/login.md", "specs/auth/login.md")]
    [InlineData("specs/", "specs/login-requirements.md")]
    public async Task Another_path_or_folder_is_taken_instead(string typed, string saved)
    {
        sof.Interactive = true;
        model.Reply(Requirements);

        var chat = sof.RunAsync("chat");
        sof.In.Type("Write the login requirements.");
        await sof.Out.WaitForAsync("Save this as docs/login-requirements.md?", Ct);
        sof.In.Type(typed);
        await sof.Out.WaitForAsync($"Saved {saved}. It is not committed.", Ct);
        sof.In.Type("/quit");
        Assert.Equal(ExitCodes.Success, (await sof.EndedAsync(chat)).ExitCode);

        Assert.Equal(Requirements + "\n", Read(saved));
        Assert.False(Directory.Exists(PathOf("docs")));
    }

    // A long reply with no heading is a document too, named by the owner's message; a short one is not offered, and /save still saves it.
    [Fact]
    public async Task A_long_reply_is_named_by_the_message_and_a_short_one_is_not_offered()
    {
        sof.Interactive = true;
        var criteria = string.Join("\n", Enumerable.Range(1, 45).Select(number => $"- Criterion {number}."));
        model.Reply("Hi.").Reply(criteria);

        var chat = sof.RunAsync("chat");
        sof.In.Type("Say hi.");
        await sof.Out.WaitForAsync("dev: Completed", Ct);
        sof.In.Type("List the acceptance criteria!");
        await sof.Out.WaitForAsync("Save this as docs/list-the-acceptance-criteria.md?", Ct);
        sof.In.Type("n");
        await sof.Out.WaitForAsync("Not saved", Ct);
        sof.In.Type("/quit");
        var (exitCode, output, _) = await sof.EndedAsync(chat);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Single(output.Split('\n'), line => line.StartsWith("Save this as", StringComparison.Ordinal)); // "Hi." was not offered
    }

    // An existing file is never overwritten unasked: the offer suggests a numbered name, and a path that exists is asked about.
    [Fact]
    public async Task An_existing_file_gets_a_numbered_name_or_is_overwritten_only_when_the_owner_says_so()
    {
        sof.Interactive = true;
        Directory.CreateDirectory(PathOf("docs"));
        sof.Write("docs/login-requirements.md", "Mine.");
        model.Reply(Requirements);

        var chat = sof.RunAsync("chat");
        sof.In.Type("Write the login requirements.");
        await sof.Out.WaitForAsync("Save this as docs/login-requirements-2.md?", Ct);
        sof.In.Type("docs/login-requirements.md");
        await sof.Out.WaitForAsync("docs/login-requirements.md exists. Overwrite it? [y = yes, Enter = no]", Ct);
        sof.In.Type("");
        await sof.Out.WaitForAsync("docs/login-requirements.md is left as it was.", Ct);
        Assert.Equal("Mine.", Read("docs/login-requirements.md"));
        await sof.Out.WaitForAsync("Save this as docs/login-requirements-2.md? [", Ct); // asked again
        sof.In.Type("y");
        await sof.Out.WaitForAsync("Saved docs/login-requirements-2.md.", Ct);
        sof.In.Type("/save docs/login-requirements.md");
        await sof.Out.WaitForAsync("docs/login-requirements.md exists. Overwrite it?", Ct);
        sof.In.Type("y");
        await sof.Out.WaitForAsync("Saved docs/login-requirements.md.", Ct);
        sof.In.Type("/quit");
        Assert.Equal(ExitCodes.Success, (await sof.EndedAsync(chat)).ExitCode);

        Assert.Equal(Requirements + "\n", Read("docs/login-requirements-2.md"));
        Assert.Equal(Requirements + "\n", Read("docs/login-requirements.md"));
    }

    // Only files in the project, and none of Officina's state, its configuration, the files it extends or the protected paths.
    [Fact]
    public async Task Save_refuses_state_configuration_protected_paths_and_paths_out_of_the_project()
    {
        sof.Write("sof.json", """
            {
              "extends": ["shared.json"],
              "providers": { "claude": { "prices": { "claude-opus-5-5": { "input": 1 } } } },
              "agents": { "dev": { "instructions": "Work." } },
              "capabilities": { "workspace": { "protectedPaths": [{ "path": "secrets/**" }] } }
            }
            """);
        sof.Write("shared.json", "{}");
        var outside = Directory.CreateTempSubdirectory("officina-outside-").FullName;
        try
        {
            var linked = TryLink(PathOf("link"), outside);
            model.Reply("Hi.");

            var chat = sof.RunAsync("chat");
            sof.In.Type("Say hi.");
            await sof.Out.WaitForAsync("dev: Completed", Ct);
            foreach (var path in new[] { ".sof/notes.md", "sof.json", "sof.dev.json", "shared.json", "secrets/key.md", ".git/notes.md", "../out.md", "docs/../../out.md", outside + "/out.md", "link/out.md" })
            {
                sof.In.Type($"/save \"{path}\"");
            }

            sof.In.Dispose();
            var (exitCode, output, _) = await sof.EndedAsync(chat);

            Assert.Equal(ExitCodes.Success, exitCode);
            foreach (var refused in new[] { ".sof/notes.md", "sof.json", "sof.dev.json", "shared.json", "secrets/key.md", ".git/notes.md" })
            {
                Assert.Contains($"error: {refused} is Officina's state, its configuration, or a protected path, which is not saved over.", output, StringComparison.Ordinal);
            }

            Assert.Contains("error: ../out.md is outside the project, and only files in it are saved.", output, StringComparison.Ordinal);
            Assert.Contains("error: docs/../../out.md is outside the project", output, StringComparison.Ordinal);
            Assert.Contains($"error: {outside}/out.md is outside the project", output, StringComparison.Ordinal);
            Assert.Equal("{}", Read("shared.json"));
            Assert.DoesNotContain("Saved", output, StringComparison.Ordinal);
            Assert.Empty(Directory.GetFiles(outside));
            Assert.False(File.Exists(Path.Combine(sof.Directory, "..", "out.md")));
            if (linked)
            {
                Assert.Contains("error: link/out.md goes through a symbolic link, which is not followed.", output, StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    // Piped input is never asked anything: a document is printed, not offered; /save still saves, and never overwrites.
    [Fact]
    public async Task Piped_input_is_never_prompted_and_save_still_works()
    {
        model.Reply(Requirements);

        var chat = sof.RunAsync("chat");
        sof.In.Type("Write the login requirements.");
        await sof.Out.WaitForAsync("dev: Completed", Ct);
        sof.In.Type("/save");
        sof.In.Type("/save docs/login-requirements.md");
        sof.In.Dispose();
        var (exitCode, output, _) = await sof.EndedAsync(chat);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.DoesNotContain("Save this as", output, StringComparison.Ordinal);
        Assert.Contains("Saved docs/login-requirements.md. It is not committed.", output, StringComparison.Ordinal);
        Assert.Contains(
            "error: docs/login-requirements.md exists, and is not overwritten without asking; give another path, such as docs/login-requirements-2.md.",
            output, StringComparison.Ordinal);
        Assert.Single(Directory.GetFiles(PathOf("docs")));
    }

    // Masking tokens are saved as they are, and the owner is told so.
    [Fact]
    public async Task Save_says_when_the_output_holds_masking_tokens()
    {
        model.Reply("Write to [email-1] about it.");

        var chat = sof.RunAsync("chat");
        sof.In.Type("Who do I write to?");
        await sof.Out.WaitForAsync("dev: Completed", Ct);
        sof.In.Type("/save");
        sof.In.Dispose();
        var (_, output, _) = await sof.EndedAsync(chat);

        Assert.Contains("note: this holds masking tokens, such as [email-1], in place of the values they mask; the file gets the tokens.", output, StringComparison.Ordinal);
        Assert.Contains("Saved docs/who-do-i-write-to.md.", output, StringComparison.Ordinal);
    }

    // RUN-11: the report /report showed is what /save saves next.
    [Fact]
    public async Task Save_after_report_saves_the_report()
    {
        model.Reply("Hi.");

        var chat = sof.RunAsync("chat");
        sof.In.Type("Say hi.");
        await sof.Out.WaitForAsync("dev: Completed", Ct);
        sof.In.Type("/report");
        sof.In.Type("/save");
        sof.In.Dispose();
        var (_, output, _) = await sof.EndedAsync(chat);

        var runId = output.Split('\n').First(line => line.StartsWith("run ", StringComparison.Ordinal))["run ".Length..];
        Assert.Contains($"Saved docs/report-{runId[..8]}.txt. It is not committed.", output, StringComparison.Ordinal);
        Assert.StartsWith($"Run {runId}: ", Read($"docs/report-{runId[..8]}.txt"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Save_with_nothing_to_save_says_so()
    {
        var chat = sof.RunAsync("chat");
        sof.In.Type("/save");
        sof.In.Dispose();
        var (_, output, _) = await sof.EndedAsync(chat);

        Assert.Contains("error: there is nothing to save yet", output, StringComparison.Ordinal);
    }

    // TEAM-10: once the owner approves the lead's plan, the plan is offered as Markdown under docs/plans/, dated.
    [Fact]
    public async Task The_plan_approved_at_sign_off_is_offered_as_Markdown()
    {
        sof.Interactive = true;
        sof.Write("sof.json", """
            {
              "providers": { "claude": { "prices": { "claude-opus-5-5": { "input": 1 } } } },
              "agents": {
                "team": { "instructions": "A team.", "pattern": { "type": "team", "lead": "lead", "roles": { "developer": { "max": 1 } } } },
                "lead": { "instructions": "Lead.", "tools": ["planning"] },
                "developer": { "instructions": "Develop.", "tools": ["work"] }
              },
              "tools": { "create": { "source": "builtin:tasks.create" }, "submit": { "source": "builtin:tasks.submit_for_review" } },
              "toolSets": { "planning": ["create"], "work": ["submit"] },
              "capabilities": {
                "taskBoard": { "enabled": true }, "team": { "enabled": true },
                "humanInteraction": { "enabled": true, "signOffs": ["planApproval"] }
              }
            }
            """);
        model.When(request => Work(request).StartsWith("You lead a team", StringComparison.Ordinal))
            .CallTools(
                ("create", """{ "id": "a", "title": "Parse", "description": "Parse the input.", "acceptanceCriteria": ["It parses."], "reason": "plan" }"""),
                ("create", """{ "id": "b", "title": "Print", "dependsOn": ["a"], "reason": "plan" }"""))
            .Reply("Planned.");
        model.When(request => Given(request, "Every task is done")).Reply("Done.");
        model.When(request => Work(request).Contains("Do task ", StringComparison.Ordinal))
            .CallTools(("submit", """{ "id": "a" }""")).Reply("Submitted.").CallTools(("submit", """{ "id": "b" }""")).Reply("Submitted.");
        var plan = $"docs/plans/{sof.Time.GetLocalNow():yyyy-MM-dd}-write-a-parser.md";

        var chat = sof.RunAsync("chat", "--agent", "team");
        sof.In.Type("Write a parser.");
        await sof.Out.WaitForAsync("Answer with /approve or /deny.", Ct);
        sof.In.Type("/approve 1");
        await sof.Out.WaitForAsync($"Save this as {plan}?", Ct);
        sof.In.Type("");
        await sof.Out.WaitForAsync($"Saved {plan}. It is not committed.", Ct);
        await sof.Out.WaitForAsync("team: Completed", Ct);
        sof.In.Type("/quit");
        var (exitCode, output, _) = await sof.EndedAsync(chat);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Single(output.Split('\n'), line => line.StartsWith("Save this as", StringComparison.Ordinal)); // "Done." is short
        Assert.Equal(
            $"""
            # Plan: Write a parser.

            The team lead's plan, approved on {sof.Time.GetLocalNow():yyyy-MM-dd}.

            ## Goal

            Write a parser.

            ## Parse (`a`)

            Parse the input.

            Acceptance criteria:

            - It parses.

            ## Print (`b`)

            Depends on: `a`

            """.ReplaceLineEndings("\n"),
            Read(plan));
    }

    [Theory]
    [InlineData("# Login requirements\nText.", "Ignored.", true, "docs/login-requirements.md")]
    [InlineData("## `Parser` — design, v2 ##", "Ignored.", true, "docs/parser-design-v2.md")]
    [InlineData("Short.", "What is it?", false, "docs/what-is-it.md")]
    [InlineData("""{ "steps": ["Parse", "Print"] }""", "Plan the parser.", true, "docs/plans/plan-the-parser.json")]
    [InlineData("""{ "total": 3 }""", "Count them.", false, "docs/count-them.json")]
    [InlineData("Ok.", "!!!", false, "docs/output.md")]
    public void Output_is_a_document_or_not_and_its_default_path_follows_its_kind(string output, string message, bool document, string suggested)
    {
        Assert.Equal(document, OutputFiles.IsDocument(output));
        Assert.Equal(suggested, OutputFiles.Suggest(output, message));
    }

    private static string Work(Sleepyshark.Officina.Core.Extensibility.ModelRequest request) => ScriptedModelProvider.WorkOf(request);

    /// <summary>Whether any text the agent was given holds <paramref name="text"/>: the lead's later work follows its plan in its conversation.</summary>
    private static bool Given(Sleepyshark.Officina.Core.Extensibility.ModelRequest request, string text) =>
        request.History.Any(message => message.Content.OfType<Sleepyshark.Officina.Core.Messages.TextContent>().Any(content => content.Text.Contains(text, StringComparison.Ordinal)));

    /// <summary>A symbolic link to a folder, where the system lets the test make one (Windows may not).</summary>
    private static bool TryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
