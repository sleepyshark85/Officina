// Package mcp is Officina's own client of Model Context Protocol servers, over stdio or Streamable HTTP. Connect
// reads a server's tool list once, keeps the tools the host allows and presents them as tools of the core, which
// go through the same pipeline as any tool.
package mcp
