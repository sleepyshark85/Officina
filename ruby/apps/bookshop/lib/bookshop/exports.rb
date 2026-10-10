# frozen_string_literal: true

module Bookshop
  # The export tools: those of the reference filesystem MCP server, which the compose file's filesystem service runs in
  # Docker over Streamable HTTP, and which sees only the exports folder, /projects/exports. The allow-list holds what an
  # export needs: writing a file, with approval, and listing the folder, marked read, as a server's tools are writes
  # unless the host says otherwise.
  module Exports
    Mcp = Sleepyshark::Officina::Mcp
    # The server's name, which prefixes its tools' names.
    NAME = 'filesystem'
    ALLOWED = [
      Mcp::AllowedTool.new(name: 'write_file', needs_approval: true),
      Mcp::AllowedTool.new(name: 'list_directory', kind: :read)
    ].freeze
    # The compose file's export server.
    COMPOSE_URL = 'http://localhost:18800/mcp'
    private_constant :Mcp, :NAME, :ALLOWED, :COMPOSE_URL

    # Connects to the export server BOOKSHOP_EXPORTS names, its MCP endpoint, the compose file's if not set, and pins
    # its allowed tools.
    #
    # @param env [#fetch]
    # @return [Sleepyshark::Officina::Mcp::ToolSource, nil] nil when BOOKSHOP_EXPORTS is empty: the assistant then
    #   cannot export
    # @raise [SettingError] when BOOKSHOP_EXPORTS is not an http or https URL
    # @raise [ExportServerError] when the server cannot be reached or lacks a tool of the allow-list
    def self.from(env)
      url = env.fetch('BOOKSHOP_EXPORTS', COMPOSE_URL)
      return if url.empty?

      Mcp::ToolSource.new(Mcp::Server.new(name: NAME, url:), allowed: ALLOWED)
    rescue ArgumentError => e # the transport's refusal of the URL
      raise SettingError, "BOOKSHOP_EXPORTS #{url.inspect} cannot be used: #{e.message}"
    rescue Mcp::Error => e
      raise ExportServerError, "The export server at #{url} is not available: #{e.message}\nStart it with " \
                               './start.sh (or pwsh -File start.ps1) in apps/BookshopAssistant, or set ' \
                               'BOOKSHOP_EXPORTS to its endpoint, or to nothing to go without exports.'
    end
  end
  private_constant :Exports
end
