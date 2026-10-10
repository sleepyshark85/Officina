# frozen_string_literal: true

require 'json'
require 'sleepyshark/officina/testing'

# The prefix and sessions the implementations share in the repository's testdata/session: the agent of the shared
# prefix, and the session Ruby saves mid-reply, which testdata/session/ruby-session.json holds. That file is written
# once, by running this file from ruby/ (see ruby/docs/design.md), never by a test.
module SharedSession
  Officina = Sleepyshark::Officina
  Model = Officina::Testing::ScriptedModel
  SESSION = File.expand_path('../../../../testdata/session', __dir__)
  # The result each call left without one gets when the conversation is resumed.
  INTERRUPTED = 'The call was interrupted: the application stopped before its result was recorded, so it may or ' \
                'may not have taken effect.'
  # A thinking block as the provider might send it: odd spacing and escapes, kept byte for byte.
  THINKING = '{ "type":"thinking",  "thinking":"caf\\u00e9 \\"quoted\\" \\/ é", "signature":"c2ln+/=" }'

  # @return [String] the shared file's text
  def self.file(name) = File.read(File.join(SESSION, name), encoding: 'UTF-8')

  # @return [Hash] the shared prefix: model settings, instructions, tools and the fingerprint .NET computed
  def self.prefix = JSON.parse(file('prefix.json'))

  # The agent of the shared prefix on the model: its search tool reads, and place_order writes; each answers "ok".
  def self.agent(model, **parts)
    prefix = self.prefix
    tools = prefix['tools'].map do |tool|
      Officina::Tool.new(name: tool['name'], description: tool['description'],
                         input: Officina::Schema.new(tool['inputSchema']),
                         kind: tool['name'] == 'place_order' ? :write : :read) { |_, _| 'ok' }
    end
    Officina::Agent.new(model:, instructions: prefix['instructions'], tools:, **parts)
  end

  # A scripted model with the shared prefix's settings.
  def self.model(*replies) = Model.new(*replies, settings: prefix['settings'])

  # The session's JSON as saved when the application stopped while the order's tool ran: after the reply that asked
  # for it was appended, before its result was.
  def self.saved_mid_reply
    search = Model.tool_use_block('rb_01', 'search', '{"query":"café"}')
    model = model([Officina::Reply.new(blocks: [Officina::Block.new(raw: THINKING),
                                                Model.text_block('Looking up «Café Libro» & <friends>.'), search],
                                       stop: :tool_use)],
                  Model.text('It is in stock: 3 copies.'),
                  Model.tool_use(Model.tool_use_block('rb_02', 'place_order', '{"bookId":7,"quantity":1}')))
    agent = agent(model)
    conversation = Officina::Conversation.new(id: 'ruby-0001')
    agent.run(conversation, 'Is Café Libro in stock?',
              context: 'Today is Friday 9 October 2026. The staff member is Zoë.')
    crash(agent, conversation, 'Order one copy for <Ann & Bob>.')
  end

  # Runs the agent until it appends a reply, and returns the conversation's JSON then, as a host that saves after
  # every append has it when the application stops.
  def self.crash(agent, conversation, input)
    agent.run(conversation, input) do |event|
      break conversation.to_json if event.is_a?(Officina::ConversationAppended) && event.message.role == :assistant
    end
  end
end

$stdout.write(SharedSession.saved_mid_reply, "\n") if $PROGRAM_NAME == __FILE__
