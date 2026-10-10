# frozen_string_literal: true

require 'tmpdir'
require_relative 'claude_test_case'

# Failures of a call: which are retried and after what wait, what a failed attempt leaves, and what remains.
class RetryTest < ClaudeTestCase
  PARTIAL = '{"type":"message_delta","delta":{"stop_reason":null,"stop_sequence":null},"usage":{"output_tokens":3}}'
  # What a call raises for a failure retrying cannot fix, before the failure's own message.
  SAYS = { Claude::InvalidRequestError => "Claude's API call cannot succeed as it is: ",
           Claude::AuthenticationError => "Claude's credentials were missing or refused: " }.freeze

  # A reply that streams "Hel" and reports 3 output tokens, then fails mid-stream with an error event of the type
  # given, under the response's status, 200.
  def cut_off(type = 'overloaded_error')
    FakeApi.sse(START, *text_events('Hel')[0..1], PARTIAL,
                JSON.generate({ type: 'error', error: { type:, message: 'Failed.' } }))
  end

  # What a call streams before cut_off fails.
  def streamed_before_cut_off = [TextDelta.new(text: 'Hel'), UsageReported.new(usage: Usage.new(input: 10, output: 3))]

  def test_mdl04_a_transient_failure_waits_then_succeeds
    {
      # Error types and statuses are told apart: each case is retried for one of them alone.
      'a rate limit waits as long as Retry-After asks' =>
        [[FakeApi.error(429, 'unknown_error', 'Slow down.', retry_after: '7')], [7..7]],
      'a long Retry-After is capped' =>
        [[FakeApi.error(429, 'unknown_error', 'Slow down.', retry_after: '3600')], [30..30]],
      'a Retry-After of zero backs off' => [[FakeApi.error(429, 'unknown_error', 'Slow down.', retry_after: '0')],
                                            [0.5..1]],
      'a Retry-After date, which the API does not send, backs off' =>
        [[FakeApi.error(429, 'unknown_error', 'Slow down.', retry_after: 'Thu, 01 Jan 2099 00:00:00 GMT')],
         [0.5..1]],
      'a server error then an overload back off exponentially with jitter' =>
        [[FakeApi.error(500, 'unknown_error', 'Oops'), FakeApi.error(529, 'unknown_error', 'Overloaded')],
         [0.5..1, 1..2]],
      'a request timeout is retried' => [[FakeApi.error(408, 'unknown_error', 'Timeout')], [0.5..1]],
      'a conflict is retried' => [[FakeApi.error(409, 'unknown_error', 'Conflict')], [0.5..1]],
      'an overload mid-stream is retried' => [[cut_off('overloaded_error')], [0.5..1]],
      'an API error mid-stream is retried' => [[cut_off('api_error')], [0.5..1]],
      'a rate limit mid-stream is retried' => [[cut_off('rate_limit_error')], [0.5..1]],
      'a timeout mid-stream is retried' => [[cut_off('timeout_error')], [0.5..1]],
      'a connection dropped mid-stream is retried' => [[FakeApi.sse(START, text_events('Hel')[0], ending: :cut)],
                                                       [0.5..1]],
      'a stream that ends before its stop reason is retried' => [[FakeApi.sse(START, *text_events('Hel'))], [0.5..1]],
      'a reply that stops without a stop reason is retried' =>
        [[FakeApi.sse(START, *text_events('Hel'), PARTIAL, '{"type":"message_stop"}')], [0.5..1]]
    }.each do |name, (failures, waits)|
      @waits.clear
      api = serve(*failures, text_reply)

      events, reply = collect(model(api), hi)

      assert_equal :end, reply.stop, name
      assert_equal [failures.size, waits.size], [events.count(Retried.new), @waits.size], name
      waits.zip(@waits).each { |range, waited| assert_includes range, waited, name }
      assert_empty @waits.grep_v(Float), name
      assert_equal [api.bodies[0]] * (failures.size + 1), api.bodies, name
    end
  end

  def test_mdl04_backoff_jitter_spreads_the_waits_over_the_upper_half_of_each_step
    previous = srand(20_261_010)
    errors = Array.new(50) { FakeApi.error(500, 'unknown_error', 'Oops') }
    api = serve(*errors)
    claude = model(api)

    10.times { assert_raises(Claude::TransientError) { collect(claude, hi) } }

    # Each call waits 4 times, on steps of 1, 2, 4 and 8 seconds.
    shares = @waits.each_slice(4).flat_map { |waits| waits.each_with_index.map { |waited, index| waited / (2**index) } }

    assert_equal 40, shares.size
    assert_empty shares.grep_v(0.5..1)
    assert_operator shares.min, :<, 0.6
    assert_operator shares.max, :>, 0.9
  ensure
    srand(previous) if previous
  end

  def test_mdl04_a_failure_mid_stream_restarts_the_reply_keeping_the_tokens_it_used
    {
      cut_off => Usage.new(input: 10, output: 3),
      # Before any message delta, the tokens are those the message started with.
      FakeApi.sse(START, *text_events('Hel')[0..1], ending: :cut) => Usage.new(input: 10, output: 1)
    }.each do |failure, used|
      api = serve(failure, text_reply)

      events, reply = collect(model(api), hi)

      assert_equal [TextDelta.new(text: 'Hel'), UsageReported.new(usage: used), Retried.new,
                    TextDelta.new(text: 'Hello.'), UsageReported.new(usage: Usage.new(input: 10, output: 5))], events
      assert_equal ['Hello.'], reply.blocks.map(&:text)
    end
  end

  def test_mdl04_a_failure_that_persists_is_transient_after_every_attempt
    {
      FakeApi.error(529, 'overloaded_error', 'Overloaded') => [Anthropic::Errors::InternalServerError, 'Overloaded'],
      FakeApi.sse(START, *text_events('Hel')) => [Error, "Claude's reply ended before its stop reason"],
      FakeApi.sse(START, *text_events('Hel'), PARTIAL, '{"type":"message_stop"}') =>
        [Error, "Claude's reply stopped without a stop reason"]
    }.each do |failure, (cause, reason)|
      @waits.clear
      failures = Array.new(5, failure)
      api = serve(*failures)
      events = []

      error = assert_raises(Claude::TransientError) { model(api).stream(hi, cancel: Cancellation.new) { events << it } }

      assert_equal [5, 4, 4], [api.requests.size, events.count(Retried.new), @waits.size]
      assert_kind_of cause, error.cause
      assert_includes error.cause.message, reason
      assert_equal "Claude's API failed 5 times: #{error.cause.message}", error.message
    end
  end

  def test_mdl04_errors_retrying_cannot_fix_are_classified_and_not_retried
    streamed = streamed_before_cut_off
    {
      FakeApi.error(400, 'invalid_request_error', 'messages: bad') =>
        [Claude::InvalidRequestError, Anthropic::Errors::BadRequestError, []],
      FakeApi.error(404, 'not_found_error', 'No such model') =>
        [Claude::InvalidRequestError, Anthropic::Errors::NotFoundError, []],
      FakeApi.error(499, 'unknown_error', 'Client closed') =>
        [Claude::InvalidRequestError, Anthropic::Errors::APIStatusError, []],
      cut_off('invalid_request_error') => [Claude::InvalidRequestError, Anthropic::Errors::APIStatusError, streamed],
      # Error types and statuses are told apart: each case is an authentication error by one of them alone.
      FakeApi.error(401, 'unknown_error', 'Bad key') =>
        [Claude::AuthenticationError, Anthropic::Errors::AuthenticationError, []],
      FakeApi.error(403, 'unknown_error', 'Not allowed') =>
        [Claude::AuthenticationError, Anthropic::Errors::PermissionDeniedError, []],
      cut_off('authentication_error') => [Claude::AuthenticationError, Anthropic::Errors::APIStatusError, streamed],
      cut_off('permission_error') => [Claude::AuthenticationError, Anthropic::Errors::APIStatusError, streamed],
      # An event the SDK cannot read would fail again.
      FakeApi.sse('{"type":"message_start","message":"x"}') =>
        [Claude::InvalidRequestError, Anthropic::Errors::ConversionError, []]
    }.each do |response, (kind, cause, before)|
      @waits.clear
      api = serve(response)
      events = []

      error = assert_raises(kind) { model(api).stream(hi, cancel: Cancellation.new) { events << it } }

      assert_kind_of cause, error.cause
      assert_equal "#{SAYS.fetch(kind)}#{error.cause.message}", error.message
      assert_equal [1, before, 0], [api.requests.size, events, @waits.size]
    end
  end

  def test_mdl04_credentials_the_sdk_cannot_find_are_an_authentication_error_and_not_retried
    with_profile_without_credentials do
      api = serve
      claude = Claude::Model.new(name: 'claude-opus-5-5', effort: :low, base_url: api.url,
                                 wait: ->(seconds, _cancel) { @waits << seconds })

      error = assert_raises(Claude::AuthenticationError) { collect(claude, hi) }

      assert_kind_of Anthropic::Errors::ConfigurationError, error.cause
      assert_equal [0, 0], [api.requests.size, @waits.size]
    end
  end

  def test_mdl04_a_prompt_longer_than_the_context_window_stops_for_context_full
    ['prompt is too long: 1000001 tokens > 1000000 maximum', 'Prompt is too long'].each do |message|
      api = serve(FakeApi.error(400, 'invalid_request_error', message))

      events, reply = collect(model(api), hi)

      assert_equal [[], Reply.new(blocks: [], stop: :context_full)], [events, reply], message
    end
  end

  def test_mdl04_an_invalid_request_that_does_not_say_so_in_the_apis_shape_is_not_context_full
    [FakeApi::Response.new(status: 400, headers: { 'content-type' => 'text/plain' }, body: 'Prompt is too long',
                           ending: :close),
     FakeApi.error(400, 'invalid_request_error', 5)].each do |response|
      api = serve(response)

      error = assert_raises(Claude::InvalidRequestError) { collect(model(api), hi) }

      assert_kind_of Anthropic::Errors::BadRequestError, error.cause
    end
  end

  def test_mdl04_agt05_cancelling_during_a_retry_wait_ends_the_call_at_once
    api = serve(FakeApi.error(429, 'rate_limit_error', 'Slow down.', retry_after: '30'), text_reply)
    claude = Claude::Model.new(name: 'claude-opus-5-5', effort: :low, api_key: 'test-key', base_url: api.url)
    cancel = Cancellation.new
    retried = Thread::Queue.new
    canceller = Thread.new do
      retried.pop
      cancel.cancel
    end
    began = Process.clock_gettime(Process::CLOCK_MONOTONIC)

    reply = claude.stream(hi, cancel:) { retried << it }

    assert_nil reply
    assert_operator Process.clock_gettime(Process::CLOCK_MONOTONIC) - began, :<, 1
    assert_equal 1, api.requests.size
  ensure
    retried&.close
    canceller&.join
  end

  private

  # Runs the block with no API key or token in the environment and the SDK's profile naming a credentials file that
  # does not exist; the environment is restored after it.
  def with_profile_without_credentials(&)
    saved = ENV.to_h.slice('ANTHROPIC_API_KEY', 'ANTHROPIC_AUTH_TOKEN', 'ANTHROPIC_CONFIG_DIR', 'ANTHROPIC_PROFILE')
    Dir.mktmpdir do |dir|
      Dir.mkdir(File.join(dir, 'configs'))
      File.write(File.join(dir, 'configs', 'p.json'), '{"authentication":{"type":"user_oauth"}}')
      ENV.delete('ANTHROPIC_API_KEY')
      ENV.delete('ANTHROPIC_AUTH_TOKEN')
      ENV.update('ANTHROPIC_CONFIG_DIR' => dir, 'ANTHROPIC_PROFILE' => 'p')
      yield
    ensure
      %w[ANTHROPIC_CONFIG_DIR ANTHROPIC_PROFILE].each { ENV.delete(it) }
      ENV.update(saved)
    end
  end
end
