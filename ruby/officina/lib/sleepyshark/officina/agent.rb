# frozen_string_literal: true

require 'digest'

module Sleepyshark
  module Officina
    # What an agent is: a model, instructions and optionally tools. It is frozen once built, so any number of runs, in
    # any number of threads, may share it.
    class Agent
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
      # @return [#call] returns the current Time, for the audit trail's and telemetry's times and durations
      attr_reader :clock
      # @return [Telemetry] where the agent's traces and metrics go
      attr_reader :telemetry
      # @return [ContextManagement, nil] how the model's provider shortens a long conversation; nil for not at all
      attr_reader :context_management

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
      # @param telemetry [Telemetry, nil] none unless given: no spans or metrics
      # @param context_management [ContextManagement, nil] part of the prefix; it needs the provider's support
      #   (ModelInfo). Without compaction, a run that fills the model's context window stops with +:context_full+
      # @raise [Error] when the instructions are blank, two tools share a name, or the model's provider cannot manage
      #   the context as asked
      def initialize(model:, instructions:, tools: [], approver: nil, audit_sink: nil, name: nil, secrets: [],
                     clock: nil, telemetry: nil, context_management: nil)
        keep_prefix(model, instructions, tools, context_management)
        @approver = approver
        @audit_sink = audit_sink
        @name = name && -name
        @secrets = Secrets.new(secrets)
        @clock = clock || -> { Time.now }
        @telemetry = telemetry || Telemetry.new
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
      #
      # A conversation saved while a reply's tools ran, before their results were appended, has calls without results:
      # the run first answers each with an error result saying it was interrupted, and audits it.
      # @param conversation [Conversation]
      # @param input [String] the user's message
      # @param context [String, nil] the run context, appended after the message as an operator message
      # @param cancel [Cancellation, nil] the host's way to stop the run
      # @param budget [Budget, nil] limits on the run; none unless given. A host may give each run what is left of a
      #   session's budget
      # @return [Completed, Stopped, Failed] how the run ended, with what it used, after the last event
      # @raise [Error] when the message or the context is blank, a cost budget is given for a model with no price, or
      #   another run is using the conversation
      def run(conversation, input, context: nil, cancel: nil, budget: nil, &on_event)
        check_run(input, context, budget)
        conversation.hold do |append|
          trace = RunTrace.new(agent: self, conversation:, input:)
          RunEngine.new(agent: self, conversation:, append:, cancel: cancel || Cancellation.new, on_event:, trace:,
                        budget:).run(input, context)
        end
      end

      private

      def check_run(input, context, budget)
        raise Error, 'A run needs a message' unless input.match?(/\S/)
        raise Error, 'A run context cannot be blank' unless context.nil? || context.match?(/\S/)
        raise Error, 'A cost budget needs a model with a price' if budget&.cost && !@model.info.price
      end

      # Keeps what the prefix holds, and its fingerprint.
      def keep_prefix(model, instructions, tools, context_management)
        @tools = sorted(tools)
        @model = model
        @instructions = given(instructions)
        @context_management = supported(context_management, model.info)
        @fingerprint = prefix_fingerprint(model.settings)
      end

      def supported(context_management, info)
        return unless context_management

        if context_management.compact_at && !info.compacts?
          raise Error, "The model's provider does not compact conversations"
        end
        if context_management.clear_tool_results && !info.clears_tool_results?
          raise Error, "The model's provider does not clear old tool results"
        end

        context_management
      end

      def given(instructions)
        raise Error, 'An agent needs instructions' unless instructions.match?(/\S/)

        -instructions
      end

      def sorted(tools)
        twin = tools.map(&:name).tally.find { |_, count| count > 1 }
        raise Error, "Two tools are named #{twin.first}" if twin

        tools.sort_by(&:name).freeze
      end

      # The bytes every implementation hashes, which must never change: {"model":…,"instructions":…,"tools":[{"name":…,
      # "description":…,"inputSchema":…},…],"contextManagement":{…}}, with the strings escaped as .NET's default JSON
      # encoder escapes them, each schema as given, and the context management only when it asks for something.
      def prefix_fingerprint(settings)
        tools = @tools.map do |tool|
          name = DotnetJson.string(tool.name)
          description = DotnetJson.string(tool.description)
          %({"name":#{name},"description":#{description},"inputSchema":#{tool.input_schema}})
        end
        model = DotnetJson.string(settings)
        instructions = DotnetJson.string(@instructions)
        Digest::SHA256.hexdigest(%({"model":#{model},"instructions":#{instructions},"tools":[#{tools.join(',')}]) +
                                 "#{@context_management&.fingerprint}}")
      end
    end
  end
end
