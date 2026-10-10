# frozen_string_literal: true

require 'test_helper'
require_relative 'console_session'

# A session summary the database fails to store, end to end: the real console, agents, core and database, with the
# chat and summarizer models and the staff member scripted.
class ConsoleSummarySavesTest < Minitest::Test
  include ConsoleSession

  def test_app15_a_summary_that_cannot_be_saved_is_still_shown_and_the_session_is_tried_again_later
    first, = left_with_unsavable_summary

    id = session_id(first)

    assert_in_order first, "you> /new\n[The summary of session #{id} could not be saved: #{UNSAVABLE}"
    assert_includes first, "Session #{id} summarized: Greeting\n"
    assert_equal [nil, 't'], select_row('select title, summarized is null from sessions where id = $1', id)
  end

  def test_app20_a_summary_that_cannot_be_saved_is_logged_as_a_warning_in_its_trace
    first, logged = left_with_unsavable_summary

    warning = logged.find { it.body.include?('not saved') }

    assert_equal 'WARN', warning.severity_text
    assert_includes warning.body, "Session #{session_id(first)} summary not saved: #{UNSAVABLE}"
    refute_equal OpenTelemetry::Trace::INVALID_SPAN_ID, warning.span_id
  end

  UNSAVABLE = 'ERROR:  column "summarized" of relation "sessions" does not exist'

  private

  # A session left with /new whose summary cannot be saved, as the column is missing; returns its console's
  # transcript and the log records written by then.
  def left_with_unsavable_summary
    gone = -> { execute('alter table sessions rename column summarized to summarized_gone') }
    telemetry = MemoryTelemetry.new
    logged = []
    transcript = session(ScriptedModel.new(ScriptedModel.text('Hello.')), 'Sam', 'Hi.', gone, '/new',
                         -> { logged.concat(telemetry.logs) }, '/quit',
                         summarizer: ScriptedModel.new(summary_reply('Greeting', 'Sam said hello.')), telemetry:)
    execute('alter table sessions rename column summarized_gone to summarized')
    [transcript, logged]
  end
end
