# frozen_string_literal: true

require 'test_helper'
require_relative 'mcp_sources'

# Runs of an agent with MCP tools: their calls go through the tool pipeline, each run connects the source, and a
# server lost before or during a run fails the run or its calls. ToolSourceCancelTest has cancelled runs.
class ToolSourceRunTest < Minitest::Test
  include McpSources

  cover 'Sleepyshark::Officina::Mcp*'

  LONG = FakeTool.new(name: 'long', handler: ->(_) { 'x' * 70_000 })

  def test_mcp02_an_mcp_tool_input_is_validated_and_its_call_approved_before_it_reaches_the_server
    fake = FakeServer.new(tools: [ECHO])
    with_source(fake, allow('echo', kind: :read, needs_approval: true)) do |source|
      approver = Officina::Testing::ScriptedApprover.new(false)
      reply = Model.tool_use(call('1', 'fake__echo', '{"text":5}'), call('2', 'fake__echo', '{"text":"hi"}'))
      conversation = Officina::Conversation.new

      agent(source, reply, Model.text('Sorry.'), approver:).run(conversation, 'Go')

      assert_match(%r{\AThe input does not match the tool's schema:\n/text: }, results(conversation)[0].content)
      assert_equal [true, true], results(conversation).map(&:error?)
      assert_equal ['2'], approver.asked.map(&:id)
      assert_empty fake.calls
    end
  end

  def test_mcp02_an_mcp_write_is_audited_before_it_runs_its_result_cut_and_its_call_reported
    with_source(FakeServer.new(tools: [LONG]), allow('long')) do |source|
      sink = Sink.new
      events = []
      conversation = Officina::Conversation.new

      agent(source, Model.tool_use(call('1', 'fake__long', '{}')), Model.text('Done.'), audit_sink: sink)
        .run(conversation, 'Go') { events << it }

      assert_equal %i[tool_started tool_ended], sink.entries.map(&:kind).grep(/\Atool_(started|ended)/)
      assert_match(/\Ax{64000}\n\[Truncated: the result had 70000 characters/, results(conversation)[0].content)
      assert_equal %w[ToolCallStarted ToolCallFinished],
                   events.map { it.class.name.split('::').last }.grep(/\AToolCall/)
    end
  end

  def test_mcp04_a_server_down_at_the_start_of_a_run_fails_it_clearly_and_the_next_run_reconnects
    fake = FakeServer.new(tools: [ECHO])
    with_source(fake, allow('echo')) do |source|
      sink = Sink.new
      agent = agent(source, Model.text('Back.'), audit_sink: sink)
      conversation = Officina::Conversation.new
      down, gone = while_down(fake, source, agent, conversation)

      assert_equal [:tool_source_unavailable, Officina::Completed], [down.reason, agent.run(conversation, 'Hi').class]
      assert_match(/\AThe tool source fake is not available: MCP server fake could not be reached: /, down.detail)
      assert_equal [2, Officina::ToolFailure.new(message: 'MCP server fake is not connected')],
                   [conversation.messages.size, gone], 'the failed run appended nothing, and left no client'
      assert_equal %w[connected disconnected failed connected], changes(sink).map(&:first)
      assert(changes(sink)[1..2].all? { |_, detail| detail.start_with?('MCP server fake could not be reached: ') })
    end
  end

  def test_mcp04_an_http_server_that_fails_mid_run_gives_error_results_and_the_run_goes_on
    fake = FakeServer.new(tools: [FakeTool.new(name: 'down', handler: ->(_) { fake.go_down.then { '' } }), ECHO])
    with_source(fake, allow('down'), allow('echo')) do |source|
      sink = Sink.new
      conversation = Officina::Conversation.new
      reply = Model.tool_use(call('1', 'fake__down', '{}'), call('2', 'fake__echo', '{"text":"hi"}'))

      result = agent(source, reply, Model.text('Sorry.'), audit_sink: sink).run(conversation, 'Go')
      lost = results(conversation)

      assert_instance_of Officina::Completed, result
      assert_equal [true, true], lost.map(&:error?)
      assert(lost.all? { it.content.start_with?('MCP server fake could not be reached: ') })
      assert_equal [['connected', nil], ['disconnected', lost[0].content]], changes(sink)
    end
  end

  def test_mcp04_a_stdio_server_that_exits_mid_run_gives_an_error_result_and_the_next_run_starts_it_again
    source = Mcp::ToolSource.new(stdio_server, allowed: [allow('crash'), allow('echo')])
    sink = Sink.new
    agent = agent(source, Model.tool_use(call('1', 'fs__echo', '{"text":"hi"}'), call('2', 'fs__crash', '{}')),
                  Model.text('Sorry.'), Model.tool_use(call('3', 'fs__crash', '{}')), Model.text('Again.'),
                  audit_sink: sink)
    conversation = Officina::Conversation.new

    2.times { agent.run(conversation, 'Go') }
    answers = [*conversation.messages[2].blocks, *conversation.messages[6].blocks].map(&:tool_result)
    crashed = 'MCP server fs could not be reached: it closed its connection: out of memory'

    assert_equal([[false, 'hi'], [true, crashed], [true, crashed]], answers.map { [it.error?, it.content] })
    assert_equal [['connected', nil], ['disconnected', crashed], ['connected', nil], ['disconnected', crashed]],
                 changes(sink), "the second run's server is lost too, and recorded"
  ensure
    source&.close
  end

  private

  # A run, and a call of the source's tool outside any run, while fake is down.
  def while_down(fake, source, agent, conversation)
    fake.go_down
    [agent.run(conversation, 'Hi'), source.tools.first.invoke('{"text":"hi"}', Officina::Cancellation.new)]
  ensure
    fake.come_back_up
  end
end
