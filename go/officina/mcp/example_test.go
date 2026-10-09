package mcp_test

import (
	"context"
	"encoding/json/jsontext"
	"fmt"
	"net/http"
	"net/http/httptest"

	"github.com/sleepyshark85/officina/go/officina"
	"github.com/sleepyshark85/officina/go/officina/mcp"
	"github.com/sleepyshark85/officina/go/officina/officinatest"
)

// An agent uses the allowed tools of a server over Streamable HTTP: here the test kit's fake server, in place of a
// real one, and a scripted model. The host allows only the tool the agent needs, and marks it read.
func ExampleConnect() {
	ctx := context.Background()
	fake := officinatest.NewMCPServer(
		officinatest.MCPTool{Name: "upper", Handler: func(jsontext.Value) (string, error) { return "HI", nil }},
		officinatest.MCPTool{Name: "delete_everything"},
	)
	srv := httptest.NewServer(fake)
	defer srv.Close()

	server := mcp.Server{Name: "files", URL: srv.URL + "/mcp", Header: http.Header{"Authorization": {"Bearer secret"}}}
	source, err := mcp.Connect(ctx, server, []mcp.AllowedTool{{Name: "upper", Kind: officina.Read}})
	if err != nil {
		fmt.Println(err)
		return
	}
	defer source.Close()
	for _, t := range source.Tools() {
		fmt.Println(t.Name, t.Kind)
	}

	model := officinatest.NewModel("scripted",
		officinatest.ToolUseReply(officinatest.ToolUseBlock("c1", "files__upper", `{"text":"hi"}`)),
		officinatest.TextReply("Done."))
	agent, err := officina.NewAgent(model, "You use tools.", officina.AgentOptions{
		Tools: source.Tools(), Secrets: server.Secrets(),
	})
	if err != nil {
		fmt.Println(err)
		return
	}
	res, err := agent.Run(ctx, nil, "Shout hi.", officina.RunOptions{})
	if err != nil {
		fmt.Println(err)
		return
	}
	fmt.Println(res.Status, res.Text)
	fmt.Println(fake.Calls())
	// Output:
	// files__upper Read
	// Completed Done.
	// [{upper {"text":"hi"}}]
}
