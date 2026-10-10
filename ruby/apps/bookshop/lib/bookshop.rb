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
require_relative 'bookshop/catalogue_tools'
require_relative 'bookshop/customer_tools'
require_relative 'bookshop/order_tools'
require_relative 'bookshop/tools'
require_relative 'bookshop/chat_agent'
require_relative 'bookshop/approvals'
require_relative 'bookshop/session'
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

  # The composition root: builds the application from its boundaries and settings. Nothing connects to the database
  # or the model until the first reply needs it, nor to the dashboard until there is telemetry to send.
  #
  # @param input [IO] the staff member's lines
  # @param output [IO] where the console writes
  # @param model [Sleepyshark::Officina::_Model, nil] the chat agent's model; Claude (ChatAgent.claude) if nil
  # @param env [#fetch] the settings: BOOKSHOP_DATABASE, a PostgreSQL URL, the compose file's database if not set;
  #   BOOKSHOP_DASHBOARD, the telemetry dashboard /audit links to, the compose file's if not set
  # @param clock [#call] returns the current Time, for the run context, the audit trail and telemetry
  # @param telemetry [Telemetry, nil] where traces, metrics and logs go; OTLP to the compose file's dashboard if nil
  # @return [Application]
  # rubocop:disable-next Metrics/AbcSize, Metrics/MethodLength -- the composition root names every part in one place
  def self.build(input:, output:, model: nil, env: ENV, clock: -> { Time.now }, telemetry: nil)
    url = env.fetch('BOOKSHOP_DATABASE', COMPOSE_DATABASE)
    database = Database.new(url)
    model ||= ChatAgent.claude
    telemetry ||= Telemetry.otlp
    approvals = Approvals.new
    audit = AuditTable.new(database:, price: model.info.price)
    agent = Sleepyshark::Officina::Agent.new(
      name: 'bookshop', model:, instructions: ChatAgent::INSTRUCTIONS, tools: Tools.all(Shop.new(database:)),
      approver: approvals, audit_sink: audit, secrets: [Database.password(url)].compact, clock:,
      telemetry: telemetry.officina
    )
    view = AuditView.new(table: audit, dashboard: env.fetch('BOOKSHOP_DASHBOARD', COMPOSE_DASHBOARD))
    console = Console.new(agent:, approvals:, input:, output:, clock:, audit: view, telemetry:)
    Application.new(database:, telemetry:, console:)
  end
end
