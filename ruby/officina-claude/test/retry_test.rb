# frozen_string_literal: true

require_relative 'claude_test_case'

# Failures another attempt may pass: which are retried, after what wait, and what a failed attempt leaves. Error
# types and statuses are told apart: each case is retried for one of them alone.
class RetryTest < ClaudeTestCase
  def test_mdl04_a_retry_waits_as_long_as_retry_after_asks_in_seconds_within_a_cap
    {
      'seconds' => ['7', 7..7], 'a long wait, capped' => ['3600', 30..30], 'zero, backing off' => ['0', 0.5..1],
      'a date, which the API does not send, backing off' => ['Thu, 01 Jan 2099 00:00:00 GMT', 0.5..1]
    }.each do |name, (asked, wait)|
      assert_retried(name, [FakeApi.error(429, 'unknown_error', 'Slow down.', retry_after: asked)], [wait])
    end
  end

  def test_mdl04_backoff_doubles_from_a_second
    failures = [FakeApi.error(500, 'unknown_error', 'Oops'), FakeApi.error(529, 'unknown_error', 'Overloaded')]

    assert_retried('a server error then an overload', failures, [0.5..1, 1..2])
  end

  def test_mdl04_a_status_another_attempt_may_pass_is_retried_whatever_its_type
    [408, 409, 429, 500].each do |status|
      assert_retried("status #{status}", [FakeApi.error(status, 'unknown_error', 'Failed.')], [0.5..1])
    end
  end

  def test_mdl04_an_error_type_another_attempt_may_pass_is_retried_mid_stream
    %w[overloaded_error api_error rate_limit_error timeout_error].each do |type|
      assert_retried(type, [cut_off(type)], [0.5..1])
    end
  end

  def test_mdl04_a_broken_stream_is_retried
    {
      'a connection dropped mid-stream' => FakeApi.sse(START, text_events('Hel')[0], ending: :cut),
      'a stream that ends before its stop reason' => FakeApi.sse(START, *text_events('Hel')),
      'a reply that stops without a stop reason' => FakeApi.sse(START, *text_events('Hel'), PARTIAL,
                                                                '{"type":"message_stop"}')
    }.each { |name, failure| assert_retried(name, [failure], [0.5..1]) }
  end

  def test_mdl04_backoff_jitter_spreads_the_waits_over_the_upper_half_of_each_step
    previous = srand(20_261_010)
    errors = Array.new(50) { FakeApi.error(500, 'unknown_error', 'Oops') }
    claude = model(serve(*errors))

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
      events, reply = collect(model(serve(failure, text_reply)), hi)

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
    }.each { |failure, (cause, reason)| assert_transient_after_every_attempt(failure, cause, reason) }
  end

  private

  # Serves the failures, then a whole reply: the call returns that reply, having said Retried and waited a number of
  # seconds within each range given before each attempt after the first, which sent the same request.
  def assert_retried(name, failures, waits)
    @waits.clear
    api = serve(*failures, text_reply)

    events, reply = collect(model(api), hi)

    assert_equal :end, reply.stop, name
    assert_equal failures.size, events.count(Retried.new), name
    assert_equal waits.size, @waits.size, name
    waits.zip(@waits).each { |range, waited| assert_includes range, waited, name }
    assert_empty @waits.grep_v(Float), name
    assert_equal [api.bodies[0]] * (failures.size + 1), api.bodies, name
  end

  # Serves the failure five times: the call raises a TransientError after the last, its cause the failure.
  def assert_transient_after_every_attempt(failure, cause, reason)
    @waits.clear
    failures = Array.new(5, failure)
    api = serve(*failures)
    events = []

    error = assert_raises(Claude::TransientError) { model(api).stream(hi, cancel: Cancellation.new) { events << it } }

    assert_equal 5, api.requests.size
    assert_equal 4, events.count(Retried.new)
    assert_equal 4, @waits.size
    assert_kind_of cause, error.cause
    assert_includes error.cause.message, reason
    assert_equal "Claude's API failed 5 times: #{error.cause.message}", error.message
  end
end
