# frozen_string_literal: true

require 'test_helper'
require 'tmpdir'
require_relative 'console_session'

# Memory per staff member through the console end to end. Each session is an application start: what one remembers,
# the next finds only in the memory store.
class ConsoleMemoryTest < Minitest::Test
  include ConsoleSession

  SAVE = '{"command":"create","path":"/memories/preferences.md","file_text":"Show prices with tax.\n"}'
  VIEW = '{"command":"view","path":"/memories/preferences.md"}'

  def test_app11_memory_shows_what_is_remembered_for_the_staff_member_at_the_counter
    memory = Officina::HashMemoryStore.new
    memory.write('sam', 'preferences.md', "Prices with tax.\n\nBrief answers.\n\n")
    memory.write('sam', 'customers/ana.md', 'Likes crime.')
    memory.write('ana', 'notes.md', "Ana's notes.")

    sam = session(ScriptedModel.new, '../sam', 'Sam', '/memory', '/quit', memory:)
    ben = session(ScriptedModel.new, 'Ben', '/memory', '/quit', memory:)

    assert_in_order sam, "Your name: ../sam\nThat name cannot be used. Please give another name.\n",
                    "Your name: Sam\nHello, Sam.\n",
                    "you> /memory\nRemembered:\n/memories/customers/ana.md\n  Likes crime.\n" \
                    "/memories/preferences.md\n  Prices with tax.\n  \n  Brief answers.\nyou> /quit"
    refute_includes sam, "Ana's"
    assert_in_order ben, "you> /memory\nNothing remembered yet.\nyou> /quit"
  end

  def test_app11_memory_that_cannot_be_read_is_said_in_one_line_and_the_session_goes_on
    Dir.mktmpdir('r09b2-') do |root|
      Officina::FileMemoryStore.new(root).write('sam', 'preferences.md', 'Prices with tax.')
      File.binwrite(File.join(root, 'sam'.unpack1('H*'), 'preferences.md'), "\xFF")

      transcript = session(ScriptedModel.new, 'Sam', '/memory', '/help', '/quit',
                           memory: Officina::FileMemoryStore.new(root))

      assert_in_order transcript, "you> /memory\nThe memory could not be read: The memory file preferences.md is " \
                                  "not UTF-8 text.\nyou> /help\nCommands:"
    end
  end

  def test_app11_mem03_mem04_mem05_a_preference_saved_in_one_session_is_applied_in_a_new_one
    Dir.mktmpdir('r09b2-') do |root|
      first, second, transcript = remember_then_recall(root)

      assert_equal ["Here's the content of /memories/preferences.md with line numbers:\n     1\tShow prices with tax."],
                   results(second, -1).map(&:content)
      assert_in_order transcript, "  > memory #{VIEW}\n", "  < memory: ok\n", 'costs £7.54 with tax.',
                      "you> /memory\nRemembered:\n/memories/preferences.md\n  Show prices with tax.\n"
      assert_memory_kept_out_of_the_prefix(first, second)
      assert_equal [%w[ToolStarted sam], %w[ToolEnded sam]],
                   execute("select kind, memory_scope from audit where call_id = 'save-1' order by id").values
    end
  end

  private

  # Sam states a preference in one application start, which the model saves; in the next, with a new store over the
  # same folder, the model views it before it answers. Returns both models and the second start's transcript.
  def remember_then_recall(root)
    first = ScriptedModel.new(say_then_call("I'll remember that.", call('save-1', 'memory', SAVE)),
                              ScriptedModel.text('Noted: prices with tax from now on.'))
    session(first, 'Sam', 'I prefer prices with tax.', '/quit', memory: Officina::FileMemoryStore.new(root))
    second = ScriptedModel.new(ScriptedModel.tool_use(call('view-1', 'memory', VIEW)),
                               ScriptedModel.text('The Winter Archive costs £7.54 with tax.'))
    transcript = session(second, 'sam', 'What does book 144 cost?', '/memory', '/quit',
                         memory: Officina::FileMemoryStore.new(root))
    [first, second, transcript]
  end

  # The run context names who is at the counter, and the instructions never change with memory.
  def assert_memory_kept_out_of_the_prefix(first, second)
    requests = first.requests + second.requests
    instructions = requests.map(&:instructions).uniq

    assert_includes second.requests.first.messages.map(&:text).join("\n"),
                    'The staff member using the assistant is sam.'
    assert_equal [first.requests.first.instructions], instructions
    refute_includes instructions.first, 'Show prices with tax.'
  end
end
