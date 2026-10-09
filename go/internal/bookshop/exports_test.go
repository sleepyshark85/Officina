package bookshop_test

import (
	"context"
	"encoding/json/v2"
	"fmt"
	"io"
	"os"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/google/go-cmp/cmp"
	"github.com/testcontainers/testcontainers-go"
	"github.com/testcontainers/testcontainers-go/wait"

	"github.com/sleepyshark85/officina/go/internal/bookshop"
	"github.com/sleepyshark85/officina/go/officina/officinatest"
)

// The export tests run the console against the real filesystem MCP server, built from the application's
// exports-server image in Docker, over HTTP; they read its exports folder from its container. Only the model and the
// staff member are scripted.

// exportsImage is the export server's build context, which the .NET tests build too.
const exportsImage = "../../../apps/BookshopAssistant/exports-server"

// exports is the export server of this run, started by the first test that needs it and shared by the others,
// which write files of their own names; TestMain stops it.
var exports struct { //nolint:gochecknoglobals // The run's export server, shared by its tests.
	once      sync.Once
	url       string
	container testcontainers.Container
	err       error
}

// exportServer returns the export server's endpoint; it skips t where the database tests do not run.
func exportServer(t *testing.T) string {
	t.Helper()
	if server.skip != "" {
		t.Skip(server.skip)
	}
	exports.once.Do(startExports)
	if exports.err != nil {
		t.Fatalf("start the export server: %v", exports.err)
	}
	return exports.url
}

func startExports() {
	ctx := context.Background()
	exports.container, exports.err = testcontainers.Run(ctx, "",
		// Built under a fixed name and kept, so later runs reuse Docker's layer cache.
		testcontainers.WithDockerfile(testcontainers.FromDockerfile{
			Context: exportsImage, Repo: "bookshop-exports-server", Tag: "test", KeepImage: true,
		}),
		testcontainers.WithExposedPorts("8000/tcp"),
		testcontainers.WithWaitStrategyAndDeadline(5*time.Minute, wait.ForHTTP("/healthz").WithPort("8000/tcp")),
	)
	if exports.err != nil {
		return
	}
	// The compose file mounts the folder; here it is the container's own. The server starts with each session.
	code, _, err := exports.container.Exec(ctx, []string{"mkdir", "-p", "/projects/exports"})
	if err != nil || code != 0 {
		exports.err = fmt.Errorf("make the exports folder: exit code %d, %w", code, err)
		return
	}
	endpoint, err := exports.container.PortEndpoint(ctx, "8000/tcp", "http")
	exports.url, exports.err = endpoint+"/mcp", err
}

// stopExports stops the export server, if a test started it.
func stopExports() {
	if exports.container != nil {
		if err := testcontainers.TerminateContainer(exports.container); err != nil {
			fmt.Fprintln(os.Stderr, "terminate the export server's container:", err)
		}
	}
}

// exported returns the file of the export server's exports folder, or the error reading it.
func exported(t *testing.T, name string) (string, error) {
	t.Helper()
	file, err := exports.container.CopyFileFromContainer(t.Context(), "/projects/exports/"+name)
	if err != nil {
		return "", err
	}
	defer func() { _ = file.Close() }() // It has been read.
	data, err := io.ReadAll(file)
	return string(data), err
}

func TestConsole_APP12_TheAllowListOffersOnlyWritingAFileWithApprovalAndListingTheFolder(t *testing.T) {
	t.Parallel()
	d := newDatabase(t)
	url := exportServer(t)
	model := officinatest.NewModel("scripted", officinatest.TextReply("Hello."))

	sessionOf(t, bookshop.Config{Exports: url}, d, model, "", "Sam", "Hi.", "/quit")

	var got []string
	for _, tl := range model.Requests()[0].Tools {
		if strings.HasPrefix(tl.Name, "filesystem__") {
			got = append(got, fmt.Sprintf("%s %v approval:%v", tl.Name, tl.Kind, tl.NeedsApproval))
		}
	}
	want := []string{"filesystem__list_directory Read approval:false", "filesystem__write_file Write approval:true"}
	if diff := cmp.Diff(want, got); diff != "" {
		t.Errorf("export tools mismatch (-want +got):\n%s", diff)
	}
}

func TestConsole_APP12_AnOrderHistoryIsExportedAsCSVAfterApproval(t *testing.T) {
	t.Parallel()
	d := newDatabase(t)
	url := exportServer(t)
	csv := "order_id,status,placed_on,total\n" + scalar[string](t, d, "select string_agg(format('%s,%s,%s,%s', id, "+
		"status, to_char(placed_at at time zone 'UTC', 'YYYY-MM-DD'), total), E'\\n' order by placed_at desc, id desc) "+
		"from orders where customer_id = $1", 1) + "\n"
	write, err := json.Marshal(map[string]string{"path": "/projects/exports/order-history-alice-martin.csv", "content": csv})
	if err != nil {
		t.Fatalf("Marshal() error = %v", err)
	}
	model := officinatest.NewModel("scripted",
		sayThenCall("Let me find Alice.", officinatest.ToolUseBlock("c1", "find_customer", `{"nameOrEmail":"Alice Martin"}`)),
		sayThenCall("Reading her orders.", officinatest.ToolUseBlock("c2", "list_customer_orders", `{"customerId":1}`)),
		sayThenCall("I'll write the file.", officinatest.ToolUseBlock("c3", "filesystem__write_file", string(write))),
		sayThenCall("Checking the folder.",
			officinatest.ToolUseBlock("c4", "filesystem__list_directory", `{"path":"/projects/exports"}`)),
		officinatest.TextReply("Exported to order-history-alice-martin.csv."))

	transcript := sessionOf(t, bookshop.Config{Exports: url}, d, model, "",
		"Sam", "Export Alice Martin's order history as CSV.", "y", "/quit")

	inOrder(t, transcript,
		`  > list_customer_orders {"customerId":1}`+"\n",
		"  ? filesystem__write_file needs your approval. Its exact input:\n",
		"    "+string(write)+"\n",
		"    Approve? [y/N] y\n",
		"  < filesystem__write_file: ok\n",
		"  < filesystem__list_directory: ok\n",
		"Exported to order-history-alice-martin.csv.")
	file, err := exported(t, "order-history-alice-martin.csv")
	if err != nil {
		t.Fatalf("read the export: %v", err)
	}
	if diff := cmp.Diff(csv, file); diff != "" {
		t.Errorf("export mismatch (-want +got):\n%s", diff)
	}
	if listed := results(t, model, -1)[0]; listed.IsError ||
		!strings.Contains(listed.Content, "[FILE] order-history-alice-martin.csv") {
		t.Errorf("the folder's listing = %+v, want the export in it", listed)
	}
}

func TestConsole_APP12_ADeclinedExportWritesNoFile(t *testing.T) {
	t.Parallel()
	d := newDatabase(t)
	url := exportServer(t)
	model := officinatest.NewModel("scripted",
		sayThenCall("I'll write the file.", officinatest.ToolUseBlock("c1", "filesystem__write_file",
			`{"path":"/projects/exports/declined.csv","content":"id\n"}`)),
		officinatest.TextReply("Understood, no file."))

	transcript := sessionOf(t, bookshop.Config{Exports: url}, d, model, "", "Sam", "Export it.", "n", "/quit")

	inOrder(t, transcript,
		"    Approve? [y/N] n\n",
		"  < filesystem__write_file: error: The call was denied: the staff member declined\n",
		"Understood, no file.")
	if file, err := exported(t, "declined.csv"); err == nil {
		t.Errorf("declined.csv holds %q, want no such file", file)
	}
}

func TestBuild_APP12_AnExportServerThatCannotBeReachedFailsTheStartClearly(t *testing.T) {
	t.Parallel()
	const url = "http://127.0.0.1:1/mcp"

	_, err := bookshop.Build(t.Context(), bookshop.Config{
		Database: "postgres://bookshop@127.0.0.1:1/bookshop", Model: officinatest.NewModel("scripted"),
		In: strings.NewReader(""), Out: &strings.Builder{}, Exports: url,
	})

	if want := "build bookshop: the export server at " + url + " is not available"; err == nil ||
		!strings.HasPrefix(err.Error(), want) {
		t.Errorf("Build() error = %v, want it to start %q", err, want)
	}
}
