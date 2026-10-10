# frozen_string_literal: true

require 'test_helper'
require_relative 'console_session'

# Budgets and spending end to end: each reply's status line, /cost, what the session store keeps, and the budgets
# that stop a reply.
class ConsoleBudgetsTest < Minitest::Test
  include ConsoleSession

  def test_app14_each_reply_ends_with_its_tokens_and_cost_and_the_sessions_which_cost_shows_and_the_store_keeps
    model = ScriptedModel.new(
      ScriptedModel.text('One.',
                         usage: Officina::Usage.new(input: 1000, output: 200, cache_read: 3000, cache_write: 1000)),
      ScriptedModel.text('Two.', usage: Officina::Usage.new(input: 100, output: 50, cache_read: 4900))
    )

    transcript = session(model, 'Sam', 'Hi.', 'Again.', '/cost', '/quit')
    id = session_id(transcript)

    assert_in_order transcript, "assistant> One.\n[tokens: 5,000 in (60% from cache), 200 out · reply $0.0178 · " \
                                "session $0.0178]\n",
                    "assistant> Two.\n[tokens: 5,000 in (98% from cache), 50 out · reply $0.0042 · session $0.0220]\n",
                    "you> /cost\nSession #{id}: tokens: 10,000 in (79% from cache), 250 out; cost $0.0220 of its " \
                    "$5.00 budget.\n"
    assert_equal ['1100', '250', '7900', '1000', '0.02195'],
                 select_row('select input_tokens, output_tokens, cache_read_tokens, cache_write_tokens, cost ' \
                            'from sessions')
  end

  def test_app14_a_reply_that_reaches_its_budget_stops_and_says_so
    usage = Officina::Usage.new(input: 3000)
    model = ScriptedModel.new(ScriptedModel.tool_use(call('c1', 'search_books', '{"title":"Winter"}'), usage:))

    transcript = session(model, 'Sam', 'Do we have The Winter Archive?', '/quit',
                         env: { 'BOOKSHOP_REPLY_BUDGET' => '0.01' })

    assert_in_order transcript, "[Stopped: this reply has reached its budget of $0.01.]\n" \
                                "[tokens: 3,000 in (0% from cache), 0 out · reply $0.0150 · session $0.0150]\n"
    assert_equal 1, model.requests.size
  end

  def test_app14_a_reply_the_budget_cut_short_is_not_kept_and_its_cost_still_counts
    cut = Officina::Reply.new(blocks: [call('c1', 'search_books', '{"title":"Winter"}')], stop: :max_tokens)
    model = ScriptedModel.new([Officina::UsageReported.new(usage: Officina::Usage.new(input: 3000)), cut])

    transcript = session(model, 'Sam', 'Do we have The Winter Archive?', '/cost', '/quit',
                         env: { 'BOOKSHOP_REPLY_BUDGET' => '0.01' })

    assert_in_order transcript, "[Stopped: this reply has reached its budget of $0.01.]\n",
                    "you> /cost\nSession #{session_id(transcript)}: tokens: 3,000 in (0% from cache), 0 out; " \
                    "cost $0.0150 of its $5.00 budget.\n"
    assert_equal [%({"id":"#{session_id(transcript)}","messages":[]}), '0.015'],
                 select_row('select conversation, cost from sessions')
  end

  def test_app14_a_reply_that_reaches_the_sessions_budget_stops_and_says_to_start_a_new_session
    select_row(<<~SQL)
      insert into sessions (id, staff_member, conversation, input_tokens, output_tokens, cache_read_tokens,
                            cache_write_tokens, cost, updated)
      values ('a1b2c3d4e5f6', 'Sam', '{"id":"a1b2c3d4e5f6","messages":[]}', 0, 0, 0, 0, 4.9999, now())
      returning id
    SQL
    usage = Officina::Usage.new(input: 3000)
    model = ScriptedModel.new(ScriptedModel.tool_use(call('c1', 'search_books', '{"title":"Winter"}'), usage:))

    transcript = session(model, 'Sam', '/resume a1b2c3d4e5f6', 'Do we have The Winter Archive?', '/quit')

    assert_in_order transcript, "Resumed session a1b2c3d4e5f6: 0 messages, $4.9999 so far.\n",
                    "[Stopped: this session has reached its budget of $5.00. Type /new to start a new session.]\n" \
                    "[tokens: 3,000 in (0% from cache), 0 out · reply $0.0150 · session $5.0149]\n"
    assert_equal 1, model.requests.size
  end
end
