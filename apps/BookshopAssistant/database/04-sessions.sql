-- Sessions (APP-10): one row per conversation, saved after every step of a reply. The conversation is the core's JSON,
-- kept as text so it reads back byte for byte (jsonb would rewrite it). Title, summary and changes come from the
-- summarizer (APP-15), as of the time in summarized: a session updated since needs a new summary. The usage and cost
-- columns add up the session's replies for /cost and the session budget (APP-14).

create table sessions (
    id                 text primary key,
    staff_member       text not null,
    conversation       text not null,
    title              text,
    summary            text,
    changes            text[],
    summarized         timestamptz,
    input_tokens       bigint not null,
    output_tokens      bigint not null,
    cache_read_tokens  bigint not null,
    cache_write_tokens bigint not null,
    cost               numeric not null,
    updated            timestamptz not null
);

create index sessions_by_updated on sessions (updated desc);
