# frozen_string_literal: true

require 'test_helper'
require_relative 'database_server'

# The session store against the real database: a session reads back byte for byte with its totals, and a save never
# loses what another console saved.
class SessionStoreTest < Minitest::Test
  include DatabaseServer

  Officina = Sleepyshark::Officina
  ScriptedModel = Officina::Testing::ScriptedModel
  # Private to the application, which hands them only to the console.
  SessionStore = Bookshop.const_get(:SessionStore)
  SessionChangedError = Bookshop.const_get(:SessionChangedError)
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

  def test_app10_a_saved_session_loads_back_byte_for_byte_with_its_totals
    conversation = conversation_after('Hi.', 'Hello.')

    saved = save(conversation, cost: BigDecimal('0.012345'))
    stored = @store.load(conversation.id)

    assert_equal conversation.to_json, saved
    assert_equal [saved, saved, 'Sam', USAGE, BigDecimal('0.012345')],
                 [stored.saved, stored.conversation.to_json, stored.staff_member, stored.usage, stored.cost]
    assert_nil @store.load('nobody')
  end

  def test_app10_a_save_replaces_the_totals_and_moves_the_time_only_when_the_conversation_changed
    conversation = conversation_after('Hi.', 'Hello.')
    saved = save(conversation, cost: BigDecimal(1))
    backdate(conversation.id)

    saved = save(conversation, usage: USAGE + USAGE, cost: BigDecimal(2), previous: saved)

    assert_equal [USAGE + USAGE, BigDecimal(2)], @store.load(conversation.id).to_h.values_at(:usage, :cost)
    assert_equal [Time.utc(2026, 1, 1)], @store.list(20).map(&:updated)

    chat(conversation, 'Again.', 'Hello again.')
    save(conversation, cost: BigDecimal(2), previous: saved)

    assert_operator @store.list(20).first.updated, :>, Time.utc(2026, 1, 1)
  end

  def test_app10_a_save_whose_answer_was_lost_is_saved_over_its_earlier_state
    conversation = conversation_after('Hi.', 'Hello.')
    first = save(conversation)
    chat(conversation, 'Again.', 'Hello again.')
    save(conversation, previous: first)
    chat(conversation, 'Once more.', 'Hello once more.')

    # This console never learnt that its second save landed, so it still expects the first.
    saved = save(conversation, previous: first)

    assert_equal saved, @store.load(conversation.id).saved
  end

  def test_app10_a_session_another_console_went_on_with_is_not_overwritten
    conversation = conversation_after('Hi.', 'Hello.')
    first = save(conversation)
    elsewhere = Officina::Conversation.from_json(first)
    chat(elsewhere, 'Elsewhere.', 'Hello from elsewhere.')
    theirs = save(elsewhere, previous: first)
    chat(conversation, 'Here.', 'Hello from here.')

    error = assert_raises(SessionChangedError) { save(conversation, previous: first) }

    assert_equal "Session #{conversation.id} changed elsewhere since it was last saved here, so it was not " \
                 'overwritten.', error.message
    assert_equal theirs, @store.load(conversation.id).saved
  end

  def test_app10_a_new_session_whose_id_another_session_has_is_not_saved
    taken = conversation_after('Hi.', 'Hello.')
    theirs = save(taken)
    mine = Officina::Conversation.new(id: taken.id)
    chat(mine, 'Mine.', 'Yours.')

    assert_raises(SessionChangedError) { save(mine, staff_member: 'Jo') }
    assert_equal theirs, @store.load(taken.id).saved
  end

  def test_app10_a_session_deleted_elsewhere_is_not_saved_again
    conversation = conversation_after('Hi.', 'Hello.')
    saved = save(conversation)
    select_row('delete from sessions')

    assert_raises(SessionChangedError) { save(conversation, previous: saved) }
    assert_nil @store.load(conversation.id)
  end

  def test_app10_a_stored_session_that_is_not_a_conversation_cannot_be_loaded
    select_row(<<~SQL)
      insert into sessions (id, staff_member, conversation, input_tokens, output_tokens, cache_read_tokens,
                            cache_write_tokens, cost, updated)
      values ('broken', 'Sam', '{}', 0, 0, 0, 0, 0, now())
    SQL

    error = assert_raises(Officina::Error) { @store.load('broken') }

    assert_equal "Not a conversation's JSON: a member is missing or of the wrong type", error.message
  end

  def test_app02_the_sessions_are_listed_latest_first_with_no_title_until_one_is_written
    ids = %w[first second third].map { conversation_after(it, 'Hello.').tap { save(it, cost: BigDecimal('0.5')) }.id }
    backdate(ids.first)

    listed = @store.list(2)

    assert_equal 2, listed.size
    assert_equal [nil, 'Sam', BigDecimal('0.5')], listed.first.to_h.values_at(:title, :staff_member, :cost)
    assert_equal ids.drop(1).sort, listed.map(&:id).sort
    assert_equal ids.first, @store.list(3).last.id
  end

  private

  # Saves the conversation as the store's other tests do, unless told otherwise.
  def save(conversation, previous: nil, staff_member: 'Sam', usage: USAGE, cost: BigDecimal(0))
    @store.save(conversation, staff_member:, usage:, cost:, previous:)
  end

  # A new conversation after a run in which the model answered the message with the reply.
  def conversation_after(message, reply) = Officina::Conversation.new.tap { chat(it, message, reply) }

  def chat(conversation, message, reply)
    agent = Officina::Agent.new(model: ScriptedModel.new(ScriptedModel.text(reply)), instructions: 'Help.')
    agent.run(conversation, message) { nil }
  end

  # Sets the session's time to the start of 2026, as if it was saved then.
  def backdate(id) = select_row("update sessions set updated = '2026-01-01Z' where id = $1", id)
end
