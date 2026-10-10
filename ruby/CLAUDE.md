# Officina in Ruby — working rules

Applies to everything under `ruby/`, on top of [`docs/conventions.md`](../docs/conventions.md). Decisions:
[`docs/implementations/ruby.md`](../docs/implementations/ruby.md). Plan:
[`docs/plan/phase-1.md`](../docs/plan/phase-1.md).

**These rules are strict.** The reviewer treats a breach as must-fix, like a design-rule breach. The references, in
order of precedence: [The Ruby Style Guide](https://rubystyle.guide/), RuboCop's defaults as configured in
`.rubocop.yml`, the [RubyGems guides](https://guides.rubygems.org/) (naming, patterns, specification) and the
[RBS syntax](https://github.com/ruby/rbs/blob/master/docs/syntax.md). Where a rule below is stricter, it wins. A rule
is broken only with a `# rubocop:disable Department/Cop -- <reason>` on the line or a comment saying why, and the
reviewer must agree with the reason.

Port the behaviour, never the C# or the Go. If Ruby code reads like Java or C# (abstract base classes raising
`NotImplementedError`, `I`-prefixed modules, `get_x` methods, a container, builders, exceptions for a run's outcome) or
like Go (multiple return values for errors, `ctx` threaded through everything, a type switch on classes where a method
would do), rewrite it.

## Tooling (enforced by hooks and CI)

- `bundle exec rubocop`; `bundle exec steep check`; `bundle exec rake test` with warnings on and none printed;
  `bundle exec bundler-audit check --update` clean. All before a commit or push, as for .NET and Go.
- Required checks before a merge, from `.github/workflows/ruby.yml`: `ruby-changes`, `ruby-ubuntu`, `ruby-windows`,
  `ruby-quality`, `ruby-mutation`, besides the .NET and Go ones.
- Every file starts with `# frozen_string_literal: true`.
- No monkey patching or refinements of classes Officina does not own; no `method_missing` or `respond_to_missing?`;
  no `eval`, `instance_eval` or `class_eval` of strings; no global variables, class variables (`@@`) or mutable
  constants (freeze them); no `ObjectSpace`; `send` to a private method only in tests, and even there prefer the public
  API.
- New dependencies need a line in `docs/implementations/ruby.md` saying why the standard library is not enough.

## Gems and API

- Names follow the RubyGems guide (R2): `Sleepyshark::Officina` and nested modules; snake-case files named after the
  constant they define. No module named `Utils`, `Helpers`, `Common`, `Models` or `Types`. No stutter:
  `Officina::Agent`, not `Officina::OfficinaAgent`; `Claude::Model`, not `Claude::ClaudeModel`.
- Keep the public API minimal. Internals are `private_constant` and private methods. Every public class, module and
  method has a YARD comment (what it is for, `@param`, `@return`, `@raise`) and an RBS signature in `sig/`; the comment
  says what the signature cannot.
- **Contracts are duck types**, written as RBS interfaces (`_Model`, `_Approver`, `_MemoryStore`, `_AuditSink`,
  `_ToolSource`), defined beside their consumer, small (one to three methods). No base class whose methods raise
  `NotImplementedError`; no interface with one implementation and no consumer that needs to swap it.
- **Values are immutable:** `Data.define` for value objects (usage, messages, results, events), frozen collections
  inside them. No `attr_writer` or `attr_accessor` on shared objects: an agent is frozen once built (AGT-01). A field
  reader is `name`, never `get_name`; a predicate ends in `?` (`cancelled?`), a method that mutates its receiver or
  raises where a sibling does not ends in `!`.
- Keyword arguments for required dependencies and for every optional setting; positional arguments only for the one or
  two values a call is obviously about (`agent.run(conversation, input, cancel:)`). Optional settings default to
  `nil` or a frozen constant, never a shared mutable object.
- Results are values, decided with `case … in` pattern matching on `Data` classes where a branch depends on the kind.
- No setting without a known case, per the "simplest thing" rule: a constant until then.

## Errors

- Exceptions are for the API misused or the environment broken; a run's outcome (completed, stopped, failed) is a
  result value, never raised (AGT-03). Officina's errors inherit from `Sleepyshark::Officina::Error < StandardError`,
  and their names end in `Error`.
- Rescue specific classes, as narrowly as the code allows. Never `rescue Exception`, never a bare `rescue nil`, never
  rescue `Interrupt` or `SignalException` except to cancel and re-raise. `rescue StandardError` only at the boundaries
  that must turn any failure into a value: a tool handler (an error result for the model), a stream from the provider
  (a classified failure), a sink write (a reported audit failure).
- Handle an error once: return it as a value, raise it, or log it, never two of them. A message says what failed and
  with what, never a secret (EVT-03).

## Concurrency

- Threads from the standard library (R11). **Every thread has an owner that joins it**, in an `ensure`, and a way to
  stop (the run's `Cancellation`). No fire-and-forget. The test helper that compares `Thread.list` before and after
  each test proves it.
- Never `Thread#raise`, `Thread#kill`, `Timeout.timeout` or `Thread.abort_on_exception = true`; cancellation is
  cooperative (R10). A signal trap only cancels a token: no locking, I/O or logging in trap context.
- Shared mutable state lives behind one `Mutex` owned by the object that holds it; work is handed over through a
  `Thread::Queue`, closed by the side that sends. A queue for a reply's tool events is sized from the number of calls
  (`Thread::SizedQueue`), never "big enough".
- A block passed to `run` may `break`: the code that started tools waits for them in an `ensure`.

## Tests

- Minitest (`Minitest::Test`), plain `assert_*` and `refute_*`; no matcher library, no `mocha`. Test names carry the
  requirement ID in lower case: `test_agt05_cancelling_mid_stream_appends_nothing`. Each test is independent and runs
  in random order (Minitest's default); `parallelize_me!` unless a test shares a boundary fake.
- Fakes only at the boundaries (docs/conventions.md); never a stub of an Officina object. `assert_equal expected,
  actual`, expected first.
- Property tests with `prop_check` for TEST-07, generated inputs for every parser and validator (JSON Schema subset,
  MCP messages, memory paths); CI runs a fixed seed, and a failure prints the seed that reproduces it.
- Each sample in `examples/` runs as a test.
- Golden files under the gem's `test/fixtures/`, updated only with `UPDATE_GOLDEN=1`, reviewed like code. The shared
  files in the repository's top-level `testdata/` are read only: .NET and Go read them too, and nothing in Ruby
  writes them.

## Files

- One public class or module per file, named after it; private nested types stay in their owner's file. A file over
  about 300 lines, or a method over RuboCop's length limit, is a sign to split by concept.
- Layout per gem: `lib/`, `sig/`, `test/`, `<gem>.gemspec`; the application adds `exe/`.
