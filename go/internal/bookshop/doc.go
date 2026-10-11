// Package bookshop is Bookshop Assistant, the reference application: a console chatbot for the staff of a bookshop
// over the PostgreSQL database of the compose file in bookshop/, which the .NET implementation shares.
//
// Build is the composition root: it wires the database, the nine bookshop tools, the chat agent and the console, and
// tests call it with a scripted model and scripted input in place of Claude and the staff member.
package bookshop
