using System.Text.Encodings.Web;
using System.Text.Json;
using CsCheck;
using Sleepyshark.Officina.Testing;

namespace Sleepyshark.Officina.Tests;

/// <summary>
/// TEST-07, EVT-03: a generated secret, put in tool inputs, tool results, tool errors, a denial's reason and a model
/// failure, never reaches the events, the telemetry (with or without content, EVT-04) or the audit trail. The model's
/// own reply is the exception: the conversation keeps the blocks it sent as they are (append-only), so an assistant
/// message that holds a tool input is checked through every other view of that input instead.
/// </summary>
public class SecretPropertyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Where the secret goes in one run.</summary>
    public sealed record Placement(string Secret, bool InInput, bool InResult, bool InError, bool InDenial, bool ModelFails, bool Content);

    // Secrets mix characters that JSON escapes with others, and always hold one that no id, time or redaction mark has.
    private static readonly Gen<string> Secret = Gen.Select(
        Gen.Char["0123456789XYZ\"\\é€ü#% "].Array[5, 12], Gen.Char["XYZ\"\\é€ü#%"], (chars, mark) => mark + new string(chars));

    private static readonly Gen<Placement> Cases = Gen.Select(
        Secret, Gen.Bool, Gen.Bool, Gen.Bool, Gen.Bool, Gen.Bool, Gen.Bool,
        (secret, input, result, error, denial, fails, content) => new Placement(secret, input, result, error, denial, fails, content));

    [Fact]
    public async Task No_secret_reaches_the_events_the_telemetry_or_the_audit_trail()
    {
        using var telemetry = new TelemetryCollector();
        await Property.CheckAsync(Cases, CheckAsync, test => JsonSerializer.Serialize(test), cases: 100);

        async Task CheckAsync(Placement test)
        {
            var name = $"agent-{Guid.NewGuid():N}";
            var sink = new RecordingSink();
            var query = JsonSerializer.Serialize(new { query = test.InInput ? $"find {test.Secret}" : "find" });
            var model = new ScriptedModel().CallTools(new ToolCall("c1", "search", query), new ToolCall("c2", "save", query), new ToolCall("c3", "fail", query));
            _ = test.ModelFails ? model.Fail(new IOException($"Rejected key {test.Secret}."), new TextDelta("Do")) : model.Reply("Done.");
            var agent = new AgentDefinition
            {
                Name = name,
                Model = model,
                Instructions = Agents.Instructions,
                Tools =
                [
                    Agents.Tool("search", schema: Agents.SearchSchema, handler: (_, _) => Task.FromResult(new ToolOutput(test.InResult ? $"found {test.Secret}" : "found"))),
                    Agents.Tool("save", schema: Agents.SearchSchema, kind: ToolKind.Write, needsApproval: true),
                    Agents.Tool("fail", schema: Agents.SearchSchema, handler: (_, _) => throw new InvalidOperationException(test.InError ? $"Login {test.Secret} refused." : "Refused.")),
                ],
                Approver = new ScriptedApprover().Answer(test.InDenial ? Approval.Denied($"not with {test.Secret}") : Approval.Granted),
                AuditSink = sink,
                Secrets = [test.Secret],
                TelemetryContent = test.Content,
            };

            var events = await Agents.CollectAsync(agent.StreamAsync(new Conversation(), "Go.", cancellationToken: Ct));

            var seen = new List<string>
            {
                TelemetryTests.Dump(telemetry.Spans(name)),
                string.Join(' ', telemetry.Measurements(name).SelectMany(each => each.Tags.Values)),
                JsonSerializer.Serialize(sink.Entries),
            };
            seen.AddRange(events.Select(runEvent => runEvent switch
            {
                ConversationAppended { Message.Role: Role.Assistant } => "",
                ConversationAppended appended => JsonSerializer.Serialize(appended.Message),
                _ => JsonSerializer.Serialize(runEvent, runEvent.GetType()),
            }));
            var forms = new[]
            {
                test.Secret,
                JsonEncodedText.Encode(test.Secret).Value,
                JsonEncodedText.Encode(test.Secret, JavaScriptEncoder.UnsafeRelaxedJsonEscaping).Value,
            };
            Assert.DoesNotContain(seen, text => forms.Any(form => text.Contains(form, StringComparison.Ordinal)));
        }
    }
}
