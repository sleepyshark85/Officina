# frozen_string_literal: true

require 'json'
require 'test_helper'
require 'sleepyshark/officina/mcp'

# Which messages a server writes are responses, and what an event's data is. The reader is internal to the gem, so the
# test reaches it through const_get rather than through a connection, to run hundreds of cases in a fraction of a
# second.
class WireMessageTest < Minitest::Test
  cover 'Sleepyshark::Officina::Mcp*'

  Wire = Sleepyshark::Officina::Mcp.const_get(:Wire)
  ID = 7
  RESPONSE = { 'jsonrpc' => '2.0', 'id' => ID,
               'result' => { 'content' => [{ 'type' => 'text', 'text' => 'Ünïcode 日本' }] } }.freeze

  def test_mcp01_a_response_is_deeply_frozen
    id, response = Wire.response(JSON.generate(RESPONSE))

    assert_equal ID, id
    assert_predicate response.dig('result', 'content', 0, 'text'), :frozen?
  end

  def test_mcp01_an_events_data_is_its_data_lines_without_one_leading_space_joined_as_utf8
    stream = Wire.const_get(:EventStream).new
    events = []
    # Starting with a blank line; bytes, then text, as chunks may come; then an event with no data.
    ["\ndata:  two spaces\r\ndata: é".b, 'é', "\n\n: comment\nevent: none\n\n"].each do |chunk|
      stream.feed(chunk) { events << it }
    end

    assert_equal [" two spaces\néé"], events
    assert_equal Encoding::UTF_8, events.first.encoding
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
        answer = Wire.response(JSON.generate(message))

        if response?(fields)
          assert_equal [fields[:id], message], answer
        else
          assert_nil answer
        end
      end
    end
  end

  private

  def response?(fields)
    id = fields[:id]
    id.is_a?(Integer) && id.positive? && fields[:method].nil? && !(fields[:result] || fields[:error]).nil?
  end
end
