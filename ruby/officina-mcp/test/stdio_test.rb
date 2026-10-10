# frozen_string_literal: true

require 'rbconfig'
require 'test_helper'
require 'tmpdir'
require 'sleepyshark/officina/mcp'
require_relative 'cancellations'

# The MCP client over stdio, against the test kit's fake server run as a child process (test/fixtures/stdio_server.rb).
class StdioTest < Minitest::Test
  cover 'Sleepyshark::Officina::Mcp*'

  Mcp = Sleepyshark::Officina::Mcp
  SERVER = File.join(__dir__, 'fixtures', 'stdio_server.rb')
  # The fake server needs only the test kit's own files and the standard library, so it starts without gems or
  # Bundler, in a few milliseconds.
  COMMAND = [RbConfig.ruby, '--disable-gems', '-I', File.expand_path('../../officina-testing/lib', __dir__),
             SERVER].freeze
  WITHOUT_BUNDLER = { 'RUBYOPT' => nil }.freeze

  def test_mcp01_a_stdio_server_agrees_on_the_protocol_lists_every_page_of_its_tools_and_runs_them
    with_client do |client|
      tools = client.list_tools
      echo = { 'type' => 'object', 'properties' => { 'text' => { 'type' => 'string' } } }

      assert_equal %w[echo upper fail crash hang], tools.map(&:name)
      assert_equal Mcp::Tool.new(name: 'echo', description: 'The echo tool.', input_schema: echo), tools.first
      assert_equal Mcp::CallResult.new(text: 'HI', error: false), client.call_tool('upper', { 'text' => 'hi' })
    end
  end

  def test_mcp01_requests_from_several_threads_each_get_their_own_response
    # The server answers each pair of requests in reverse, so a response handed to any request but its own shows.
    with_client('reverse') do |client|
      texts = Array.new(8) { |n| "call #{n}" }
      results = texts.map { |text| Thread.new { client.call_tool('echo', { 'text' => text }).text } }.map(&:value)

      assert_equal texts, results
    end
  end

  def test_mcp04_a_tool_that_fails_gives_an_error_result
    with_client do |client|
      assert_equal Mcp::CallResult.new(text: 'it broke', error: true), client.call_tool('fail', {})
    end
  end

  def test_mcp04_a_request_the_server_refuses_raises_its_error
    with_client do |client|
      error = assert_raises(Mcp::Error) { client.call_tool('missing', {}) }

      assert_equal 'MCP server fs answered tools/call with an error: Method or tool not found.', error.message
      assert_equal 'HI', client.call_tool('upper', { 'text' => 'hi' }).text, 'the connection is kept'
    end
  end

  def test_mcp04_a_server_that_exits_mid_call_raises_with_what_it_said_and_stays_lost
    with_client do |client|
      messages = [['crash', {}], ['echo', { 'text' => 'hi' }]].map do |name, arguments|
        assert_raises(Mcp::Error) { client.call_tool(name, arguments) }.message
      end

      assert_equal ['MCP server fs could not be reached: it closed its connection: out of memory'] * 2, messages
    end
  end

  def test_mcp04_a_program_that_cannot_start_raises_clearly
    server = Mcp::Server.new(name: 'fs', command: ['officina-no-such-program'])
    error = assert_raises(Mcp::Error) { Mcp.connect(server) }

    assert_match(/\AMCP server fs could not be started \(officina-no-such-program\): /, error.message)
  end

  def test_mcp04_a_server_that_sends_a_message_longer_than_16_mb_is_lost
    skip 'Moving 16 MB through a pipe takes seconds on Windows' if Gem.win_platform?
    error = assert_raises(Mcp::Error) { connect('flood') }

    assert_equal 'MCP server fs could not be reached: it sent a message longer than 16 MB', error.message
  end

  def test_mcp04_a_server_that_exits_while_connecting_raises_with_what_it_said
    error = assert_raises(Mcp::Error) { connect('complain') }

    assert_equal 'MCP server fs could not be reached: it closed its connection: configuration file missing',
                 error.message
  end

  def test_mcp04_a_server_silent_for_30_seconds_at_connect_raises_and_is_killed_and_reaped
    with_pid_file do |pid_file|
      # Once the server has started, each reading of the clock is 10 seconds on, so the wait for the answer, and
      # then for the exit, run out.
      now = 0
      clock = -> { File.exist?(pid_file) ? now += 10 : now }
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

  def test_mcp04_a_call_cancelled_while_it_waits_raises
    with_client do |client|
      error = assert_raises(Mcp::Error) { client.call_tool('hang', {}, cancel: CancelledAfter.new(2)) }

      assert_equal 'MCP server fs, tools/call: cancelled', error.message
    end
  end

  def test_mcp01_closing_stops_and_reaps_the_server
    with_pid_file do |pid_file|
      connect(env: { 'FAKE_MCP_PID' => pid_file }).close

      refute_running pid_file
    end
  end

  private

  def connect(*arguments, env: {}, **)
    Mcp.connect(Mcp::Server.new(name: 'fs', command: [*COMMAND, *arguments], env: WITHOUT_BUNDLER.merge(env)), **)
  end

  def with_client(*)
    client = connect(*)
    yield client
  ensure
    client&.close
  end

  def with_pid_file = Dir.mktmpdir { |directory| yield File.join(directory, 'pid') }

  def refute_running(pid_file) = assert_raises(Errno::ESRCH) { Process.kill(0, Integer(File.read(pid_file))) }
end
