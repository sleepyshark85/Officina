// Bookshop is Bookshop Assistant, the reference application: a console chatbot for bookshop staff over the
// PostgreSQL database of the compose file in apps/BookshopAssistant, with Claude Opus 5.5 as its model.
//
// Start the database with `docker compose up --detach --wait postgres` in apps/BookshopAssistant, set
// ANTHROPIC_API_KEY, and run it:
//
//	go run ./cmd/bookshop
//
// BOOKSHOP_DATABASE, a PostgreSQL connection string, names another database than the compose file's. Ctrl+C stops
// the reply in progress; /quit leaves.
package main
