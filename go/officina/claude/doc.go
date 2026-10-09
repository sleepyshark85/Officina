// Package claude is Officina's model provider for Claude, built on the Anthropic Go SDK's beta messages API. It is
// the only package in the module that imports that SDK.
//
// A Model streams every request, with adaptive thinking and an explicit effort. It lays a request out for the cache:
// the tools sorted by name, then the instructions with a cache point on them, then the conversation, which automatic
// caching follows to its end; run context goes as a system message after the user message. Each block of a reply is
// kept as the API sent it, compacted and with <, > and & escaped, and is sent back byte for byte.
//
// An agent's context management becomes Claude's: threshold compaction, whose summary block is appended to the
// conversation like any other, and clearing of old tool results. Each is reported from what the API says it did,
// and a compaction's tokens are counted and priced with the reply's.
//
// Transient failures (rate limits, overload, server and network errors, also mid-stream) are retried with backoff,
// waiting as long as a Retry-After header asks; what remains is an error that wraps ErrTransient,
// ErrAuthentication or ErrInvalidRequest.
package claude
