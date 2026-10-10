# frozen_string_literal: true

require 'test_helper'
require_relative 'stdio_connections'

# A stdio server that cannot be started, or that breaks the connection: what the client says of it.
class StdioFailureTest < Minitest::Test
  include StdioConnections

  cover 'Sleepyshark::Officina::Mcp*'

  def test_mcp04_a_program_that_cannot_start_raises_clearly
    server = Mcp::Server.new(name: 'fs', command: ['officina-no-such-program'])
    error = assert_raises(Mcp::Error) { Mcp.connect(server) }

    assert_equal "MCP server fs could not be started (officina-no-such-program): #{Errno::ENOENT.new.message}",
                 error.message.delete_suffix(' - officina-no-such-program')
  end

  def test_mcp04_an_empty_command_raises_clearly
    error = assert_raises(Mcp::Error) { Mcp.connect(Mcp::Server.new(name: 'fs', command: [])) }

    assert_equal 'MCP server fs could not be started: its command is empty', error.message
  end

  def test_mcp04_a_message_of_16_mb_is_read_and_a_longer_one_loses_the_server_and_kills_it
    skip 'Moving 16 MB through a pipe takes seconds on Windows' if Gem.win_platform?
    with_pid_file do |pid_file|
      with_client('flood', env: { 'FAKE_MCP_PID' => pid_file }) do |client|
        error = assert_raises(Mcp::Error) { client.list_tools }

        assert_equal 'MCP server fs could not be reached: it sent a message longer than 16 MB', error.message
        assert_stops pid_file
      end
    end
  end

  def test_mcp04_a_server_that_stops_reading_is_killed_and_raises_with_what_it_said
    with_pid_file do |pid_file|
      error = assert_raises(Mcp::Error) { connect('deaf', env: { 'FAKE_MCP_PID' => pid_file }) }

      assert_equal 'MCP server fs could not be reached: it closed its connection: not listening', error.message
      refute_running pid_file
    end
  end

  def test_mcp04_a_server_that_closes_its_output_stays_lost_though_it_still_reads
    with_client('mute') do |client|
      messages = Array.new(2) { assert_raises(Mcp::Error) { client.call_tool('echo', { 'text' => 'hi' }) }.message }

      assert_equal ['MCP server fs could not be reached: it closed its connection'] * 2, messages
    end
  end

  def test_mcp04_an_answer_the_server_cut_short_as_it_exited_is_no_answer
    with_client do |client|
      error = assert_raises(Mcp::Error) { client.call_tool('cut', {}) }

      assert_equal 'MCP server fs could not be reached: it closed its connection', error.message
    end
  end

  # The server closes its output before it says why on its error output, so the reason is read once that ends.
  def test_mcp04_a_server_that_exits_while_connecting_raises_with_what_it_said
    error = assert_raises(Mcp::Error) { connect('complain') }

    assert_equal "MCP server fs could not be reached: it closed its connection: configuration file missing \uFFFD",
                 error.message
  end

  # A thousand lines, then one of 9,010 bytes: of a line longer than 4,096 bytes, its last piece is kept.
  def test_mcp04_a_server_that_says_much_as_it_exits_raises_with_the_end_of_its_last_line
    error = assert_raises(Mcp::Error) { connect('rant') }

    assert_equal "MCP server fs could not be reached: it closed its connection: #{'x' * 808}the reason", error.message
  end
end
