# frozen_string_literal: true

require 'bookshop'
require 'test_helper'

# The transcript the summarizer reads.
class TranscriptTest < Minitest::Test
  Transcript = Bookshop.const_get(:Transcript)

  def test_app15_a_transcript_names_each_results_call_marks_errors_cuts_long_results_and_leaves_out_the_context
    long = 'é' * 1_001
    conversation = Sleepyshark::Officina::Conversation.from_json(
      File.read(File.expand_path('fixtures/transcript-conversation.json', __dir__)).sub('<long>', long)
    )

    assert_equal <<~TEXT, Transcript.of(conversation)
      Staff: Order Emma.
      Assistant: Looking.
      Tool call search_books {"title":"Emma"}
      Tool call place_order {}
      Tool result of search_books: #{'é' * 1_000} […]
      Tool result of place_order (error): Not enough stock.
      Tool result of a call (error): Gone.
      Assistant: Emma is out of stock.
    TEXT
  end

  def test_app15_a_result_of_exactly_the_most_characters_kept_is_kept_whole
    result = { toolResult: { callId: 'c1', content: 'a' * 1_000, isError: false } }
    conversation = Sleepyshark::Officina::Conversation.from_json(
      JSON.generate({ id: 's1', messages: [{ role: 'user', blocks: [result] }] })
    )

    assert_equal "Tool result of a call: #{'a' * 1_000}\n", Transcript.of(conversation)
  end
end
