# frozen_string_literal: true

require 'tmpdir'
require_relative 'claude_test_case'

# Failures retrying cannot fix: each is raised at once, as the error class that says what went wrong, with the SDK's
# error as its cause; and the prompt too long for the context window, which is a reply.
class ClassificationTest < ClaudeTestCase
  # What a call raises, before the failure's own message.
  SAYS = { Claude::InvalidRequestError => "Claude's API call cannot succeed as it is: ",
           Claude::AuthenticationError => "Claude's credentials were missing or refused: " }.freeze

  def test_mdl04_a_request_the_api_rejects_is_invalid_and_not_retried
    {
      FakeApi.error(400, 'invalid_request_error', 'messages: bad') => Anthropic::Errors::BadRequestError,
      FakeApi.error(404, 'not_found_error', 'No such model') => Anthropic::Errors::NotFoundError,
      # Just below the statuses another attempt may pass.
      FakeApi.error(499, 'unknown_error', 'Client closed') => Anthropic::Errors::APIStatusError
    }.each { |response, cause| assert_raised_at_once(response, Claude::InvalidRequestError, cause) }
  end

  def test_mdl04_an_invalid_request_mid_stream_keeps_what_streamed_before_it
    assert_raised_at_once(cut_off('invalid_request_error'), Claude::InvalidRequestError,
                          Anthropic::Errors::APIStatusError, streamed: true)
  end

  def test_mdl04_refused_credentials_are_an_authentication_error_by_status_or_type_alone
    {
      FakeApi.error(401, 'unknown_error', 'Bad key') => [Anthropic::Errors::AuthenticationError, false],
      FakeApi.error(403, 'unknown_error', 'Not allowed') => [Anthropic::Errors::PermissionDeniedError, false],
      cut_off('authentication_error') => [Anthropic::Errors::APIStatusError, true],
      cut_off('permission_error') => [Anthropic::Errors::APIStatusError, true]
    }.each do |response, (cause, streamed)|
      assert_raised_at_once(response, Claude::AuthenticationError, cause, streamed:)
    end
  end

  def test_mdl04_an_event_the_sdk_cannot_read_is_an_invalid_request_and_not_retried
    assert_raised_at_once(FakeApi.sse('{"type":"message_start","message":"x"}'), Claude::InvalidRequestError,
                          Anthropic::Errors::ConversionError)
  end

  def test_mdl04_credentials_the_sdk_cannot_find_are_an_authentication_error_and_not_retried
    with_profile_without_credentials do
      api = serve
      claude = Claude::Model.new(name: 'claude-opus-5-5', effort: :low, base_url: api.url,
                                 wait: ->(seconds, _cancel) { @waits << seconds })

      error = assert_raises(Claude::AuthenticationError) { collect(claude, hi) }

      assert_kind_of Anthropic::Errors::ConfigurationError, error.cause
      assert_empty api.requests
      assert_empty @waits
    end
  end

  def test_mdl04_a_prompt_longer_than_the_context_window_stops_for_context_full
    ['prompt is too long: 1000001 tokens > 1000000 maximum', 'Prompt is too long'].each do |message|
      api = serve(FakeApi.error(400, 'invalid_request_error', message))

      events, reply = collect(model(api), hi)

      assert_empty events, message
      assert_equal Reply.new(blocks: [], stop: :context_full), reply, message
    end
  end

  def test_mdl04_an_invalid_request_that_does_not_say_so_in_the_apis_shape_is_not_context_full
    [FakeApi::Response.new(status: 400, headers: { 'content-type' => 'text/plain' }, body: 'Prompt is too long',
                           ending: :close),
     FakeApi.error(400, 'invalid_request_error', 5)].each do |response|
      assert_raised_at_once(response, Claude::InvalidRequestError, Anthropic::Errors::BadRequestError)
    end
  end

  private

  # Serves the response once: the call raises the kind of error given, with the cause given, after one request and no
  # wait, having streamed what came before the failure when +streamed+.
  def assert_raised_at_once(response, kind, cause, streamed: false)
    @waits.clear
    api = serve(response)
    events = []

    error = assert_raises(kind) { model(api).stream(hi, cancel: Cancellation.new) { events << it } }

    assert_kind_of cause, error.cause
    assert_equal "#{SAYS.fetch(kind)}#{error.cause.message}", error.message
    assert_equal 1, api.requests.size
    assert_empty @waits

    before = [TextDelta.new(text: 'Hel'), UsageReported.new(usage: Usage.new(input: 10, output: 3))]

    assert_equal streamed ? before : [], events
  end

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
