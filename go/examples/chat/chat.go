package chat

import (
	"context"
	"encoding/json/v2"
	"fmt"
	"time"

	"github.com/sleepyshark85/officina/go/officina"
)

// Assistant answers each user in a conversation of their own, which it keeps as JSON in the host's storage between
// replies. One reply at a time: the storage is a plain map.
type Assistant struct {
	agent *officina.Agent
	// conversations is the host's storage, user id to conversation JSON; a database table in a real application.
	conversations map[string][]byte
}

// NewAgent returns the chat agent of model: a temperature conversion tool and the memory tool over memory.
func NewAgent(model officina.Model, memory officina.MemoryStore) (*officina.Agent, error) {
	temperature, err := officina.NewTool("celsius_to_fahrenheit",
		"Converts a temperature from degrees Celsius to degrees Fahrenheit.", officina.Read,
		func(_ context.Context, in celsius) (float64, error) { return in.Celsius*9/5 + 32, nil })
	if err != nil {
		return nil, fmt.Errorf("chat agent: %w", err)
	}
	agent, err := officina.NewAgent(model, instructions, officina.AgentOptions{
		Name: "chat", Tools: []officina.Tool{temperature, officina.NewMemoryTool(memory)},
	})
	if err != nil {
		return nil, fmt.Errorf("chat agent: %w", err)
	}
	return agent, nil
}

// celsius is the conversion tool's input.
type celsius struct {
	Celsius float64 `json:"celsius" jsonschema:"The temperature in degrees Celsius."`
}

// NewAssistant returns the assistant of agent, made by NewAgent, keeping conversations in conversations.
func NewAssistant(agent *officina.Agent, conversations map[string][]byte) *Assistant {
	return &Assistant{agent: agent, conversations: conversations}
}

// Reply answers message from user, whose id also names their memory's scope, and saves their conversation. The
// error is a stored conversation that cannot be read, or the run's API misused, such as a blank message.
func (a *Assistant) Reply(ctx context.Context, user, message string) (officina.Result, error) {
	c := &officina.Conversation{ID: user}
	if saved, ok := a.conversations[user]; ok {
		if err := json.Unmarshal(saved, c); err != nil {
			return officina.Result{}, fmt.Errorf("reply to %s: %w", user, err)
		}
	}
	// A conversation of an older agent, with other tools or instructions, cannot go on: start anew.
	if !a.agent.CanContinue(c) {
		c = &officina.Conversation{ID: user}
	}
	res, err := a.agent.Run(ctx, c, message, officina.RunOptions{
		Context: "Today is " + time.Now().UTC().Format(time.DateOnly) + ".", MemoryScope: user,
	})
	if err != nil {
		return res, fmt.Errorf("reply to %s: %w", user, err)
	}
	data, err := json.Marshal(c)
	if err != nil {
		return res, fmt.Errorf("save the conversation of %s: %w", user, err)
	}
	a.conversations[user] = data
	return res, nil
}

const instructions = `You are a friendly general assistant in a chat. Answer briefly and plainly. Use the conversion tool for
temperatures rather than working them out. Your memory belongs to the user you are talking to: keep their
preferences there, such as the units they like, check it when it may help, and follow it.`
