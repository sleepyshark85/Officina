# frozen_string_literal: true

require_relative 'claude_test_case'

# Failures of a call: which are retried and after what wait, what a failed attempt leaves, and what remains.
class RetryTest < ClaudeTestCase
  OVERLOADED = '{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}'
  PARTIAL = '{"type":"message_delta","delta":{"stop_reason":null,"stop_sequence":null},"usage":{"output_tokens":3}}'

  # A reply that streams "Hel" and reports 3 output tokens, then fails with an overload mid-stream.
  def cut_off = FakeApi.sse(START, *text_events('Hel')[0..1], PARTIAL, OVERLOADED)

  def test_mdl04_a_transient_failure_waits_then_succeeds
    {
      'a rate limit waits as long as Retry-After asks' =>
        [[FakeApi.error(429, 'rate_limit_error', 'Slow down.', retry_after: '7')], [7..7]],
      'a long Retry-After is capped' =>
        [[FakeApi.error(429, 'rate_limit_error', 'Slow down.', retry_after: '3600')], [30..30]],
      'a Retry-After date, which the API does not send, backs off' =>
        [[FakeApi.error(429, 'rate_limit_error', 'Slow down.', retry_after: 'Thu, 01 Jan 2099 00:00:00 GMT')],
         [0.5..1]],
      'an overload then a server error back off exponentially with jitter' =>
        [[FakeApi.error(529, 'overloaded_error', 'Overloaded'), FakeApi.error(500, 'api_error', 'Oops')],
         [0.5..1, 1..2]],
      'any server error is retried, by its status alone' => [[FakeApi.error(500, 'unknown_error', 'Oops')], [0.5..1]],
      'a request timeout is retried' => [[FakeApi.error(408, 'timeout_error', 'Timeout')], [0.5..1]],
      'a conflict is retried' => [[FakeApi.error(409, 'conflict_error', 'Conflict')], [0.5..1]],
      'an error event mid-stream is retried' => [[cut_off], [0.5..1]],
      'a connection dropped mid-stream is retried' => [[FakeApi.sse(START, text_events('Hel')[0], cut: true)],
                                                       [0.5..1]],
      'a stream that ends before its stop reason is retried' => [[FakeApi.sse(START, *text_events('Hel'))], [0.5..1]],
      'a reply that stops without a stop reason is retried' =>
        [[FakeApi.sse(START, *text_events('Hel'), PARTIAL, '{"type":"message_stop"}')], [0.5..1]]
    }.each do |name, (failures, waits)|
      @waits.clear
      api = serve(*failures, text_reply)

      events, reply = collect(model(api), hi)

      assert_equal :end, reply.stop, name
      assert_equal failures.size, events.count(Retried.new), name
      assert_equal waits.size, @waits.size, name
      waits.zip(@waits).each { |range, waited| assert_includes range, waited, name }
      assert_equal [api.bodies[0]] * (failures.size + 1), api.bodies, name
    end
  end

  def test_mdl04_a_failure_mid_stream_restarts_the_reply_keeping_the_tokens_it_used
    api = serve(cut_off, text_reply)

    events, reply = collect(model(api), hi)

    assert_equal [TextDelta.new(text: 'Hel'), UsageReported.new(usage: Usage.new(input: 10, output: 3)), Retried.new,
                  TextDelta.new(text: 'Hello.'), UsageReported.new(usage: Usage.new(input: 10, output: 5))], events
    assert_equal ['Hello.'], reply.blocks.map(&:text)
  end

  def test_mdl04_a_failure_that_persists_is_transient_after_every_attempt
    overloads = Array.new(5) { FakeApi.error(529, 'overloaded_error', 'Overloaded') }
    api = serve(*overloads)

    events = []
    error = assert_raises(Claude::TransientError) { model(api).stream(hi, cancel: Cancellation.new) { events << it } }

    assert_equal [5, 4, 4], [api.requests.size, events.count(Retried.new), @waits.size]
    assert_kind_of Anthropic::Errors::InternalServerError, error.cause
    assert_match(/failed 5 times/, error.message)
  end

  def test_mdl04_errors_retrying_cannot_fix_are_classified_and_not_retried
    {
      FakeApi.error(400, 'invalid_request_error', 'messages: bad') => Claude::InvalidRequestError,
      FakeApi.error(404, 'not_found_error', 'No such model') => Claude::InvalidRequestError,
      FakeApi.error(401, 'authentication_error', 'Bad key') => Claude::AuthenticationError,
      FakeApi.error(403, 'permission_error', 'Not allowed') => Claude::AuthenticationError
    }.each do |response, kind|
      api = serve(response)

      assert_raises(kind) { collect(model(api), hi) }
      assert_equal 1, api.requests.size
    end
  end

  def test_mdl04_a_prompt_longer_than_the_context_window_stops_for_context_full
    api = serve(FakeApi.error(400, 'invalid_request_error', 'prompt is too long: 1000001 tokens > 1000000 maximum'))

    events, reply = collect(model(api), hi)

    assert_equal [[], Reply.new(blocks: [], stop: :context_full)], [events, reply]
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
end
