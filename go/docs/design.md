# Go design notes

Choices in the Go core that the code alone does not explain. Concepts are in
[`ARCHITECTURE.md`](../../ARCHITECTURE.md); decisions G1…G15 in [`go.md`](../../docs/implementations/go.md).

| Choice | Why |
|---|---|
| `Agent.Stream` returns `(iter.Seq[RunEvent], func() (Result, error))`; `Agent.Run` is `Stream` without the events | G10. The two closures hold the context, so nothing stores it in a struct. The run happens inside the consumer's `range`, so the core starts no goroutine of its own; a `break` stops ranging the model's stream, which releases what it started, and the run's own context is cancelled as it returns |
| The `error` is only for misuse (blank message or context, a conversation in use); the run's outcome is the `Result` | AGT-03 and go/CLAUDE.md: model behaviour is a result, not an error |
| `Result` is one struct with a `Status`, not three types | Every result carries usage, and a struct compares with `cmp.Diff` and has a useful zero value; `Stop` and `Failure` are set only for their status |
| A reply and the messages it answers are all appended before any `ConversationAppended` is yielded | A host that stops reading after the first append still holds a valid conversation: the user message never stands without its reply |
| A cancel seen before the reply's `Finished` drops the reply, even if the model ignores the context | AGT-05: a reply cut off mid-stream is not appended. The core checks the context after every model event |
| Conversation JSON is .NET's form: a block's raw JSON is a JSON **string** (`"raw":"{…}"`) | G9 (the same format as .NET, for the shared golden files and cross-implementation resume). The raw bytes come back exactly however a store or encoder rewrites the outer JSON (indent, canonicalize, HTML-escape: all tested), so the core needs no encoder options for them. `jsontext.PreserveRawStrings(true)` matters where a block is written as a JSON value, which is the Claude adapter's request (Go S04) |
| Model events and run events are sealed interfaces (`ModelEvent`, `RunEvent`) over small structs | A type switch reads like the contract; other packages can create the events but not add kinds |
