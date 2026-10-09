package bookshop

import (
	"context"
	"fmt"

	"github.com/sleepyshark85/officina/go/officina"
	"github.com/sleepyshark85/officina/go/officina/mcp"
)

// exportsServer names the export server, the reference filesystem MCP server that the compose file's filesystem
// service runs in Docker over Streamable HTTP, and which sees only the exports folder. Its name prefixes its tools.
const exportsServer = "filesystem"

// exportTools is the export server's allow-list: what an export needs. Writing a file needs approval; listing the
// folder is marked read, as MCP tools are writes unless the host says otherwise.
func exportTools() []mcp.AllowedTool {
	return []mcp.AllowedTool{
		{Name: "write_file", Kind: officina.Write, NeedsApproval: true},
		{Name: "list_directory", Kind: officina.Read},
	}
}

// connectExports connects to the export server at url, its /mcp endpoint, and pins its allowed tools.
func connectExports(ctx context.Context, url string) (*mcp.Source, error) {
	source, err := mcp.Connect(ctx, mcp.Server{Name: exportsServer, URL: url}, exportTools())
	if err != nil {
		return nil, fmt.Errorf("the export server at %s is not available (start it with start.sh in "+
			"apps/BookshopAssistant): %w", url, err)
	}
	return source, nil
}
