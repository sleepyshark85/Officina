// Bookshop is Bookshop Assistant, the reference application: a console chatbot for bookshop staff over the
// PostgreSQL database of the compose file in bookshop/, with Claude Opus 5.5 as its model.
//
// Start the database and the telemetry dashboard with `docker compose up --detach --wait postgres dashboard` in
// bookshop/, set ANTHROPIC_API_KEY, and run it:
//
//	go run ./cmd/bookshop
//
// Its traces, metrics and logs go to the dashboard at http://localhost:18888, and /audit links each run to its
// trace there. BOOKSHOP_DATABASE, a PostgreSQL connection string, names another database than the compose file's;
// OTEL_EXPORTER_OTLP_ENDPOINT another OTLP/gRPC endpoint than the dashboard's, http://localhost:4317; and
// BOOKSHOP_DASHBOARD another dashboard. Ctrl+C stops the reply in progress; /quit leaves.
package main
