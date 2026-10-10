# frozen_string_literal: true

require 'json'

module Sleepyshark
  module Officina
    # An action the model may request. Its name, description and input schema reach the model, so they are part of
    # the prefix; its kind, approval need and handler do not. It is frozen once built.
    #
    # @example
    #   Tool.new(name: 'search_books', description: 'Searches the catalogue.', input: SearchBooks, kind: :read) do
    #     |input, cancel|
    #     catalogue.search(input.title)
    #   end
    class Tool
      KINDS = %i[read write].freeze

      # @return [String] unique among an agent's tools
      attr_reader :name
      # @return [String]
      attr_reader :description
      # @return [Symbol] +:read+ (a reply's read calls run concurrently) or +:write+ (a write runs alone, in call order,
      #   and only once its attempt is in the audit trail, when the agent has one)
      attr_reader :kind

      # @param input [Class, Schema] the input's type: a class from Input.define, whose value the handler gets, or a
      #   Schema, whose JSON value (as JSON.parse returns it) it gets
      # @param kind [Symbol] one of KINDS
      # @param needs_approval [Boolean] whether the agent's approver must approve each call before it runs
      # @yieldparam input [Data, Object] the call's input, valid against the schema
      # @yieldparam cancel [Cancellation] the run's, to stop at the next check once the host cancels
      # @yieldreturn [String, Object] the result for the model: a String as it is, anything else as JSON.generate
      #   writes it. An exception is the call's error result, with its message; the run goes on.
      # @raise [Error] when the name is blank, the kind unknown, the handler missing or the schema not an object's
      def initialize(name:, description:, input:, kind:, needs_approval: false, &handler)
        @input = input
        check(name, kind, handler)
        @name = -name
        @description = -description
        @kind = kind
        @needs_approval = needs_approval
        @handler = handler
        freeze
      end

      # @return [String] the JSON Schema of the input, which reaches the model, and the prefix fingerprint, as given
      def input_schema = @input.schema.to_s

      def needs_approval? = @needs_approval

      def write? = @kind == :write

      # Why the input cannot be given to the handler, said for the model, or nil when it can.
      # @param input [String] JSON text, as the model wrote it
      # @return [String, nil]
      def input_problem(input)
        problems = @input.schema.validate(JSON.parse(input, allow_duplicate_key: false))
        "The input does not match the tool's schema:\n#{problems.join("\n")}" unless problems.empty?
      rescue JSON::ParserError => e
        "The input is not valid JSON: #{e}"
      end

      # Runs the handler on the input.
      # @param input [String] JSON text that #input_problem accepts
      # @param cancel [Cancellation]
      # @return [String] the result for the model
      # @raise [StandardError] whatever the handler raises
      def invoke(input, cancel)
        # #input_problem has refused duplicate keys already.
        case @handler.call(@input.from_json(JSON.parse(input)), cancel)
        in String => text then text
        in output then JSON.generate(output)
        end
      end

      private

      def check(name, kind, handler)
        raise Error, 'A tool needs a name' unless name.match?(/\S/)
        raise Error, "Tool #{name}'s kind must be :read or :write, not #{kind.inspect}" unless KINDS.include?(kind)
        raise Error, "Tool #{name} needs a handler" unless handler
        raise Error, "The input schema of tool #{name} is not a JSON object" unless JSON.parse(input_schema) in Hash
      end
    end
  end
end
