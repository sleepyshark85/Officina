# Bookshop Assistant demo script (APP-19)

A walkthrough of every Bookshop Assistant capability (APP-01…20), in one sitting of about 30 minutes. Each step gives
what to type and what to expect. The model's wording varies from run to run; the tool lines (`>` a call, `<` its
outcome, `?` an approval, `~` a context event) and the bracketed lines come from the console and do not.

**Cost.** Steps 1–12 cost about $0.30 together, and step 13 (demo mode) about $0.40. The status line after each reply
shows what was spent.

**Live check (S13b).** Steps marked ✓ were run live with Opus 5.5 on 6 October 2026, and the others only through their
offline tests (TEST-09): see [What was checked live](#what-was-checked-live).

## 0. Setup

| # | Do | Expect |
|---|---|---|
| 0.1 | `cd apps/BookshopAssistant` then `docker compose up -d --wait` | `postgres` and `dashboard` healthy. The schema and seed (480 books, 40 customers, 80 orders) are created on first start; `docker compose down -v` resets them. |
| 0.2 | `docker compose --profile mcp pull` | The filesystem MCP server's image (APP-12). The application starts it itself; `up` does not. |
| 0.3 | Open http://localhost:18888 | The telemetry dashboard (APP-20): Traces, Metrics and Structured logs, empty for now. |
| 0.4 | `export BOOKSHOP_CONNECTION_STRING="Host=localhost;Port=5432;Username=bookshop;Password=shelf-demo-41;Database=bookshop"` and `export ANTHROPIC_API_KEY=…` | |
| 0.5 | `dotnet run` | `Bookshop Assistant. Type /help for commands.` and `Who is using the assistant? Your name:` |

If a port is taken, set `BOOKSHOP_DB_PORT` (and the connection string), `BOOKSHOP_DASHBOARD_PORT` with
`BOOKSHOP_DASHBOARD_URL`, or `BOOKSHOP_OTLP_PORT` with `OTEL_EXPORTER_OTLP_ENDPOINT`, as `compose.yaml` explains.

## 1. Run context ✓ (APP-13)

| Type | Expect |
|---|---|
| `Sam` | `Hello, Sam.` and `Session <id>.` Note the id. |
| `What is today's date, and who am I?` | `> memory {"command":"view",…}` (the model checks Sam's memory first), then today's date and "you're Sam". Neither is in the instructions: they come as run context, sent at the start of the session and again only on a new day. |

## 2. Streaming, tool activity and parallel reads (APP-01, APP-05)

| Type | Expect |
|---|---|
| `Is The Winter Archive in stock, and what has Alice Martin ordered before?` | The reply streams as it is written. `> search_books {"title":"Winter Archive"}` and `> find_customer {"nameOrEmail":"Alice Martin"}` both start before either `<` line: read calls of one reply run in parallel. Then `> list_customer_orders {"customerId":1}`, with any text the model writes between the calls, and the answer. |

## 3. A multi-step order with approval ✓ (APP-09, APP-06)

| Type | Expect |
|---|---|
| `Order the two cheapest fantasy books in stock for Alice Martin and tell me the total.` | `> find_customer` and `> search_books {"genre":"Fantasy","inStock":true,…}` in parallel; a sentence saying what it will order; then `? place_order needs your approval. Its exact input:` with `{"customerId":1,"lines":[{"bookId":144,"quantity":1},{"bookId":216,"quantity":1}]}`. |
| `y` | `< place_order: ok` and the answer: order 81 on a fresh database, The Winter Archive (£6.28) and The Hollow Island (£6.92), **total £13.20**. The cheapest fantasy book, The Silent Mountain (id 72), is out of stock, so it is skipped. |

## 4. A denial the model works around (APP-06, TOOL-04)

| Type | Expect |
|---|---|
| `Order three copies of The Hollow Island for Ben Hughes.` | `? place_order needs your approval.` with the exact input. |
| `n` | `< place_order: error: The call was denied: the staff member declined`. The model says nothing was ordered and offers another way, such as fewer copies or holding it for later. Nothing changed in the database. |

## 5. Not enough stock (APP-07)

| Type | Expect |
|---|---|
| `Order 15 copies of The Winter Archive for Alice Martin.` then `y` | `< place_order: error: Not enough stock for "The Winter Archive" (id 144): 15 requested, 11 in stock. Nothing was ordered.` (12 on a fresh database). Within the same reply the model explains and offers the 11; approve that with `y` if you like. |

## 6. The database stops mid-session (APP-18)

| Do | Expect |
|---|---|
| In another terminal, in `apps/BookshopAssistant`: `docker compose stop postgres` | |
| `Is The Glass Tide in stock?` | `< search_books: error: …`, possibly `[The session could not be saved: …]` (the session is saved after every step), and the model says plainly that the database cannot be reached and to try again shortly. |
| `docker compose start postgres`, then `Try again, please.` | `< search_books: ok` and the answer: the session goes on, and is saved again. |

## 7. Ctrl+C (APP-03)

| Do | Expect |
|---|---|
| `Describe each of the ten most expensive books in a short paragraph.` and press Ctrl+C while it streams | The text stops, `[Cancelled.]`, and the prompt returns. The application keeps running. |
| `Just the titles, please.` | A normal reply: the cancelled exchange left nothing behind. Ctrl+C at a `you>` prompt quits. |

## 8. Status line and `/cost` ✓ (APP-14, CTX-05)

| Type | Expect |
|---|---|
| (after any reply) | `[tokens: 9,586 in (94% from cache), 305 out · reply $0.0128 · session $0.0541]`: the reply's tokens, the share of its input read from the cache, its cost and the session's. From the second reply on, most input comes from the cache. |
| `/cost` | `Session <id>: tokens: … in (…% from cache), … out; cost $… of its $5.00 budget.` |

## 9. Memory (APP-11)

| Type | Expect |
|---|---|
| `I prefer prices with tax.` | `> memory {"command":"create","path":"/memories/preferences.md",…}` (or `str_replace`), then a short confirmation. |
| `/memory` | `Remembered:`, `/memories/preferences.md` and its text. Memory is per staff member: under another name, `/memory` says `Nothing remembered yet.` |

## 10. Export through MCP (APP-12)

| Type | Expect |
|---|---|
| `Export Alice Martin's order history as CSV.` | `> list_customer_orders`, then `? filesystem__write_file needs your approval.` with the path `/projects/exports/order-history-alice-martin.csv` and the CSV text. |
| `y` | `< filesystem__write_file: ok` and the file's name. `cat exports/order-history-alice-martin.csv` shows it: a header row and one line per order. |

## 11. `/audit` and the dashboard ✓ (APP-16, APP-20)

| Do | Expect |
|---|---|
| `/audit` | `Audit of session <id>:`, then per run `Run N, trace: http://localhost:18888/traces/detail/<trace id>` and its entries in order: `RunStarted`, `ToolSource filesystem connected`, `ToolStarted`/`ToolEnded` with outcome and duration, `ApprovalAsked`/`ApprovalAnswered` with the answer, `RunEnded` with tokens, cached tokens and cost. |
| Open the trace link of step 3's run | One trace: `reply` → `invoke_agent bookshop` → its `chat` model calls and `execute_tool` spans, with tokens, cache reads, cost and the approval wait on each. Structured logs for the trace show the console's log of the reply. |
| Back from the trace | The `invoke_agent` span's `gen_ai.conversation.id` is the session id: `/audit <that id>` lists the same run. Under Metrics, `bookshop-assistant` charts `officina.model.cost`, `officina.model.cache_hit_ratio`, `gen_ai.client.operation.duration`, `officina.tool.calls` and `officina.tool.approvals` over the session. |

## 12. Sessions, summaries, resume and a budget stop ✓ (APP-10, APP-15, APP-14)

| Do | Expect |
|---|---|
| `/quit` | `Session <id> summarized: <title>`: a separate stateless agent with typed output wrote its title, summary and changes. |
| `dotnet run`, `Sam`, `/sessions` | `Sessions, most recent first:` with id, time, staff member, title and cost, then the summary and `Changes: …` (the orders placed, the export). A session left without a summary (killed) is summarized here. |
| `How much is The Winter Archive?` | The price with tax, from Sam's memory (step 9), in a new session. |
| `/resume <id of the first session>` | `Resumed session <id>: N messages, $… so far.` |
| `What was the total of the order you placed for Alice?` | £13.20, from the conversation; the status line shows most of the input read from the cache (94% when checked live). |
| `/quit`, then `BOOKSHOP_REPLY_BUDGET=0.01 dotnet run`, `Sam` | Each reply may now spend one cent. |
| `Compare the average price of fantasy and mystery books in stock.` | A step or two, then `[Stopped: this reply has reached its budget of $0.01.]` and the status line. The session goes on; `/quit`. |

## 13. Long conversations in demo mode (APP-17, HIST-01…04)

Demo mode compacts from 50,000 input tokens and clears old tool results once a conversation holds more than 12 tool
calls. Its sessions do not resume in normal mode, nor the other way round.

| Type | Expect |
|---|---|
| `dotnet run -- --demo`, `Sam` | `Demo mode: compaction from 50,000 input tokens, and old tool results cleared after 12 tool calls.` |
| `Show me every book priced at most £18, up to 300 of them. Just tell me how many there are and the cheapest one.` | `> search_books {"maxPrice":18,"limit":300}`: 245 books. No `~` line yet. |
| `Now the 250 cheapest books in the catalogue, in stock or not. How many of them are fantasy?` | `> search_books {"limit":250}`. No `~` line yet. |
| `Every book in stock priced at most £18, up to 300 of them. Just tell me how many.` | `> search_books {"maxPrice":18,"inStock":true,"limit":300}`: 224. No `~` line yet. |
| `Last one: every book priced at most £16, up to 300 of them. Just tell me how many are out of stock.` | `> search_books {"maxPrice":16,"limit":300}`, then `~ Conversation compacted: about 55,000 tokens summarized into about 3,000.` The compacting reply may come without text: `[The conversation was compacted and the reply has no text. Please ask again.]`; ask again. |
| `Look up books 1, 2, 3 and 4 with get_book, one at a time, then list their titles.` | Once the session holds more than 12 tool calls: `~ Old tool results cleared: N tool calls, … tokens.` If it does not show yet, ask for a few more books. |
| `/audit` | `Compacted` and `Cleared` entries with their numbers. |

The four searches return 10–15k tokens each (measured with the token-counting endpoint, on the seed data):

| Search | Books | Characters | Tokens |
|---|---|---|---|
| `maxPrice` 18, `limit` 300 | 245 | 26,474 | 12,625 |
| `limit` 250 | 250 | 27,014 | 12,882 |
| `maxPrice` 18, `inStock`, `limit` 300 | 224 | 24,227 | 11,561 |
| `maxPrice` 16, `limit` 300 | 208 | 22,462 | 10,715 |

With the agent's tools and instructions (about 4,200 tokens) and the replies, the conversation crosses 50,000 tokens on
the fourth search, so compaction lands just over the threshold. The live smoke test asks for the same four searches in
one turn: compaction summarized 52,255 tokens into 3,068 and the reply cost $0.33.

## What was checked live

| Step | Live | Notes |
|---|---|---|
| 1 Run context | ✓ | Date and name given; the model viewed its memory first. |
| 3 APP-09 | ✓ | In the smoke test: order of books 144 and 216, £13.20, find and search in parallel. |
| 8 Status line, `/cost` | ✓ | |
| 11 `/audit`, trace | ✓ | The trace link opened and the dashboard held the run's spans. |
| 12 Summaries, `/sessions`, `/resume`, budget stop | ✓ | Resume read 94% from the cache; `$0.01` stopped the reply after two steps. |
| 13 Compaction | Partly | The smoke test reached it with the same searches in one turn. One search a turn was run before the fix below, and did not compact. |
| 2, 4–7, 9, 10 | Offline only | Their console flows are TEST-09 tests against the real database, with the model scripted. |

The first live run of step 13 found that clearing after 4 tool calls emptied the searches before they could compact:
the model calls the memory tool once or twice a turn, and those calls count. Demo mode now clears after 12.
