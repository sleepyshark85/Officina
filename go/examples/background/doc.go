// Package background is the background agent sample: one job per support ticket, started by a queue or a schedule,
// with nobody to ask. Its tools are the helpdesk's MCP server, over Streamable HTTP, and the host's own refund tool;
// it audits to a JSON-lines file, each job has a budget, and it returns typed output beside its side effect, a note on
// the ticket. Its tests run it offline on the test kit's scripted model and fake MCP server.
package background
