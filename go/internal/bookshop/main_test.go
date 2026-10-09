package bookshop_test

import (
	"context"
	"errors"
	"fmt"
	"os"
	"os/signal"
	"path/filepath"
	"runtime"
	"sync"
	"syscall"
	"testing"
	"time"

	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgxpool"
	"github.com/testcontainers/testcontainers-go"
	"github.com/testcontainers/testcontainers-go/wait"
	"go.uber.org/goleak"

	"github.com/sleepyshark85/officina/go/internal/bookshop"
)

// The database tests share one PostgreSQL container per run of this package, made like the compose file's: its
// image, user and password, and the application's schema and seed, which .NET reads too.
const (
	image    = "postgres:17"
	user     = "bookshop"
	password = "shelf-demo-41"
	seeded   = "bookshop"
	scripts  = "../../../apps/BookshopAssistant/database"
)

// server is the PostgreSQL server of this run, or why there is none; TestMain sets it before any test runs.
var server struct { //nolint:gochecknoglobals // TestMain's container, shared by the tests it runs.
	host, port string
	skip       string
	// clones serializes cloning the seeded database, which needs no other session on it.
	clones sync.Mutex
	copies int
}

func TestMain(m *testing.M) {
	// Started by the crash test, the test binary is the application, which that test kills.
	if os.Getenv(crashEnv) != "" {
		os.Exit(crashingApp())
	}
	code := runWithDatabase(m)
	if code == 0 {
		if err := goleak.Find(); err != nil {
			fmt.Fprintln(os.Stderr, "goleak:", err)
			code = 1
		}
	}
	os.Exit(code)
}

// runWithDatabase runs the tests with the database server started first, where the database tests run: on Linux,
// always in CI, where a missing Docker fails them rather than skipping them silently, and elsewhere when Docker is
// found.
func runWithDatabase(m *testing.M) int {
	_, docker := os.Stat("/var/run/docker.sock")
	switch {
	case runtime.GOOS != "linux":
		server.skip = "the database tests need Linux and Docker (TEST-03)"
		return m.Run()
	case os.Getenv("CI") == "" && docker != nil && os.Getenv("DOCKER_HOST") == "":
		server.skip = "the database tests need Docker, which was not found"
		return m.Run()
	}
	// The container is removed here, also when the run is interrupted, so testcontainers' reaper, a container of
	// its own pulled from Docker Hub, is not needed.
	if err := os.Setenv("TESTCONTAINERS_RYUK_DISABLED", "true"); err != nil {
		fmt.Fprintln(os.Stderr, "disable the reaper:", err)
		return 1
	}
	ctx := context.Background()
	files, err := filepath.Glob(filepath.Join(scripts, "*.sql"))
	if err != nil || len(files) == 0 {
		fmt.Fprintln(os.Stderr, "no database scripts in", scripts, err)
		return 1
	}
	var copies []testcontainers.ContainerFile
	for _, f := range files {
		copies = append(copies, testcontainers.ContainerFile{
			HostFilePath: f, ContainerFilePath: "/docker-entrypoint-initdb.d/" + filepath.Base(f), FileMode: 0o644,
		})
	}
	container, err := testcontainers.Run(ctx, image,
		testcontainers.WithEnv(map[string]string{"POSTGRES_USER": user, "POSTGRES_PASSWORD": password, "POSTGRES_DB": seeded}),
		testcontainers.WithExposedPorts("5432/tcp"),
		testcontainers.WithFiles(copies...),
		// PostgreSQL restarts once the scripts have run, so it is ready the second time it says so.
		testcontainers.WithWaitStrategyAndDeadline(2*time.Minute,
			wait.ForLog("database system is ready to accept connections").WithOccurrence(2),
			wait.ForListeningPort("5432/tcp")),
	)
	defer func() {
		if err := testcontainers.TerminateContainer(container); err != nil {
			fmt.Fprintln(os.Stderr, "terminate the database container:", err)
		}
	}()
	if err != nil {
		fmt.Fprintln(os.Stderr, "start the database container:", err)
		return 1
	}
	// An interrupted run removes the container before it exits; a run killed outright leaves it, labelled
	// org.testcontainers, for docker container prune.
	interrupted := make(chan os.Signal, 1)
	signal.Notify(interrupted, os.Interrupt, syscall.SIGTERM)
	done := make(chan struct{})
	var watch sync.WaitGroup
	watch.Go(func() {
		select {
		case <-interrupted:
			if err := testcontainers.TerminateContainer(container); err != nil {
				fmt.Fprintln(os.Stderr, "terminate the database container:", err)
			}
			os.Exit(1)
		case <-done:
		}
	})
	defer func() {
		signal.Stop(interrupted)
		close(done)
		watch.Wait()
	}()
	if server.host, err = container.Host(ctx); err == nil {
		var port interface{ Port() string }
		port, err = container.MappedPort(ctx, "5432/tcp")
		if err == nil {
			server.port = port.Port()
		}
	}
	if err != nil {
		fmt.Fprintln(os.Stderr, "find the database container:", err)
		return 1
	}
	defer stopExports()
	return m.Run()
}

// database is a fresh copy of the seeded bookshop database, of one test.
type database struct {
	name, url string
	pool      *pgxpool.Pool
}

// newDatabase returns a copy of the seeded database for t alone, so tests can run in parallel; it skips t where the
// database tests do not run.
func newDatabase(t *testing.T) *database {
	t.Helper()
	if server.skip != "" {
		t.Skip(server.skip)
	}
	server.clones.Lock()
	server.copies++
	d := &database{name: fmt.Sprintf("test_%d", server.copies)}
	err := admin(t, "create database "+d.name+" template "+seeded)
	server.clones.Unlock()
	if err != nil {
		t.Fatalf("clone the seeded database: %v", err)
	}
	d.url = connectionString(d.name)
	pool, err := bookshop.Connect(t.Context(), d.url)
	if err != nil {
		t.Fatalf("Connect() error = %v", err)
	}
	t.Cleanup(pool.Close)
	d.pool = pool
	return d
}

func connectionString(database string) string {
	return fmt.Sprintf("postgres://%s:%s@%s:%s/%s", user, password, server.host, server.port, database)
}

// admin runs sql on the server's maintenance database.
func admin(t *testing.T, sql string) error {
	t.Helper()
	conn, err := pgx.Connect(t.Context(), connectionString("postgres"))
	if err != nil {
		return err
	}
	_, err = conn.Exec(t.Context(), sql)
	return errors.Join(err, conn.Close(context.WithoutCancel(t.Context())))
}

// takeDown makes the database unreachable, as if its server had stopped: new connections are refused and open ones
// are ended. bringBack undoes it.
func (d *database) takeDown(t *testing.T) {
	t.Helper()
	if err := admin(t, "alter database "+d.name+" allow_connections false"); err != nil {
		t.Fatalf("take the database down: %v", err)
	}
	if err := admin(t, "select pg_terminate_backend(pid) from pg_stat_activity where datname = '"+d.name+"'"); err != nil {
		t.Fatalf("end the database's connections: %v", err)
	}
}

// bringBack makes the database reachable again after takeDown.
func (d *database) bringBack(t *testing.T) {
	t.Helper()
	if err := admin(t, "alter database "+d.name+" allow_connections true"); err != nil {
		t.Fatalf("bring the database back: %v", err)
	}
}

// scalar runs a query of the test's own and returns its one value.
func scalar[T any](t *testing.T, d *database, sql string, args ...any) T {
	t.Helper()
	var v T
	if err := d.pool.QueryRow(t.Context(), sql, args...).Scan(&v); err != nil {
		t.Fatalf("%s: %v", sql, err)
	}
	return v
}

// stock returns the copies of book id in stock.
func (d *database) stock(t *testing.T, id int) int {
	t.Helper()
	return scalar[int](t, d, "select quantity from stock where book_id = $1", id)
}
