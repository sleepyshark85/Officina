package bookshop

import (
	"context"
	"errors"
	"fmt"
	"strings"
	"time"

	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgxpool"

	"github.com/sleepyshark85/officina/go/officina"
)

// MaxSearchResults is the most books one search returns: enough for a broad search of about 15k tokens.
const MaxSearchResults = 400

// Tools returns the bookshop's nine tools over db. Every query is a fixed, parameterized constant: the model gives
// values, never SQL. A business rule failure, such as too little stock, and a database that cannot be reached are
// both errors, which reach the model as error results.
func Tools(db *pgxpool.Pool) ([]officina.Tool, error) {
	s := shop{db: db}
	var errs []error
	tool := func(t officina.Tool, err error) officina.Tool {
		errs = append(errs, err)
		return t
	}
	approved := func(t officina.Tool, err error) officina.Tool {
		t.NeedsApproval = true
		return tool(t, err)
	}
	tools := []officina.Tool{
		tool(officina.NewTool("search_books", fmt.Sprintf("Searches the catalogue. Every filter is optional; text "+
			"filters match part of the title or author name, ignoring case. Returns books cheapest first, at most %d.",
			MaxSearchResults), officina.Read, s.searchBooks)),
		tool(officina.NewTool("get_book", "Gets one book with its author, genre, price, year and copies in stock.",
			officina.Read, s.getBook)),
		tool(officina.NewTool("find_customer", "Finds customers whose name or email contains the given text, "+
			"ignoring case.", officina.Read, s.findCustomer)),
		tool(officina.NewTool("list_customer_orders", "Lists a customer's orders, newest first, with status and total.",
			officina.Read, s.listCustomerOrders)),
		tool(officina.NewTool("get_order", "Gets one order with its customer, status, lines and total.", officina.Read,
			s.getOrder)),
		approved(officina.NewTool("add_customer", "Adds a new customer. Emails are unique.", officina.Write,
			s.addCustomer)),
		approved(officina.NewTool("place_order", "Places an order for a customer at the books' current prices, "+
			"taking the copies from stock. Fails, changing nothing, if a book is unknown or has too few copies in stock.",
			officina.Write, s.placeOrder)),
		approved(officina.NewTool("cancel_order", "Cancels a placed order and returns its copies to stock.",
			officina.Write, s.cancelOrder)),
		approved(officina.NewTool("restock_book", "Adds copies of a book to its stock.", officina.Write, s.restockBook)),
	}
	if err := errors.Join(errs...); err != nil {
		return nil, fmt.Errorf("bookshop tools: %w", err)
	}
	return tools, nil
}

// shop runs the tools' queries on the database.
type shop struct {
	db *pgxpool.Pool
}

// pounds is an amount in pence, written in JSON as pounds to the penny, such as 13.20. Queries read amounts in
// pence, so totals are exact.
type pounds int64

// MarshalJSON writes the amount as a JSON number with two decimals.
func (p pounds) MarshalJSON() ([]byte, error) {
	return fmt.Appendf(nil, "%d.%02d", p/100, p%100), nil
}

// What the tools return, as the model reads it.
type (
	book struct {
		ID     int    `json:"id"`
		Title  string `json:"title"`
		Author string `json:"author"`
		Genre  string `json:"genre"`
		Price  pounds `json:"price"`
		Stock  int    `json:"stock"`
	}
	bookDetails struct {
		ID     int    `json:"id"`
		Title  string `json:"title"`
		Author string `json:"author"`
		Genre  string `json:"genre"`
		Price  pounds `json:"price"`
		Year   int    `json:"year"`
		Stock  int    `json:"stock"`
	}
	customer struct {
		ID    int    `json:"id"`
		Name  string `json:"name"`
		Email string `json:"email"`
	}
	orderSummary struct {
		ID       int       `json:"id"`
		Status   string    `json:"status"`
		PlacedAt time.Time `json:"placedAt"`
		Total    pounds    `json:"total"`
		Copies   int64     `json:"copies"`
	}
	orderHeader struct {
		ID         int       `json:"id"`
		CustomerID int       `json:"customerId"`
		Customer   string    `json:"customer"`
		Status     string    `json:"status"`
		PlacedAt   time.Time `json:"placedAt"`
		Total      pounds    `json:"total"`
	}
	order struct {
		Order orderHeader `json:"order"`
		Lines []line      `json:"lines"`
	}
	line struct {
		BookID    int    `json:"bookId"`
		Title     string `json:"title"`
		Quantity  int    `json:"quantity"`
		UnitPrice pounds `json:"unitPrice"`
	}
	placedOrder struct {
		OrderID    int    `json:"orderId"`
		CustomerID int    `json:"customerId"`
		Customer   string `json:"customer"`
		Lines      []line `json:"lines"`
		Total      pounds `json:"total"`
	}
	cancelledOrder struct {
		OrderID         int    `json:"orderId"`
		Status          string `json:"status"`
		ReturnedToStock []line `json:"returnedToStock"`
	}
	restocked struct {
		BookID int    `json:"bookId"`
		Title  string `json:"title"`
		Stock  int    `json:"stock"`
	}
)

// The searches run unprepared (pgx.QueryExecModeExec), so each is planned with its values: a cached generic plan
// cannot use the trigram indexes for a pattern it does not know.
const searchBooksSQL = `
	select b.id, b.title, a.name, g.name, (b.price * 100)::bigint, s.quantity
	from books b
	join authors a on a.id = b.author_id
	join genres g on g.id = b.genre_id
	join stock s on s.book_id = b.id
	where ($1::text is null or lower(b.title) like lower($1))
	  and ($2::text is null or lower(a.name) like lower($2))
	  and ($3::text is null or lower(g.name) = lower($3))
	  and ($4::numeric is null or b.price <= $4::numeric)
	  and (not $5::boolean or s.quantity > 0)
	order by b.price, b.title
	limit $6`

type searchBooksInput struct {
	Title    string   `json:"title,omitzero" jsonschema:"Part of the title."`
	Author   string   `json:"author,omitzero" jsonschema:"Part of the author's name."`
	Genre    string   `json:"genre,omitzero" jsonschema:"The genre: Fantasy, Science Fiction, Mystery, Romance, History, Biography, Poetry, Horror, Children, Cookery, Travel or Philosophy."`
	MaxPrice *float64 `json:"maxPrice,omitzero" jsonschema:"The highest price."`
	InStock  bool     `json:"inStock,omitzero" jsonschema:"Only books with copies in stock."`
	Limit    int      `json:"limit,omitzero" jsonschema:"The most books to return, 20 if not given."`
}

func (s shop) searchBooks(ctx context.Context, in searchBooksInput) ([]book, error) {
	limit := in.Limit
	if limit == 0 {
		limit = 20
	}
	limit = min(max(limit, 1), MaxSearchResults)
	rows, err := s.db.Query(ctx, searchBooksSQL, pgx.QueryExecModeExec,
		containing(in.Title), containing(in.Author), blankAsNull(in.Genre), in.MaxPrice, in.InStock, limit)
	if err != nil {
		return nil, fmt.Errorf("search the books: %w", err)
	}
	books, err := pgx.CollectRows(rows, pgx.RowToStructByPos[book])
	if err != nil {
		return nil, fmt.Errorf("search the books: %w", err)
	}
	return books, nil
}

const getBookSQL = `
	select b.id, b.title, a.name, g.name, (b.price * 100)::bigint, b.published_year, s.quantity
	from books b
	join authors a on a.id = b.author_id
	join genres g on g.id = b.genre_id
	join stock s on s.book_id = b.id
	where b.id = $1`

type bookInput struct {
	BookID int `json:"bookId" jsonschema:"The book's id."`
}

func (s shop) getBook(ctx context.Context, in bookInput) (bookDetails, error) {
	rows, err := s.db.Query(ctx, getBookSQL, in.BookID)
	if err != nil {
		return bookDetails{}, fmt.Errorf("get the book: %w", err)
	}
	b, err := pgx.CollectExactlyOneRow(rows, pgx.RowToStructByPos[bookDetails])
	switch {
	case errors.Is(err, pgx.ErrNoRows):
		return b, fmt.Errorf("there is no book with id %d", in.BookID)
	case err != nil:
		return b, fmt.Errorf("get the book: %w", err)
	}
	return b, nil
}

const findCustomerSQL = `
	select id, name, email
	from customers
	where lower(name) like lower($1) or lower(email) like lower($1)
	order by name, id
	limit 20`

type findCustomerInput struct {
	NameOrEmail string `json:"nameOrEmail" jsonschema:"Part of the customer's name or email."`
}

func (s shop) findCustomer(ctx context.Context, in findCustomerInput) ([]customer, error) {
	pattern := containing(in.NameOrEmail)
	if pattern == nil {
		// Blank text names no one, rather than every customer.
		return []customer{}, nil
	}
	// Unprepared, as the book search is.
	rows, err := s.db.Query(ctx, findCustomerSQL, pgx.QueryExecModeExec, *pattern)
	if err != nil {
		return nil, fmt.Errorf("find the customer: %w", err)
	}
	customers, err := pgx.CollectRows(rows, pgx.RowToStructByPos[customer])
	if err != nil {
		return nil, fmt.Errorf("find the customer: %w", err)
	}
	return customers, nil
}

const customerNameSQL = `select name from customers where id = $1`

const listCustomerOrdersSQL = `
	select o.id, o.status, o.placed_at, (o.total * 100)::bigint,
	       (select sum(quantity) from order_lines l where l.order_id = o.id)
	from orders o
	where o.customer_id = $1
	order by o.placed_at desc, o.id desc`

type customerInput struct {
	CustomerID int `json:"customerId" jsonschema:"The customer's id."`
}

func (s shop) listCustomerOrders(ctx context.Context, in customerInput) ([]orderSummary, error) {
	if _, err := customerName(ctx, s.db, in.CustomerID); err != nil {
		return nil, err
	}
	rows, err := s.db.Query(ctx, listCustomerOrdersSQL, in.CustomerID)
	if err != nil {
		return nil, fmt.Errorf("list the customer's orders: %w", err)
	}
	orders, err := pgx.CollectRows(rows, pgx.RowToStructByPos[orderSummary])
	if err != nil {
		return nil, fmt.Errorf("list the customer's orders: %w", err)
	}
	return orders, nil
}

const getOrderSQL = `
	select o.id, o.customer_id, c.name, o.status, o.placed_at, (o.total * 100)::bigint
	from orders o
	join customers c on c.id = o.customer_id
	where o.id = $1`

const orderLinesSQL = `
	select l.book_id, b.title, l.quantity, (l.unit_price * 100)::bigint
	from order_lines l
	join books b on b.id = l.book_id
	where l.order_id = $1
	order by l.book_id`

type orderInput struct {
	OrderID int `json:"orderId" jsonschema:"The order's id."`
}

func (s shop) getOrder(ctx context.Context, in orderInput) (order, error) {
	var o order
	rows, err := s.db.Query(ctx, getOrderSQL, in.OrderID)
	if err != nil {
		return o, fmt.Errorf("get the order: %w", err)
	}
	o.Order, err = pgx.CollectExactlyOneRow(rows, pgx.RowToStructByPos[orderHeader])
	switch {
	case errors.Is(err, pgx.ErrNoRows):
		return o, fmt.Errorf("there is no order with id %d", in.OrderID)
	case err != nil:
		return o, fmt.Errorf("get the order: %w", err)
	}
	o.Lines, err = orderLines(ctx, s.db, in.OrderID)
	return o, err
}

const addCustomerSQL = `
	insert into customers (name, email) values ($1, $2)
	on conflict (email) do nothing
	returning id`

type addCustomerInput struct {
	Name  string `json:"name" jsonschema:"The customer's full name."`
	Email string `json:"email" jsonschema:"The customer's email."`
}

func (s shop) addCustomer(ctx context.Context, in addCustomerInput) (customer, error) {
	c := customer{Name: strings.TrimSpace(in.Name), Email: strings.TrimSpace(in.Email)}
	if c.Name == "" || c.Email == "" {
		return c, errors.New("a customer needs a name and an email")
	}
	rows, err := s.db.Query(ctx, addCustomerSQL, c.Name, c.Email)
	if err != nil {
		return c, fmt.Errorf("add the customer: %w", err)
	}
	c.ID, err = pgx.CollectExactlyOneRow(rows, pgx.RowTo[int])
	switch {
	case errors.Is(err, pgx.ErrNoRows):
		return c, fmt.Errorf("a customer with the email %s already exists", c.Email)
	case err != nil:
		return c, fmt.Errorf("add the customer: %w", err)
	}
	return c, nil
}

// Locks the books' stock rows in id order, so concurrent orders cannot deadlock, until the order commits.
const lockStockSQL = `
	select b.id, b.title, (b.price * 100)::bigint, s.quantity
	from books b
	join stock s on s.book_id = b.id
	where b.id = any($1)
	order by b.id
	for update of s`

const (
	insertOrderSQL     = `insert into orders (customer_id, status, total) values ($1, 'placed', $2 / 100.0) returning id`
	takeStockSQL       = `update stock set quantity = quantity - $2 where book_id = $1`
	insertOrderLineSQL = `
		insert into order_lines (order_id, book_id, quantity, unit_price)
		values ($1, $2, $3, $4 / 100.0)`
)

type placeOrderInput struct {
	CustomerID int         `json:"customerId" jsonschema:"The customer's id."`
	Lines      []orderLine `json:"lines" jsonschema:"The books and copies to order, each book once."`
}

type orderLine struct {
	BookID   int `json:"bookId" jsonschema:"The book's id."`
	Quantity int `json:"quantity" jsonschema:"How many copies, at least 1."`
}

func (s shop) placeOrder(ctx context.Context, in placeOrderInput) (placedOrder, error) {
	placed := placedOrder{CustomerID: in.CustomerID}
	ids := make([]int, len(in.Lines))
	seen := map[int]bool{}
	for i, l := range in.Lines {
		if l.Quantity < 1 || seen[l.BookID] {
			ids = nil
			break
		}
		seen[l.BookID], ids[i] = true, l.BookID
	}
	if len(ids) == 0 {
		return placed, errors.New("an order needs at least one line, each book once, each for at least one copy")
	}
	err := s.inTransaction(ctx, func(tx pgx.Tx) error {
		var err error
		if placed.Customer, err = customerName(ctx, tx, in.CustomerID); err != nil {
			return err
		}
		type stocked struct {
			ID    int
			Title string
			Price pounds
			Stock int
		}
		rows, err := tx.Query(ctx, lockStockSQL, ids)
		if err != nil {
			return fmt.Errorf("lock the stock: %w", err)
		}
		locked, err := pgx.CollectRows(rows, pgx.RowToStructByPos[stocked])
		if err != nil {
			return fmt.Errorf("lock the stock: %w", err)
		}
		books := map[int]stocked{}
		for _, b := range locked {
			books[b.ID] = b
		}
		for _, l := range in.Lines {
			b, found := books[l.BookID]
			switch {
			case !found:
				return fmt.Errorf("there is no book with id %d; nothing was ordered", l.BookID)
			case b.Stock < l.Quantity:
				return fmt.Errorf("not enough stock for %q (id %d): %d requested, %d in stock; nothing was ordered",
					b.Title, l.BookID, l.Quantity, b.Stock)
			}
			placed.Lines = append(placed.Lines, line{BookID: l.BookID, Title: b.Title, Quantity: l.Quantity,
				UnitPrice: b.Price})
			placed.Total += b.Price * pounds(l.Quantity)
		}
		if err := tx.QueryRow(ctx, insertOrderSQL, in.CustomerID, int64(placed.Total)).Scan(&placed.OrderID); err != nil {
			return fmt.Errorf("insert the order: %w", err)
		}
		for _, l := range placed.Lines {
			if _, err := tx.Exec(ctx, takeStockSQL, l.BookID, l.Quantity); err != nil {
				return fmt.Errorf("take the copies from stock: %w", err)
			}
			if _, err := tx.Exec(ctx, insertOrderLineSQL, placed.OrderID, l.BookID, l.Quantity, int64(l.UnitPrice)); err != nil {
				return fmt.Errorf("insert the order's line: %w", err)
			}
		}
		return nil
	})
	return placed, err
}

const (
	lockOrderSQL = `select status from orders where id = $1 for update`
	// Locks the order's stock rows in book id order, as placing an order does, so the two cannot deadlock.
	lockOrderStockSQL = `
		select s.book_id
		from stock s
		join order_lines l on l.book_id = s.book_id
		where l.order_id = $1
		order by s.book_id
		for update of s`
	returnStockSQL = `
		update stock s set quantity = s.quantity + l.quantity
		from order_lines l
		where l.order_id = $1 and s.book_id = l.book_id`
	cancelOrderSQL = `update orders set status = 'cancelled' where id = $1`
)

func (s shop) cancelOrder(ctx context.Context, in orderInput) (cancelledOrder, error) {
	cancelled := cancelledOrder{OrderID: in.OrderID, Status: "cancelled"}
	err := s.inTransaction(ctx, func(tx pgx.Tx) error {
		rows, err := tx.Query(ctx, lockOrderSQL, in.OrderID)
		if err != nil {
			return fmt.Errorf("lock the order: %w", err)
		}
		status, err := pgx.CollectExactlyOneRow(rows, pgx.RowTo[string])
		switch {
		case errors.Is(err, pgx.ErrNoRows):
			return fmt.Errorf("there is no order with id %d", in.OrderID)
		case err != nil:
			return fmt.Errorf("lock the order: %w", err)
		case status == "cancelled":
			return fmt.Errorf("order %d is already cancelled", in.OrderID)
		}
		for _, sql := range []string{lockOrderStockSQL, returnStockSQL, cancelOrderSQL} {
			if _, err := tx.Exec(ctx, sql, in.OrderID); err != nil {
				return fmt.Errorf("cancel the order: %w", err)
			}
		}
		cancelled.ReturnedToStock, err = orderLines(ctx, tx, in.OrderID)
		return err
	})
	return cancelled, err
}

const restockBookSQL = `
	update stock s set quantity = s.quantity + $2
	from books b
	where s.book_id = $1 and b.id = s.book_id
	returning b.title, s.quantity`

type restockBookInput struct {
	BookID   int `json:"bookId" jsonschema:"The book's id."`
	Quantity int `json:"quantity" jsonschema:"How many copies to add, at least 1."`
}

func (s shop) restockBook(ctx context.Context, in restockBookInput) (restocked, error) {
	r := restocked{BookID: in.BookID}
	if in.Quantity < 1 {
		return r, errors.New("restock at least one copy")
	}
	err := s.db.QueryRow(ctx, restockBookSQL, in.BookID, in.Quantity).Scan(&r.Title, &r.Stock)
	switch {
	case errors.Is(err, pgx.ErrNoRows):
		return r, fmt.Errorf("there is no book with id %d", in.BookID)
	case err != nil:
		return r, fmt.Errorf("restock the book: %w", err)
	}
	return r, nil
}

// inTransaction runs fn in a transaction, which commits if fn returns nil and rolls back otherwise. Its error is
// fn's, as it is, so a business rule failure reaches the model unchanged.
func (s shop) inTransaction(ctx context.Context, fn func(tx pgx.Tx) error) error {
	tx, err := s.db.Begin(ctx)
	if err != nil {
		return fmt.Errorf("begin a transaction: %w", err)
	}
	// After a commit, a rollback does nothing; after a failure, the failure is what counts, and a connection the
	// rollback could not reach is closed.
	defer func() { _ = tx.Rollback(ctx) }()
	if err := fn(tx); err != nil {
		return err
	}
	if err := tx.Commit(ctx); err != nil {
		return fmt.Errorf("commit the transaction: %w", err)
	}
	return nil
}

// querier is what the queries that the pool and a transaction share need.
type querier interface {
	Query(ctx context.Context, sql string, args ...any) (pgx.Rows, error)
}

// customerName returns the name of the customer with id, or an error that says there is none.
func customerName(ctx context.Context, q querier, id int) (string, error) {
	rows, err := q.Query(ctx, customerNameSQL, id)
	if err != nil {
		return "", fmt.Errorf("find the customer: %w", err)
	}
	name, err := pgx.CollectExactlyOneRow(rows, pgx.RowTo[string])
	switch {
	case errors.Is(err, pgx.ErrNoRows):
		return "", fmt.Errorf("there is no customer with id %d", id)
	case err != nil:
		return "", fmt.Errorf("find the customer: %w", err)
	}
	return name, nil
}

// orderLines returns the lines of the order with id.
func orderLines(ctx context.Context, q querier, id int) ([]line, error) {
	rows, err := q.Query(ctx, orderLinesSQL, id)
	if err != nil {
		return nil, fmt.Errorf("read the order's lines: %w", err)
	}
	lines, err := pgx.CollectRows(rows, pgx.RowToStructByPos[line])
	if err != nil {
		return nil, fmt.Errorf("read the order's lines: %w", err)
	}
	return lines, nil
}

// containing returns a LIKE pattern that matches text containing part, its wildcards and backslashes taken
// literally; nil, for no filter, when part is blank.
func containing(part string) *string {
	if strings.TrimSpace(part) == "" {
		return nil
	}
	pattern := "%" + strings.NewReplacer(`\`, `\\`, "%", `\%`, "_", `\_`).Replace(part) + "%"
	return &pattern
}

// blankAsNull returns nil, for no filter, when text is blank, and text otherwise.
func blankAsNull(text string) *string {
	if strings.TrimSpace(text) == "" {
		return nil
	}
	return &text
}
