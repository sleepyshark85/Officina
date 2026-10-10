# frozen_string_literal: true

require 'json'
require 'socket'

# An HTTP server on a local port that answers each connection, whatever it asks, with the next of the answers it is
# given, as raw text: for what the test kit's fake server never sends, such as answers that are not HTTP or results a
# client must refuse.
module ScriptedHttpServer
  ACCEPTED = "HTTP/1.1 202 Accepted\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"

  # The HTTP answer with body, of the given content type, if any, and other header lines.
  def self.answer(body, type: 'application/json', headers: [])
    lines = [*(type && "Content-Type: #{type}"), *headers, "Content-Length: #{body.bytesize}", 'Connection: close']
    "HTTP/1.1 200 OK\r\n#{lines.map { "#{it}\r\n" }.join}\r\n#{body}"
  end

  # The JSON-RPC response to request id: its result, or with error: its error.
  def self.message(id, result = nil, error: nil)
    JSON.generate({ 'jsonrpc' => '2.0', 'id' => id, 'result' => result, 'error' => error }.compact)
  end

  # The HTTP answer with the response to request id as a JSON body.
  def self.response(id, result = nil, error: nil) = answer(message(id, result, error:))

  # The result of initialize.
  INITIALIZED = { 'protocolVersion' => '2025-06-18', 'capabilities' => {},
                  'serverInfo' => { 'name' => 'scripted', 'version' => '1.0.0' } }.freeze

  # The answers to a client's handshake: its initialize request (id 1), then its notification.
  HANDSHAKE = [response(1, INITIALIZED), ACCEPTED].freeze

  # Serves answers, one connection each, while the block runs, and yields the endpoint's URL; returns the requests
  # it answered, each its header lines' names (in lower case) to their values. A connection once the answers have run
  # out is closed unanswered, so a client asking for more fails rather than waits.
  def self.serve(answers)
    listener = TCPServer.new('127.0.0.1', 0)
    left = answers.dup
    requests = []
    server = Thread.new { serve_all(listener, left, requests) }
    begin
      yield "http://127.0.0.1:#{listener.addr[1]}/mcp"
    ensure
      listener.close
      server.join
    end
    requests
  end

  def self.serve_all(listener, left, requests)
    loop { answer_one(listener.accept, left.shift, requests) }
  rescue IOError
    # The listener was closed: serving is over.
    nil
  end

  def self.answer_one(connection, text, requests)
    return unless text

    lines = connection.each_line("\r\n").take_while { it != "\r\n" }.drop(1)
    headers = lines.to_h do |line|
      name, value = line.chomp.split(': ', 2)
      [name.downcase, value]
    end
    # The whole request is read first: closing a connection with some of it unread would reset it.
    connection.read(headers.fetch('content-length', '0').to_i)
    connection.write(text)
    requests << headers
  ensure
    connection.close
  end
  private_class_method :serve_all, :answer_one
end
