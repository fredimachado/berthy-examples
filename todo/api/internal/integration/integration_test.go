//go:build integration

// Package integration runs the API against a real Postgres and a real Redis.
//
//	TEST_POSTGRES_URL=postgres://user:pass@localhost:5432/db?sslmode=disable \
//	TEST_REDIS_ADDR=localhost:6379 \
//	go test -tags integration ./internal/integration
//
// The tests drop and recreate the todos tables of that database: point them at
// a throwaway one. Without both variables they are skipped.
package integration

import (
	"context"
	"encoding/json"
	"io"
	"log/slog"
	"net/http"
	"net/http/httptest"
	"os"
	"strings"
	"testing"
	"time"

	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgxpool"
	"github.com/redis/go-redis/v9"

	"github.com/fredimachado/berthy-examples/todo/api/internal/cache"
	"github.com/fredimachado/berthy-examples/todo/api/internal/migrate"
	"github.com/fredimachado/berthy-examples/todo/api/internal/server"
	"github.com/fredimachado/berthy-examples/todo/api/internal/store"
	"github.com/fredimachado/berthy-examples/todo/api/internal/todo"
)

func target(t *testing.T) (pgURL, redisAddr string) {
	t.Helper()
	pgURL, redisAddr = os.Getenv("TEST_POSTGRES_URL"), os.Getenv("TEST_REDIS_ADDR")
	if pgURL == "" || redisAddr == "" {
		t.Skip("set TEST_POSTGRES_URL and TEST_REDIS_ADDR to run the integration tests")
	}
	return pgURL, redisAddr
}

func connect(t *testing.T, url string) *pgx.Conn {
	t.Helper()
	conn, err := pgx.Connect(context.Background(), url)
	if err != nil {
		t.Fatalf("connect to postgres: %v", err)
	}
	t.Cleanup(func() { _ = conn.Close(context.Background()) })
	return conn
}

func migrator(t *testing.T, conn *pgx.Conn) *migrate.Migrator {
	t.Helper()
	list, err := migrate.Embedded()
	if err != nil {
		t.Fatal(err)
	}
	return migrate.New(conn, list)
}

func wantVersion(t *testing.T, m *migrate.Migrator, want int) {
	t.Helper()
	got, err := m.Version(context.Background())
	if err != nil || got != want {
		t.Fatalf("version = %d, %v; want %d", got, err, want)
	}
}

func TestMigrations(t *testing.T) {
	pgURL, _ := target(t)
	ctx := context.Background()
	m := migrator(t, connect(t, pgURL))

	if _, err := m.DownTo(ctx, 0); err != nil {
		t.Fatalf("reset: %v", err)
	}
	wantVersion(t, m, 0)

	applied, err := m.Up(ctx)
	if err != nil || len(applied) != m.Latest() {
		t.Fatalf("up applied %v, %v; want %d migrations", applied, err, m.Latest())
	}
	wantVersion(t, m, m.Latest())

	if again, err := m.Up(ctx); err != nil || len(again) != 0 {
		t.Fatalf("a second up applied %v, %v; want nothing", again, err)
	}

	reverted, err := m.DownTo(ctx, 1)
	if err != nil || len(reverted) != m.Latest()-1 {
		t.Fatalf("down-to 1 reverted %v, %v", reverted, err)
	}
	wantVersion(t, m, 1)

	if _, err := m.DownTo(ctx, 9); err == nil {
		t.Fatal("a target above the latest version must be refused")
	}
	if _, err := m.Up(ctx); err != nil {
		t.Fatalf("up after down: %v", err)
	}
	wantVersion(t, m, m.Latest())
}

func TestMigratorsDoNotCollide(t *testing.T) {
	pgURL, _ := target(t)
	ctx := context.Background()
	first := migrator(t, connect(t, pgURL))
	if _, err := first.DownTo(ctx, 0); err != nil {
		t.Fatal(err)
	}
	second, third := migrator(t, connect(t, pgURL)), migrator(t, connect(t, pgURL))
	errs := make(chan error, 2)
	for _, m := range []*migrate.Migrator{second, third} {
		go func() { _, err := m.Up(ctx); errs <- err }()
	}
	for range 2 {
		if err := <-errs; err != nil {
			t.Fatalf("concurrent up: %v", err)
		}
	}
	wantVersion(t, first, first.Latest())
}

// stack builds the real service on the real Postgres and Redis, with a Redis
// key of its own so tests do not touch a real cache.
func stack(t *testing.T) (*todo.Service, *store.Postgres, *redis.Client, string) {
	t.Helper()
	pgURL, redisAddr := target(t)
	ctx := context.Background()

	if _, err := migrator(t, connect(t, pgURL)).Up(ctx); err != nil {
		t.Fatalf("migrate: %v", err)
	}
	pool, err := pgxpool.New(ctx, pgURL)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(pool.Close)
	if _, err := pool.Exec(ctx, "TRUNCATE todos RESTART IDENTITY"); err != nil {
		t.Fatal(err)
	}

	rdb := redis.NewClient(&redis.Options{Addr: redisAddr, Password: os.Getenv("TEST_REDIS_PASSWORD")})
	t.Cleanup(func() { _ = rdb.Close() })
	key := "todo:test:" + strings.ReplaceAll(t.Name(), "/", ":")
	t.Cleanup(func() { rdb.Del(context.Background(), key) })
	rdb.Del(ctx, key)

	st := store.New(pool)
	svc := todo.NewService(st, cache.New(rdb, key, 30*time.Second), slog.New(slog.NewTextHandler(io.Discard, nil)))
	return svc, st, rdb, key
}

func TestAPIAgainstRealPostgresAndRedis(t *testing.T) {
	svc, _, rdb, key := stack(t)
	ctx := context.Background()
	srv := httptest.NewServer(server.New(svc, server.BuildInfo{Version: "test", Commit: "none"}, slog.New(slog.NewTextHandler(io.Discard, nil))))
	defer srv.Close()

	do := func(method, path, body string) (*http.Response, []byte) {
		t.Helper()
		req, err := http.NewRequest(method, srv.URL+path, strings.NewReader(body))
		if err != nil {
			t.Fatal(err)
		}
		res, err := http.DefaultClient.Do(req)
		if err != nil {
			t.Fatal(err)
		}
		defer res.Body.Close()
		b, _ := io.ReadAll(res.Body)
		return res, b
	}

	if res, body := do("GET", "/health", ""); res.StatusCode != 200 {
		t.Fatalf("health = %d %s", res.StatusCode, body)
	}

	res, body := do("POST", "/api/todos", `{"title":"integration"}`)
	if res.StatusCode != 201 {
		t.Fatalf("create = %d %s", res.StatusCode, body)
	}
	var created todo.Todo
	if err := json.Unmarshal(body, &created); err != nil || created.ID != 1 {
		t.Fatalf("created = %s (%v)", body, err)
	}

	res, _ = do("GET", "/api/todos", "")
	if res.Header.Get("X-Cache") != "miss" {
		t.Fatalf("first list X-Cache = %q", res.Header.Get("X-Cache"))
	}
	if n, _ := rdb.Exists(ctx, key).Result(); n != 1 {
		t.Fatal("the list was not written to Redis")
	}
	if ttl, _ := rdb.TTL(ctx, key).Result(); ttl <= 0 {
		t.Fatalf("the cached list has no expiry (ttl %v)", ttl)
	}
	res, body = do("GET", "/api/todos", "")
	if res.Header.Get("X-Cache") != "hit" || !strings.Contains(string(body), "integration") {
		t.Fatalf("second list X-Cache = %q body %s", res.Header.Get("X-Cache"), body)
	}

	if res, body := do("PATCH", "/api/todos/1", `{"done":true}`); res.StatusCode != 200 || !strings.Contains(string(body), `"done":true`) {
		t.Fatalf("patch = %d %s", res.StatusCode, body)
	}
	if n, _ := rdb.Exists(ctx, key).Result(); n != 0 {
		t.Fatal("a write must drop the cached list")
	}
	if res, _ := do("PATCH", "/api/todos/99", `{"done":true}`); res.StatusCode != 404 {
		t.Fatalf("patch of a missing todo = %d", res.StatusCode)
	}
	if res, _ := do("POST", "/api/todos", `{"title":""}`); res.StatusCode != 422 {
		t.Fatalf("empty title = %d", res.StatusCode)
	}
	if res, _ := do("DELETE", "/api/todos/1", ""); res.StatusCode != 204 {
		t.Fatalf("delete = %d", res.StatusCode)
	}
	if res, body := do("GET", "/api/todos", ""); strings.TrimSpace(string(body)) != "[]" {
		t.Fatalf("list after delete = %s (%d)", body, res.StatusCode)
	}
}

func TestWarmAndCleanup(t *testing.T) {
	svc, st, rdb, key := stack(t)
	ctx := context.Background()

	old, err := svc.Create(ctx, "old and done")
	if err != nil {
		t.Fatal(err)
	}
	keep, err := svc.Create(ctx, "recent and done")
	if err != nil {
		t.Fatal(err)
	}
	yes := true
	for _, id := range []int64{old.ID, keep.ID} {
		if _, err := svc.Update(ctx, id, nil, &yes); err != nil {
			t.Fatal(err)
		}
	}
	// Age one todo by hand: the service always stamps "now".
	pgURL, _ := target(t)
	conn := connect(t, pgURL)
	if _, err := conn.Exec(ctx, "UPDATE todos SET updated_at = now() - interval '40 days' WHERE id = $1", old.ID); err != nil {
		t.Fatal(err)
	}

	if n, err := svc.Warm(ctx); err != nil || n != 2 {
		t.Fatalf("warm = %d, %v", n, err)
	}
	if n, _ := rdb.Exists(ctx, key).Result(); n != 1 {
		t.Fatal("warm did not fill Redis")
	}

	n, err := svc.Cleanup(ctx, 30*24*time.Hour)
	if err != nil || n != 1 {
		t.Fatalf("cleanup = %d, %v", n, err)
	}
	if n, _ := rdb.Exists(ctx, key).Result(); n != 0 {
		t.Fatal("cleanup must drop the cached list")
	}
	left, err := st.List(ctx)
	if err != nil || len(left) != 1 || left[0].ID != keep.ID {
		t.Fatalf("left = %+v, %v", left, err)
	}
}

func TestHealthReportsAnUnreachableRedis(t *testing.T) {
	svc, st, _, _ := stack(t)
	dead := redis.NewClient(&redis.Options{Addr: "127.0.0.1:1", DialTimeout: 500 * time.Millisecond, MaxRetries: -1})
	defer dead.Close()
	broken := todo.NewService(st, cache.New(dead, "k", time.Second), slog.New(slog.NewTextHandler(io.Discard, nil)))
	if f := broken.Health(context.Background()); len(f) != 1 || f[0] != "redis" {
		t.Fatalf("health failed = %v", f)
	}
	if f := svc.Health(context.Background()); len(f) != 0 {
		t.Fatalf("healthy stack reported %v", f)
	}
}
