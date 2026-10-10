# frozen_string_literal: true

module Sleepyshark
  module Officina
    # One model call's span and metrics, from its start to its end, retries included: what it streamed tells it its
    # usage, retries and time to first token.
    class ModelCallTrace
      def initialize(run, model)
        @run = run
        @started = run.now
        @span = run.start("chat #{model}", kind: :client, attributes: { 'gen_ai.operation.name' => 'chat',
                                                                        **run.dimensions })
        @usage = Usage.new
        @retries = 0
        @first_text = nil
      end

      # Notes what the model streamed. The time to first token is the wait for the first text of the attempt that
      # counts, from the call's start.
      def observe(event)
        case event
        in TextDelta then @first_text ||= @run.now - @started
        in Retried then retried
        in UsageReported(usage:) then @usage += usage
        end
      end

      # Records the call's metrics and ends its span: how it ended is its reply, the run's failure when it got none, or
      # nil when it was cancelled or the host left.
      # @param outcome [Reply, Failed, nil]
      def finish(outcome)
        case outcome
        in Reply => reply then ended(replied(reply))
        in Failed(detail:) then ended({}, 'model_error', detail)
        in nil then ended({})
        end
      end

      private

      def ended(attributes, error_type = nil, description = nil)
        measure(error_type)
        @span.add_attributes({ 'officina.model.retries' => @retries,
                               'officina.model.time_to_first_token' => @first_text,
                               **@run.usage_attributes(@usage), **attributes }.compact)
        @run.stop(@span, error_type, description)
      end

      def retried
        @retries += 1
        @first_text = nil
        @run.add(:retries, 1, @run.dimensions)
      end

      # A call that reported no tokens, such as one that failed before its reply started, records none.
      def measure(error_type)
        dimensions = @run.dimensions
        chat = { **dimensions, 'gen_ai.operation.name' => 'chat' }
        input = @usage.input + @usage.cache_read + @usage.cache_write
        tokens(dimensions, chat, input) unless @usage == Usage.new
        @run.record(:cache_hit_ratio, @usage.cache_read.fdiv(input), dimensions) if input.positive?
        @run.record(:model_duration, @run.now - @started, { **chat, 'error.type' => error_type }.compact)
      end

      def tokens(dimensions, chat, input)
        @run.record(:tokens, input, { **chat, 'gen_ai.token.type' => 'input' })
        @run.record(:tokens, @usage.output, { **chat, 'gen_ai.token.type' => 'output' })
        @run.record(:cache_tokens, @usage.cache_read, { **dimensions, 'officina.cache.type' => 'read' })
        @run.record(:cache_tokens, @usage.cache_write, { **dimensions, 'officina.cache.type' => 'write' })
        @run.record(:cost, @run.cost(@usage).to_f, dimensions)
      end

      # The reply's finish reason, its stop's word or the provider's own for an unknown one, and its text if the host
      # opted in.
      def replied(reply)
        reason = reply.stop == :unknown ? reply.detail || 'unknown' : reply.stop.to_s
        { 'gen_ai.response.finish_reasons' => [reason],
          **@run.content('gen_ai.output.messages', reply.text, role: 'assistant') }
      end
    end
    private_constant :ModelCallTrace
  end
end
