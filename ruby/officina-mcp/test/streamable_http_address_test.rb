# frozen_string_literal: true

require 'socket'
require 'test_helper'
require 'sleepyshark/officina/mcp'
require_relative 'scripted_http_server'

# Which Streamable HTTP URLs the client reaches, and how: the host it names, IPv4 or IPv6, and TLS for https.
class StreamableHttpAddressTest < Minitest::Test
  cover 'Sleepyshark::Officina::Mcp*'

  Mcp = Sleepyshark::Officina::Mcp
  Scripted = ScriptedHttpServer

  def test_mcp01_a_server_is_reached_at_an_ipv6_address
    Scripted.serve(Scripted.results({ 'content' => [] }), host: '::1') do |url|
      client = connect(url)

      assert_equal '', echo(client)
    ensure
      client&.close
    end
  rescue Errno::EADDRNOTAVAIL
    skip 'This machine has no IPv6 loopback'
  end

  # Not localhost, which a client that left out the host would reach. Linux and Windows answer on all of 127.0.0.0/8.
  def test_mcp01_a_server_is_reached_at_the_host_its_url_names
    Scripted.serve(Scripted.results({ 'content' => [] }), host: '127.0.0.2') do |url|
      client = connect(url)

      assert_equal '', echo(client)
    ensure
      client&.close
    end
  rescue Errno::EADDRNOTAVAIL
    skip 'This machine answers only on 127.0.0.1'
  end

  def test_mcp01_an_https_url_is_reached_over_tls
    listener = TCPServer.new('127.0.0.1', 0)
    # The first byte a TLS client sends is that of a handshake record.
    first = Thread.new { listener.accept.then { |connection| connection.read(1).tap { connection.close } } }
    error = assert_raises(Mcp::Error) { connect("https://127.0.0.1:#{listener.addr[1]}/mcp") }

    assert_equal "\x16".b, first.value
    assert_match(/\AMCP server web could not be reached: /, error.message)
  ensure
    listener.close
  end

  def test_mcp01_a_url_that_is_not_http_has_no_host_or_cannot_be_read_is_refused
    urls = ['ftp://127.0.0.1/mcp', 'http://:8080/mcp', 'http:/mcp', 'http://exa mple/mcp']
    messages = urls.map { |url| assert_raises(ArgumentError) { connect(url) }.message }

    assert_equal(urls.map { "MCP server web: #{it} is not an http or https URL" }, messages)
  end

  private

  def echo(client) = client.call_tool('echo', { 'text' => 'hi' }).text

  def connect(url) = Mcp.connect(Mcp::Server.new(name: 'web', url:))
end
