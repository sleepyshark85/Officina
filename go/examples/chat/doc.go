// Package chat is the chat assistant sample: stateful, one conversation per user kept by the host as JSON, a tool,
// and memory per user. The date is run context, never in the instructions. Its output is text, and it has no
// approver, as no tool needs approval. Its tests run it offline on the test kit's scripted model and the core's
// in-memory memory store.
package chat
