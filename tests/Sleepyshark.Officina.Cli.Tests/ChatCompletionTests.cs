namespace Sleepyshark.Officina.Cli.Tests;

/// <summary>What the chat session's line editor suggests for a word, apart from the terminal that shows it.</summary>
public sealed class ChatCompletionTests
{
    private readonly ChatCompletion completion = new(
        SofCommandLine.Create(new ConfigurationCommandOptions(), new SofEnvironment(TextWriter.Null, TextWriter.Null, ".", new Dictionary<string, string>())),
        () => ["dev", "developer[1]", "lead", "team"],
        () => ["run-2", "run-1"],
        () => ["fix-divide", "average"]);

    [Theory]
    [InlineData("", "/con", "/config")]
    [InlineData("", "/re", "/report|/resume")]
    [InlineData("", "/ro", "/rollback")]
    [InlineData("", "/q", "/quit")]
    [InlineData("", "/Con", "")] // case matters, as it does to the line editor
    [InlineData("", "Hello", "")] // a message
    [InlineData("/config ", "", "show|validate|dry-run")]
    [InlineData("/config show ", "--o", "--origin")]
    [InlineData("/rollback ", "", "run-2|run-1")]
    [InlineData("/rollback run-2 ", "--t", "--to")]
    [InlineData("/report ", "run-1", "run-1")]
    [InlineData("/tell ", "dev", "dev|developer[1]")]
    [InlineData("/resume ", "", "dev|developer[1]|lead|team|run-2|run-1")]
    [InlineData("/mode ", "a", "ask|auto")]
    [InlineData("/run --agent ", "l", "lead")]
    [InlineData("/help ", "ro", "rollback")]
    [InlineData("Tell me about /config ", "", "")]
    [InlineData("", "/ta", "/task")] // TASK-08: the owner's task commands, their options, the board's tasks and the team's agents
    [InlineData("/task ", "", "add|edit|priority|assign|cancel|show")]
    [InlineData("/task edit ", "", "fix-divide|average")]
    [InlineData("/task cancel ", "a", "average")]
    [InlineData("/task assign average ", "dev", "dev|developer[1]")]
    [InlineData("/task add Fix it ", "--dep", "--depends")]
    [InlineData("/task edit average --depends ", "f", "fix-divide")]
    [InlineData("/task add ", "", "")]
    [InlineData("/help ", "ta", "task")]
    public void Suggests_commands_options_agents_and_runs(string prefix, string word, string expected)
    {
        var suggested = completion.Complete(prefix, word);

        Assert.Equal(expected.Split('|', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal), suggested.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Every_command_is_suggested_after_a_slash()
    {
        var suggested = completion.Complete("", "/");

        Assert.Superset(
            new HashSet<string> { "/status", "/approve", "/tell", "/mode", "/new", "/help", "/quit", "/config", "/report", "/resume", "/rollback", "/run" },
            suggested.ToHashSet());
        Assert.DoesNotContain("/chat", suggested); // the session is a chat already, so /chat is refused
        Assert.DoesNotContain("/init", suggested); // it runs outside a session
    }
}
