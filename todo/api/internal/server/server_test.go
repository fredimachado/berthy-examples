package server_test

import (
	"encoding/json"
	"io"
	"log/slog"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"

	"github.com/fredimachado/berthy-examples/todo/api/internal/server"
	"github.com/fredimachado/berthy-examples/todo/api/internal/todo"
	"github.com/fredimachado/berthy-examples/todo/api/internal/todo/memory"
)

type fixture struct {
	h     http.Handler
	store *memory.Store
	cache *memory.Cache
}

func newFixture() *fixture {
	st, ca := &memory.Store{}, &memory.Cache{}
	log := slog.New(slog.NewTextHandler(io.Discard, nil))
	svc := todo.NewService(st, ca, log)
	return &fixture{
		h:     server.New(svc, server.BuildInfo{Version: "v1.2.3", Commit: "abc1234"}, log),
		store: st,
		cache: ca,
	}
}

func (f *fixture) do(method, path, body string) *httptest.ResponseRecorder {
	req := httptest.NewRequest(method, path, strings.NewReader(body))
	rec := httptest.NewRecorder()
	f.h.ServeHTTP(rec, req)
	return rec
}

func decodeBody[T any](t *testing.T, rec *httptest.ResponseRecorder) T {
	t.Helper()
	var v T
	if err := json.Unmarshal(rec.Body.Bytes(), &v); err != nil {
		t.Fatalf("body %q is not JSON: %v", rec.Body.String(), err)
	}
	return v
}

func wantStatus(t *testing.T, rec *httptest.ResponseRecorder, want int) {
	t.Helper()
	if rec.Code != want {
		t.Fatalf("status = %d, want %d (body %q)", rec.Code, want, rec.Body.String())
	}
}

func TestHealth(t *testing.T) {
	f := newFixture()
	rec := f.do("GET", "/health", "")
	wantStatus(t, rec, 200)
	if got := decodeBody[map[string]string](t, rec); got["status"] != "ok" {
		t.Fatalf("body = %v", got)
	}

	f.cache.Fail = true
	rec = f.do("GET", "/health", "")
	wantStatus(t, rec, 503)
	got := decodeBody[struct {
		Status string
		Failed []string
	}](t, rec)
	if got.Status != "unavailable" || len(got.Failed) != 1 || got.Failed[0] != "redis" {
		t.Fatalf("body = %+v", got)
	}

	f.cache.Fail, f.store.Fail = false, true
	rec = f.do("GET", "/health", "")
	wantStatus(t, rec, 503)
	if !strings.Contains(rec.Body.String(), "postgres") {
		t.Fatalf("body = %s", rec.Body)
	}
}

func TestVersion(t *testing.T) {
	rec := newFixture().do("GET", "/version", "")
	wantStatus(t, rec, 200)
	if got := decodeBody[server.BuildInfo](t, rec); got.Version != "v1.2.3" || got.Commit != "abc1234" {
		t.Fatalf("body = %+v", got)
	}
}

func TestIndexPage(t *testing.T) {
	rec := newFixture().do("GET", "/", "")
	wantStatus(t, rec, 200)
	if ct := rec.Header().Get("Content-Type"); !strings.HasPrefix(ct, "text/html") {
		t.Fatalf("content type = %q", ct)
	}
	if !strings.Contains(rec.Body.String(), "<title>Todo</title>") {
		t.Fatal("not the todo page")
	}
}

func TestListStartsEmptyAndShowsCacheHits(t *testing.T) {
	f := newFixture()
	rec := f.do("GET", "/api/todos", "")
	wantStatus(t, rec, 200)
	if strings.TrimSpace(rec.Body.String()) != "[]" {
		t.Fatalf("empty list body = %q, want []", rec.Body.String())
	}
	if rec.Header().Get("X-Cache") != "miss" {
		t.Fatalf("first X-Cache = %q", rec.Header().Get("X-Cache"))
	}
	rec = f.do("GET", "/api/todos", "")
	if rec.Header().Get("X-Cache") != "hit" {
		t.Fatalf("second X-Cache = %q", rec.Header().Get("X-Cache"))
	}
}

func TestCreateListUpdateDelete(t *testing.T) {
	f := newFixture()

	rec := f.do("POST", "/api/todos", `{"title":"  write tests "}`)
	wantStatus(t, rec, 201)
	created := decodeBody[todo.Todo](t, rec)
	if created.ID != 1 || created.Title != "write tests" || created.Done {
		t.Fatalf("created = %+v", created)
	}
	if rec.Header().Get("Location") != "/api/todos/1" {
		t.Fatalf("Location = %q", rec.Header().Get("Location"))
	}

	f.do("GET", "/api/todos", "") // fill the cache
	rec = f.do("PATCH", "/api/todos/1", `{"done":true}`)
	wantStatus(t, rec, 200)
	if got := decodeBody[todo.Todo](t, rec); !got.Done || got.Title != "write tests" {
		t.Fatalf("patched = %+v", got)
	}

	rec = f.do("GET", "/api/todos", "")
	if rec.Header().Get("X-Cache") != "miss" {
		t.Fatal("a write must drop the cache")
	}
	if list := decodeBody[[]todo.Todo](t, rec); len(list) != 1 || !list[0].Done {
		t.Fatalf("list = %+v", list)
	}

	rec = f.do("PATCH", "/api/todos/1", `{"title":"renamed"}`)
	wantStatus(t, rec, 200)
	if got := decodeBody[todo.Todo](t, rec); got.Title != "renamed" || !got.Done {
		t.Fatalf("renamed = %+v", got)
	}

	rec = f.do("DELETE", "/api/todos/1", "")
	wantStatus(t, rec, 204)
	if rec.Body.Len() != 0 {
		t.Fatalf("204 with a body: %q", rec.Body)
	}
	wantStatus(t, f.do("DELETE", "/api/todos/1", ""), 404)
}

func TestInputErrors(t *testing.T) {
	f := newFixture()
	f.do("POST", "/api/todos", `{"title":"x"}`)
	cases := []struct {
		name         string
		method, path string
		body         string
		want         int
	}{
		{"empty title", "POST", "/api/todos", `{"title":""}`, 422},
		{"blank title", "POST", "/api/todos", `{"title":"   "}`, 422},
		{"missing title", "POST", "/api/todos", `{}`, 422},
		{"title too long", "POST", "/api/todos", `{"title":"` + strings.Repeat("a", 201) + `"}`, 422},
		{"not json", "POST", "/api/todos", `title=x`, 400},
		{"empty body", "POST", "/api/todos", ``, 400},
		{"wrong type", "POST", "/api/todos", `{"title":5}`, 400},
		{"unknown field", "POST", "/api/todos", `{"title":"x","id":9}`, 400},
		{"trailing data", "POST", "/api/todos", `{"title":"x"} {}`, 400},
		{"body too large", "POST", "/api/todos", `{"title":"` + strings.Repeat("a", 20<<10) + `"}`, 413},
		{"patch nothing", "PATCH", "/api/todos/1", `{}`, 422},
		{"patch bad title", "PATCH", "/api/todos/1", `{"title":""}`, 422},
		{"patch done wrong type", "PATCH", "/api/todos/1", `{"done":"yes"}`, 400},
		{"patch missing todo", "PATCH", "/api/todos/99", `{"done":true}`, 404},
		{"id not a number", "PATCH", "/api/todos/abc", `{"done":true}`, 400},
		{"id zero", "DELETE", "/api/todos/0", ``, 400},
		{"id negative", "DELETE", "/api/todos/-3", ``, 400},
		{"unknown path", "GET", "/nope", ``, 404},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			rec := f.do(tc.method, tc.path, tc.body)
			wantStatus(t, rec, tc.want)
			if ct := rec.Header().Get("Content-Type"); ct != "application/json" {
				t.Fatalf("content type = %q", ct)
			}
			if got := decodeBody[map[string]string](t, rec); got["error"] == "" {
				t.Fatalf("no error message: %s", rec.Body)
			}
		})
	}
}

func TestWrongMethodIsRejected(t *testing.T) {
	f := newFixture()
	if rec := f.do("PUT", "/api/todos/1", `{}`); rec.Code != 405 && rec.Code != 404 {
		t.Fatalf("PUT status = %d", rec.Code)
	}
}

func TestServerErrorsDoNotLeakDetails(t *testing.T) {
	f := newFixture()
	f.store.Fail = true
	rec := f.do("GET", "/api/todos", "")
	wantStatus(t, rec, 500)
	if strings.Contains(rec.Body.String(), memory.ErrDown.Error()) {
		t.Fatalf("body leaks the cause: %s", rec.Body)
	}
	rec = f.do("POST", "/api/todos", `{"title":"x"}`)
	wantStatus(t, rec, 500)
}
