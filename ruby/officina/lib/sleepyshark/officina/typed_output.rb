# frozen_string_literal: true

require 'json'

module Sleepyshark
  module Officina
    # Reads a completed run's reply as the agent's typed output. There is no correction round: structured output
    # already holds the reply to the schema, so a reply that is not the output fails the run.
    module TypedOutput
      # @param result [Completed, Stopped, Failed] the run's
      # @param output [Class, nil] the agent's output type, a class from Input.define
      # @return [Completed, Stopped, Failed] a completed result with the reply's value as its output, or
      #   +:invalid_output+ saying why the reply is not one; any other result, or any result when there is no output
      #   type, as it is
      def self.read(result, output)
        case result
        in Completed if output then parse(result, output)
        else result
        end
      end

      def self.parse(result, output)
        value = JSON.parse(result.text)
        problems = output.schema.validate(value)
        return result.with(output: output.from_json(value)) if problems.empty?

        invalid("The output does not match its schema: #{problems.join('; ')}")
      rescue JSON::ParserError => e
        # The parser's message quotes the reply, which must not reach the detail: telemetry shows it.
        invalid("The output is not JSON: it breaks off at line #{e.line}, column #{e.column}")
      end

      def self.invalid(detail) = Failed.new(reason: :invalid_output, detail:)
      private_class_method :parse, :invalid
    end
    private_constant :TypedOutput
  end
end
