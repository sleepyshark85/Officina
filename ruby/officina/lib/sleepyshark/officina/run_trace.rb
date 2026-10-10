# frozen_string_literal: true

require 'json'
require 'securerandom'

module Sleepyshark
  module Officina
    # One run's telemetry, and its id: it starts the run's span when it is made, under the span current on that thread,
    # if any, and makes the spans of the run's model and tool calls under it. Spans are parented explicitly and never
    # made current, as tool calls end on other threads. Every time comes from the agent's clock.
    class RunTrace
      # @return [String] the run's id, which its span and audit entries carry
      attr_reader :run
      # @return [OpenTelemetry::Trace::Span] the run's, which its audit entries name when no step's span does
      attr_reader :span
      # @return [Hash{String => String}] the attributes of every measurement: the agent, provider and model
      attr_reader :dimensions

      # @param input [String] the user's message
      def initialize(agent:, conversation:, input:)
        @agent = agent
        @telemetry = agent.telemetry
        @info = agent.model.info
        @run = SecureRandom.hex
        @dimensions = { 'gen_ai.agent.name' => agent.name, 'gen_ai.provider.name' => @info.provider,
                        'gen_ai.request.model' => @info.name }.compact.freeze
        parent = OpenTelemetry::Context.current
        @span = start_run(conversation, input, parent)
        @children = OpenTelemetry::Trace.context_with_span(@span, parent_context: parent)
      end

      # Traces the model call the block makes, which returns how it ended: its reply, the run's failure when it got
      # none, or nil when it was cancelled. The call's span ends with the block, also when the host leaves.
      # @return [Reply, Failed, nil] what the block returns
      def model_call
        # @type var outcome: (Reply | Failed)?
        # @type var call: ModelCallTrace?
        call = @call = ModelCallTrace.new(self, @info.name)
        outcome = yield
      ensure
        call&.finish(outcome)
      end

      # Notes what the model streamed in the call being made.
      def observe(event) = @call&.observe(event)

      # The span of a tool call, from its start; +tool+ is nil for a call of a tool the agent does not have.
      def tool_call(tool, call) = ToolCallTrace.new(self, tool, call)

      # Counts the run and ends its span: how it ended (nil when the host left it), its usage and its calls.
      def finish(result, usage, model_calls:, tool_calls:)
        kind, reason = ended(result)
        outcome = { 'officina.run.result' => kind, 'officina.run.reason' => reason }.compact
        add(:runs, 1, @dimensions.merge(outcome))
        @span.add_attributes(outcome)
        if result
          @span.add_attributes({ 'officina.run.model_calls' => model_calls, 'officina.run.tool_calls' => tool_calls,
                                 **usage_attributes(usage), **answer(result) })
        end
        stop_run(result, reason)
      end

      # Counts an audit entry the sink failed to write, and marks the span of the step it records.
      def audit_failed(span, kind)
        attributes = { 'officina.audit.kind' => pascal(kind) }
        add(:audit_failures, 1, @dimensions.merge(attributes))
        span.add_event('officina.audit.failed', attributes:, timestamp: now)
      end

      # What the call traces share.

      # Starts a span under the run's.
      def start(name, attributes:, kind: :internal)
        @telemetry.start_span(name, parent: @children, kind:, attributes:, at: now)
      end

      def add(counter, value, attributes) = @telemetry.add(counter, value, attributes)
      def record(histogram, value, attributes) = @telemetry.record(histogram, value, attributes)
      def now = @agent.clock.call

      # The span attributes of the usage and its cost.
      def usage_attributes(usage)
        { 'officina.usage.cost' => cost(usage).to_f,
          'gen_ai.usage.input_tokens' => usage.input + usage.cache_read + usage.cache_write,
          'gen_ai.usage.output_tokens' => usage.output, 'gen_ai.usage.cache_read.input_tokens' => usage.cache_read,
          'gen_ai.usage.cache_creation.input_tokens' => usage.cache_write }
      end

      # @return [BigDecimal] nothing when the model's price is not known
      def cost(usage) = @info.price&.cost(usage) || BigDecimal(0)

      # The content attribute of the text, without the agent's secrets, only when the host opted in: as one message of
      # the role in the semantic conventions' format when a role is given.
      def content(name, text, role: nil)
        return {} unless @telemetry.content?

        text = JSON.generate([{ role:, parts: [{ type: 'text', content: text }] }]) if role
        { name => @agent.redact(text) }
      end

      # Ends the span, marked failed with the error type and its description, without secrets, when there is one.
      def stop(span, error_type = nil, description = nil)
        if error_type
          span.set_attribute('error.type', error_type)
          span.status = OpenTelemetry::Trace::Status.error(@agent.redact(description.to_s))
        end
        span.finish(end_timestamp: now)
      end

      private

      def start_run(conversation, input, parent)
        attributes = { 'gen_ai.operation.name' => 'invoke_agent', 'gen_ai.conversation.id' => conversation.id,
                       'officina.run.id' => @run, **@dimensions,
                       **content('gen_ai.input.messages', input, role: 'user') }
        name = ['invoke_agent', @agent.name].compact.join(' ')
        @telemetry.start_span(name, parent:, kind: :internal, attributes:, at: now)
      end

      # Ends the run's span, marked failed when the run failed.
      def stop_run(result, reason)
        case result
        in Failed(detail:) then stop(@span, reason, detail)
        else stop(@span)
        end
      end

      def answer(result)
        case result
        in Completed(text:) then content('gen_ai.output.messages', text, role: 'assistant')
        else {}
        end
      end

      def ended(result)
        case result
        in Completed then ['completed']
        in Stopped(reason:) then ['stopped', pascal(reason)]
        in Failed(reason:) then ['failed', pascal(reason)]
        in nil then ['abandoned']
        end
      end

      # A symbol as .NET names its enum value, such as ModelError for :model_error.
      def pascal(symbol) = symbol.to_s.split('_').map(&:capitalize).join
    end
    private_constant :RunTrace
  end
end
