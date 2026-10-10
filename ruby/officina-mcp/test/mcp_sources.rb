# frozen_string_literal: true

require 'rbconfig'
require 'sleepyshark/officina/mcp'

# What the tests of MCP tool sources share: the test kit's fake server, over Streamable HTTP on a local port or over
# stdio as a child process, and agents of its tools on a scripted model.
module McpSources
  Officina = Sleepyshark::Officina
  Mcp = Officina::Mcp
  FakeServer = Officina::Testing::FakeMcpServer
  FakeTool = Officina::Testing::FakeMcpTool
  Model = Officina::Testing::ScriptedModel
  Sink = Officina::Testing::RecordingAuditSink
  ECHO = FakeTool.new(name: 'echo', handler: ->(input) { input.fetch('text') })
  # The test kit's fake server over stdio, started without gems or Bundler.
  STDIO = [RbConfig.ruby, '--disable-gems', '-I', File.expand_path('../../officina-testing/lib', __dir__),
           File.join(__dir__, 'fixtures', 'stdio_server.rb')].freeze

  private

  def allow(name, **) = Mcp::AllowedTool.new(name:, **)

  def server(url) = Mcp::Server.new(name: 'fake', url:)

  def stdio_server = Mcp::Server.new(name: 'fs', command: STDIO, env: { 'RUBYOPT' => nil })

  def call(id, name, input) = Model.tool_use_block(id, name, input)

  # Serves fake over HTTP while the block runs, and yields a source of its allowed tools, closed afterwards.
  def with_source(fake, *allowed)
    fake.serve_http do |url|
      source = Mcp::ToolSource.new(server(url), allowed:)
      yield source
    ensure
      source&.close
    end
  end

  def agent(source, *replies, **)
    Officina::Agent.new(model: Model.new(*replies), instructions: 'You help.', tools: source.tools, **)
  end

  # The results the calls of a conversation's first reply got.
  def results(conversation) = conversation.messages.fetch(2).blocks.map(&:tool_result)

  # The source changes the trail recorded, each its outcome and detail.
  def changes(sink) = sink.entries.select { it.kind == :tool_source }.map { [it.outcome, it.detail] }
end
