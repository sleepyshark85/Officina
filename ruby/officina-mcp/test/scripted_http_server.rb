# frozen_string_literal: true

require 'json'
require 'socket'

# An HTTP server on a local port that answers each connection, whatever it asks, with the next of the answers it is
# given, as raw text: for what the test kit's fake server never sends, such as answers that are not HTTP or results a
# client must refuse.
module ScriptedHttpServer
  ACCEPTED = "HTTP/1.1 202 Accepted\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"

  # The HTTP answer with a JSON body.
  def self.json(body)
    "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: #{body.bytesize}\r\n" \
      "Connection: close\r\n\r\n#{body}"
  end

  # The HTTP answer with the response to request id: its result, or with error: its error.
  def self.response(id, result = nil, error: nil)
    json(JSON.generate({ 'jsonrpc' => '2.0', 'id' => id, 'result' => result, 'error' => error }.compact))
  end

  # The answers to a client's handshake: its initialize request (id 1), then its notification.
  HANDSHAKE = [response(1, { 'protocolVersion' => '2025-06-18', 'capabilities' => {},
                             'serverInfo' => { 'name' => 'scripted', 'version' => '1.0.0' } }), ACCEPTED].freeze

  # Serves answers, one connection each, while the block runs, and yields the endpoint's URL; returns how many it
  # answered. A connection once they have run out is closed unanswered, so a client asking for more fails rather
  # than waits.
  def self.serve(answers)
    listener = TCPServer.new('127.0.0.1', 0)
    left = answers.dup
    server = Thread.new { serve_all(listener, left) }
    begin
      yield "http://127.0.0.1:#{listener.addr[1]}/mcp"
    ensure
      listener.close
      server.join
    end
    answers.size - left.size
  end

  def self.serve_all(listener, left)
    loop { answer(listener.accept, left.shift) }
  rescue IOError
    # The listener was closed: serving is over.
    nil
  end

  def self.answer(connection, text)
    return unless text

    headers = connection.each_line("\r\n").take_while { it != "\r\n" }
    length = headers.find { it.downcase.start_with?('content-length:') }.to_s.split(':').last.to_i
    # The whole request is read first: closing a connection with some of it unread would reset it.
    connection.read(length)
    connection.write(text)
  ensure
    connection.close
  end
  private_class_method :serve_all, :answer
end
