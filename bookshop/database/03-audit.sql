-- The audit trail (APP-16, AUD-03), written by the application's audit sink and never changed. One row per entry; the
-- trace and span open the step it records in the telemetry dashboard (APP-20).

create table audit (
    id                 bigint generated always as identity primary key,
    time               timestamptz not null,
    sequence           bigint not null,
    run                text not null,
    conversation       text not null,
    agent              text not null,
    memory_scope       text,
    trace_id           text,
    span_id            text,
    kind               text not null,
    tool               text,
    call_id            text,
    input              text,
    outcome            text,
    detail             text,
    duration           interval,
    input_tokens       bigint,
    output_tokens      bigint,
    cache_read_tokens  bigint,
    cache_write_tokens bigint,
    cost               numeric,
    unique (run, sequence)
);

create index audit_by_conversation on audit (conversation, id);
