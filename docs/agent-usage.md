# Agent usage

The agents each Claude Code session on this project ran, by role and task, and the tokens each used. Generated
by [`scripts/agent-usage.py`](../scripts/agent-usage.py) from the session transcripts on the lead's machine;
regenerate it at the end of a session rather than editing it.

- **Input processed:** every token sent to the model over the agent's calls (input, cache reads and cache writes).
  Cache reads are billed at a fraction of input, so most of a long agent's input is cheap.
- **Output** is not shown: transcripts record a message's usage as its stream begins, before the output is counted.
- **Peak context:** the input of the agent's largest single request, what it carried at its fullest. The context-size
  hook (`.claude/hooks/context-size.py`) warns an agent at 150k and 300k.
- **Minutes:** from the agent's first to its last transcript entry, including time spent waiting for CI.

## Session 882087f6 (2026-10-10 02:00 to 2026-10-10 10:03 UTC)

| Role | Agents | Input processed |
|---|---|---|
| lead | 1 | 179,499k |
| developer | 23 | 691,972k |
| fixer | 2 | 1,205k |
| reviewer | 32 | 115,633k |
| claude-code-guide | 2 | 115k |

| Agent | Role | Task | Calls | Input processed | Of which cache reads | Peak context | Minutes |
|---|---|---|---|---|---|---|---|
| lead | lead | This session's main conversation | 426 | 179,499k | 178,644k | 715k | 483 |
| a8eb4164 | reviewer | Review Ruby plan and licence PRs | 190 | 41,017k | 38,076k | 384k | 406 |
| a033fe44 | developer | Ruby S02 Claude features spike | 96 | 18,555k | 18,276k | 279k | 19 |
| a9bee389 | reviewer | Review PR #83 Ruby S02 | 28 | 1,676k | 1,593k | 83k | 5 |
| af836887 | developer | Ruby S01 skeleton slice | 147 | 28,135k | 27,565k | 294k | 49 |
| ab6d8f7e | reviewer | Review PR 84 Ruby S01 | 64 | 5,328k | 5,205k | 124k | 12 |
| ada44bb2 | developer | Diff-scoped mutation for Go and Ruby | 93 | 8,975k | 8,823k | 151k | 18 |
| ac7b3343 | developer | Ruby S03 run loop slice | 160 | 36,542k | 35,910k | 336k | 36 |
| a0054be8 | developer | .NET fix for issue #86 | 16 | 885k | 835k | 68k | 4 |
| a3509c65 | developer | Ruby S05a schema DSL and validator | 173 | 35,639k | 35,325k | 332k | 44 |
| a69ba430 | developer | Ruby S11a MCP client protocol | 176 | 41,901k | 41,239k | 374k | 47 |
| aa6986f7 | reviewer | Review PR 87 | 38 | 1,634k | 1,572k | 65k | 5 |
| acb63113 | reviewer | Review PR 88 | 11 | 231k | 209k | 27k | 1 |
| a3a37aae | developer | Ruby S04 Claude adapter slice | 146 | 36,597k | 36,244k | 369k | 37 |
| a3740c93 | developer | Fix flaky Go MCP test on Windows | 42 | 2,836k | 2,577k | 86k | 74 |
| a0264476 | reviewer | Review PR 89 | 19 | 511k | 417k | 36k | 60 |
| a8e52a21 | reviewer | Review PR #90 Ruby S03 | 27 | 1,844k | 1,752k | 96k | 8 |
| a7f6d890 | reviewer | Review PR 91 | 62 | 4,866k | 4,753k | 117k | 16 |
| adc9d3e9 | reviewer | Review PR #92 | 45 | 3,189k | 3,001k | 110k | 17 |
| a58a7a79 | developer | Ruby S05B tool pipeline and audit | 448 | 204,891k | 200,526k | 824k | 143 |
| a9520ea8 | reviewer | Review PR 93 Ruby S04 | 52 | 4,355k | 4,240k | 119k | 8 |
| a72e97df | developer | Ruby S06A Bookshop data layer | 167 | 35,989k | 35,658k | 349k | 39 |
| a02f78e9 | developer | Ruby S09A memory stores | 234 | 55,807k | 54,946k | 384k | 66 |
| ac4362a2 | reviewer | Review PR 94 Ruby S06A | 52 | 4,563k | 4,349k | 135k | 18 |
| aa90e0d0 | reviewer | Review PR 95 | 56 | 4,864k | 4,518k | 152k | 41 |
| a4598c87 | reviewer | Strict re-review of #91 | 26 | 1,605k | 1,516k | 93k | 5 |
| a42254e3 | reviewer | Strict re-review of #92 | 36 | 2,749k | 2,484k | 117k | 33 |
| aacd55e9 | reviewer | Strict re-review of #93 | 44 | 3,488k | 3,365k | 127k | 13 |
| a0ab3cc3 | reviewer | Strict audit of merged Ruby code | 47 | 5,389k | 5,224k | 169k | 11 |
| acfcde0a | developer | Fix #91 strict review findings | 65 | 7,000k | 6,858k | 158k | 17 |
| a71af28e | developer | Ruby S03 quality cleanup PR | 118 | 17,431k | 17,212k | 236k | 29 |
| a632b7c1 | developer | Fix #93 strict review findings | 153 | 29,177k | 28,632k | 320k | 43 |
| a251f740 | reviewer | Review PR 91 round 2 | 22 | 955k | 897k | 60k | 6 |
| a8c62d46 | reviewer | Review PR 98 | 36 | 2,548k | 2,363k | 109k | 15 |
| adca4233 | fixer | Fix #89 discarded wait error | 23 | 951k | 856k | 49k | 14 |
| a546a966 | reviewer | Review PR 89 new head | 8 | 116k | 90k | 17k | 10 |
| a7dc3e8f | developer | Fix #92 strict review findings | 240 | 72,131k | 70,760k | 513k | 99 |
| a0e09729 | reviewer | Review PR 93 round 2 | 66 | 6,395k | 6,101k | 163k | 17 |
| ac3d145a | reviewer | Owner-approved round 4 for #95 | 19 | 854k | 796k | 63k | 4 |
| a206ee6e | developer | Conventions single source of truth | 57 | 6,358k | 6,225k | 151k | 14 |
| a100949b | developer | Fix #95 lock-removal test gap | 83 | 8,086k | 7,948k | 155k | 33 |
| a24be168 | reviewer | Review PR 99 conventions | 30 | 1,599k | 1,524k | 77k | 5 |
| a7533d10 | reviewer | Review PR 100 | 73 | 8,175k | 7,866k | 172k | 29 |
| a14cb536 | reviewer | Verify PR 95 round-4 fix | 26 | 845k | 802k | 45k | 4 |
| ae76923f | reviewer | Verdict #95 after main merge | 13 | 259k | 216k | 30k | 11 |
| a98e1d10 | developer | Review gate carries approval over merges | 79 | 10,029k | 9,721k | 181k | 29 |
| a0fe8e6d | developer | Dispatch skill and fresh-reviewer rule | 32 | 2,270k | 2,193k | 94k | 9 |
| ad3d18b9 | developer | Context-size monitoring hook | 69 | 5,476k | 5,375k | 117k | 16 |
| a5bac968 | claude-code-guide | Verify project skill layout | 3 | 58k | 34k | 24k | 1 |
| aa934d4e | claude-code-guide | Hook fields in subagents | 3 | 57k | 47k | 21k | 1 |
| a544e539 | reviewer | Review PR 101 dispatch skill | 19 | 705k | 643k | 66k | 4 |
| abeaf449 | reviewer | Review PR 102 | 33 | 1,764k | 1,683k | 85k | 9 |
| aba25af7 | reviewer | Merge-check verdict for #95 | 6 | 78k | 56k | 16k | 10 |
| aaf9aac9 | reviewer | Review PR 103 review gate | 52 | 3,077k | 2,984k | 97k | 13 |
| a9b3d707 | reviewer | Merge-check verdict for #100 | 20 | 590k | 496k | 50k | 17 |
| a776164a | developer | Ruby S06 part B Bookshop console | 79 | 13,773k | 13,528k | 263k | 15 |
| ace4eb44 | developer | Ruby S07 part A core telemetry | 81 | 13,489k | 13,229k | 278k | 14 |
| acd00305 | reviewer | Merge-check verdict for #102 | 5 | 88k | 67k | 23k | 1 |
| ac530fe0 | fixer | Run hook tests in CI | 7 | 254k | 232k | 39k | 1 |
| aa6d9fad | reviewer | Review PR #104 hook tests in CI | 13 | 276k | 253k | 28k | 2 |

## Session d3089700 (2026-10-05 13:58 to 2026-10-06 07:35 UTC)

| Role | Agents | Input processed |
|---|---|---|
| lead | 1 | 73,844k |
| general-purpose | 39 | 299,882k |

| Agent | Role | Task | Calls | Input processed | Of which cache reads | Peak context | Minutes |
|---|---|---|---|---|---|---|---|
| lead | lead | This session's main conversation | 239 | 73,844k | 72,910k | 506k | 1058 |
| a52cc06d | general-purpose | Implement slice S01 skeleton | 32 | 2,228k | 2,139k | 89k | 7 |
| ad1b4fe5 | general-purpose | Run slice S02 live API check | 46 | 6,560k | 6,369k | 191k | 12 |
| a4dacc7e | general-purpose | Review PR #3 S01 skeleton | 10 | 500k | 457k | 61k | 2 |
| a4652a5e | general-purpose | Implement slice S03 run loop | 51 | 6,223k | 6,071k | 168k | 20 |
| a0eade42 | general-purpose | Review PR #4 S02 spike | 18 | 1,499k | 1,396k | 120k | 3 |
| ad6b7e38 | general-purpose | Review PR #5 docs findings | 12 | 969k | 882k | 104k | 2 |
| af930c91 | general-purpose | Fix PR #4 review items | 6 | 241k | 195k | 46k | 1 |
| a4bd1e7a | general-purpose | Review PR #6 S03 run loop | 24 | 2,577k | 2,308k | 163k | 10 |
| ae3252d8 | general-purpose | Fix S01 review nits | 6 | 215k | 193k | 39k | 2 |
| a626d868 | general-purpose | Review PR #7 S01 nits | 5 | 181k | 156k | 41k | 1 |
| ae8c5543 | general-purpose | Review PR #8 CI gates | 17 | 664k | 636k | 46k | 5 |
| aea57be4 | general-purpose | Implement slice S04 Claude adapter | 84 | 15,956k | 15,453k | 268k | 24 |
| a7237bed | general-purpose | Review PR #9 test strategy docs | 12 | 577k | 534k | 60k | 4 |
| a918e58d | general-purpose | Review PR #10 S04 Claude adapter | 38 | 4,583k | 4,443k | 157k | 16 |
| a57269a5 | general-purpose | Implement slice S05 tools and audit | 124 | 26,570k | 26,042k | 290k | 32 |
| aba77a48 | general-purpose | Fix PR #10 concurrency test | 11 | 424k | 381k | 43k | 3 |
| ab42362f | general-purpose | Review PR #11 S05 tools and audit | 39 | 3,642k | 3,438k | 131k | 14 |
| ab3bf6b3 | general-purpose | Implement slice S06 Bookshop console | 107 | 16,570k | 16,184k | 226k | 31 |
| aa96d2ed | general-purpose | Fix PR #11 End-with-tools result | 12 | 463k | 419k | 44k | 3 |
| aaa1e649 | general-purpose | Review PR #12 S06 Bookshop console | 38 | 3,684k | 3,480k | 131k | 17 |
| afb08abf | general-purpose | Implement slice S07 telemetry | 139 | 29,453k | 28,908k | 305k | 38 |
| a254400b | general-purpose | Fix PR #12 approval read race | 11 | 395k | 355k | 39k | 3 |
| a3c3fdc9 | general-purpose | Review PR #13 S07 telemetry | 31 | 3,087k | 2,962k | 142k | 14 |
| a1336cff | general-purpose | Implement slice S08 sessions and budgets | 123 | 29,969k | 29,348k | 339k | 32 |
| a4686b38 | general-purpose | Review PR #14 S08 sessions budgets | 30 | 3,669k | 3,382k | 169k | 11 |
| a8c4c142 | general-purpose | Implement slice S09 memory | 121 | 27,543k | 26,711k | 321k | 41 |
| a03605e1 | general-purpose | Implement slice S10 long conversations | 118 | 24,324k | 23,551k | 287k | 32 |
| a97d9827 | general-purpose | Implement slice S11 MCP | 89 | 15,000k | 14,586k | 237k | 27 |
| ada640fd | general-purpose | Implement slice S12 typed output | 93 | 16,804k | 16,358k | 255k | 22 |
| a7a4c4c1 | general-purpose | Review PR #16 S12 typed output | 30 | 3,264k | 3,133k | 148k | 9 |
| abdc9a9c | general-purpose | Review PR #17 S11 MCP | 29 | 2,411k | 2,218k | 124k | 12 |
| a5e1e48d | general-purpose | Review PR #18 S09 memory | 36 | 4,587k | 4,283k | 180k | 24 |
| a51e94c4 | general-purpose | Review PR #19 S10 long conversations | 35 | 4,342k | 4,192k | 167k | 13 |
| aa709a54 | general-purpose | Implement S13a samples and cleanup | 99 | 18,486k | 18,239k | 263k | 26 |
| a4d84518 | general-purpose | Implement S13b demo and smoke test | 99 | 15,445k | 15,234k | 227k | 33 |
| a734fa61 | general-purpose | Review PR #21 S13b demo smoke | 31 | 2,318k | 2,154k | 109k | 12 |
| a4264eb7 | general-purpose | Review PR #20 S13a samples | 38 | 3,313k | 3,208k | 121k | 11 |
| a969d427 | general-purpose | Fix demo-mode clearing tuning | 19 | 884k | 829k | 55k | 4 |
| a01121f5 | general-purpose | Review PR #22 demo clearing fix | 7 | 265k | 222k | 43k | 2 |

## Session d6c4a9e9 (2026-10-06 01:37 to 2026-10-08 12:45 UTC)

| Role | Agents | Input processed |
|---|---|---|
| lead | 1 | 257,154k |
| general-purpose | 19 | 23,085k |

| Agent | Role | Task | Calls | Input processed | Of which cache reads | Peak context | Minutes |
|---|---|---|---|---|---|---|---|
| lead | lead | This session's main conversation | 584 | 257,154k | 254,834k | 964k | 3548 |
| a8e11095 | general-purpose | Review PR 23 tool context | 7 | 286k | 240k | 46k | 1 |
| a34f834d | general-purpose | Review PR 24 RunOptions | 16 | 1,028k | 966k | 79k | 3 |
| a7cef14e | general-purpose | Review PR 25 run decision | 8 | 338k | 304k | 51k | 1 |
| a9b6f66d | general-purpose | Review PR 26 outcomes cohesion | 6 | 249k | 219k | 47k | 1 |
| a69d5b58 | general-purpose | Review PR 27 RunScope | 10 | 471k | 413k | 58k | 2 |
| a2c1a4ea | general-purpose | Review PR 28 design docs | 43 | 5,137k | 4,991k | 163k | 7 |
| a1ad3481 | general-purpose | Review PR 29 principle gaps | 10 | 512k | 450k | 62k | 1 |
| a427c91b | general-purpose | Review PR 30 entry point | 10 | 487k | 426k | 62k | 1 |
| ab095d16 | general-purpose | Review PR 30 entry point | 14 | 781k | 730k | 67k | 2 |
| a65a93af | general-purpose | Review PR 31 rename | 16 | 791k | 746k | 62k | 2 |
| a0bd49e0 | general-purpose | Review PR 32 RequestPrefix | 19 | 1,166k | 1,086k | 81k | 3 |
| af73adb0 | general-purpose | Review PR 33 comment shortening | 35 | 3,592k | 3,448k | 144k | 8 |
| a49282a5 | general-purpose | Review PR 34 session fixes | 23 | 1,560k | 1,464k | 96k | 7 |
| a752ec05 | general-purpose | Review PR 35 SessionManager | 18 | 1,135k | 1,073k | 79k | 5 |
| ae84b7e8 | general-purpose | Review PR 36 Claude strings | 30 | 2,284k | 2,179k | 105k | 6 |
| a78c6fec | general-purpose | Review PR 37 built-in stores | 17 | 1,049k | 967k | 82k | 3 |
| af7cfeb0 | general-purpose | Review PR 45 search indexes | 15 | 760k | 696k | 64k | 6 |
| ada54a9a | general-purpose | Review PR 46 CodeQL | 11 | 532k | 489k | 61k | 2 |
| ae3f6d54 | general-purpose | Review PR 48 gates | 19 | 927k | 880k | 64k | 6 |
