# frozen_string_literal: true

require 'net/http'
require 'open3'
require_relative 'docker'

# The export server the export tests share: the reference filesystem MCP server over Streamable HTTP, built from the
# application's exports-server image, which the .NET and Go tests build too, and started with Docker once per run. The
# tests write files of their own names, and read them back from its container.
module ExportServer
  CONTEXT = File.expand_path('../../../../apps/BookshopAssistant/exports-server', __dir__)
  # Go's tests build it under this name too, so each run reuses Docker's layer cache.
  IMAGE = 'bookshop-exports-server:test'
  # Unique per run, so runs side by side, or the compose file's own server, do not clash.
  CONTAINER = "officina-ruby-exports-test-#{Process.pid}".freeze
  # The folder the server sees, which the compose file mounts and here is the container's own.
  FOLDER = '/projects/exports'
  # Seconds the server may take to start once built.
  STARTUP = 60

  class << self
    # The server's MCP endpoint, on the server started first if it is not yet.
    def url = @url ||= start

    # The text of the file the server's folder holds by that name, nil when it holds none.
    def read(name)
      text, _, status = Open3.capture3('docker', 'exec', CONTAINER, 'cat', "#{FOLDER}/#{name}")
      text if status.success?
    end

    private

    def start
      Docker.run('build', '--quiet', '--tag', IMAGE, CONTEXT)
      Docker.start(CONTAINER, '--publish', '127.0.0.1::8000', IMAGE)
      # The filesystem server starts with each MCP session, and refuses a folder that does not exist.
      Docker.run('exec', CONTAINER, 'mkdir', '-p', FOLDER)
      port = Docker.port(CONTAINER, '8000/tcp')
      wait_until_healthy(port)
      "http://127.0.0.1:#{port}/mcp"
    end

    def wait_until_healthy(port)
      deadline = Process.clock_gettime(Process::CLOCK_MONOTONIC) + STARTUP
      until healthy?(port)
        raise 'the export server did not start' if Process.clock_gettime(Process::CLOCK_MONOTONIC) > deadline

        sleep 0.2
      end
    end

    def healthy?(port)
      Net::HTTP.get_response('127.0.0.1', '/healthz', Integer(port)).is_a?(Net::HTTPSuccess)
    rescue SystemCallError, IOError
      false
    end
  end
end
