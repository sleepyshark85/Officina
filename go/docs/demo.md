# Bookshop Assistant demo script, Go (APP-19)

The walkthrough of [`docs/demo.md`](../../docs/demo.md), for the Go application in [`cmd/bookshop`](../cmd/bookshop/):
every Bookshop Assistant capability (APP-01…20) in one sitting of about 30 minutes, with what to type and what to
expect, then a session the .NET application saved, resumed here. The model's wording varies from run to run; the tool
lines (`>` a call, `<` its outcome, `?` an approval, `~` a context event) and the bracketed lines come from the console
and do not.

**Cost.** Steps 1–12 cost about $0.30 to $1 together (step 7's request can make the model fetch much of the
catalogue before you stop it), step 13 (demo mode) about $0.40 and step 14 a few cents. The status line after each
reply shows what was spent.

**Live check.** Every step was run as written, live with Opus 5.5, on 10 October 2026: see
[What was checked live](#what-was-checked-live).

## 0. Setup

| # | Do | Expect |
|---|---|---|
| 0.1 | `bookshop/start.sh` | `postgres`, `dashboard` and `filesystem` (the export server, APP-12) healthy, and their addresses. The schema and seed (480 books, 40 customers, 80 orders) are created on first start; `docker compose down -v` resets them. |
| 0.2 | Open http://localhost:18888 | The telemetry dashboard (APP-20): Traces, Metrics and Structured logs, empty for now. |
| 0.3 | `export ANTHROPIC_API_KEY=sk-ant-…` | |
| 0.4 | `cd go` then `go run ./cmd/bookshop` | `Bookshop Assistant. Type /help for commands.` and `Who is using the assistant? Your name: ` |

If a port is taken, set another in `bookshop/.env`, beside `compose.yaml` (`BOOKSHOP_DB_PORT`, `BOOKSHOP_DASHBOARD_PORT`,
`BOOKSHOP_OTLP_PORT` or `BOOKSHOP_EXPORTS_PORT`), as `compose.yaml` explains, and give the application the matching
address: `BOOKSHOP_DATABASE` (such as `postgres://bookshop:shelf-demo-41@localhost:5433/bookshop`),
`BOOKSHOP_DASHBOARD`, `OTEL_EXPORTER_OTLP_ENDPOINT` or `BOOKSHOP_EXPORTS`.

## 1. Run context (APP-13)

| Type | Expect |
|---|---|
| `Sam` | `Hello, Sam.` and `Session <id>.` Note the id. |
| `/help` | The commands (APP-02): `/help`, `/new`, `/sessions`, `/resume <id>`, `/cost`, `/audit [<id>]`, `/memory`, `/quit`, and that Ctrl+C stops a reply. |
| `What is today's date, and who am I?` | Often `> memory {"command":"view",…}` first (the model checks Sam's memory), then today's date and "you're Sam". Neither is in the instructions: they come as run context, sent at the start of the session and again only on a new day. |

## 2. Streaming, tool activity and parallel reads (APP-01, APP-05)

| Type | Expect |
|---|---|
| `Is The Winter Archive in stock, and what has Alice Martin ordered before?` | The reply streams as it is written. `> search_books {"title":"Winter Archive"}` and `> find_customer {"nameOrEmail":"Alice Martin"}` both start before either `<` line: read calls of one reply run in parallel. Then `> list_customer_orders {"customerId":1}`, with any text the model writes between the calls, and the answer. |

## 3. A multi-step order with approval (APP-09, APP-06)

| Type | Expect |
|---|---|
| `Order the two cheapest fantasy books in stock for Alice Martin and tell me the total.` | `> find_customer` and `> search_books {"genre":"Fantasy","inStock":true,…}` in parallel; a sentence saying what it will order; then `? place_order needs your approval. Its exact input:` with `{"customerId":1,"lines":[{"bookId":144,"quantity":1},{"bookId":216,"quantity":1}]}` and `Approve? [y/N]`. |
| `y` | `< place_order: ok` and the answer: order 81 on a fresh database, The Winter Archive (£6.28) and The Hollow Island (£6.92), **total £13.20**. The cheapest fantasy book, The Silent Mountain (id 72), is out of stock, so it is skipped. |

## 4. A denial the model works around (APP-06, TOOL-04)

| Type | Expect |
|---|---|
| `Order three copies of The Hollow Island for Ben Hughes.` | `? place_order needs your approval.` with the exact input. |
| `n` | `< place_order: error: The call was denied: the staff member declined`. The model says nothing was ordered and offers another way, such as fewer copies or holding it for later. Nothing changed in the database. |

## 5. Not enough stock (APP-07)

| Type | Expect |
|---|---|
| `Order 15 copies of The Winter Archive for Alice Martin.` then `y` | `< place_order: error: Not enough stock for "The Winter Archive" (id 144): 15 requested, 11 in stock. Nothing was ordered.` (12 on a fresh database). Within the same reply the model explains and offers the 11; approve that with `y` if you like, or decline with `n`. The model often checks the stock first (`> get_book {"bookId": 144}`) and asks straight away, with no `place_order` at all: nothing is ordered either way. |

## 6. The database stops mid-session (APP-18)

| Do | Expect |
|---|---|
| In another terminal, in `bookshop`: `docker compose stop postgres` | |
| `Is The Glass Tide in stock?` | `< search_books: error: …`, possibly `[The session could not be saved: …]` (the session is saved after every step), and the model says plainly that the database cannot be reached and to try again shortly. |
| `docker compose start postgres`, then `Try again, please.` | `< search_books: ok` and the answer: the session goes on, and is saved again. |

## 7. Ctrl+C (APP-03)

| Do | Expect |
|---|---|
| `Describe each of the ten most expensive books in a short paragraph.` and press Ctrl+C while it streams | The text stops, `[Cancelled.]`, and the prompt returns. The application keeps running. |
| `Just the titles, please.` | A normal reply. Calls the cancelled reply had started get "cancelled" results, so the model may look the books up again. Ctrl+C at a `you>` prompt quits. After a Ctrl+C, `go run` itself exits with status 1 when the application ends; the application does not fail. |

## 8. Status line and `/cost` (APP-14, CTX-05)

| Type | Expect |
|---|---|
| (after any reply) | `[tokens: 9,586 in (94% from cache), 305 out · reply $0.0128 · session $0.0541]`: the reply's tokens, the share of its input read from the cache, its cost and the session's. From the second reply on, most input comes from the cache. |
| `/cost` | `Session <id>: tokens: … in (…% from cache), … out; cost $… of its $5.00 budget.` |

## 9. Memory (APP-11)

| Type | Expect |
|---|---|
| `I prefer prices with tax.` | `> memory {"command":"create","path":"/memories/preferences.md",…}` (or `str_replace`), then a short confirmation. |
| `/memory` | `Remembered:`, `/memories/preferences.md` and its text. Memory is per staff member, in `data/memory/` under `go/`: under another name, `/memory` says `Nothing remembered yet.` |

## 10. Export through MCP (APP-12)

| Type | Expect |
|---|---|
| `Export Alice Martin's order history as CSV.` | `> list_customer_orders`, then `? filesystem__write_file needs your approval.` with the path `/projects/exports/order-history-alice-martin.csv` and the CSV text. |
| `y` | `< filesystem__write_file: ok` and the file's name. `cat ../bookshop/exports/order-history-alice-martin.csv` shows it: a header row and one line per order. |

## 11. `/audit` and the dashboard (APP-16, APP-20)

| Do | Expect |
|---|---|
| `/audit` | `Audit of session <id>:`, then per run `Run N, trace: http://localhost:18888/traces/detail/<trace id>` and its entries in order: `RunStarted`, `ToolSource filesystem connected`, `ToolStarted`/`ToolEnded` with outcome and duration, `ApprovalAsked`/`ApprovalAnswered` with the answer, `RunEnded` with tokens, cached tokens and cost. |
| Open the trace link of step 3's run | One trace: `reply` → `invoke_agent bookshop` → its `chat` model calls and `execute_tool` spans, with tokens, cache reads, cost and the approval wait on each. Structured logs for the trace show the console's log of the reply. |
| Back from the trace | Under Metrics, the core's `officina.model.cost`, `officina.model.cache_hit_ratio`, `gen_ai.client.operation.duration`, `officina.tool.calls` and `officina.tool.approvals` over the session. `BOOKSHOP_TELEMETRY_CONTENT=true` puts message text and tool inputs and results in the traces, for debugging. |

## 12. Sessions, summaries, resume, a cancellation and a budget stop (APP-02, APP-06, APP-10, APP-14, APP-15)

| Do | Expect |
|---|---|
| `/quit` | `Session <id> summarized: <title>`: a separate stateless agent with typed output wrote its title, summary and changes. |
| `go run ./cmd/bookshop`, `Sam`, `/sessions` | `Sessions, most recent first:` with id, time, staff member, title and cost, then the summary and `Changes: …` (the orders placed, the export). A session left without a summary (killed) is summarized here. |
| `How much is The Winter Archive?` | The price with tax, from Sam's memory (step 9), in a new session. |
| `/resume <id of the first session>` | `Resumed session <id>: N messages, $… so far.` |
| `What was the total of the order you placed for Alice?` | £13.20, from the conversation; the status line shows most of the input read from the cache. |
| `Cancel order 81.` then `y` | `? cancel_order needs your approval.` with `{"orderId":81}`, then `< cancel_order: ok`: the order is cancelled and its copies go back to stock, which undoes step 3. |
| `/new` | `Session <id> summarized: …` for the session left, then `New session <id>.` |
| `/quit`, then `BOOKSHOP_REPLY_BUDGET=0.01 go run ./cmd/bookshop`, `Sam` | Each reply may now spend one cent. A value that is not an amount above zero stops the start with a message. |
| `Compare the average price of fantasy and mystery books in stock.` | A step or two, then `[Stopped: this reply has reached its budget of $0.01.]` and the status line. The session goes on; `/quit`. |

## 13. Long conversations in demo mode (APP-17, HIST-01…04, APP-10)

Demo mode compacts from 50,000 input tokens and clears old tool results when a request holds more than 12 tool calls.
Its sessions do not resume in normal mode, nor the other way round.

| Type | Expect |
|---|---|
| `go run ./cmd/bookshop --demo`, `Sam` | `Demo mode: compaction from 50,000 input tokens, and old tool results cleared above 12 tool calls.` Note the session id. |
| `Run these four catalogue searches together, then just give me the four counts: every book priced at most £18 (up to 300 of them), the 250 cheapest books in stock or not, every book in stock priced at most £18 (up to 300), and every book priced at most £16 (up to 300).` | Four `> search_books` lines together (`{"maxPrice":18,"limit":300}`, `{"limit":250}`, `{"maxPrice":18,"inStock":true,"limit":300}`, `{"maxPrice":16,"limit":300}`), then `~ Conversation compacted: … tokens summarized into ….` and the counts 245, 250, 224 and 208. About $0.33. The compacting reply may come without text: `[The conversation was compacted and the reply has no text. Please ask again.]`; ask again. |
| `Look up books 1 to 8 with get_book, then list their titles.` | Eight `> get_book` calls in parallel and the eight titles. Usually no clearing yet; when the model called the memory tool several times in the first turn, those calls count, and the clearing comes here. |
| `Now look up books 9 to 16 with get_book, then list their titles.` | Eight more `> get_book` calls in parallel, then `~ Old tool results cleared: … tool calls, … tokens.`: the next request holds more than 12 tool calls, so the oldest results are cleared, and the 10 most recent stay. The model does not fetch books 9 to 16 again. |
| `/audit` | `Compacted` and `Cleared` entries with their numbers. |
| `/quit`, then `go run ./cmd/bookshop` (normal mode), `Sam`, `/resume <demo session id>` | `Session <id> was started with another version of the assistant, so it cannot go on. Type /new to start a new session.` (APP-10): its context management differs, so its prefix would not match. |

## 14. A session the .NET application saved resumes here (APP-10)

Both applications store sessions in the same table, with the same prefix fingerprint, so one goes on in the other with
its cache intact. This needs the .NET 10 SDK; the .NET application reads its settings from `appsettings.json` and
`appsettings.Local.json` in `apps/BookshopAssistant` (step 0.3 of [`docs/demo.md`](../../docs/demo.md)).

| Do | Expect |
|---|---|
| In `apps/BookshopAssistant`: `dotnet run`, `Sam`, `How many orders has Alice Martin placed?` | The .NET console's answer. Note the session id; `/quit`. |
| In `go/`: `go run ./cmd/bookshop`, `Sam`, `/resume <that id>` | `Resumed session <id>: N messages, $… so far.`: the Go chat agent's tools, instructions, model settings and context management give .NET's fingerprint. |
| `And when was the latest?` | The answer from the .NET session's lookups; the status line shows most of the input read from the cache (written by the .NET application's calls). |

## What was checked live

Every step was run as written on 10 October 2026, live with Opus 5.5 and a database fresh from the seed, by a script
that typed each line at the prompt and answered each approval. Together the runs cost about $1.40.

| Step | Live | Notes |
|---|---|---|
| 1 Run context | ✓ | `/help` listed the commands; the model viewed Sam's memory, then gave the date and his name. |
| 2 Parallel reads | ✓ | `search_books` and `find_customer` started together, then `list_customer_orders`. |
| 3 APP-09 | ✓ | Order 82 (order 81 was step 4's, run first by mistake), books 144 and 216, total £13.20. |
| 4 Denial | ✓ | `< place_order: error: The call was denied: the staff member declined`, and an alternative offered. |
| 5 Not enough stock | ✓ | The model checked the stock first and offered the copies there were, without a `place_order`; the error result path is the offline test's (APP-07). |
| 6 Database stopped | ✓ | `[The session could not be saved: …]`, `< search_books: error: …`, then `ok` after the restart. |
| 7 Ctrl+C | ✓ | `[Cancelled.]` mid-reply, its unfinished calls answered as cancelled; the next reply was normal. |
| 8 Status line, `/cost` | ✓ | 95–99% of each reply's input read from the cache from the second reply on. |
| 9 Memory | ✓ | The preference written to `/memories/progress.md`; `/memory` showed it; the next session read it. |
| 10 Export | ✓ | `order-history-alice-martin.csv` written after approval, a header row and a line per order line. |
| 11 `/audit`, dashboard | ✓ | Runs with their trace links and entries; the dashboard held `reply` → `invoke_agent bookshop` → `chat` and `execute_tool` spans. |
| 12 Sessions, resume, budget | ✓ | Summaries in `/sessions`; resume read 97% from the cache; `cancel_order` after approval; `$0.01` stopped the reply after its third model call. |
| 13 Demo mode | ✓ | Compacted 53,189 tokens into 2,512 ($0.34); the counts 245, 250, 224 and 208; clearing after the first lookup turn (the model's four memory calls counted) and again after the second; the demo session refused in normal mode. |
| 14 .NET session resumed | ✓ | `Resumed session …: 7 messages`, 96% of the first call's input read from the cache. Also a live test (`TestLive_APP10_…`). |
