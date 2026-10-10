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
- **Report** in this format, about ten lines, since the lead reads every report and must keep a small context:

  ```text
  PR #<n> · head <full sha>
  Verdict: <APPROVE|CHANGES REQUESTED|none> at <sha>, or the `review` status
  Checks: <passing|failing: names|pending>
  Live spend: $<x>                  (only if you made live calls)
  Needs the lead: <one line each, or "nothing">
  Left open: <one line each>
  ```

  `Needs the lead` lists every decision, block, surprise, claim the lead must verify and anything a permission check
  denied, one line each: the line count yields to it, never the reverse. Everything else (what changed per finding,
  file lists, survivor tables, mutation counts) goes in the PR description or a PR comment, which the lead reads only
  if it needs to. A hand-back at the context limit adds everything the context hook's warning asks for.
- **Review rounds.** An agent that fixes a verdict sends the next round straight to the PR's reviewer with
  `SendMessage`, using the text and the reviewer id its brief gives, waits for the reply before it ends, and finishes
  with one line: the verdict and the head sha. The reviewer stays the same for the PR's rounds; the lead dispatches any
  new one, and alone sets auto-merge.
