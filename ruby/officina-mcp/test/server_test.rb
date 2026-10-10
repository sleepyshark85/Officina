# frozen_string_literal: true

require 'test_helper'
require 'sleepyshark/officina/mcp'

# How the host says where an MCP server is.
class ServerTest < Minitest::Test
  cover 'Sleepyshark::Officina::Mcp*'

  Server = Sleepyshark::Officina::Mcp::Server

  def test_mcp01_a_server_is_reached_by_either_a_command_and_its_env_or_a_url_and_its_headers
    either = 'MCP server fs: give either a command or a url'
    meaning = 'MCP server fs: env is for a command, headers for a url'

    { {} => either, { command: ['server'], url: 'http://127.0.0.1/mcp' } => either,
      { url: 'http://127.0.0.1/mcp', env: {} } => meaning, { command: ['server'], headers: {} } => meaning }
      .each do |reach, message|
        assert_equal message, assert_raises(ArgumentError) { Server.new(name: +'fs', **reach) }.message
      end
  end

  def test_mcp01_a_server_holds_frozen_copies_of_what_it_was_given
    command = [+'server', +'--verbose']
    stdio = Server.new(name: +'fs', command:, env: { 'TOKEN' => +'secret', 'HOME' => nil })
    http = Server.new(name: 'web', url: +'http://127.0.0.1/mcp', headers: { 'Authorization' => +'Bearer t' })

    refute_predicate command.first, :frozen?, "the caller's strings stay as they were"
    assert_equal [%w[server --verbose], { 'TOKEN' => 'secret', 'HOME' => nil }, { 'Authorization' => 'Bearer t' }],
                 [stdio.command, stdio.env, http.headers]
    assert [stdio.name, stdio.command, *stdio.command, stdio.env, stdio.env['TOKEN'], http.url, http.headers,
            http.headers['Authorization']].all?(&:frozen?)
  end
end
