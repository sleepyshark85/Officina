# frozen_string_literal: true

require_relative 'claude_test_case'

# A call that ends before its reply does: cancelled, also while it waits to retry, or left by the consumer's
# block, its connection closed.
class EarlyEndTest < ClaudeTestCase
  def test_agt05_a_call_cancelled_before_it_starts_sends_nothing
    api = serve(text_reply)
    cancel = Cancellation.new
    cancel.cancel

    events, reply = collect(model(api), hi, cancel:)

    assert_nil reply
    assert_empty events
    assert_empty api.requests
  end

  def test_agt05_cancelling_mid_stream_ends_the_call_with_no_reply_and_closes_the_connection
    api = serve(FakeApi.sse(START, *text_events('one', 'two', 'three'), ending: :hold))
    cancel = Cancellation.new
    events = []

    reply = model(api).stream(hi, cancel:) do |event|
      events << event
      cancel.cancel
    end

    assert_nil reply
    assert_equal [TextDelta.new(text: 'one')], events
    assert_predicate api, :closed_by_client?
  end

  def test_evt01_what_the_consumers_block_raises_passes_through_is_not_retried_and_closes_the_connection
    host = Class.new(StandardError)
    api = serve(text_reply(ending: :hold), text_reply)

    raised = assert_raises(host) { model(api).stream(hi, cancel: Cancellation.new) { raise host, 'the host failed' } }

    assert_equal 'the host failed', raised.message
    assert_equal 1, api.requests.size
    assert_predicate api, :closed_by_client?
  end

  def test_evt01_a_consumer_that_leaves_the_block_ends_the_call_and_closes_the_connection
    api = serve(FakeApi.sse(START, *text_events('one', 'two'), ending: :hold))
    seen = []

    model(api).stream(hi, cancel: Cancellation.new) do |event|
      seen << event
      break
    end

    assert_equal [TextDelta.new(text: 'one')], seen
    assert_predicate api, :closed_by_client?
  end

  def test_mdl04_agt05_cancelling_during_a_retry_wait_ends_the_call_at_once
    api = serve(FakeApi.error(429, 'rate_limit_error', 'Slow down.', retry_after: '30'), text_reply)
    claude = Claude::Model.new(name: 'claude-opus-5-5', effort: :low, api_key: 'test-key', base_url: api.url)
    cancel = Cancellation.new
    retried = Thread::Queue.new
    canceller = Thread.new { retried.pop && cancel.cancel }
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
