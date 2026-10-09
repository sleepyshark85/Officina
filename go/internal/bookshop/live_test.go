//go:build live

package bookshop_test

import (
	"context"
	"iter"
	"net/http"
	"os"
	"strings"
	"sync"
	"testing"

	"github.com/sleepyshark85/officina/go/internal/bookshop"
	"github.com/sleepyshark85/officina/go/officina"
)

// The live smoke tests: the real console, Claude in demo mode and the database in Docker, with only the staff member
// scripted. One places an order with approval; the other reaches a compaction with the demo script's catalogue
// searches (docs/demo.md). Both check that every model call after the first reads the cache. Together they cost about
// $0.40, so they are built only with the live tag:
//
//	go test -tags live -run TestLive -v ./internal/bookshop

// orderRequest is the order request, as in the demo script.
const orderRequest = "Order the two cheapest fantasy books in stock for Alice Martin and tell me the total."

// searches are the demo script's four 10–15k-token searches, which together cross 50,000 input tokens, in one turn.
const searches = "Run these four catalogue searches together, then just give me the four counts: every book priced " +
	"at most £18 (up to 300 of them), the 250 cheapest books in stock or not, every book in stock priced at most £18 " +
	"(up to 300), and every book priced at most £16 (up to 300)."

func TestLive_TEST04_APP09_PlacesTheOrderAfterApprovalAndReadsTheCacheFromTheSecondCall(t *testing.T) {
	t.Parallel()
	d := newDatabase(t)

	transcript, calls := liveSession(t, d, 0.20, "Smoke", orderRequest, "y", "/quit")

	// The order holds the two cheapest fantasy books in stock, one copy each, and the reply gives its total.
	cheapest := scalar[string](t, d, `select string_agg(id::text, ',' order by id) from (
		select b.id from books b join genres g on g.id = b.genre_id join stock s on s.book_id = b.id
		where g.name = 'Fantasy' and s.quantity > 0 order by b.price, b.title limit 2) cheapest`)
	newest := scalar[int](t, d, "select max(id) from orders")
	if newest <= 80 {
		t.Fatalf("no order was placed:\n%s", transcript)
	}
	if customer := scalar[int](t, d, "select customer_id from orders where id = $1", newest); customer != 1 {
		t.Errorf("order %d is for customer %d, want Alice Martin (1)", newest, customer)
	}
	books := scalar[string](t, d, `select string_agg(book_id::text, ',' order by book_id) from order_lines
		where order_id = $1 and quantity = 1`, newest)
	if books != cheapest {
		t.Errorf("order %d holds one copy each of books %s, want %s", newest, books, cheapest)
	}
	total := scalar[string](t, d, "select total::text from orders where id = $1", newest)
	inOrder(t, transcript, "  ? place_order needs your approval.", "  < place_order: ok", total)
	cacheReadFromTheSecondCall(t, calls)
}

func TestLive_TEST04_APP17_DemoModeCompactsAfterTheDemoScriptsSearches(t *testing.T) {
	t.Parallel()

	transcript, calls := liveSession(t, newDatabase(t), 0.40, "Smoke", searches, "/quit")

	if !strings.Contains(transcript, "  ~ Conversation compacted:") {
		t.Errorf("the transcript shows no compaction:\n%s", transcript)
	}
	cacheReadFromTheSecondCall(t, calls)
}

// liveSession runs the console on Claude in demo mode against d, with the script and a reply and session budget of
// budget US dollars, and returns the transcript and each model call's usage, in order.
func liveSession(t *testing.T, d *database, budget float64, script ...any) (string, []officina.Usage) {
	t.Helper()
	if os.Getenv("ANTHROPIC_API_KEY") == "" {
		t.Fatal("the live tests need ANTHROPIC_API_KEY")
	}
	// The SDK sends through http.DefaultClient; its connections must not outlive the tests.
	t.Cleanup(http.DefaultClient.CloseIdleConnections)
	claude, err := bookshop.Model(true)
	if err != nil {
		t.Fatalf("Model() error = %v", err)
	}
	model := &metered{Model: claude}
	out := &transcript{}
	app, err := bookshop.Build(t.Context(), bookshop.Config{
		Database: d.url, Model: model, In: &input{t: t, script: script}, Out: out, Echo: true, Demo: true,
		Budgets: bookshop.Budgets{Reply: budget, Session: budget},
	})
	if err != nil {
		t.Fatalf("Build() error = %v", err)
	}
	defer app.Close()
	if err := app.Run(t.Context()); err != nil {
		t.Fatalf("Run() error = %v\ntranscript:\n%s", err, out)
	}
	calls := model.usage()
	t.Log(out.String())
	for i, u := range calls {
		t.Logf("call %d: %d input tokens, %d read from the cache, %d written to it, %d output", i+1,
			u.Input+u.CacheRead+u.CacheWrite, u.CacheRead, u.CacheWrite, u.Output)
	}
	if strings.Contains(out.String(), "[Failed") {
		t.Errorf("a reply failed:\n%s", out)
	}
	return out.String(), calls
}

// cacheReadFromTheSecondCall checks that there were several model calls and each after the first read the cache.
func cacheReadFromTheSecondCall(t *testing.T, calls []officina.Usage) {
	t.Helper()
	if len(calls) < 3 {
		t.Fatalf("%d model calls, want several", len(calls))
	}
	for i, u := range calls[1:] {
		if u.CacheRead == 0 {
			t.Errorf("model call %d read nothing from the cache: %+v", i+2, calls)
		}
	}
}

// metered is a model that records each call's usage, as the provider reports it.
type metered struct {
	officina.Model
	mu    sync.Mutex
	calls []officina.Usage
}

func (m *metered) Stream(ctx context.Context, req officina.Request) iter.Seq2[officina.ModelEvent, error] {
	return func(yield func(officina.ModelEvent, error) bool) {
		var used officina.Usage
		defer func() {
			m.mu.Lock()
			defer m.mu.Unlock()
			m.calls = append(m.calls, used)
		}()
		for e, err := range m.Model.Stream(ctx, req) {
			if u, ok := e.(officina.UsageReceived); ok {
				used.Input += u.Usage.Input
				used.Output += u.Usage.Output
				used.CacheRead += u.Usage.CacheRead
				used.CacheWrite += u.Usage.CacheWrite
			}
			if !yield(e, err) {
				return
			}
		}
	}
}

func (m *metered) usage() []officina.Usage {
	m.mu.Lock()
	defer m.mu.Unlock()
	return append([]officina.Usage(nil), m.calls...)
}
