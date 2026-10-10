# Rules for every dispatched agent

Your brief points here. docs/conventions.md and your implementation's language rules (CLAUDE.md for .NET,
go/CLAUDE.md, ruby/CLAUDE.md) are the standard, the [code quality bar](../../../docs/conventions.md#code-quality-bar)
included; these add what running beside other agents needs.

- **Your files.** Change only the files and folders your brief gives you. Another agent owns the rest: if your change
  needs one of them, stop and report what it needs instead of editing it.
- **Scratch files** go in your scratchpad, named with the prefix your brief gives. Agents share that directory: never
  write to, run or delete a file you did not create.
- **Live API calls** stay under the cap your brief gives, for the whole task. Before each call, check that its worst
  case still fits; when the next one might not, stop and report the spend so far rather than make it.
- **Ruby** commands run through `~/.local/bin/mise exec ruby@4.0.7 -- …` (the version in `ruby/.ruby-version`).
- **Never merge** a pull request, and never turn on auto-merge: the lead does, after the review.
- **Report** the PR, the head commit's full sha, the newest review verdict and the sha it names, the required checks'
  state (`gh pr checks <n>`), and what is left open.
