# frozen_string_literal: true

module Sleepyshark
  module Officina
    module Claude
      # What one failed attempt of a call means: whether another may pass, how long to wait before it, and the error a
      # call raises when none is left.
      class Failure
        # Error types the API sends for a failure another attempt may not meet, also mid-stream, where the response's
        # status (200) says nothing.
        TRANSIENT_TYPES = %i[overloaded_error api_error rate_limit_error timeout_error].freeze
        TRANSIENT_STATUSES = [408, 409, 429].freeze
        AUTHENTICATION_TYPES = %i[authentication_error permission_error].freeze
        AUTHENTICATION_STATUSES = [401, 403].freeze
        # Seconds: the longest wait a Retry-After may ask for.
        LONGEST_WAIT = 30.0

        # The API rejected the prompt, given as its invalid request, as longer than the context window. It gives that
        # no type of its own, only a message saying so: the one place this gem reads an error's text.
        def self.prompt_too_long?(answer)
          case answer.body
          in { error: { message: String => message } } then message.downcase.include?('prompt is too long')
          else false
          end
        end

        # @param failure [StandardError] the SDK's error, or a stream that ended before its reply did
        def initialize(failure)
          @failure = failure
          # The API's own answer, when the failure is one.
          @answer = failure if failure.is_a?(Anthropic::Errors::APIStatusError)
        end

        # Another attempt may pass: the API is busy or failing, or the network or stream broke. Without an answer of
        # the API, any other failure of the SDK (credentials it cannot find, an event it cannot read) would fail again.
        def transient?
          answer = @answer
          unless answer
            return @failure.is_a?(Anthropic::Errors::APIConnectionError) || @failure.is_a?(Call::IncompleteError)
          end

          TRANSIENT_TYPES.include?(answer.type) || TRANSIENT_STATUSES.include?(answer.status) || answer.status >= 500
        end

        # Seconds to wait before the attempt after +attempt+: what the response's Retry-After asks, capped, or else an
        # exponential backoff from one second, with jitter.
        def wait(attempt)
          asked = retry_after
          return [asked.to_f, LONGEST_WAIT].min if asked&.positive?

          (0.5 + (rand / 2)) * (2**(attempt - 1))
        end

        # The error a call raises for this failure after +attempts+ attempts; raised where the failure is rescued, it
        # has the failure as its cause.
        def to_raise(attempts)
          if transient?
            TransientError.new("Claude's API failed #{attempts} times: #{@failure}")
          elsif authentication?
            AuthenticationError.new("Claude's credentials were missing or refused: #{@failure}")
          else
            InvalidRequestError.new("Claude's API call cannot succeed as it is: #{@failure}")
          end
        end

        private

        def authentication?
          answer = @answer
          return @failure.is_a?(Anthropic::Errors::ConfigurationError) unless answer

          AUTHENTICATION_TYPES.include?(answer.type) || AUTHENTICATION_STATUSES.include?(answer.status)
        end

        # The API sends Retry-After in seconds; a date, which it does not send, counts as no wait asked.
        def retry_after
          Integer(@answer&.headers&.fetch('retry-after', nil), exception: false)
        end
      end
      private_constant :Failure
    end
  end
end
