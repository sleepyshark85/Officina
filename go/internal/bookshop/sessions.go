package bookshop

import (
	"bytes"
	"context"
	"encoding/json/v2"
	"errors"
	"fmt"
	"time"

	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgxpool"

	"github.com/sleepyshark85/officina/go/officina"
)

// Sessions is the session store: the sessions table, the same table and columns as the .NET implementation's, one
// row per conversation keyed by its id. The conversation is kept as the core's JSON in a text column, so it reads
// back byte for byte and resumes with its prefix and cache intact, in either implementation.
type Sessions struct {
	db *pgxpool.Pool
}

// NewSessions returns the session store of db.
func NewSessions(db *pgxpool.Pool) *Sessions {
	return &Sessions{db: db}
}

// StoredSession is a stored session: its conversation, who started it, what its replies used, and the text it is
// stored as.
type StoredSession struct {
	Conversation *officina.Conversation
	StaffMember  string
	Usage        officina.Usage
	// Cost is in US dollars.
	Cost  float64
	Saved string
}

var (
	// ErrNoSession is returned for a session that is not stored.
	ErrNoSession = errors.New("no such session")
	// ErrSessionChanged is returned for a save that would overwrite what another console saved.
	ErrSessionChanged = errors.New("changed elsewhere since it was last saved here, so it was not overwritten")
)

const (
	createSession = `insert into sessions (id, staff_member, conversation, input_tokens, output_tokens, cache_read_tokens,
	cache_write_tokens, cost, updated)
values ($1, $2, $3, $4, $5, $6, $7, $8, now())
on conflict (id) do nothing`

	saveSession = `update sessions
set conversation = $3, input_tokens = $4, output_tokens = $5, cache_read_tokens = $6, cache_write_tokens = $7,
	cost = $8, updated = case when conversation is distinct from $3 then now() else updated end
where id = $1 and staff_member = $2 and conversation = $9`

	storedSession = `select conversation from sessions where id = $1`

	loadSession = `select conversation, staff_member, input_tokens, output_tokens, cache_read_tokens,
	cache_write_tokens, cost::float8
from sessions
where id = $1`

	listSessions = `select id, staff_member, coalesce(title, ''), coalesce(summary, ''), coalesce(changes, '{}'),
	cost::float8, updated, summarized is null or summarized < updated
from sessions
order by updated desc
limit $1`

	saveSummary = `update sessions
set title = $2, summary = $3, changes = $4, summarized = now(), input_tokens = input_tokens + $5,
	output_tokens = output_tokens + $6, cache_read_tokens = cache_read_tokens + $7,
	cache_write_tokens = cache_write_tokens + $8, cost = cost + $9
where id = $1`
)

// Save saves the session as it is now and returns the text it is stored as. previous is the text this console last
// saved or loaded, empty for a new session. A save never loses messages: if the stored conversation is no longer
// previous, the session is saved only if the stored one is an earlier state of this one, as when a save landed but
// its answer was lost; otherwise another console changed it, or the id is another session's, and Save returns
// ErrSessionChanged. usage and cost replace the stored totals. The database stamps the time, moving it only when the
// conversation changed.
func (s *Sessions) Save(ctx context.Context, c *officina.Conversation, staffMember string, usage officina.Usage,
	cost float64, previous string,
) (string, error) {
	data, err := json.Marshal(c)
	if err != nil {
		return "", fmt.Errorf("save session %s: %w", c.ID, err)
	}
	saved := string(data)
	write := func(expected string) (bool, error) {
		args := []any{c.ID, staffMember, saved, usage.Input, usage.Output, usage.CacheRead, usage.CacheWrite, cost}
		sql := createSession
		if expected != "" {
			sql, args = saveSession, append(args, expected)
		}
		tag, err := s.db.Exec(ctx, sql, args...)
		if err != nil {
			return false, fmt.Errorf("write: %w", err)
		}
		return tag.RowsAffected() == 1, nil
	}
	written, err := write(previous)
	if err == nil && !written {
		var stored string
		err = s.db.QueryRow(ctx, storedSession, c.ID).Scan(&stored)
		switch {
		case errors.Is(err, pgx.ErrNoRows):
			err = nil
		case err == nil && earlier(stored, c):
			written, err = write(stored)
		}
	}
	switch {
	case err != nil:
		return "", fmt.Errorf("save session %s: %w", c.ID, err)
	case !written:
		return "", fmt.Errorf("session %s %w", c.ID, ErrSessionChanged)
	}
	return saved, nil
}

// earlier reports whether stored holds the first messages of c, and no others.
func earlier(stored string, c *officina.Conversation) bool {
	var e officina.Conversation
	if json.Unmarshal([]byte(stored), &e) != nil {
		return false
	}
	before, now := e.Messages(), c.Messages()
	if len(before) > len(now) {
		return false
	}
	for i, m := range before {
		x, errX := json.Marshal(m)
		y, errY := json.Marshal(now[i])
		if errX != nil || errY != nil || !bytes.Equal(x, y) {
			return false
		}
	}
	return true
}

// Load returns the session with id: ErrNoSession when there is none, and an error saying it cannot be read when its
// conversation is not one.
func (s *Sessions) Load(ctx context.Context, id string) (StoredSession, error) {
	stored := StoredSession{Conversation: &officina.Conversation{}}
	u := &stored.Usage
	err := s.db.QueryRow(ctx, loadSession, id).Scan(&stored.Saved, &stored.StaffMember, &u.Input, &u.Output,
		&u.CacheRead, &u.CacheWrite, &stored.Cost)
	switch {
	case errors.Is(err, pgx.ErrNoRows):
		return StoredSession{}, fmt.Errorf("load session %s: %w", id, ErrNoSession)
	case err != nil:
		return StoredSession{}, fmt.Errorf("load session %s: %w", id, err)
	}
	if err := json.Unmarshal([]byte(stored.Saved), stored.Conversation); err != nil {
		return StoredSession{}, fmt.Errorf("session %s cannot be read: %w", id, err)
	}
	return stored, nil
}

// listing is a session as /sessions lists it. A session has a title, summary and changes once the summarizer has
// written them; it is stale when it has none, or changed since.
type listing struct {
	id, staffMember, title, summary string
	changes                         []string
	cost                            float64
	updated                         time.Time
	stale                           bool
}

// list returns the count sessions updated last, most recent first.
func (s *Sessions) list(ctx context.Context, count int) ([]listing, error) {
	rows, err := s.db.Query(ctx, listSessions, count)
	if err != nil {
		return nil, fmt.Errorf("list sessions: %w", err)
	}
	defer rows.Close()
	var listed []listing
	for rows.Next() {
		var l listing
		if err := rows.Scan(&l.id, &l.staffMember, &l.title, &l.summary, &l.changes, &l.cost, &l.updated,
			&l.stale); err != nil {
			return nil, fmt.Errorf("list sessions: %w", err)
		}
		listed = append(listed, l)
	}
	if err := rows.Err(); err != nil {
		return nil, fmt.Errorf("list sessions: %w", err)
	}
	return listed, nil
}

// saveSummary stores the summary of session id, as of now, and adds what it cost to the session's totals. A save of
// the session that a console makes after it replaces the totals with its own, so a summary's cost that another
// console added since is lost.
func (s *Sessions) saveSummary(ctx context.Context, id string, sum summary, usage officina.Usage, cost float64) error {
	changes := sum.Changes
	if changes == nil {
		changes = []string{}
	}
	if _, err := s.db.Exec(ctx, saveSummary, id, sum.Title, sum.Summary, changes, usage.Input, usage.Output,
		usage.CacheRead, usage.CacheWrite, cost); err != nil {
		return fmt.Errorf("save the summary of session %s: %w", id, err)
	}
	return nil
}
