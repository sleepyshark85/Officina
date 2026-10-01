# S08 — Events, storage and observability

**Milestone:** M2 · **Size:** M · **Depends on:** S04 · **Issue:** [#10](https://github.com/sleepyshark85/Officina/issues/10) · **Status:** todo

## Goal

Durable storage, the event stream, telemetry, and data rules.

**Closes:** EVT-01, EVT-02, EVT-03, EVT-04, EVT-05, STO-01, REL-04, OBS-01, OBS-02, OBS-03, PRIV-01, PRIV-02, SEC-02, TEST-18, TEST-28

## Acceptance criteria

- [ ] The SQLite and in-memory stores pass one shared contract test suite.
- [ ] Events are ordered per agent and can be caught up after a reconnect. A stalled consumer does not slow agents.
- [ ] Traces and metrics follow the OpenTelemetry naming, and logs contain no conversation content by default.
- [ ] Retention, export and deletion on request work. Tenants cannot see each other's data.
- [ ] Stored data from an unknown format version is refused.

## Notes

S02 records each run with its resolved `OfficinaOptions` object (CFG-07); writing it as text belongs to the durable run store here.
