# frozen_string_literal: true

require 'json'
require 'test_helper'
require 'sleepyshark/officina/mcp'

# Reading what a server writes, with generated messages. The reader is internal to the gem, so the test reaches it
# through const_get rather than through a connection, to run hundreds of cases in a fraction of a second.
class WireTest < Minitest::Test
  cover 'Sleepyshark::Officina::Mcp*'

  Wire = Sleepyshark::Officina::Mcp.const_get(:Wire)
  Error = Sleepyshark::Officina::Mcp::Error
  ID = 7
  RESPONSE = { 'jsonrpc' => '2.0', 'id' => ID,
               'result' => { 'content' => [{ 'type' => 'text', 'text' => 'Ünïcode 日本' }] } }.freeze
  # What a server may write before the response, none of which is it.
  OTHERS = [
    { 'jsonrpc' => '2.0', 'method' => 'notifications/progress', 'params' => { 'progress' => 1, 'message' => 'é' } },
    # A request of the server's own, with the id of the client's request.
    { 'jsonrpc' => '2.0', 'id' => ID, 'method' => 'ping' },
    { 'jsonrpc' => '2.0', 'id' => ID + 1, 'result' => {} },
    { 'jsonrpc' => '2.0', 'id' => ID.to_s, 'result' => {} },
    { 'jsonrpc' => '2.0', 'id' => ID.to_f, 'result' => {} },
    { 'jsonrpc' => '2.0', 'id' => ID },
    [RESPONSE],
    'not JSON {',
    '{"jsonrpc":"2.0","id":7,"id":7,"result":{}}'
  ].freeze
  # How many ways an event may be written.
  FORMS = 4

  # A body that yields the given chunks.
  Body = Data.define(:chunks) do
    def read_body(&) = chunks.each(&)
  end

  def test_mcp01_the_response_is_found_in_any_event_stream_and_nothing_else_is_taken_for_it
    Pbt.assert do
      Pbt.property(others, form, Pbt.boolean, cuts) do |before, last, crlf, lengths|
        assert_equal RESPONSE, answer(stream(before, last), crlf, lengths)
      end
    end
  end

  def test_mcp01_a_stream_that_ends_before_the_response_is_complete_raises
    Pbt.assert do
      Pbt.property(form, Pbt.boolean, cuts) do |last, crlf, lengths|
        error = assert_raises(Error) { answer(event(RESPONSE, last).chomp, crlf, lengths) }

        assert_equal 'the server ended its event stream without a response', error.message
      end
    end
  end

  def test_mcp01_a_json_body_in_any_chunks_is_its_response
    Pbt.assert do
      Pbt.property(cuts) do |lengths|
        assert_equal RESPONSE, Wire.answer(body(JSON.generate(RESPONSE), lengths), ID, events: false)
      end
    end
  end

  def test_mcp01_a_body_that_is_not_the_response_raises
    bodies = ['not JSON', JSON.generate(RESPONSE.merge('id' => ID + 1)), '{"jsonrpc":"2.0","method":"ping"}']
    messages = bodies.map { |text| assert_raises(Error) { Wire.answer(Body.new([text]), ID, events: false) }.message }

    assert_equal ["the server's answer is not a response to the request"] * 3, messages
  end

  def test_mcp04_a_body_of_16_mb_is_read_and_one_byte_more_raises
    response = JSON.generate(RESPONSE).b
    padded = response + (' ' * (Wire::MAX_MESSAGE - response.bytesize))
    chunks = (0...padded.bytesize).step(1024 * 1024).map { padded.byteslice(it, 1024 * 1024) }
    error = assert_raises(Error) { Wire.answer(Body.new(chunks + [' ']), ID, events: false) }

    assert_equal RESPONSE, Wire.answer(Body.new(chunks), ID, events: false)
    assert_equal 'it sent a message longer than 16 MB', error.message
  end

  def test_mcp01_a_response_is_deeply_frozen
    response = Wire.response(JSON.generate(RESPONSE))

    assert_predicate response.dig('result', 'content', 0, 'text'), :frozen?
  end

  def test_mcp01_an_events_data_is_its_data_lines_without_one_leading_space_joined_as_utf8
    stream = Wire.const_get(:EventStream).new
    events = []
    # Starting with a blank line, a character in a chunk of its own, then an event with no data.
    ["\ndata:  two spaces\r\ndata:", 'é', "\n\n: comment\nevent: none\n\n"].each do |chunk|
      stream.feed(chunk) { events << it }
    end

    assert_equal [" two spaces\né"], events
    assert_equal Encoding::UTF_8, events.first.encoding
  end

  # Each byte is searched for a line end once: a 4 MB event in 256-byte chunks reads in about 0.03 s, where searching
  # the line from its start again for each chunk took 9 s. The bound is far from both.
  def test_mcp01_a_long_event_in_small_chunks_is_read_in_linear_time
    response = { 'jsonrpc' => '2.0', 'id' => ID, 'result' => { 'text' => 'x' * (4 * 1024 * 1024) } }
    stream = "data: #{JSON.generate(response)}\n\n".b
    chunks = (0...stream.bytesize).step(256).map { stream.byteslice(it, 256) }
    started = Process.clock_gettime(Process::CLOCK_MONOTONIC)
    answer = Wire.answer(Body.new(chunks), ID, events: true)

    assert_operator Process.clock_gettime(Process::CLOCK_MONOTONIC) - started, :<, 1
    assert_equal response, answer
  end

  def test_mcp01_any_text_is_no_response
    Pbt.assert do
      Pbt.property(Pbt.printable_string) { |text| assert_nil Wire.response(text) }
    end
  end

  def test_mcp01_a_message_is_a_response_only_with_a_result_or_error_a_positive_integer_id_and_no_method
    members = Pbt.fixed_hash(id: Pbt.one_of(nil, -1, 0, 1, 2.0, '1'), method: Pbt.one_of(nil, 'ping'),
                             result: Pbt.one_of(nil, {}), error: Pbt.one_of(nil, { 'message' => 'no' }))
    Pbt.assert do
      Pbt.property(members) do |fields|
        message = fields.compact.transform_keys(&:to_s)

        assert_equal(response?(fields), Wire.response(JSON.generate(message)) == message)
      end
    end
  end

  private

  # Which of the FORMS an event is written in.
  def form = Pbt.integer(min: 0, max: FORMS - 1)

  # Where a body is cut into chunks: the chunks' lengths, in turn.
  def cuts = Pbt.array(Pbt.integer(min: 1, max: 50))

  # Messages before the response: each [index in OTHERS, form].
  def others = Pbt.array(Pbt.tuple(Pbt.integer(min: 0, max: OTHERS.size - 1), form), max: 6)

  # An event stream of the others before, then the response in form last.
  def stream(before, last)
    before.map { |other, other_form| event(OTHERS[other], other_form) }.join + event(RESPONSE, last)
  end

  # message as one event, written in form 0 to FORMS - 1: compact; with no space after "data:"; spread over several
  # data lines; among other fields and a comment.
  def event(message, form)
    text = message.is_a?(String) ? message : JSON.generate(message)
    case form
    when 0 then "data: #{text}\n\n"
    when 1 then "data:#{text}\n\n"
    when 2 then "#{JSON.pretty_generate(message).lines.map { "data: #{it.chomp}\n" }.join}\n"
    else ": keep-alive\nevent: message\nid: 42\ndata: #{text}\nretry: 10\n\n"
    end
  end

  # The response to ID in the event stream, with CRLF line endings when crlf, read in chunks of the given lengths.
  def answer(stream, crlf, lengths)
    Wire.answer(body(crlf ? stream.gsub("\n", "\r\n") : stream, lengths), ID, events: true)
  end

  # A body of text's bytes, in chunks of the given lengths, then what is left.
  def body(text, lengths)
    bytes = text.b
    Body.new(lengths.filter_map { |length| bytes.slice!(0, length) unless bytes.empty? } << bytes)
  end

  def response?(fields)
    id = fields[:id]
    id.is_a?(Integer) && id.positive? && fields[:method].nil? && !(fields[:result] || fields[:error]).nil?
  end
end
