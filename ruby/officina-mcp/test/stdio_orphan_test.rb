# frozen_string_literal: true

require 'test_helper'
require_relative 'stdio_connections'

# A stdio server that leaves a process behind, in a process group of its own, holding its pipes: the client neither
# waits for that process nor leaves anything running of its own. On Windows, where taskkill stops a killed server with
# what it started, and a pipe another thread reads is not known to close, these do not run.
class StdioOrphanTest < Minitest::Test
  include StdioConnections

  cover 'Sleepyshark::Officina::Mcp*'

  def setup
    skip 'Windows stops what a killed server started, and may not close a pipe another thread reads' if
      Gem.win_platform?
  end

  def test_mcp01_closing_ends_the_reads_of_pipes_a_process_the_server_started_still_holds
    with_orphan do |pid_file|
      client = connect('keep-errors', env: { 'FAKE_MCP_PID' => pid_file })
      client.close

      assert_equal 'MCP server fs could not be reached: the connection was closed',
                   assert_raises(Mcp::Error) { client.list_tools }.message
    end
  end

  # Each reading of the clock is half a second on, so the second the reason waits for the error output takes two polls.
  def test_mcp04_a_server_whose_error_output_is_still_held_raises_without_waiting_for_what_it_said
    with_orphan do |pid_file|
      error = assert_raises(Mcp::Error) do
        connect('keep-errors', 'complain', env: { 'FAKE_MCP_PID' => pid_file }, clock: stepping(0, 0.5))
      end

      assert_equal 'MCP server fs could not be reached: it closed its connection', error.message
    end
  end

  # The server is killed, but the output it can no longer write to stays open.
  def test_mcp04_a_server_that_stops_reading_while_its_output_is_still_held_raises_that_it_could_not_be_written_to
    with_orphan do |pid_file|
      error = assert_raises(Mcp::Error) do
        connect('keep-output', 'deaf', env: { 'FAKE_MCP_PID' => pid_file }, clock: stepping(0, 0.5))
      end

      assert_equal "MCP server fs could not be reached: it could not be written to: #{Errno::EPIPE.new.message}",
                   error.message
    end
  end

  private

  def with_orphan
    with_pid_file do |pid_file|
      yield pid_file
    ensure
      kill_orphan(pid_file)
    end
  end
end
