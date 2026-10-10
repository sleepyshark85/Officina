# frozen_string_literal: true

require 'io/wait'
require 'json'
require 'socket'

# The Claude API, faked on a local TCPServer: it answers each request with the next of the responses it was given,
# and records what each request sent. Responses stream chunked, so a reply cut off without its last chunk is a dropped
# connection, as the client sees one.
class FakeApi
  # One answer: an HTTP status, headers and a body, and how it ends: +:close+ after the body's last chunk, +:cut+
  # closing the connection without it, or +:hold+ keeping the connection open without it until the client closes it.
  Response = Data.define(:status, :headers, :body, :ending)
  # A request as it arrived: the path it was sent to, its headers, with lower-case names, and its body.
  Request = Data.define(:target, :headers, :body)
  # Seconds a held response waits for the client to close the connection.
  HOLD = 1

  # A reply that streams the server-sent events given, each a JSON string.
  def self.sse(*events, ending: :close)
    body = events.map { "event: #{JSON.parse(it).fetch('type')}\ndata: #{it}\n\n" }.join
    Response.new(status: 200, headers: { 'content-type' => 'text/event-stream' }, body:, ending:)
  end

  # A reply that streams a recorded file of server-sent events.
  def self.recorded(body)
    Response.new(status: 200, headers: { 'content-type' => 'text/event-stream' }, body:, ending: :close)
  end

  # An error response of the API's shape.
  def self.error(status, type, message, retry_after: nil)
    headers = { 'content-type' => 'application/json' }
    headers['retry-after'] = retry_after if retry_after
    Response.new(status:, headers:, body: JSON.generate(type: 'error', error: { type:, message: }), ending: :close)
  end

  def initialize(*responses)
    @responses = Thread::Queue.new(responses)
    @responses.close
    @requests = Thread::Queue.new
    @closes = Thread::Queue.new
    @server = TCPServer.new('127.0.0.1', 0)
    @thread = Thread.new { serve }
  end

  def url = "http://127.0.0.1:#{@server.addr[1]}"

  # Stops serving and waits for the server's thread.
  def close
    @server.close
    @thread.join
  end

  # The requests received so far, in order.
  def requests
    received = []
    received << @requests.pop until @requests.empty?
    received.each { @requests << it }
    received
  end

  # The bodies of the requests received so far, as sent.
  def bodies = requests.map(&:body)

  # Whether the client closed the connection of the next held response within HOLD seconds; waits until it is known.
  def closed_by_client? = @closes.pop

  private

  def serve
    while (response = @responses.pop)
      client = @server.accept
      begin
        @requests << read(client)
        write(client, response)
      ensure
        client.close
      end
    end
  rescue IOError, Errno::EBADF
    # The test closed the server before every response was asked for.
  end

  def read(client)
    headers = {}
    target = client.gets.split[1]
    while (line = client.gets) && line != "\r\n"
      name, value = line.split(':', 2)
      headers[name.downcase] = value.strip
    end
    Request.new(target:, headers:, body: client.read(Integer(headers.fetch('content-length'))).force_encoding('UTF-8'))
  end

  def write(client, response)
    head = response.headers.map { |name, value| "#{name}: #{value}\r\n" }.join
    client.write("HTTP/1.1 #{response.status} Status\r\n#{head}transfer-encoding: chunked\r\nconnection: close\r\n\r\n")
    body = response.body.b
    client.write("#{body.bytesize.to_s(16)}\r\n#{body}\r\n") unless body.empty?
    case response.ending
    when :close then client.write("0\r\n\r\n")
    # The client sends nothing more, so the connection turns readable only when the client closes it.
    when :hold then @closes << !client.wait_readable(HOLD).nil?
    end
  end
end
