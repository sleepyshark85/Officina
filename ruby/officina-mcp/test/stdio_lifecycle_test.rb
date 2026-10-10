# frozen_string_literal: true

require 'test_helper'
require_relative 'cancellations'
require_relative 'stdio_connections'

# Starting and stopping a stdio server: no server, thread or pipe outlives the client.
class StdioLifecycleTest < Minitest::Test
  include StdioConnections

  cover 'Sleepyshark::Officina::Mcp*'

  def test_mcp04_a_server_silent_for_30_seconds_at_connect_raises_and_is_killed_and_reaped
    with_pid_file do |pid_file|
      # Once the server has started, each reading of the clock is 7 seconds on, so the wait for the answer, and then
      # for the exit, run out, and no reading falls on the 30 seconds exactly.
      now = 0
      clock = -> { File.exist?(pid_file) ? now += 7 : now }
      error = assert_raises(Mcp::Error) { connect('stubborn', env: { 'FAKE_MCP_PID' => pid_file }, clock:) }

      assert_equal 'MCP server fs did not answer within 30 seconds', error.message
      refute_running pid_file
    end
  end

  def test_mcp04_a_connect_cancelled_while_the_server_starts_leaves_no_server_running
    with_pid_file do |pid_file|
      error = assert_raises(Mcp::Error) do
        connect('silent', env: { 'FAKE_MCP_PID' => pid_file }, cancel: CancelledOnceExists.new(pid_file))
      end

      assert_equal 'MCP server fs, initialize: cancelled', error.message
      refute_running pid_file
    end
  end

  def test_mcp01_closing_stops_and_reaps_the_server_and_closes_its_pipes
    with_pid_file do |pid_file|
      # Files an earlier test left to the garbage collector are closed first, so none closes now and frees its number
      # for a pipe.
      GC.start
      before = open_files
      client = connect(env: { 'FAKE_MCP_PID' => pid_file })
      client.close
      error = assert_raises(Mcp::Error) { client.list_tools }

      assert_equal 'MCP server fs could not be reached: the connection was closed', error.message
      refute_running pid_file
      assert_empty open_files - before, 'the pipes to the server are closed'
    end
  end

  # The clock starts far from 0, and each reading is half a second on, so the 5 seconds a server has to exit take ten
  # polls.
  def test_mcp01_closing_lets_a_server_that_takes_a_moment_shut_down_by_itself
    Dir.mktmpdir do |directory|
      done = File.join(directory, 'done')
      connect('tidy', env: { 'FAKE_MCP_EXIT' => done }, clock: stepping(1_000, 0.5)).close

      assert_path_exists done, 'the server shut down by itself rather than being killed'
    end
  end

  # Once closing, the clock reads 5 seconds on from its first reading, and then stays there, so the 5 seconds run out
  # exactly at the first poll.
  def test_mcp01_closing_kills_a_server_still_running_5_seconds_after_its_input_ends
    with_pid_file do |pid_file|
      readings = nil
      client = connect('stuck', env: { 'FAKE_MCP_PID' => pid_file }, clock: -> { readings&.shift || 1_005 })
      readings = [1_000]
      client.close

      refute_running pid_file
    end
  end

  def test_mcp01_closing_stops_what_the_server_started_that_holds_its_output
    skip 'Windows has no process group to stop once the server has exited' if Gem.win_platform?
    with_pid_file do |pid_file|
      connect('parent', env: { 'FAKE_MCP_PID' => pid_file }).close

      # Killed, though maybe not yet gone.
      assert_stops pid_file
    end
  end

  private

  # The process's open files, on Linux; none elsewhere.
  def open_files = File.directory?('/proc/self/fd') ? Dir.children('/proc/self/fd').sort : []
end
