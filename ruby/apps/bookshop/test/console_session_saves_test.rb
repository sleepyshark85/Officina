# frozen_string_literal: true

require 'test_helper'
require_relative 'console_session'

# The session saved after every step of a reply, end to end: what a crash keeps, and a save that fails or would
# overwrite another console's.
class ConsoleSessionSavesTest < Minitest::Test
  include ConsoleSession

  # The process ending in the middle of a reply.
  class Crash < StandardError
  end

  ADD = '{"name":"Jo Bloggs","email":"jo@example.com"}'

  def test_app10_a_crash_mid_reply_loses_only_the_step_in_flight_and_resuming_tells_the_model
    model = ScriptedModel.new(say_then_call('Adding Jo.', call('c1', 'add_customer', ADD)))
    assert_raises(Crash) { session(model, 'Sam', 'Add Jo Bloggs, jo@example.com.', -> { raise Crash }) }
    id = select_row('select id from sessions').first

    assert_equal %i[user operator assistant], stored(id).messages.map(&:role)
    later = ScriptedModel.new(ScriptedModel.text('Jo may not have been added.'))

    session(later, 'Sam', "/resume #{id}", 'Was Jo added?', '/quit')

    interrupted = later.requests.first.messages[3].blocks.filter_map(&:tool_result)

    assert_equal([['c1', true]], interrupted.map { [it.call_id, it.error?] })
    assert_includes interrupted.first.content, 'interrupted'
    assert_equal 0, select_integer("select count(*) from customers where email = 'jo@example.com'")
  end

  def test_app14_a_crash_mid_reply_keeps_what_the_saved_step_spent
    usage = Officina::Usage.new(input: 1000, output: 100)
    model = ScriptedModel.new(ScriptedModel.tool_use(call('c1', 'add_customer', ADD), usage:))

    assert_raises(Crash) { session(model, 'Sam', 'Add Jo Bloggs, jo@example.com.', -> { raise Crash }) }
    # At the model's price: $5 a million input tokens and $25 a million output tokens.
    assert_equal %w[1000 100 0.0075], select_row('select input_tokens, output_tokens, cost from sessions')
  end

  def test_app10_a_session_that_cannot_be_saved_is_told_once_a_reply_and_the_next_save_stores_everything
    model = ScriptedModel.new(say_then_call('Adding Jo.', call('c1', 'add_customer', ADD)),
                              ScriptedModel.text('It could not be added.'), ScriptedModel.text('Not yet.'),
                              ScriptedModel.text('Back again.'))

    transcript = session(model, 'Sam', 'Add Jo Bloggs, jo@example.com.', -> { take_database_down }, 'y',
                         'Are you back?', -> { bring_database_back }, 'And now?', '/quit')

    # Once for the reply whose steps and end could not be saved, and once for the next.
    assert_equal 2, transcript.scan('[The session could not be saved: ').size
    assert_equal %i[user operator assistant user assistant user assistant user assistant],
                 stored(session_id(transcript)).messages.map(&:role)
  end

  def test_app10_a_session_another_console_went_on_with_is_not_overwritten_and_the_reply_goes_on
    model = ScriptedModel.new(ScriptedModel.text('Hello.'), ScriptedModel.text('Hello again.'))
    elsewhere = -> { select_row("update sessions set conversation = replace(conversation, 'Hi.', 'Hi there.')") }

    transcript = session(model, 'Sam', 'Hi.', elsewhere, 'Again.', '/quit')
    id = session_id(transcript)
    messages = stored(id).messages

    assert_in_order transcript, "you> Again.\nassistant> Hello again.\n[The session could not be saved: Session " \
                                "#{id} changed elsewhere since it was last saved here, so it was not overwritten. " \
                                "Type /resume #{id} to go on from what was saved.]\n"
    assert_equal 3, messages.size
    assert_equal 'Hi there.', messages.first.text
  end
end
