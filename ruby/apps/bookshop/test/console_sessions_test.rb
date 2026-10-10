# frozen_string_literal: true

require 'test_helper'
require_relative 'console_session'

# The session commands end to end: a session resumed after a restart, or refused, and the sessions listed and started
# anew.
class ConsoleSessionsTest < Minitest::Test
  include ConsoleSession

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

  def test_app13_resuming_as_the_same_staff_member_on_the_same_day_sends_no_new_context
    noon = -> { Time.new(2026, 10, 10, 12) }
    id = session_id(session(ScriptedModel.new(ScriptedModel.text('Hello.')), 'Sam', 'Hi.', '/quit', clock: noon))
    model = ScriptedModel.new(ScriptedModel.text('Hello again.'))

    session(model, 'Sam', "/resume #{id}", 'Again.', '/quit', clock: noon)

    assert_equal %i[user operator assistant user], model.requests.first.messages.map(&:role)
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

  def test_app10_resume_says_a_stored_session_cannot_be_read_and_the_session_in_use_goes_on
    execute(<<~SQL)
      insert into sessions (id, staff_member, conversation, input_tokens, output_tokens, cache_read_tokens,
                            cache_write_tokens, cost, updated)
      values ('broken', 'Sam', '{}', 0, 0, 0, 0, 0, now())
    SQL
    model = ScriptedModel.new(ScriptedModel.text('Hello.'))

    transcript = session(model, 'Sam', '/resume broken', 'Hi.', '/quit')

    assert_in_order transcript, "you> /resume broken\nThe session could not be read: Not a conversation's JSON: " \
                                "a member is missing or of the wrong type\n",
                    "assistant> Hello.\n"
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

  def test_app02_sessions_says_so_when_the_sessions_cannot_be_read
    transcript = session(ScriptedModel.new, 'Sam', -> { take_database_down }, '/sessions', -> { bring_database_back },
                         '/quit')

    assert_match %r{you> /sessions\nThe sessions could not be read: .+\nyou> /quit\n}, transcript
  end

  private

  # What a request sends before its messages: the instructions and the tools.
  def prefix(request) = [request.instructions, request.tools.map { [it.name, it.description, it.input_schema] }]
end
