# frozen_string_literal: true

require 'digest'

module Sleepyshark
  module Officina
    # What an agent is: a model, instructions and optionally tools. It is frozen once built, so any number of runs, in
    # any number of threads, may share it.
    class Agent
      # Every character .NET's default JSON encoder escapes: all but printable ASCII, and " & ' + < > ` \.
      ESCAPED = /[^\x20-\x7E]|["&'+<>`\\]/
      SHORT = { '\\' => '\\\\', "\b" => '\\b', "\t" => '\\t', "\n" => '\\n', "\f" => '\\f', "\r" => '\\r' }.freeze
      private_constant :ESCAPED, :SHORT

      # @return [_Model]
      attr_reader :model
      # @return [String] frozen for every conversation: nothing per user, run or date goes there
      attr_reader :instructions
      # @return [Array<Tool>] sorted by name
      attr_reader :tools
      # @return [String] a hash of everything in the prefix that reaches the model (its settings, the instructions and
      #   the tools), which a conversation binds on its first append. It is the hash every implementation of Officina
      #   computes, so a conversation one stored resumes in another.
      attr_reader :fingerprint
      # @return [_Approver, nil] answers approval requests; without one, runs are unattended and a call that needs
      #   approval is denied
      attr_reader :approver
      # @return [_AuditSink, nil] where the audit trail goes; without one there is none, and nothing else changes
      attr_reader :audit_sink
      # @return [String, nil] names the agent in the audit trail; it is not sent to the model
      attr_reader :name
      # @return [#call] returns the current Time, for the audit trail's times and durations
      attr_reader :clock

      # @param model [_Model]
      # @param instructions [String]
      # @param tools [Array<Tool>] in any order
      # @param approver [_Approver, nil]
      # @param audit_sink [_AuditSink, nil]
      # @param name [String, nil]
      # @param secrets [Array<String>] values that must never reach tool results, the events that show tool calls, the
      #   result's text and detail or the audit trail, such as a tool's database password; redacted there, as written
      #   and as a JSON string may escape them
      # @param clock [#call, nil] the real time unless given
      # @raise [Error] when the instructions are blank or two tools share a name
      def initialize(model:, instructions:, tools: [], approver: nil, audit_sink: nil, name: nil, secrets: [],
                     clock: nil)
        @tools = sorted(tools)
        @model = model
        @instructions = given(instructions)
        @fingerprint = prefix_fingerprint(model.settings)
        @approver = approver
        @audit_sink = audit_sink
        @name = name && -name
        @secrets = Secrets.new(secrets)
        @clock = clock || -> { Time.now }
        freeze
      end

      # @return [Tool, nil] the tool of that name
      def tool(name) = @tools.find { |tool| tool.name == name }

      # @return [String] the text with every form of the agent's secrets replaced by "[redacted]"
      def redact(text) = @secrets.redact(text)

      # Runs the agent on the conversation with the user's message: calls the model, and again after each reply that
      # asks for tools, until a reply ends the run. Yields each event to the block as it happens, the host's chance to
      # show progress and to save the conversation after each append. Leaving the block early (+break+, an exception)
      # ends the run without a result; while tools run, it cancels the run's cancellation and waits for them to stop.
      #
      # A reply's tool calls run on a thread of the run's own, read calls each on a thread, while the run yields their
      # events; the approver and the tools' handlers are called on those threads.
      #
      # The message and run context enter the conversation only with the reply that answers them, so a run that gets
      # none leaves the conversation as it was. A stateless run passes a new conversation and drops it afterwards.
      # @param conversation [Conversation]
      # @param input [String] the user's message
      # @param context [String, nil] the run context, appended after the message as an operator message
      # @param cancel [Cancellation, nil] the host's way to stop the run
      # @return [Completed, Stopped, Failed] how the run ended, after the last event
      # @raise [Error] when the message or the context is blank, or another run is using the conversation
      def run(conversation, input, context: nil, cancel: nil, &on_event)
        raise Error, 'A run needs a message' if input.strip.empty?
        raise Error, 'A run context cannot be blank' if context&.strip&.empty?

        conversation.hold do |append|
          RunEngine.new(agent: self, conversation:, append:, cancel: cancel || Cancellation.new, on_event:)
                   .run(input, context)
        end
      end

      private

      def given(instructions)
        raise Error, 'An agent needs instructions' if instructions.strip.empty?

        -instructions
      end

      def sorted(tools)
        twin = tools.map(&:name).tally.find { |_, count| count > 1 }
        raise Error, "Two tools are named #{twin.first}" if twin

        tools.sort_by(&:name).freeze
      end

      # The bytes every implementation hashes, which must never change: {"model":…,"instructions":…,"tools":[{"name":…,
      # "description":…,"inputSchema":…},…]}, with the strings escaped as .NET's default JSON encoder escapes them and
      # each schema as given.
      def prefix_fingerprint(settings)
        tools = @tools.map do |tool|
          %({"name":#{string(tool.name)},"description":#{string(tool.description)},"inputSchema":#{tool.input_schema}})
        end
        Digest::SHA256.hexdigest(
          %({"model":#{string(settings)},"instructions":#{string(@instructions)},"tools":[#{tools.join(',')}]})
        )
      end

      # A JSON string as .NET writes it: short escapes for the backslash and five control characters, and each other
      # escaped character as its UTF-16 code units in upper-case hex. Invalid UTF-8 becomes U+FFFD.
      def string(text)
        escaped = text.scrub.gsub(ESCAPED) do |char|
          SHORT[char] || char.encode(Encoding::UTF_16BE).unpack('n*').map { |unit| format('\\u%04X', unit) }.join
        end
        %("#{escaped}")
      end
    end
  end
end
