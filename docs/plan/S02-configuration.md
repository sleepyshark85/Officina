# S02 — Configuration

**Milestone:** M1 · **Size:** M · **Depends on:** S01 · **Issue:** [#4](https://github.com/sleepyshark85/Officina/issues/4) · **Status:** todo

## Goal

Build the configuration machinery: Options classes, loading, layers, validation, and showing where each value came from.

**Closes:** CFG-01, CFG-02, CFG-04, CFG-05, CFG-06, CFG-07, CFG-08, CFG-09, CFG-10, CFG-14, CFG-15, CFG-16, CFG-17, MDL-02, MDL-03, DOC-01, TEST-04, TEST-05

## Acceptance criteria

- [ ] Layers merge as in the reference (§13). `sof config show --origin` names the layer of every value, including "code default, core x.y".
- [ ] Validation reports every error with its path and a fix. There is a test per validation phase and per attempt to weaken an invariant.
- [ ] The build generates the JSON Schema and the settings reference from the Options classes, and the documentation's examples validate against the schema.
- [ ] The placeholder rules are implemented and tested.
- [ ] Each run stores its resolved configuration.

## Notes

This slice delivers the machinery. Each later slice adds its own Options and validation rules.
