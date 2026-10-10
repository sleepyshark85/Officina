# frozen_string_literal: true

require 'test_helper'
require_relative 'clock'
require_relative 'database_server'
require_relative 'memory_telemetry'

# The audit table sink against the real database: what each row holds, in which order, with which trace and span, and
# a sink that cannot write reported in telemetry.
class AuditTableTest < Minitest::Test
  include DatabaseServer

  Officina = Sleepyshark::Officina
  ScriptedModel = Officina::Testing::ScriptedModel
  # Private to the application, which hands it only to the agent and the console.
  AuditTable = Bookshop.const_get(:AuditTable)
  PRICE = Officina::Price.new(input: BigDecimal('5'), output: BigDecimal('25'), cache_read: BigDecimal('0.5'),
                              cache_write: BigDecimal('6.25'), cache_write_hour: BigDecimal('10'))
  PRICED = Officina::ModelInfo.new(provider: 'scripted', name: 'scripted', price: PRICE)
  START = Time.utc(2026, 10, 10, 8)

  def setup
    super
    @database = Bookshop::Database.new(database_url)
    @telemetry = MemoryTelemetry.new
  end

  def teardown
    @database&.close
    super
  end

  def test_aud03_each_entry_is_a_row_in_order_with_its_run_and_the_trace_and_span_of_its_step
    conversation, = search_run

    rows = select_rows
    run, tool = %w[invoke_agent execute_tool].map { |name| @telemetry.spans.find { it.name.start_with?(name) } }

    assert_equal(%w[RunStarted ToolStarted ToolEnded RunEnded], rows.map { it['kind'] })
    assert_equal(%w[1 2 3 4], rows.map { it['sequence'] })
    assert_equal [[run.attributes['officina.run.id'], conversation.id, 'bookshop', run.hex_trace_id]],
                 rows.map { it.values_at('run', 'conversation', 'agent', 'trace_id') }.uniq
    assert_equal([run, tool, tool, run].map(&:hex_span_id), rows.map { it['span_id'] })
    assert_equal ['sam'], rows.map { it['memory_scope'] }.uniq
  end

  def test_aud03_a_call_and_the_run_end_keep_what_they_record
    search_run

    started, ended, run_ended = select_rows.drop(1)

    assert_equal ['search_books', 'c1', '{"title":"Winter Archive"}'], started.values_at('tool', 'call_id', 'input')
    assert_equal ['search_books', 'c1', 'ok', '00:00:01'], ended.values_at('tool', 'call_id', 'outcome', 'duration')
    assert_includes ended['detail'], '"title":"The Winter Archive"'
    assert_equal ['completed', '1000', '200', '3000', '0', '0.0115'],
                 run_ended.values_at('outcome', 'input_tokens', 'output_tokens', 'cache_read_tokens',
                                     'cache_write_tokens', 'cost')
  end

  def test_app16_a_conversations_entries_read_back_in_the_order_they_were_written
    conversation, = search_run
    search_run

    records = AuditTable.new(database: @database).entries(conversation.id)

    assert_equal %w[RunStarted ToolStarted ToolEnded RunEnded], records.map(&:kind)
    assert_equal [START, START, START + 1, START + 1], records.map(&:time)
    assert_equal [nil, nil, 1.0, nil], records.map(&:duration)
    assert_equal [Officina::Usage.new(input: 1000, output: 200, cache_read: 3000), BigDecimal('0.0115')],
                 records.last.to_h.values_at(:usage, :cost)
    assert_equal [], AuditTable.new(database: @database).entries('another')
  end

  def test_aud06_a_sink_that_cannot_reach_the_database_is_seen_in_telemetry_and_the_run_goes_on
    take_database_down

    _, result = run_agent(ScriptedModel.new(ScriptedModel.text('Hello.')))

    assert_instance_of Officina::Completed, result
    run = @telemetry.spans.find { it.name.start_with?('invoke_agent') }

    assert_equal [{ 'officina.audit.kind' => 'RunStarted' }, { 'officina.audit.kind' => 'RunEnded' }],
                 run.events.select { it.name == 'officina.audit.failed' }.map(&:attributes)
    assert_includes @telemetry.metric_names, 'officina.audit.failures'
  ensure
    bring_database_back
  end

  private

  # A run that searches the catalogue, then answers, with its usage.
  def search_run
    call = ScriptedModel.tool_use_block('c1', 'search_books', '{"title":"Winter Archive"}')
    usage = Officina::Usage.new(input: 1000, output: 200, cache_read: 3000)
    run_agent(ScriptedModel.new(ScriptedModel.tool_use(call), ScriptedModel.text('Twelve copies.', usage:),
                                info: PRICED))
  end

  # Runs the agent, auditing to the table, in Sam's memory, on a clock that moves only while the search runs, by a
  # second; returns the conversation and the result.
  def run_agent(model)
    clock = Clock.new(START)
    tools = Bookshop::Tools.all(shop).map { it.name == 'search_books' ? taking_a_second(it, clock) : it }
    tools << Officina::MemoryTool.new(Officina::HashMemoryStore.new)
    agent = Officina::Agent.new(name: 'bookshop', model:, instructions: 'Help the staff.', tools:,
                                audit_sink: AuditTable.new(database: @database), clock:,
                                telemetry: @telemetry.telemetry.officina)
    conversation = Officina::Conversation.new
    [conversation, agent.run(conversation, 'Do we have The Winter Archive?', memory_scope: 'sam') { nil }]
  end

  # The tool, which moves the clock on a second as it runs.
  def taking_a_second(tool, clock)
    Officina::Tool.new(name: tool.name, description: tool.description, input: Officina::Schema.new(tool.input_schema),
                       kind: tool.kind) do |input, cancel|
      clock.advance(1)
      tool.invoke(JSON.generate(input), cancel)
    end
  end

  # The audit table's rows, as text, in the order they were written.
  def select_rows
    connection = PG.connect(database_url)
    connection.exec('select * from audit order by id').to_a
  ensure
    connection&.close
  end
end
