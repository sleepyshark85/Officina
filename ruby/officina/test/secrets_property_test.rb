# frozen_string_literal: true

require 'test_helper'
require_relative 'support/traced_runs'

# What must never happen, whatever the secrets and the forms a text gives them: a secret reaching an event (but the
# appended messages and the streamed text, by design), telemetry or the audit trail, or redaction leaving any of it.
class SecretsPropertyTest < Minitest::Test
  include Sleepyshark::Officina
  include TracedRuns

  cover 'Sleepyshark::Officina*'

  # The secrets' characters: none of "[redacted]"'s, and no letter or digit, which the names, ids and numbers of
  # telemetry hold, but in a fixed core none of them holds, around which some secrets are made.
  CHARS = ['/', '<', '"', '\\', 'é', '😀', '€'].freeze
  CORE = 'Zq7Kx9'
  # The characters between the secrets, which no form of a secret holds.
  FILLERS = ['#', '!', '%', ' '].freeze
  # A secret: its characters (by index), whether it is made around the core, and the characters after the core.
  SECRET = Pbt.tuple(Pbt.array(Pbt.integer(min: 0, max: 6), min: 1, max: 4), Pbt.boolean,
                     Pbt.array(Pbt.integer(min: 0, max: 6), max: 2))
  # A piece of the text: a filler, which secret, and in which form.
  PIECE = Pbt.tuple(Pbt.array(Pbt.integer(min: 0, max: 3), min: 1, max: 3), Pbt.integer(min: 0, max: 3),
                    Pbt.integer(min: 0, max: 4))
  # A session: the secrets, the text's pieces, whether the model's last reply fails, and whether telemetry has text.
  SESSION = Pbt.tuple(Pbt.array(SECRET, min: 1, max: 4), Pbt.array(PIECE, min: 1, max: 6), Pbt.boolean, Pbt.boolean)

  def test_test07_evt03_generated_secrets_never_reach_results_events_telemetry_or_the_trail
    Pbt.assert do
      Pbt.property(SESSION) do |drawn_secrets, pieces, model_fails, content|
        secrets = drawn_secrets.map { secret(*it) }
        text = pieces.map { |filler, which, form| chars(FILLERS, filler) + form(secrets[which % secrets.size], form) }
                     .join
        fillers = pieces.map { chars(FILLERS, it.first) }.join

        seen, redacted = session(secrets, text, model_fails, content)

        redacted.each { assert_equal fillers, it.gsub('[redacted]', ''), "Redacting #{text} left more than fillers" }
        forms = secrets.flat_map { [it, JSON.generate(it)[1..-2]] }

        assert_empty forms.product(seen + redacted).select { |form, out| out.include?(form) }, 'A secret got out'
      end
    end
  end

  private

  def secret(before, core, after)
    core ? "#{chars(CHARS, before.take(2))}#{CORE}#{chars(CHARS, after)}" : chars(CHARS, before)
  end

  def chars(table, indices) = indices.map { table.fetch(it) }.join

  # The secret as written, or inside a JSON string: as Ruby writes it, with <, > and & escaped as Go does, with every
  # character beyond ASCII escaped in upper case as .NET does, or with every slash escaped.
  def form(secret, kind)
    inner = JSON.generate(secret)[1..-2]
    case kind
    when 0 then secret
    when 1 then inner
    when 2 then inner.gsub(/[<>&]/) { format('\u%04x', it.ord) }
    when 3 then inner.gsub(/[^\x00-\x7F]/) { it.encode('UTF-16BE').unpack('n*').map { format('\u%04X', it) }.join }
    else inner.gsub('/', '\/')
    end
  end

  # Runs an agent whose search echoes the text, whose lookup fails with it, whose order is denied with it as the
  # reason, and whose model then answers or fails with it: everything the host and the record got, and the texts
  # redaction made, which hold only the fillers once each "[redacted]" is taken out.
  def session(secrets, text, model_fails, content)
    collector = Collector.new(content:)
    sink = Testing::RecordingAuditSink.new
    seen = []
    result = agent(collector, sink, secrets, text, model_fails).run(Conversation.new, 'Go') do |event|
      seen.concat(strings(event)) unless event in ConversationAppended | TextDelta
    end
    ended = sink.entries.select { it.kind == :tool_ended && it.call_id != '3' }
    redacted = [*ended.map { it.detail.delete_prefix('The tool failed: ') }, model_fails ? result.detail : result.text]
    [[*seen, *strings(sink.entries), collector.dump], redacted]
  end

  def agent(collector, sink, secrets, text, model_fails)
    input = JSON.generate({ title: text })
    tools = [tool('search') { |_, _| text }, tool('lookup') { |_, _| raise text },
             tool('order', kind: :write, needs_approval: true) { |_, _| 'Ordered.' }]
    model = Model.new(Model.tool_use(call('1', 'search', input), call('2', 'lookup', input), call('3', 'order', input)),
                      model_fails ? [RuntimeError.new(text)] : Model.text(text))
    traced(collector, model, tools:, secrets:, audit_sink: sink, approver: Testing::ScriptedApprover.new(text))
  end

  # Every string a value holds.
  def strings(value)
    case value
    in String then [value]
    in Data then strings(value.to_h.values)
    in Array then value.flat_map { strings(it) }
    else []
    end
  end
end
