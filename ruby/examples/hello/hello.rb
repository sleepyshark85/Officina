# frozen_string_literal: true

# A live chat with Claude through Officina. It needs an API key in ANTHROPIC_API_KEY. Type a message per line; an
# empty line or the end of the input quits. After each reply a status line shows the call's tokens: from the second
# message on, the instructions and earlier turns are read from the cache. From ruby/:
#
#   bundle exec ruby examples/hello/hello.rb

require 'sleepyshark/officina/claude'

# The sample: a chat on standard input and output.
module Hello
  include Sleepyshark::Officina

  # Frozen, and long enough to pass the model's minimum cacheable prefix.
  INSTRUCTIONS = [
    'You are Hello, a friendly assistant in a small demonstration of the Officina library. Answer in one to three ' \
    'short sentences unless the user asks for more. Be warm, concrete and plain-spoken.',
    '',
    'House rules:',
    *(1..40).map do |rule|
      "#{rule}. When a question touches topic number #{rule}, answer from general knowledge, say so when you are " \
        'unsure, never invent facts, figures or quotations, and offer one useful next step if it helps.'
    end
  ].join("\n").freeze

  # Chats with the agent over the lines of +input+, writing to +output+, until an empty line or the end.
  def self.chat(agent, input: $stdin, output: $stdout, today: Time.now)
    conversation = Conversation.new
    output.puts 'Hello: chat with Claude. An empty line quits.'
    input.each_line(chomp: true) do |line|
      break if line.empty?

      output.puts "> #{line}"
      # The date is run context, sent after the message; the instructions never change.
      result = agent.run(conversation, line, context: "Today is #{today.strftime('%A %-d %B %Y')}.") do |event|
        show(event, output)
      end
      output.puts "\n#{status(result)}"
    end
  end

  def self.show(event, output)
    case event
    in TextDelta(text:) then output.print text
    in Retried then output.puts ' [the reply was interrupted and starts again]'
    else nil
    end
  end

  def self.status(result)
    outcome = case result
              in Completed then 'completed'
              in Stopped(reason:, detail:) then ["stopped (#{reason})", detail].compact.join(' ')
              in Failed(reason:, detail:) then "failed (#{reason}): #{detail}"
              end
    usage = result.usage
    "[#{outcome} · input #{usage.input} · cache read #{usage.cache_read} · cache write #{usage.cache_write} · " \
      "output #{usage.output}]"
  end
end

if $PROGRAM_NAME == __FILE__
  model = Sleepyshark::Officina::Claude::Model.new(name: 'claude-opus-5-5', effort: :low, max_output_tokens: 2000,
                                                   prefix_cache: '1h', conversation_cache: '1h')
  Hello.chat(Sleepyshark::Officina::Agent.new(model:, instructions: Hello::INSTRUCTIONS))
end
