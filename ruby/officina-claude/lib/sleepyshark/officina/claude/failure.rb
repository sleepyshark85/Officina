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
        # Seconds: the first backoff, and the longest wait, asked for or not.
        FIRST_BACKOFF = 1.0
        LONGEST_WAIT = 30.0

        # @param error [StandardError] the SDK's error, or a stream that ended before its reply did
        def initialize(error)
          @error = error
          # The API's own answer, when the failure is one.
          @answer = error if error.is_a?(Anthropic::Errors::APIStatusError)
        end

        # Another attempt may pass: the API is busy or failing, or the network or stream broke.
        def transient?
          answer = @answer
          return true unless answer

          TRANSIENT_TYPES.include?(answer.type) || TRANSIENT_STATUSES.include?(answer.status) || answer.status >= 500
        end

        # The API rejected the prompt as longer than the context window. It gives that no type of its own, only an
        # invalid request saying so: the one place this gem reads an error's text.
        def prompt_too_long?
          body = @answer&.body
          message = body.dig(:error, :message) if body.is_a?(Hash)
          @answer&.status == 400 && message.is_a?(String) && message.downcase.include?('prompt is too long')
        end

        # Seconds to wait before the attempt after +attempt+: what the response's Retry-After asks, capped, or else an
        # exponential backoff with jitter.
        def wait(attempt)
          asked = retry_after
          return [asked.to_f, LONGEST_WAIT].min if asked&.positive?

          [FIRST_BACKOFF * (2**(attempt - 1)), LONGEST_WAIT].min * (0.5 + (rand / 2))
        end

        # The error a call raises for this failure after +attempts+ attempts; raised where it is rescued, it has the
        # failure as its cause.
        def error(attempts)
          return TransientError.new("Claude's API failed #{attempts} times: #{@error.message}") if transient?
          if AUTHENTICATION_TYPES.include?(@answer&.type) || AUTHENTICATION_STATUSES.include?(@answer&.status)
            return AuthenticationError.new("Claude's API refused the credentials: #{@error.message}")
          end

          InvalidRequestError.new("Claude's API rejected the request: #{@error.message}")
        end

        private

        # The API sends Retry-After in seconds; a date, which it does not send, counts as no wait asked.
        def retry_after
          value = @answer&.headers&.fetch('retry-after', nil)
          Integer(value, exception: false) if value
        end
      end
      private_constant :Failure
    end
  end
end
