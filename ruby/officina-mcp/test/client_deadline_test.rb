# frozen_string_literal: true

require 'test_helper'
require 'sleepyshark/officina/mcp'
require_relative 'cancellations'
require_relative 'scripted_http_server'

# The 30 seconds the handshake, a ping and a tool list each have, on a clock the test sets, and a handshake cancelled
# while it waits, over a scripted HTTP server.
class ClientDeadlineTest < Minitest::Test
  cover 'Sleepyshark::Officina::Mcp*'

  Mcp = Sleepyshark::Officina::Mcp
  Scripted = ScriptedHttpServer

  def test_mcp04_a_connect_already_30_seconds_late_sends_nothing
    requests = Scripted.serve(Scripted::HANDSHAKE) do |url|
      error = assert_raises(Mcp::Error) { Mcp.connect(server(url), clock: readings(0, 30)) }

      assert_equal 'MCP server web did not answer within 30 seconds', error.message
    end

    assert_empty requests
  end

  # Cancelled from its second check on, or late from the clock's third reading on: either comes while initialize is
  # answered, or as the notification that follows is about to be sent.
  def test_mcp04_a_connect_cancelled_or_late_once_initialize_is_answered_raises
    [{ cancel: CancelledAfter.new(2) }, { clock: readings(0, 10, 30) }].each do |options|
      Scripted.serve(Scripted::HANDSHAKE) do |url|
        assert_raises(Mcp::Error) { Mcp.connect(server(url), **options) }
      end
    end
  end

  def test_mcp04_a_ping_is_answered_with_nothing_and_one_already_30_seconds_late_is_not_sent
    values = [0]
    clock = -> { values.size > 1 ? values.shift : values.first }
    requests = Scripted.serve(Scripted.results({})) do |url|
      client = Mcp.connect(server(url), clock:)

      assert_nil client.ping
      values.replace([0, 30])

      assert_equal 'MCP server web did not answer within 30 seconds', assert_raises(Mcp::Error) { client.ping }.message
      refute_predicate client, :lost?
    ensure
      client&.close
    end

    assert_equal 3, requests.size, 'the handshake and one ping'
  end

  def test_mcp04_a_tool_list_already_30_seconds_late_is_not_sent
    values = [0]
    requests = Scripted.serve(Scripted.results({})) do |url|
      client = Mcp.connect(server(url), clock: -> { values.size > 1 ? values.shift : values.first })
      values.replace([0, 30])

      assert_equal 'MCP server web did not answer within 30 seconds',
                   assert_raises(Mcp::Error) { client.list_tools }.message
    ensure
      client&.close
    end

    assert_equal 2, requests.size, 'the handshake only'
  end

  private

  # A clock that reads each of values in turn, then the last one on.
  def readings(*values) = -> { values.size > 1 ? values.shift : values.first }

  def server(url) = Mcp::Server.new(name: 'web', url:)
end
