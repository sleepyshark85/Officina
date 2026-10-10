# frozen_string_literal: true

require 'json'
require 'sleepyshark/officina/claude'
require_relative 'fake_api'

# What the Claude gem's tests share: a fake API per test, closed after it, a model that calls it and waits for
# nothing between attempts, and the requests and replies they use.
class ClaudeTestCase < Minitest::Test
  include Sleepyshark::Officina

  TESTDATA = File.expand_path('../../../testdata', __dir__)
  START = '{"type":"message_start","message":{"id":"msg_01","type":"message","role":"assistant",' \
          '"model":"claude-opus-5-5","content":[],"stop_reason":null,"stop_sequence":null,' \
          '"usage":{"input_tokens":10,"output_tokens":1}}}'

  def setup
    @apis = []
    @waits = []
  end

  def teardown
    @apis.each(&:close)
  end

  # A fake API that answers with the responses given, in order.
  def serve(*responses)
    FakeApi.new(*responses).tap { @apis << it }
  end

  # Claude Opus 5.5 at medium effort on the fake API; each wait between attempts is recorded, not waited.
  def model(api, **settings)
    Claude::Model.new(name: 'claude-opus-5-5', effort: :medium, api_key: 'test-key', base_url: api.url,
                      wait: ->(seconds, _cancel) { @waits << seconds }, **settings)
  end

  # The events the model streamed for the request, and its reply.
  def collect(model, request, cancel: Cancellation.new)
    events = []
    reply = model.stream(request, cancel:) { events << it }
    [events, reply]
  end

  def testdata(name) = File.read(File.join(TESTDATA, name), encoding: 'UTF-8')

  # The text with each % made a backslash, to write JSON escapes plainly.
  def esc(text) = text.tr('%', '\\')

  # A request of one user message.
  def hi(text = 'Hi')
    Request.new(tools: [], instructions: 'Answer briefly.',
                messages: [Message.new(role: :user, blocks: [Block.new(text:)])])
  end

  # The events of one text block, streamed in the pieces given.
  def text_events(*pieces, index: 0)
    [%({"type":"content_block_start","index":#{index},"content_block":{"type":"text","text":""}}),
     *pieces.map { JSON.generate({ type: 'content_block_delta', index:, delta: { type: 'text_delta', text: it } }) },
     %({"type":"content_block_stop","index":#{index}})]
  end

  # A whole reply of one text block that stops for the API's word given.
  def text_reply(stop = 'end_turn', text: 'Hello.')
    FakeApi.sse(START, *text_events(text),
                %({"type":"message_delta","delta":{"stop_reason":"#{stop}"},"usage":{"output_tokens":5}}),
                '{"type":"message_stop"}')
  end
end
