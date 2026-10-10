# frozen_string_literal: true

require 'rbconfig'
require 'tmpdir'
require 'sleepyshark/officina/mcp'

# Connecting the MCP client over stdio to the test kit's fake server, run as a child process
# (test/fixtures/stdio_server.rb), for the stdio tests to include.
module StdioConnections
  Mcp = Sleepyshark::Officina::Mcp
  SERVER = File.join(__dir__, 'fixtures', 'stdio_server.rb')
  # The fake server needs only the test kit's own files and the standard library, so it starts without gems or
  # Bundler, in a few milliseconds.
  COMMAND = [RbConfig.ruby, '--disable-gems', '-I', File.expand_path('../../officina-testing/lib', __dir__),
             SERVER].freeze
  WITHOUT_BUNDLER = { 'RUBYOPT' => nil }.freeze

  private

  # A client of the fake server started with arguments, its environment added to env.
  def connect(*arguments, env: {}, **)
    Mcp.connect(Mcp::Server.new(name: 'fs', command: [*COMMAND, *arguments], env: WITHOUT_BUNDLER.merge(env)), **)
  end

  def with_client(*, **)
    client = connect(*, **)
    yield client
  ensure
    client&.close
  end

  def with_pid_file = Dir.mktmpdir { |directory| yield File.join(directory, 'pid') }

  # A clock that reads now, then each reading step seconds on.
  def stepping(now, step) = -> { now += step }

  # A process the server started is reaped by whoever adopted it once the server exited, which may take a moment: on
  # Linux, until then it is a zombie, which runs no more.
  def refute_running(pid_file)
    refute running?(Integer(File.read(pid_file))), "process #{File.read(pid_file)} is still running"
  end

  # The process in pid_file has stopped within a second.
  def assert_stops(pid_file)
    pid = Integer(File.read(pid_file))
    deadline = Process.clock_gettime(Process::CLOCK_MONOTONIC) + 1
    sleep 0.01 while running?(pid) && Process.clock_gettime(Process::CLOCK_MONOTONIC) < deadline

    refute running?(pid), "process #{pid} is still running"
  end

  # On Linux a killed process is a zombie until reaped, which kill(0) still finds; elsewhere kill(0) is enough.
  def running?(pid)
    Process.kill(0, pid)
    !File.directory?('/proc') || File.read("/proc/#{pid}/stat")[/\) (\S)/, 1] != 'Z'
  rescue Errno::ESRCH, Errno::ENOENT # reaped between the two checks
    false
  end

  # Kills the process in pid_file, which outlived the server that started it.
  def kill_orphan(pid_file)
    Process.kill(:KILL, Integer(File.read(pid_file))) if File.exist?(pid_file)
  rescue Errno::ESRCH
    nil
  end
end
