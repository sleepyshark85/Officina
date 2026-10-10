# frozen_string_literal: true

# Conversations for the session store's tests, made by real runs of an agent on the scripted model.
module StoredConversations
  Officina = Sleepyshark::Officina
  ScriptedModel = Officina::Testing::ScriptedModel

  private

  # A new conversation after a run in which the model answered the message with the reply.
  def conversation_after(message, reply) = Officina::Conversation.new.tap { chat(it, message, reply) }

  def chat(conversation, message, reply)
    agent = Officina::Agent.new(model: ScriptedModel.new(ScriptedModel.text(reply)), instructions: 'Help.')
    agent.run(conversation, message) { nil }
  end
end
