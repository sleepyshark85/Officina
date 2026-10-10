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

The rules every implementation shares are in the conventions, and only there; a rule below marked "(conventions)" is
how Ruby realizes the one of that name.

Port the behaviour, never the shape (conventions). Ruby code that reads like Java or C# (abstract base classes raising
`NotImplementedError`, `I`-prefixed modules, `get_x` methods, a container, builders, exceptions for a run's outcome) or
like Go (multiple return values for errors, `ctx` threaded through everything, a type switch on classes where a method
would do) is rewritten.

## Tooling (enforced by hooks and CI)

- `bundle exec rubocop` and `bundle exec rake steep` before a commit; `bundle exec rake test`, with warnings on and
  any warning about the workspace's own files failing them, before a push, as for .NET and Go. `bundler-audit` runs in
  `ruby-quality`, as it needs the network.
  The Rake task also fails on a FATAL or ERROR line in Steep's log, as Steep exits 0 after skipping a file.
- Every file starts with `# frozen_string_literal: true`.
- No monkey patching or refinements of classes Officina does not own; no `method_missing` or `respond_to_missing?`;
  no `eval`, `instance_eval` or `class_eval` of strings; no global variables, class variables (`@@`) or mutable
  constants (freeze them); no `ObjectSpace`; `send` to a private method only in tests, and even there prefer the public
  API.
- A new dependency meets the conventions' *Dependencies* row, with its line in `docs/implementations/ruby.md`.

## Gems and API

- Names follow the RubyGems guide (R2): `Sleepyshark::Officina` and nested modules; snake-case files named after the
  constant they define. No module named `Utils`, `Helpers`, `Common`, `Models` or `Types`. No stutter:
  `Officina::Agent`, not `Officina::OfficinaAgent`; `Claude::Model`, not `Claude::ClaudeModel`.
- The conventions' minimal API, in Ruby: internals are `private_constant` and private methods. Every public class,
  module and method has an RBS signature in `sig/` and a YARD comment saying what it is for and what the signature
  cannot (meaning, units, `@raise`); no tag that only repeats a type.
- **Contracts are duck types**, written as RBS interfaces (`_Model`, `_Approver`, `_MemoryStore`, `_AuditSink`,
  `_ToolSource`) beside their consumer, under the conventions' contract rules. No base class whose methods raise
  `NotImplementedError`.
- **Values are immutable:** `Data.define` for value objects (usage, messages, results, events), frozen collections
  inside them. No `attr_writer` or `attr_accessor` on shared objects: an agent is frozen once built (AGT-01). A field
  reader is `name`, never `get_name`; a predicate ends in `?` (`cancelled?`); a `!` marks only the more dangerous
  version of a method that also has a safe one, as the style guide says.
- Keyword arguments for required dependencies and for every optional setting; positional arguments only for the one or
  two values a call is obviously about (`agent.run(conversation, input, cancel:)`). Optional settings default to
  `nil` or a frozen constant, never a shared mutable object.
- Results are values, decided with `case … in` pattern matching on `Data` classes where a branch depends on the kind.

## Errors

- A run's outcome is a result value, never raised (conventions' design rules). Officina's errors inherit from
  `Sleepyshark::Officina::Error < StandardError`, and their names end in `Error`.
- Rescue specific classes, as narrowly as the code allows. Never `rescue Exception`, never a bare `rescue nil`, never
  rescue `Interrupt` or `SignalException` except to cancel and re-raise. `rescue StandardError` only at the boundaries
  that must turn any failure into a value: a tool handler (an error result for the model), a stream from the provider
  (a classified failure), a sink write (a reported audit failure).
- An error is handled once (conventions): returned as a value, raised, or logged, never two of them. A message says
  what failed and with what.

## Concurrency

- Threads from the standard library (R11). **A thread's owner (conventions) joins it in an `ensure`;** the run's
  `Cancellation` stops it. No fire-and-forget. The test helper that compares `Thread.list` before and after each test
  proves it, which is why tests run one at a time.
- Never `Thread#raise`, `Thread#kill`, `Timeout.timeout` or `Thread.abort_on_exception = true`; cancellation is
  cooperative (R10). A signal trap only pushes to a `Thread::Queue` that an owned thread reads: no `Mutex`, I/O,
  logging or `Cancellation#cancel` in trap context, where a `Mutex` raises.
- Shared mutable state lives behind one `Mutex` owned by the object that holds it (conventions); work is handed over
  through a `Thread::Queue`, closed by the side that sends (conventions). A value shared across threads is frozen.
- A block passed to `run` may `break`: the code that started tools waits for them in an `ensure` (conventions).

## Tests

- Minitest (`Minitest::Test`), plain `assert_*` and `refute_*`; no matcher library, no `mocha`. The requirement ID
  in a test name is lower case: `test_agt05_cancelling_mid_stream_appends_nothing`. Each test is independent and runs
  in random order (Minitest's default), one at a time: no `parallelize_me!`, as the thread-leak check needs it.
- Never a stub of an Officina object (conventions: fakes only at the boundaries). `assert_equal expected, actual`,
  expected first.
- Property tests with `pbt` for TEST-07 and for the generated inputs every parser and validator has (conventions);
  CI runs a fixed seed.
- Each sample in `examples/` runs as a test.
- Golden files under the gem's `test/fixtures/`, updated only with `UPDATE_GOLDEN=1`, which never writes the shared
  top-level `testdata/` (conventions); Ruby S08's new shared fixture, `ruby-session.json`, is added by its own step.

## Files

- One public class or module per file, named after it; private nested types stay in their owner's file. A file over
  about 300 lines, or a method over RuboCop's length limit, is a sign to split by concept.
- Layout per gem: `lib/`, `sig/`, `test/`, `<gem>.gemspec`; the application adds `exe/`.
