# frozen_string_literal: true

require 'test_helper'
require_relative 'console_session'

# Sessions end to end: each saved after every step of a reply, resumed after a restart or a crash, listed and
# started anew.
class ConsoleSessionsTest < Minitest::Test
  include ConsoleSession

  # The process ending in the middle of a reply.
  class Crash < StandardError
  end

  ADD = '{"name":"Jo Bloggs","email":"jo@example.com"}'

  def test_app10_quit_restart_and_resume_go_on_with_the_saved_conversation_and_its_prefix
    first = ScriptedModel.new(ScriptedModel.text('Hello.'))
    id = session_id(session(first, 'Sam', 'Hi.', '/quit'))
    second = ScriptedModel.new(ScriptedModel.text('Hello again.'))
    saved = nil

    transcript = session(second, 'Jo', -> { saved = stored(id) }, "/resume #{id}", 'Again.', '/quit')

    assert_in_order transcript, "you> /resume #{id}\nResumed session #{id}: 3 messages, $0.0000 so far.\n",
                    "assistant> Hello again.\n"
    request = second.requests.first

    assert_equal saved.messages, request.messages.first(3)
    assert_equal prefix(first.requests.first), prefix(request)
    assert_equal 'Today is ' \
                 "#{Time.now.strftime('%A %-d %B %Y')}. The staff member using the assistant is Jo.",
                 request.messages.last.text
    assert_equal %i[user operator assistant user operator assistant], stored(id).messages.map(&:role)
  end

  def test_app10_a_crash_mid_reply_loses_only_the_step_in_flight_and_resuming_tells_the_model
    model = ScriptedModel.new(say_then_call('Adding Jo.', call('c1', 'add_customer', ADD)))
    assert_raises(Crash) { session(model, 'Sam', 'Add Jo Bloggs, jo@example.com.', -> { raise Crash }) }
    id = select_text('select id from sessions')

    assert_equal %i[user operator assistant], stored(id).messages.map(&:role)
    later = ScriptedModel.new(ScriptedModel.text('Jo may not have been added.'))

    session(later, 'Sam', "/resume #{id}", 'Was Jo added?', '/quit')

    interrupted = later.requests.first.messages[3].blocks.filter_map(&:tool_result)

    assert_equal([['c1', true]], interrupted.map { [it.call_id, it.error?] })
    assert_includes interrupted.first.content, 'interrupted'
    assert_equal 0, select_integer("select count(*) from customers where email = 'jo@example.com'")
  end

  def test_app10_resume_refuses_a_session_another_version_started_and_the_session_in_use_goes_on
    id = session_id(session(ScriptedModel.new(ScriptedModel.text('Hello.'), settings: 'version 1'), 'Sam', 'Hi.',
                            '/quit'))
    model = ScriptedModel.new(ScriptedModel.text('Hello, new session.'), settings: 'version 2')

    transcript = session(model, 'Sam', "/resume #{id}", '/resume', '/resume nobody', 'Hi.', '/quit')

    assert_in_order transcript, "Session #{id} was started with another version of the assistant, so it cannot go " \
                                "on. Type /new to start a new session.\n",
                    "you> /resume\nWhich session? Type /resume <id>; /sessions lists them.\n",
                    "you> /resume nobody\nThere is no session nobody. Type /sessions to list them.\n",
                    "assistant> Hello, new session.\n"
    assert_equal %i[user operator], model.requests.first.messages.map(&:role)
  end

  def test_app02_sessions_lists_the_latest_first_marking_the_one_in_use_and_new_starts_another
    model = ScriptedModel.new(ScriptedModel.text('One.'), ScriptedModel.text('Two.'))

    transcript = session(model, 'Sam', '/sessions', 'Hi.', '/new', 'Hello.', '/sessions', '/quit')
    first = session_id(transcript)
    second = transcript[/^New session (\h{12})\.$/, 1]
    listed = ->(id) { "#{id}  \\w{3} \\d{1,2} \\w{3} \\d\\d:\\d\\d  Sam  \\(no title yet\\)  \\$0\\.0000\\n" }

    assert_in_order transcript, "you> /sessions\nNo sessions yet.\n", "you> /new\nNew session #{second}.\n"
    assert_match(%r{you> /sessions\nSessions, most recent first:\n\* #{listed[second]}  #{listed[first]}}, transcript)
    assert_equal([%i[user operator assistant]] * 2, [first, second].map { stored(it).messages.map(&:role) })
  end

  def test_app10_a_session_that_cannot_be_saved_is_told_once_a_reply_and_the_next_save_stores_everything
    model = ScriptedModel.new(say_then_call('Adding Jo.', call('c1', 'add_customer', ADD)),
                              ScriptedModel.text('It could not be added.'), ScriptedModel.text('Back again.'))

    transcript = session(model, 'Sam', 'Add Jo Bloggs, jo@example.com.', -> { take_database_down }, 'y',
                         -> { bring_database_back }, 'Are you back?', '/quit')

    assert_equal 1, transcript.scan('[The session could not be saved: ').size
    assert_equal %i[user operator assistant user assistant user assistant],
                 stored(session_id(transcript)).messages.map(&:role)
  end

  private

  # What a request sends before its messages: the instructions and the tools.
  def prefix(request) = [request.instructions, request.tools.map { [it.name, it.description, it.input_schema] }]

  def session_id(transcript) = transcript[/^Session (\h{12})\.$/, 1]

  # The conversation the session with the id is stored with.
  def stored(id) = Officina::Conversation.from_json(select_text('select conversation from sessions where id = $1', id))

  def select_text(sql, *params) = select_row(sql, *params).first
end
