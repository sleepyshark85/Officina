# frozen_string_literal: true

# The test kit's fake MCP server over stdio, as a program the MCP tests start. Its tools: echo, upper, fail (an
# error result), crash (the process exits mid-call) and hang (never answers; exits when its input ends). With a file
# in FAKE_MCP_PID, it writes its process id there first. The first argument picks something else to be:
# complain (reads the first request, says why it cannot go on on its error output, and exits), silent (never
# answers, until its input ends) or stubborn (never answers, and ignores the end of its input).

require 'sleepyshark/officina/testing/fake_mcp_server'
require 'sleepyshark/officina/testing/fake_mcp_tool'

if ENV.key?('FAKE_MCP_PID')
  # Written aside and then moved, so the test never reads it half written.
  File.write("#{ENV.fetch('FAKE_MCP_PID')}.new", Process.pid.to_s)
  File.rename("#{ENV.fetch('FAKE_MCP_PID')}.new", ENV.fetch('FAKE_MCP_PID'))
end
$stdout.sync = true

case ARGV.first
when 'complain'
  $stdin.gets
  warn 'configuration file missing'
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
      tool.new(name: 'hang', handler: lambda { |_|
        $stdin.read
        exit!(0)
      })
    ]
  ).serve($stdin, $stdout)
end
