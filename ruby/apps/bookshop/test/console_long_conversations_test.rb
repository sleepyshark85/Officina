# frozen_string_literal: true

require 'test_helper'
require_relative 'console_session'

# Long conversations end to end: the compaction and clearing the chat agent asks its provider for, early in demo mode,
# what the console and /audit show of each, and a session going on past a compaction.
class ConsoleLongConversationsTest < Minitest::Test
  include ConsoleSession

  DEMO = Officina::ContextManagement.new(
    compact_at: 50_000, clear_tool_results: Officina::ToolResultClearing.new(after: 12, keep: 10)
  )
  COMPACTED = Officina::ConversationCompacted.new(tokens: 52_753, summary_tokens: 578)

  def test_app17_hist04_demo_mode_compacts_and_clears_early_and_the_console_and_the_audit_report_each
    model = search_clear_and_compact(Officina::ToolResultsCleared.new(tokens: 4_892, tool_calls: 2))

    transcript = session(model, 'Sam', 'Where is The Winter Archive?', '/audit', '/quit', demo: true)

    assert_in_order transcript, "Bookshop Assistant. Type /help for commands.\nDemo mode: compaction from 50,000 ",
                    "  ~ Old tool results cleared: 2 tool calls, 4,892 tokens.\n", "  < search_books: ok\n",
                    "  ~ Conversation compacted: 52,753 tokens summarized into 578.\n", "It is on shelf Q2.\n",
                    'you> /audit', 'Cleared', "  Results of 2 tool calls cleared: 4,892 tokens.\n",
                    'Cleared', "  Results of 2 tool calls cleared: 4,892 tokens.\n",
                    'Compacted', "  52,753 tokens summarized into 578.\n", 'you> /quit'
    assert_equal 1, transcript.scan('~ Old tool results cleared').size, transcript
    assert_equal [DEMO, DEMO], model.requests.map(&:context_management)
  end

  def test_hist01_hist02_outside_demo_mode_compaction_and_clearing_come_late_with_dotnets_and_gos_settings
    model = ScriptedModel.new(ScriptedModel.text('Hello.'))

    transcript = session(model, 'Sam', 'Hi.', '/quit')

    refute_includes transcript, 'Demo mode'
    settings = model.requests.first.context_management

    assert_equal Officina::ContextManagement.new(
      compact_at: 150_000,
      clear_tool_results: Officina::ToolResultClearing.new(after: 20, keep: 5, at_least_tokens: 20_000)
    ), settings
    assert_equal ',"contextManagement":{"compactAt":150000,"clearAfter":20,"clearKeep":5,"clearAtLeastTokens":20000}',
                 settings.fingerprint
  end

  def test_hist04_a_clearing_the_provider_repeats_is_shown_once_in_the_session_and_a_new_one_again
    model = ScriptedModel.new(
      [Officina::ToolResultsCleared.new(tokens: 42_452, tool_calls: 3), *say_then_call('One.', lookup('c1', 1))],
      [Officina::ToolResultsCleared.new(tokens: 42_359, tool_calls: 3), *ScriptedModel.text('Two.')],
      [Officina::ToolResultsCleared.new(tokens: 42_400, tool_calls: 3), *say_then_call('Three.', lookup('c2', 2))],
      [Officina::ToolResultsCleared.new(tokens: 56_912, tool_calls: 6), *ScriptedModel.text('Four.')]
    )

    transcript = session(model, 'Sam', 'Look up book 1.', 'And book 2.', '/audit', '/quit', demo: true)
    shown, audited = transcript.split('you> /audit')

    assert_in_order shown, "  ~ Old tool results cleared: 3 tool calls, 42,452 tokens.\n", 'Two.', 'Three.',
                    "  ~ Old tool results cleared: 6 tool calls, 56,912 tokens.\n", 'Four.'
    assert_equal 2, shown.scan('~ Old tool results cleared').size, shown
    # The audit trail keeps every report, as the provider made it.
    assert_equal 4, audited.scan('tool calls cleared').size, audited
  end

  def test_app01_hist04_a_reply_without_text_says_so_and_whether_the_conversation_was_compacted
    model = ScriptedModel.new(
      [COMPACTED, Officina::Reply.new(blocks: [ScriptedModel.compaction_block('Sam asked.')], stop: :end)],
      [Officina::Reply.new(blocks: [thinking], stop: :end)]
    )

    transcript = session(model, 'Sam', 'Count the books.', 'And now?', '/quit', demo: true)

    assert_in_order transcript, "you> Count the books.\n",
                    "  ~ Conversation compacted: 52,753 tokens summarized into 578.\n",
                    "[The conversation was compacted and the reply has no text. Please ask again.]\n",
                    "you> And now?\n", "[The reply has no text. Please ask again.]\n", 'you> /quit'
  end

  def test_hist01_a_session_goes_on_past_a_compaction_keeping_its_block_and_replaying_it_as_received
    summary = ScriptedModel.compaction_block('Sam asked where The Winter Archive is.')
    model = ScriptedModel.new(
      compacting('On shelf Q2.', summary),
      ScriptedModel.text('Yes, two copies.')
    )

    transcript = session(model, 'Sam', 'Where is The Winter Archive?', 'Is it in stock?', '/quit')

    assert_in_order transcript, "assistant> On shelf Q2.\n", "assistant> Yes, two copies.\n"
    replayed = model.requests.last.messages.find { it.role == :assistant }

    assert_equal summary.raw, replayed.blocks.first.raw
    assert_equal summary.raw, stored(session_id(transcript)).messages.find { it.role == :assistant }.blocks.first.raw
  end

  private

  # A model that searches, its provider clearing old tool results, then answers, its provider clearing the same
  # results again, as it does on every later call, and compacting the conversation.
  def search_clear_and_compact(clearing)
    ScriptedModel.new(
      [Officina::TextDelta.new(text: 'Searching.'), clearing,
       Officina::Reply.new(blocks: [ScriptedModel.text_block('Searching.'), call('c1', 'search_books', '{}')],
                           stop: :tool_use)],
      [clearing, *compacting('It is on shelf Q2.')]
    )
  end

  # The steps of a reply that compacts the conversation into the summary, then streams the text and ends the turn.
  def compacting(text, summary = ScriptedModel.compaction_block('Sam asked about a book.'))
    [COMPACTED, Officina::TextDelta.new(text:),
     Officina::Reply.new(blocks: [summary, ScriptedModel.text_block(text)], stop: :end)]
  end

  def lookup(id, book) = call(id, 'get_book', %({"bookId":#{book}}))

  # A thinking block with no text, all a reply may hold.
  def thinking = Officina::Block.new(raw: '{"type":"thinking","thinking":"","signature":"c2ln"}')
end
