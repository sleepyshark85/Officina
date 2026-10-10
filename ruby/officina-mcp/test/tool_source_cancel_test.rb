# frozen_string_literal: true

require 'test_helper'
require_relative 'mcp_sources'

# Runs of an agent with MCP tools that the host cancels while the source connects or a call waits on the server: they
# stop as cancelled, and the server is not taken for lost.
class ToolSourceCancelTest < Minitest::Test
  include McpSources

  cover 'Sleepyshark::Officina::Mcp*'

  def test_agt05_a_run_cancelled_while_the_source_connects_stops_as_cancelled_and_records_no_failure
    fake = FakeServer.new(tools: [ECHO])
    with_source(fake, allow('echo')) do |source|
      sink = Sink.new
      agent = agent(source, Model.text('Hi.'), audit_sink: sink)
      cancel = Officina::Cancellation.new.tap(&:cancel)
      checking = agent.run(Officina::Conversation.new, 'Hi', cancel:)
      agent.run(Officina::Conversation.new, 'Hi')
      fake.go_down
      agent.run(Officina::Conversation.new, 'Hi')
      fake.come_back_up
      reconnecting = agent.run(Officina::Conversation.new, 'Hi', cancel:)

      assert_equal %i[cancelled cancelled], [checking.reason, reconnecting.reason]
      assert_equal %w[connected disconnected failed], changes(sink).map(&:first)
      assert_equal %w[initialize tools/list ping], fake.requests, 'only the run that was not cancelled pinged'
    end
  end

  def test_agt05_a_run_cancelled_during_an_mcp_call_gives_the_call_an_error_result_and_stops_as_cancelled
    cancel = Officina::Cancellation.new
    gate = Thread::Queue.new
    with_source(FakeServer.new(tools: [held(cancel, gate)]), allow('held')) do |source|
      sink = Sink.new
      conversation = Officina::Conversation.new

      result = agent(source, Model.tool_use(call('1', 'fake__held', '{}')), audit_sink: sink)
               .run(conversation, 'Go', cancel:)

      assert_equal [:cancelled, 3], [result.reason, conversation.messages.size], 'no model call followed the results'
      assert_equal([[true, 'MCP server fake, tools/call: cancelled']],
                   results(conversation).map { [it.error?, it.content] })
      assert_equal [['connected', nil]], changes(sink), 'the server was not taken for lost'
    ensure
      gate.close
    end
  end

  private

  # A tool whose call cancels the run, then waits for gate to close: the call is cancelled while the server holds it.
  def held(cancel, gate)
    FakeTool.new(name: 'held', handler: lambda { |_|
      cancel.cancel
      gate.pop.to_s
    })
  end
end
