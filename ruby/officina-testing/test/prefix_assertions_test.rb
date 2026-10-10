# frozen_string_literal: true

require 'test_helper'

# The prefix stability assertion: which change it finds, and how its failure names the part and the requests.
class PrefixAssertionsTest < Minitest::Test
  include Sleepyshark::Officina
  include Testing::PrefixAssertions

  cover 'Sleepyshark::Officina::Testing*'

  SCHEMA = '{"type":"object","properties":{"title":{"type":"string"}},"required":["title"]}'

  def test_test02_a_stable_prefix_passes_with_or_without_an_output_schema
    [nil, SCHEMA].each do |output_schema|
      first = request(output_schema:)

      assert_stable_prefix [first, first.with(messages: [*first.messages, user('More')])]
    end
  end

  def test_test02_each_changed_part_of_the_prefix_fails_naming_it_and_the_requests
    first = request(output_schema: SCHEMA)
    changes = { tools: first.with(tools: [tool('search', 'Searches better.')]),
                instructions: first.with(instructions: 'You help more.'),
                output_schema: first.with(output_schema: '{"type":"object"}') }

    changes.each do |part, changed|
      error = assert_raises(Minitest::Assertion) { assert_stable_prefix [first, first, changed] }

      assert_match(/\AThe prefix changed in request 3, from request 2\.\n/, error.message)
      assert_match(/\[:#{part}, /, error.message)
    end
  end

  def test_test02_a_changed_earlier_message_fails_naming_the_requests
    first = request

    error = assert_raises(Minitest::Assertion) do
      assert_stable_prefix [first, first, first.with(messages: [user('Hello')])]
    end

    assert_match(/\AAn earlier message changed in request 3, from request 2\.\n/, error.message)
  end

  private

  def request(output_schema: nil)
    Request.new(tools: [tool('search', 'Searches the catalogue.')], instructions: 'You help.', messages: [user('Hi')],
                output_schema:)
  end

  def tool(name, description)
    Tool.new(name:, description:, input: Schema.new('{"type":"object"}'), kind: :read) { 'Found.' }
  end

  def user(text) = Message.new(role: :user, blocks: [Block.new(text:)])
end
