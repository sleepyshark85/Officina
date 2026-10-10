# frozen_string_literal: true

# The test kit's fake MCP server over stdio, as a program the MCP tests start. Its tools: echo, upper, fail (an
# error result), crash (the process exits mid-call), cut (answers request 2 without its line end, and exits), stray
# (sends a response to a request never made before its own) and hang (never answers; exits when its input ends). With a
# file in FAKE_MCP_PID, it writes its process id there first. The first argument picks something else to be: complain
# (reads the first request, says why it cannot go on on its error output, and exits), deaf (reads the first request,
# stops reading and says so, then answers it and waits), flood (answers the first request with a line of exactly 16 MB,
# and the next with a longer one, then waits), mute (answers the handshake, then closes its output and error output
# and reads on), parent (starts a process that holds its input and output and waits, and writes that process's id to
# FAKE_MCP_PID instead), reverse (once connected, reads requests two at a time and answers the second first), silent
# (never answers, until its input ends), stubborn (never answers, and ignores the end of its input) or tidy (takes
# 0.2 s to shut down once its input ends, then writes the file in FAKE_MCP_EXIT).

require 'json'
require 'rbconfig'
require 'sleepyshark/officina/testing/fake_mcp_server'
require 'sleepyshark/officina/testing/fake_mcp_tool'

if ENV.key?('FAKE_MCP_PID')
  pid = ARGV.first == 'parent' ? Process.spawn(RbConfig.ruby, '--disable-gems', '-e', 'sleep') : Process.pid
  # Written aside and then moved, so the test never reads it half written.
  File.write("#{ENV.fetch('FAKE_MCP_PID')}.new", pid.to_s)
  File.rename("#{ENV.fetch('FAKE_MCP_PID')}.new", ENV.fetch('FAKE_MCP_PID'))
end
$stdout.sync = true

# The input, two lines (the handshake's request and notification) as they come, then each pair of lines in reverse.
Reversed = Data.define(:input) do
  def each_line(&)
    2.times { yield input.gets }
    input.each_line.each_slice(2) { |pair| pair.reverse_each(&) }
  end
end

# The response to the initialize request in line.
def initialized(line)
  request = JSON.parse(line)
  JSON.generate({ 'jsonrpc' => '2.0', 'id' => request['id'],
                  'result' => { 'protocolVersion' => request.dig('params', 'protocolVersion'), 'capabilities' => {},
                                'serverInfo' => { 'name' => 'fake', 'version' => '1.0.0' } } })
end

case ARGV.first
when 'complain'
  $stdin.gets
  # Indented, with a byte that is not UTF-8 and a blank line after it.
  $stderr.write("  configuration file missing \xFF\n\n")
when 'deaf'
  request = $stdin.gets
  $stdin.reopen(File::NULL)
  warn 'not listening'
  puts initialized(request)
  sleep
when 'flood'
  response = initialized($stdin.gets)
  puts response + (' ' * ((16 * 1024 * 1024) - response.bytesize))
  2.times { $stdin.gets }
  $stdout.write('x' * ((16 * 1024 * 1024) + 1))
  sleep
when 'mute'
  puts initialized($stdin.gets)
  $stdin.gets
  $stdout.reopen(File::NULL)
  $stderr.reopen(File::NULL)
  $stdin.read
when 'silent'
  $stdin.read
when 'stubborn'
  sleep
else
  tool = Sleepyshark::Officina::Testing::FakeMcpTool
  Sleepyshark::Officina::Testing::FakeMcpServer.new(
    tools: [
      tool.new(name: 'echo', handler: ->(input) { input.fetch('text') }),
      tool.new(name: 'upper', handler: ->(input) { input.fetch('text').upcase }),
      tool.new(name: 'fail', handler: ->(_) { raise 'it broke' }),
      tool.new(name: 'crash', handler: lambda { |_|
        warn 'out of memory'
        exit!(3)
      }),
      tool.new(name: 'stray', handler: lambda { |_|
        puts JSON.generate({ 'jsonrpc' => '2.0', 'id' => 999, 'result' => { 'content' => [] } })
        'after'
      }),
      tool.new(name: 'cut', handler: lambda { |_|
        $stdout.write(JSON.generate({ 'jsonrpc' => '2.0', 'id' => 2, 'result' => { 'content' => [] } }))
        exit!(0)
      }),
      tool.new(name: 'hang', handler: lambda { |_|
        $stdin.read
        exit!(0)
      })
    ]
  ).serve(ARGV.first == 'reverse' ? Reversed.new($stdin) : $stdin, $stdout)
  if ARGV.first == 'tidy'
    sleep 0.2
    File.write(ENV.fetch('FAKE_MCP_EXIT'), 'done')
  end
end
