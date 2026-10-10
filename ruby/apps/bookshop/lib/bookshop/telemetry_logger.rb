# frozen_string_literal: true

module Bookshop
  # A Logger whose records go to OpenTelemetry instead of a device, each in the trace of the span current where it is
  # logged, with its severity as OpenTelemetry numbers it.
  class TelemetryLogger < ::Logger
    # Logger's severities, in order from DEBUG, as OpenTelemetry names and numbers them.
    NAMES = %w[DEBUG INFO WARN ERROR FATAL UNKNOWN].freeze
    NUMBERS = [5, 9, 13, 17, 21, 0].freeze
    private_constant :NAMES, :NUMBERS

    # @param logger [OpenTelemetry::SDK::Logs::Logger] where the records go
    def initialize(logger)
      super(nil)
      @logger = logger
    end

    # Emits a record, as Logger#add does: the message, else the block's value, else the program name.
    # @return [true]
    def add(severity, message = nil, progname = nil) # rubocop:disable Naming/PredicateMethod -- Logger#add's name
      severity ||= UNKNOWN
      return true if severity < level

      message = block_given? ? yield : progname if message.nil?
      @logger.on_emit(timestamp: Time.now, severity_text: NAMES.fetch(severity),
                      severity_number: NUMBERS.fetch(severity), body: message.to_s)
      true
    end
  end
  private_constant :TelemetryLogger
end
