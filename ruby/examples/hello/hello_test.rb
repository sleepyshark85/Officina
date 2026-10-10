# frozen_string_literal: true

require 'stringio'
require_relative '../../officina-claude/test/claude_test_case'
require_relative 'hello'

# The hello sample, on the fake API instead of Claude.
class HelloTest < ClaudeTestCase
  CHAT = <<~CHAT
    Hello: chat with Claude. An empty line quits.
    > Hello
    Hi there.
    [completed · input 10 · cache read 0 · cache write 0 · output 5]
    > Goodbye
    Bye.
    [completed · input 10 · cache read 0 · cache write 0 · output 5]
  CHAT

  def test_mdl02_hello_chats_until_an_empty_line_and_shows_each_calls_tokens
    api = serve(text_reply(text: 'Hi there.'), text_reply(text: 'Bye.'))
    agent = Agent.new(model: model(api), instructions: Hello::INSTRUCTIONS)
    output = StringIO.new

    Hello.chat(agent, input: StringIO.new("Hello\nGoodbye\n\nNot sent\n"), output:, today: Time.new(2026, 10, 10))

    assert_equal CHAT, output.string
    assert_includes api.bodies[1], '{"role":"system","content":"Today is Saturday 10 October 2026."}'
  end
end
