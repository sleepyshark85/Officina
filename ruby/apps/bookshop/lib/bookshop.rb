# frozen_string_literal: true

require 'bigdecimal'
require 'connection_pool'
require 'json'
require 'logger'
require 'opentelemetry-exporter-otlp'
require 'opentelemetry-exporter-otlp-logs'
require 'opentelemetry-exporter-otlp-metrics'
require 'opentelemetry-logs-sdk'
require 'opentelemetry-metrics-sdk'
require 'opentelemetry-sdk'
require 'pg'
require 'securerandom'
require 'sleepyshark/officina'
require 'sleepyshark/officina/claude'
require 'sleepyshark/officina/mcp'
require_relative 'bookshop/book'
require_relative 'bookshop/book_filter'
require_relative 'bookshop/customer'
require_relative 'bookshop/order_line'
require_relative 'bookshop/order'
require_relative 'bookshop/order_summary'
require_relative 'bookshop/refused_error'
require_relative 'bookshop/database'
require_relative 'bookshop/stock'
require_relative 'bookshop/catalogue'
require_relative 'bookshop/customers'
require_relative 'bookshop/orders'
require_relative 'bookshop/shop'
require_relative 'bookshop/money'
require_relative 'bookshop/shop_tool'
require_relative 'bookshop/catalogue_tools'
require_relative 'bookshop/customer_tools'
require_relative 'bookshop/order_tools'
require_relative 'bookshop/tools'
require_relative 'bookshop/chat_agent'
require_relative 'bookshop/approvals'
require_relative 'bookshop/spent'
require_relative 'bookshop/setting_error'
require_relative 'bookshop/export_server_error'
require_relative 'bookshop/exports'
require_relative 'bookshop/budgets'
require_relative 'bookshop/stored_session'
require_relative 'bookshop/session_listing'
require_relative 'bookshop/session_summary'
require_relative 'bookshop/transcript'
require_relative 'bookshop/session_changed_error'
require_relative 'bookshop/session_not_saved'
require_relative 'bookshop/session_store'
require_relative 'bookshop/summarizer'
require_relative 'bookshop/summaries'
require_relative 'bookshop/session'
require_relative 'bookshop/session_commands'
require_relative 'bookshop/staff_memory'
require_relative 'bookshop/audit_record'
require_relative 'bookshop/audit_table'
require_relative 'bookshop/audit_view'
require_relative 'bookshop/telemetry_logger'
require_relative 'bookshop/telemetry'
require_relative 'bookshop/terminal'
require_relative 'bookshop/interrupts'
require_relative 'bookshop/reply_view'
require_relative 'bookshop/console'
require_relative 'bookshop/application'

# Bookshop Assistant, Officina's reference application: a console chatbot for the staff of a bookshop.
module Bookshop
  # The compose file's database (apps/BookshopAssistant/compose.yaml), with its demo password.
  COMPOSE_DATABASE = 'postgres://bookshop:shelf-demo-41@localhost:5432/bookshop'
  # The compose file's telemetry dashboard.
  COMPOSE_DASHBOARD = 'http://localhost:18888'

  # The composition root: builds the application from its boundaries and settings. It connects to the export server
  # and reads its tools; nothing connects to the database or the model until the first reply needs it, nor to the
  # dashboard until there is telemetry to send.
  #
  # @param input [IO] the staff member's lines
  # @param output [IO] where the console writes
  # @param model [Sleepyshark::Officina::_Model, nil] the chat agent's model; Claude (the mode's) if nil
  # @param summarizer [Sleepyshark::Officina::_Model, nil] the session summarizer's model, which needs a price; Claude
  #   (Summarizer.claude) unless given, and nil for none: sessions then keep no title
  # @param memory [Sleepyshark::Officina::_MemoryStore] what the assistant remembers for each staff member: files under
  #   data/memory in the working directory unless given, the folder .NET's and Go's applications keep it in
  # @param env [#fetch] the settings: BOOKSHOP_DATABASE, a PostgreSQL URL, the compose file's database if not set;
  #   BOOKSHOP_DASHBOARD, the telemetry dashboard /audit links to, the compose file's if not set;
  #   BOOKSHOP_REPLY_BUDGET, a reply's budget in US dollars, $0.50 if not set or empty; BOOKSHOP_EXPORTS, the export
  #   server's MCP endpoint, the compose file's if not set, none if empty
  # @param clock [#call] returns the current Time, for the run context, the audit trail and telemetry
  # @param telemetry [Telemetry, nil] where traces, metrics and logs go; OTLP to the compose file's dashboard if nil
  # @param demo [Boolean] demo mode: compaction and clearing early enough to see in a short session, which the console
  #   says at the start, and Claude's caches kept five minutes
  # @return [Application]
  # @raise [SettingError] when BOOKSHOP_REPLY_BUDGET is not an amount above zero, or BOOKSHOP_EXPORTS not a URL
  # @raise [ExportServerError] when the export server cannot be reached
  # rubocop:disable-next Metrics/AbcSize, Metrics/MethodLength -- the composition root names every part in one place
  def self.build(input:, output:, model: nil, summarizer: Summarizer.claude,
                 memory: Sleepyshark::Officina::FileMemoryStore.new(File.join('data', 'memory')), env: ENV,
                 clock: -> { Time.now }, telemetry: nil, demo: false)
    budgets = Budgets.from(env)
    # Before the telemetry starts, which a failure here would leave running.
    exports = Exports.from(env)
    url = env.fetch('BOOKSHOP_DATABASE', COMPOSE_DATABASE)
    database = Database.new(url)
    mode = ChatAgent.mode(demo:)
    model ||= mode.claude
    telemetry ||= Telemetry.otlp
    approvals = Approvals.new
    audit = AuditTable.new(database:)
    tools = [*Tools.all(Shop.new(database:)), Sleepyshark::Officina::MemoryTool.new(memory), *exports&.tools]
    agent = Sleepyshark::Officina::Agent.new(
      name: 'bookshop', model:, instructions: ChatAgent::INSTRUCTIONS, tools:,
      approver: approvals, audit_sink: audit, secrets: [Database.password(url)].compact, clock:,
      telemetry: telemetry.officina, context_management: mode.context_management
    )
    view = AuditView.new(table: audit, dashboard: env.fetch('BOOKSHOP_DASHBOARD', COMPOSE_DASHBOARD))
    console = Console.new(agent:, approvals:, input:, output:, clock:, store: SessionStore.new(database:),
                          budgets:, audit: view, memory: StaffMemory.new(memory), telemetry:, demo:,
                          summarizer: summarizer && Summarizer.new(model: summarizer, clock:, telemetry:))
    Application.new(database:, exports:, telemetry:, console:)
  end
end
