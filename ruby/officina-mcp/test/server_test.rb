# frozen_string_literal: true

require 'test_helper'
require 'sleepyshark/officina/mcp'

# How the host says where an MCP server is.
class ServerTest < Minitest::Test
  cover 'Sleepyshark::Officina::Mcp*'

  Server = Sleepyshark::Officina::Mcp::Server

  def test_mcp01_a_server_is_reached_by_either_a_command_or_a_url
    [{}, { command: ['server'], url: 'http://127.0.0.1/mcp' }].each do |reach|
      assert_raises(ArgumentError) { Server.new(name: 'fs', **reach) }
    end
  end

  def test_mcp01_a_server_holds_frozen_copies_of_what_it_was_given
    command = [+'server', +'--verbose']
    server = Server.new(name: 'fs', command:, env: { 'TOKEN' => +'secret' })

    refute_predicate command.first, :frozen?, "the caller's strings stay as they were"
    assert(server.command.frozen? && server.command.all?(&:frozen?) && server.env.frozen?)
  end
end
