# frozen_string_literal: true

require 'test_helper'
require_relative 'database_server'
require_relative 'stored_conversations'

# The session store's summaries against the real database: a summary is listed, its cost added to the session's,
# until the conversation changes again.
class SessionStoreSummaryTest < Minitest::Test
  include DatabaseServer
  include StoredConversations

  # Private to the application, which hands them only to the console.
  SessionStore = Bookshop.const_get(:SessionStore)
  SessionSummary = Bookshop.const_get(:SessionSummary)
  USAGE = Officina::Usage.new(input: 10, output: 20, cache_read: 30, cache_write: 40)

  def setup
    super
    @database = Bookshop::Database.new(database_url)
    @store = SessionStore.new(database: @database)
  end

  def teardown
    @database&.close
    super
  end

  def test_app15_a_summary_is_listed_with_its_cost_added_until_the_conversation_changes_again
    conversation = conversation_after('Hi.', 'Hello.')
    saved = save(conversation, cost: BigDecimal('0.5'))
    summary = SessionSummary.new(title: 'Greeting', summary: 'Sam said hello.', changes: ['None really', "Sam's"])

    stale = @store.list(1).first.stale
    @store.save_summary(conversation.id, summary, usage: USAGE, cost: BigDecimal('0.25'))
    listed = @store.list(1).first
    tokens = select_row('select input_tokens, output_tokens, cache_read_tokens, cache_write_tokens from sessions')
    chat(conversation, 'Again.', 'Hello again.')
    save(conversation, previous: saved, cost: BigDecimal('0.75'))

    assert stale
    assert_equal ['Greeting', 'Sam said hello.', ['None really', "Sam's"], BigDecimal('0.75'), false],
                 listed.to_h.values_at(:title, :summary, :changes, :cost, :stale)
    assert_equal %w[20 40 60 80], tokens
    assert @store.list(1).first.stale
  end

  private

  def save(conversation, cost:, previous: nil)
    @store.save(conversation, staff_member: 'Sam', usage: USAGE, cost:, previous:)
  end
end
