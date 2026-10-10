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

      # @param model [_Model]
      # @param instructions [String]
      # @param tools [Array<Tool>] in any order
      # @raise [Error] when the instructions are blank or two tools share a name
      def initialize(model:, instructions:, tools: [])
        raise Error, 'An agent needs instructions' if instructions.strip.empty?

        @tools = tools.sort_by(&:name).freeze
        duplicate = @tools.map(&:name).tally.find { |_name, count| count > 1 }&.first
        raise Error, "Two tools are named #{duplicate}" if duplicate

        @model = model
        @instructions = -instructions
        @fingerprint = prefix_fingerprint(model.settings)
        freeze
      end

      # Runs the agent on the conversation with the user's message: calls the model, and again after each reply that
      # asks for tools, until a reply ends the run. Yields each event to the block as it happens, the host's chance to
      # show progress and to save the conversation after each append. Leaving the block early (+break+, an exception)
      # ends the run without a result.
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

      # The bytes every implementation hashes, which must never change: {"model":…,"instructions":…,"tools":[{"name":…,
      # "description":…,"inputSchema":…},…]}, with the strings escaped as .NET's default JSON encoder escapes them and
      # each schema as given.
      def prefix_fingerprint(settings)
        tools = @tools.map do |tool|
          name = DotnetJson.string(tool.name)
          description = DotnetJson.string(tool.description)
          %({"name":#{name},"description":#{description},"inputSchema":#{tool.input_schema}})
        end
        model = DotnetJson.string(settings)
        instructions = DotnetJson.string(@instructions)
        Digest::SHA256.hexdigest(%({"model":#{model},"instructions":#{instructions},"tools":[#{tools.join(',')}]}))
      end
    end
  end
end
