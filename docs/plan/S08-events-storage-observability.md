# S08 — Events, storage and observability

**Milestone:** M2 · **Size:** M · **Depends on:** S04 · **Issue:** [#10](https://github.com/sleepyshark85/Officina/issues/10) · **Status:** done

## Goal

Durable storage, the event stream, telemetry, and data rules.

**Closes:** EVT-01, EVT-02, EVT-03, EVT-04, EVT-05, STO-01, REL-04, OBS-01, OBS-02, OBS-03, PRIV-01, PRIV-02, SEC-02, TEST-18, TEST-28

## Acceptance criteria

- [x] The SQLite and in-memory stores pass one shared contract test suite.
- [x] Events are ordered per agent and can be caught up after a reconnect. A stalled consumer does not slow agents.
- [x] Traces and metrics follow the OpenTelemetry naming, and logs contain no conversation content by default.
- [x] Retention, export and deletion on request work. Tenants cannot see each other's data.
- [x] Stored data from an unknown format version is refused.

## Notes

S02 records each run with its resolved `OfficinaOptions` object (CFG-07); the SQLite storage writes it as text.

Parts of the closed requirements need state that later slices add:
- S06, S07, S17, S18, S19: their stores join `IStorage`, with retention for conversations, the run record and
  artifacts (PRIV-01), and their rows in export and deletion (PRIV-02).
- S09: runs carry the caller's tenant and owner; until then they are anonymous. S09 and S16 give the host a run's
  id when it starts the run, so a reader can follow it live from the start.
- S13: steps in events, traces and logs (EVT-02, OBS-01, OBS-03). S18: task status events, and task as a
  metric dimension (EVT-01, OBS-02). S18 and S20: messages between agents as events.
- S19: budget warnings; the event sequence continues from the stored log when a run resumes in a new process.
- S06: check pass rates (OBS-02). S11: fallbacks used (OBS-02); events for tools the provider runs itself (EVT-01).
- S16: the CLI opens the SQLite storage in the project directory (STO-01).
- Capabilities that add their own events (EVT-01) add their payload kinds to `EventPayload`.
