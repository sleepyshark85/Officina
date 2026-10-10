# frozen_string_literal: true

require 'open3'
require 'rbconfig'
require 'test_helper'
require_relative 'fixtures/resumed_agent'

# The prefix stays byte-identical across a run's model calls, across runs, and across a save, a new process and a
# resume.
class PrefixStabilityTest < Minitest::Test
  include Sleepyshark::Officina
  include Testing::PrefixAssertions

  cover 'Sleepyshark::Officina*'

  Model = Testing::ScriptedModel
  LIBS = [File.expand_path('../lib', __dir__), File.expand_path('../../officina-testing/lib', __dir__)].freeze
  RESUME = File.join(__dir__, 'fixtures', 'resume.rb')

  def test_test02_the_prefix_is_stable_across_the_calls_of_a_run_and_across_runs
    call = Model.tool_use_block('call_1', 'search', '{"query":"Gaudy Night"}')
    model = Model.new(Model.tool_use(call), Model.text('Not found.'), Model.text('Bye.'))
    agent = ResumedAgent.of(model)
    conversation = Conversation.new

    agent.run(conversation, 'Find Gaudy Night.', context: 'Today is Friday.')
    agent.run(conversation, 'Thanks.')

    assert_equal 3, model.requests.size
    assert_stable_prefix model.requests
  end

  def test_test02_the_prefix_is_stable_across_a_save_a_new_process_and_a_resume
    model = Model.new(Model.tool_use(Model.tool_use_block('call_1', 'search', '{}')), Model.text('Café «Libro».'))
    conversation = Conversation.new
    ResumedAgent.of(model).run(conversation, 'Find <Café> & co.', context: 'Today is Friday.')

    output, status = Open3.capture2(RbConfig.ruby, *LIBS.flat_map { ['-I', it] }, RESUME,
                                    stdin_data: conversation.to_json, binmode: true)

    assert_predicate status, :success?
    result, requests = Marshal.load(output) # rubocop:disable Security/MarshalLoad -- this test's own child wrote it

    assert_equal 'Resumed.', result.text
    assert_stable_prefix [*model.requests, *requests]
  end

  def test_test02_the_check_finds_a_changed_prefix
    request = Request.new(tools: [], instructions: 'You help.', messages: [user('Hi')])
    changed = [request.with(instructions: 'You help more.'), request.with(messages: [user('Hello')]),
               request.with(tools: [Tool.new(name: 't', description: 'd', input_schema: '{}')]),
               request.with(messages: [])]

    changed.each do |later|
      assert_raises(Minitest::Assertion) { assert_stable_prefix [request, later] }
    end
  end

  private

  def user(text) = Message.new(role: :user, blocks: [Block.new(text:)])
end
