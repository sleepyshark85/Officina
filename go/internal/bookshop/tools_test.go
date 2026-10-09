package bookshop_test

import (
	"encoding/json/jsontext"
	"encoding/json/v2"
	"fmt"
	"strings"
	"sync"
	"testing"

	"github.com/google/go-cmp/cmp"

	"github.com/sleepyshark85/officina/go/internal/bookshop"
	"github.com/sleepyshark85/officina/go/officina"
)

const alice = 1

// tools returns the bookshop's tools on d, by name.
func tools(t *testing.T, d *database) map[string]officina.Tool {
	t.Helper()
	all, err := bookshop.Tools(d.pool)
	if err != nil {
		t.Fatalf("Tools() error = %v", err)
	}
	byName := map[string]officina.Tool{}
	for _, tool := range all {
		byName[tool.Name] = tool
	}
	return byName
}

// call calls the tool name with input, as the pipeline does once the input is valid.
func call(t *testing.T, tools map[string]officina.Tool, name, input string) (string, error) {
	t.Helper()
	tool, found := tools[name]
	if !found {
		t.Fatalf("no tool %q", name)
	}
	return tool.Handler(t.Context(), jsontext.Value(input))
}

// ok calls the tool and returns its result, read into a T; it fails t on an error.
func ok[T any](t *testing.T, tools map[string]officina.Tool, name, input string) T {
	t.Helper()
	result, err := call(t, tools, name, input)
	if err != nil {
		t.Fatalf("%s(%s) error = %v", name, input, err)
	}
	var v T
	if err := json.Unmarshal([]byte(result), &v); err != nil {
		t.Fatalf("%s(%s) result %s: %v", name, input, result, err)
	}
	return v
}

// failure calls the tool and returns its error's text; it fails t when there is none.
func failure(t *testing.T, tools map[string]officina.Tool, name, input string) string {
	t.Helper()
	result, err := call(t, tools, name, input)
	if err == nil {
		t.Fatalf("%s(%s) = %s, want an error", name, input, result)
	}
	return err.Error()
}

type (
	book struct {
		ID     int     `json:"id"`
		Title  string  `json:"title"`
		Author string  `json:"author"`
		Genre  string  `json:"genre"`
		Price  float64 `json:"price"`
		Stock  int     `json:"stock"`
	}
	customer struct {
		ID    int    `json:"id"`
		Name  string `json:"name"`
		Email string `json:"email"`
	}
	line struct {
		BookID    int     `json:"bookId"`
		Quantity  int     `json:"quantity"`
		UnitPrice float64 `json:"unitPrice"`
	}
	order struct {
		Order struct {
			ID       int     `json:"id"`
			Customer string  `json:"customer"`
			Status   string  `json:"status"`
			Total    float64 `json:"total"`
		} `json:"order"`
		Lines []line `json:"lines"`
	}
)

func ids(books []book) []int {
	var ids []int
	for _, b := range books {
		ids = append(ids, b.ID)
	}
	return ids
}

func TestTools_APP05_SearchFiltersByGenreAndStockAndListsTheCheapestFirst(t *testing.T) {
	t.Parallel()
	tools := tools(t, newDatabase(t))

	books := ok[[]book](t, tools, "search_books", `{"genre":"fantasy","inStock":true,"limit":3}`)
	cheapest := ok[[]book](t, tools, "search_books", `{"genre":"Fantasy","limit":1}`)

	// Book 72 is the cheapest fantasy book, and out of stock.
	if diff := cmp.Diff([]int{144, 216, 288}, ids(books)); diff != "" {
		t.Errorf("in-stock fantasy ids mismatch (-want +got):\n%s", diff)
	}
	for _, b := range books {
		if b.Genre != "Fantasy" || b.Stock < 1 {
			t.Errorf("book %+v, want Fantasy in stock", b)
		}
	}
	if diff := cmp.Diff([]int{72}, ids(cheapest)); diff != "" {
		t.Errorf("cheapest fantasy ids mismatch (-want +got):\n%s", diff)
	}
}

func TestTools_APP05_SearchMatchesPartOfTheTitleOrAuthorAndAHighestPrice(t *testing.T) {
	t.Parallel()
	tools := tools(t, newDatabase(t))

	byTitle := ok[[]book](t, tools, "search_books", `{"title":"silver tide"}`)
	byAuthor := ok[[]book](t, tools, "search_books", `{"author":"OKAFOR","maxPrice":10,"limit":100}`)

	if len(byTitle) != 1 || byTitle[0].Title != "The Silver Tide" {
		t.Errorf("search by title = %+v, want The Silver Tide alone", byTitle)
	}
	if len(byAuthor) == 0 {
		t.Error("search by author found nothing")
	}
	for _, b := range byAuthor {
		if !strings.Contains(b.Author, "Okafor") || b.Price > 10 {
			t.Errorf("book %+v, want by an Okafor at most 10", b)
		}
	}
}

func TestTools_APP08_SearchTextIsTakenLiterallySoAWildcardOrBackslashMatchesOnlyItself(t *testing.T) {
	t.Parallel()
	tools := tools(t, newDatabase(t))

	tests := []struct {
		tool, input string
		want        int
	}{
		{"search_books", `{"title":"_"}`, 0},
		{"search_books", `{"author":"%"}`, 0},
		{"find_customer", `{"nameOrEmail":"_"}`, 0},
		{"find_customer", `{"nameOrEmail":"alice\\"}`, 0},
		{"find_customer", `{"nameOrEmail":"MARTIN@example"}`, 1},
		// Blank text names no one, rather than every customer.
		{"find_customer", `{"nameOrEmail":"  "}`, 0},
	}
	for _, tt := range tests {
		if got := len(ok[[]jsontext.Value](t, tools, tt.tool, tt.input)); got != tt.want {
			t.Errorf("%s(%s) found %d, want %d", tt.tool, tt.input, got, tt.want)
		}
	}
}

func TestTools_APP05_ABroadSearchReturns10To15kTokensWithinTheResultLimit(t *testing.T) {
	t.Parallel()
	tools := tools(t, newDatabase(t))

	result, err := call(t, tools, "search_books", fmt.Sprintf(`{"limit":%d}`, bookshop.MaxSearchResults))
	if err != nil {
		t.Fatalf("search_books() error = %v", err)
	}

	// About four characters a token for this JSON, under the core's result limit, so nothing is cut.
	var books []book
	if err := json.Unmarshal([]byte(result), &books); err != nil || len(books) != bookshop.MaxSearchResults {
		t.Errorf("broad search found %d books (error %v), want %d", len(books), err, bookshop.MaxSearchResults)
	}
	if len(result) < 40_000 || len(result) > 60_000 {
		t.Errorf("broad search result has %d bytes, want 40,000 to 60,000", len(result))
	}
}

func TestTools_APP05_ReadsFindCustomersTheirOrdersBooksAndOrders(t *testing.T) {
	t.Parallel()
	tools := tools(t, newDatabase(t))

	customers := ok[[]customer](t, tools, "find_customer", `{"nameOrEmail":"alice"}`)
	orders := ok[[]struct {
		ID     int    `json:"id"`
		Status string `json:"status"`
	}](t, tools, "list_customer_orders", `{"customerId":1}`)
	if len(orders) == 0 {
		t.Fatal("Alice has no orders")
	}
	got := ok[order](t, tools, "get_order", fmt.Sprintf(`{"orderId":%d}`, orders[0].ID))
	b := ok[book](t, tools, "get_book", `{"bookId":144}`)

	want := []customer{{ID: alice, Name: "Alice Martin", Email: "alice.martin@example.com"}}
	if diff := cmp.Diff(want, customers); diff != "" {
		t.Errorf("find_customer mismatch (-want +got):\n%s", diff)
	}
	if got.Order.Customer != "Alice Martin" {
		t.Errorf("order's customer = %q, want Alice Martin", got.Order.Customer)
	}
	var pence int
	for _, l := range got.Lines {
		pence += l.Quantity * int(l.UnitPrice*100+0.5)
	}
	if want := int(got.Order.Total*100 + 0.5); pence != want {
		t.Errorf("order's lines add up to %d pence, its total is %d", pence, want)
	}
	if b.Title != "The Winter Archive" || b.Genre != "Fantasy" || b.Price != 6.28 {
		t.Errorf("get_book(144) = %+v, want The Winter Archive, Fantasy, 6.28", b)
	}
}

func TestTools_APP07_UnknownIDsAreErrors(t *testing.T) {
	t.Parallel()
	tools := tools(t, newDatabase(t))

	tests := []struct{ tool, input, want string }{
		{"get_book", `{"bookId":9999}`, "there is no book with id 9999"},
		{"list_customer_orders", `{"customerId":9999}`, "there is no customer with id 9999"},
		{"get_order", `{"orderId":9999}`, "there is no order with id 9999"},
		{"cancel_order", `{"orderId":9999}`, "there is no order with id 9999"},
		{"restock_book", `{"bookId":9999,"quantity":1}`, "there is no book with id 9999"},
	}
	for _, tt := range tests {
		if got := failure(t, tools, tt.tool, tt.input); got != tt.want {
			t.Errorf("%s(%s) error = %q, want %q", tt.tool, tt.input, got, tt.want)
		}
	}
	if got := ok[[]customer](t, tools, "find_customer", `{"nameOrEmail":"nobody at all"}`); len(got) != 0 {
		t.Errorf("find_customer(nobody at all) = %+v, want none", got)
	}
}

func TestTools_APP06_AddCustomerAddsOneAndRefusesASecondWithTheSameEmail(t *testing.T) {
	t.Parallel()
	tools := tools(t, newDatabase(t))

	added := ok[customer](t, tools, "add_customer", `{"name":" Zoe Park ","email":"zoe.park@example.com"}`)
	found := ok[[]customer](t, tools, "find_customer", `{"nameOrEmail":"zoe.park"}`)
	again := failure(t, tools, "add_customer", `{"name":"Zoe P","email":"zoe.park@example.com"}`)

	if diff := cmp.Diff([]customer{added}, found); diff != "" || added.ID <= 40 || added.Name != "Zoe Park" {
		t.Errorf("added %+v, found mismatch (-added +found):\n%s", added, diff)
	}
	if want := "a customer with the email zoe.park@example.com already exists"; again != want {
		t.Errorf("second add_customer error = %q, want %q", again, want)
	}
}

func TestTools_APP06_PlaceOrderTakesTheCopiesFromStockAndChargesTheCurrentPrices(t *testing.T) {
	t.Parallel()
	d := newDatabase(t)
	tools := tools(t, d)
	stock160, stock161 := d.stock(t, 160), d.stock(t, 161)
	price160 := ok[book](t, tools, "get_book", `{"bookId":160}`).Price
	price161 := ok[book](t, tools, "get_book", `{"bookId":161}`).Price

	placed := ok[struct {
		OrderID int     `json:"orderId"`
		Total   float64 `json:"total"`
		Lines   []line  `json:"lines"`
	}](t, tools, "place_order", `{"customerId":1,"lines":[{"bookId":160,"quantity":2},{"bookId":161,"quantity":1}]}`)
	stored := ok[order](t, tools, "get_order", fmt.Sprintf(`{"orderId":%d}`, placed.OrderID))

	want := []line{{BookID: 160, Quantity: 2, UnitPrice: price160}, {BookID: 161, Quantity: 1, UnitPrice: price161}}
	if diff := cmp.Diff(want, placed.Lines); diff != "" {
		t.Errorf("placed lines mismatch (-want +got):\n%s", diff)
	}
	if total := pence(2*price160 + price161); pence(placed.Total) != total || pence(stored.Order.Total) != total {
		t.Errorf("totals placed %v, stored %v; want %d pence", placed.Total, stored.Order.Total, total)
	}
	if stored.Order.Status != "placed" {
		t.Errorf("stored status = %q, want placed", stored.Order.Status)
	}
	if got160, got161 := d.stock(t, 160), d.stock(t, 161); got160 != stock160-2 || got161 != stock161-1 {
		t.Errorf("stock = %d, %d; want %d, %d", got160, got161, stock160-2, stock161-1)
	}
}

func pence(pounds float64) int {
	return int(pounds*100 + 0.5)
}

func TestTools_APP07_BusinessRuleFailuresAreErrorsAndChangeNothing(t *testing.T) {
	t.Parallel()
	d := newDatabase(t)
	tools := tools(t, d)
	stock := d.stock(t, 170)
	orders := scalar[int64](t, d, "select count(*) from orders")

	tests := []struct{ tool, input, want string }{
		{
			"place_order", fmt.Sprintf(`{"customerId":1,"lines":[{"bookId":171,"quantity":1},{"bookId":170,"quantity":%d}]}`, stock+1),
			fmt.Sprintf(`not enough stock for "The Crimson Clockmaker" (id 170): %d requested, %d in stock; nothing was ordered`,
				stock+1, stock),
		},
		{"place_order", `{"customerId":9999,"lines":[{"bookId":170,"quantity":1}]}`, "there is no customer with id 9999"},
		{"place_order", `{"customerId":1,"lines":[{"bookId":9999,"quantity":1}]}`, "there is no book with id 9999; nothing was ordered"},
		{"place_order", `{"customerId":1,"lines":[{"bookId":170,"quantity":1},{"bookId":170,"quantity":1}]}`, noLines},
		{"place_order", `{"customerId":1,"lines":[{"bookId":170,"quantity":0}]}`, noLines},
		{"place_order", `{"customerId":1,"lines":[]}`, noLines},
		{"restock_book", `{"bookId":170,"quantity":0}`, "restock at least one copy"},
		{"add_customer", `{"name":" ","email":"x@example.com"}`, "a customer needs a name and an email"},
	}
	for _, tt := range tests {
		if got := failure(t, tools, tt.tool, tt.input); got != tt.want {
			t.Errorf("%s(%s) error = %q, want %q", tt.tool, tt.input, got, tt.want)
		}
	}
	if got := d.stock(t, 170); got != stock {
		t.Errorf("stock of 170 = %d, want %d", got, stock)
	}
	if got := scalar[int64](t, d, "select count(*) from orders"); got != orders {
		t.Errorf("orders = %d, want %d", got, orders)
	}
}

const noLines = "an order needs at least one line, each book once, each for at least one copy"

func TestTools_APP06_ConcurrentOrdersNeverTakeMoreCopiesThanAreInStock(t *testing.T) {
	t.Parallel()
	d := newDatabase(t)
	tools := tools(t, d)
	stock := d.stock(t, 180)

	errs := make([]error, stock+3)
	var wg sync.WaitGroup
	for i := range errs {
		wg.Go(func() {
			_, errs[i] = call(t, tools, "place_order", `{"customerId":1,"lines":[{"bookId":180,"quantity":1}]}`)
		})
	}
	wg.Wait()

	placed := 0
	for _, err := range errs {
		switch {
		case err == nil:
			placed++
		case !strings.HasPrefix(err.Error(), "not enough stock"):
			t.Errorf("place_order error = %v, want not enough stock", err)
		}
	}
	if placed != stock || d.stock(t, 180) != 0 {
		t.Errorf("placed %d orders, %d left in stock; want %d placed, none left", placed, d.stock(t, 180), stock)
	}
}

func TestTools_APP06_CancelOrderReturnsTheCopiesOnce(t *testing.T) {
	t.Parallel()
	d := newDatabase(t)
	tools := tools(t, d)
	stock := d.stock(t, 190)
	placed := ok[struct {
		OrderID int `json:"orderId"`
	}](t, tools, "place_order", `{"customerId":1,"lines":[{"bookId":190,"quantity":2}]}`)
	input := fmt.Sprintf(`{"orderId":%d}`, placed.OrderID)

	cancelled := ok[struct {
		Status   string `json:"status"`
		Returned []line `json:"returnedToStock"`
	}](t, tools, "cancel_order", input)
	afterCancel := d.stock(t, 190)
	again := failure(t, tools, "cancel_order", input)

	if cancelled.Status != "cancelled" || len(cancelled.Returned) != 1 || cancelled.Returned[0].Quantity != 2 {
		t.Errorf("cancel_order = %+v, want cancelled with 2 copies of 190 returned", cancelled)
	}
	if want := fmt.Sprintf("order %d is already cancelled", placed.OrderID); again != want {
		t.Errorf("second cancel_order error = %q, want %q", again, want)
	}
	if got := d.stock(t, 190); afterCancel != stock || got != stock {
		t.Errorf("stock after cancelling = %d, then %d; want %d", afterCancel, got, stock)
	}
}

func TestTools_APP06_RestockAddsCopies(t *testing.T) {
	t.Parallel()
	d := newDatabase(t)
	tools := tools(t, d)
	stock := d.stock(t, 200)

	restocked := ok[struct {
		Stock int `json:"stock"`
	}](t, tools, "restock_book", `{"bookId":200,"quantity":5}`)

	if restocked.Stock != stock+5 || d.stock(t, 200) != stock+5 {
		t.Errorf("restocked to %d, stored %d; want %d", restocked.Stock, d.stock(t, 200), stock+5)
	}
}

func TestTools_APP18_WithTheDatabaseDownToolsFailAndOnceItIsBackTheyWorkAgain(t *testing.T) {
	t.Parallel()
	d := newDatabase(t)
	tools := tools(t, d)

	// Several pooled connections, all of which the stop ends.
	var wg sync.WaitGroup
	for range 5 {
		wg.Go(func() { _, _ = call(t, tools, "search_books", `{}`) })
	}
	wg.Wait()
	d.takeDown(t)
	_, readErr := call(t, tools, "get_book", `{"bookId":144}`)
	_, writeErr := call(t, tools, "place_order", `{"customerId":1,"lines":[{"bookId":144,"quantity":1}]}`)
	d.bringBack(t)

	if readErr == nil || writeErr == nil {
		t.Errorf("with the database down: get_book error = %v, place_order error = %v; want both", readErr, writeErr)
	}
	for i := range 5 {
		if _, err := call(t, tools, "get_book", `{"bookId":144}`); err != nil {
			t.Errorf("get_book call %d once the database is back: %v", i+1, err)
		}
	}
	if _, err := call(t, tools, "restock_book", `{"bookId":144,"quantity":1}`); err != nil {
		t.Errorf("restock_book once the database is back: %v", err)
	}
}
