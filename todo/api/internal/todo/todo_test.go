package todo_test

import (
	"context"
	"errors"
	"io"
	"log/slog"
	"strings"
	"testing"
	"time"

	"github.com/fredimachado/berthy-examples/todo/api/internal/todo"
	"github.com/fredimachado/berthy-examples/todo/api/internal/todo/memory"
)

func newService() (*todo.Service, *memory.Store, *memory.Cache) {
	st, ca := &memory.Store{}, &memory.Cache{}
	return todo.NewService(st, ca, slog.New(slog.NewTextHandler(io.Discard, nil))), st, ca
}

func TestCleanTitle(t *testing.T) {
	long := strings.Repeat("é", todo.MaxTitleLength) // 200 characters, 400 bytes
	cases := []struct {
		in      string
		want    string
		invalid bool
	}{
		{"  buy milk ", "buy milk", false},
		{long, long, false},
		{long + "x", "", true},
		{"", "", true},
		{"   \t", "", true},
	}
	for _, tc := range cases {
		got, err := todo.CleanTitle(tc.in)
		var inv *todo.InvalidError
		if tc.invalid != errors.As(err, &inv) || got != tc.want {
			t.Errorf("CleanTitle(%.12q) = %.12q, %v", tc.in, got, err)
		}
	}
}

func TestListFillsTheCacheOnAMissAndServesFromItAfter(t *testing.T) {
	svc, st, ca := newService()
	ctx := context.Background()
	if _, err := svc.Create(ctx, "one"); err != nil {
		t.Fatal(err)
	}
	list, cached, err := svc.List(ctx)
	if err != nil || cached || len(list) != 1 {
		t.Fatalf("first list: %v cached=%v err=%v", list, cached, err)
	}
	if !ca.Filled() {
		t.Fatal("a miss must fill the cache")
	}
	list, cached, err = svc.List(ctx)
	if err != nil || !cached || len(list) != 1 {
		t.Fatalf("second list: %v cached=%v err=%v", list, cached, err)
	}
	if st.Lists != 1 {
		t.Fatalf("the store was read %d times, want 1", st.Lists)
	}
}

func TestEveryWriteInvalidatesTheCache(t *testing.T) {
	svc, _, ca := newService()
	ctx := context.Background()
	prime := func() {
		t.Helper()
		if _, _, err := svc.List(ctx); err != nil || !ca.Filled() {
			t.Fatalf("could not prime the cache: %v", err)
		}
	}
	prime()
	created, err := svc.Create(ctx, "a")
	if err != nil || ca.Filled() {
		t.Fatalf("create: err=%v cache filled=%v", err, ca.Filled())
	}
	prime()
	done := true
	if _, err := svc.Update(ctx, created.ID, nil, &done); err != nil || ca.Filled() {
		t.Fatalf("update: err=%v cache filled=%v", err, ca.Filled())
	}
	prime()
	if err := svc.Delete(ctx, created.ID); err != nil || ca.Filled() {
		t.Fatalf("delete: err=%v cache filled=%v", err, ca.Filled())
	}
}

func TestAFailedWriteKeepsTheCache(t *testing.T) {
	svc, _, ca := newService()
	ctx := context.Background()
	if _, _, err := svc.List(ctx); err != nil {
		t.Fatal(err)
	}
	if err := svc.Delete(ctx, 99); !errors.Is(err, todo.ErrNotFound) {
		t.Fatalf("delete = %v", err)
	}
	if !ca.Filled() {
		t.Fatal("a failed write must not drop the cache")
	}
}

func TestValidationHappensBeforeTheStore(t *testing.T) {
	svc, st, _ := newService()
	ctx := context.Background()
	var inv *todo.InvalidError
	if _, err := svc.Create(ctx, " "); !errors.As(err, &inv) {
		t.Fatalf("create with blank title = %v", err)
	}
	if _, err := svc.Update(ctx, 1, nil, nil); !errors.As(err, &inv) {
		t.Fatalf("update with nothing = %v", err)
	}
	bad := strings.Repeat("x", 201)
	if _, err := svc.Update(ctx, 1, &bad, nil); !errors.As(err, &inv) {
		t.Fatalf("update with a long title = %v", err)
	}
	if st.Lists != 0 {
		t.Fatal("the store must not be touched")
	}
}

func TestACacheThatIsDownDoesNotBreakReadsOrWrites(t *testing.T) {
	svc, _, ca := newService()
	ca.Fail = true
	ctx := context.Background()
	if _, err := svc.Create(ctx, "still works"); err != nil {
		t.Fatalf("create: %v", err)
	}
	list, cached, err := svc.List(ctx)
	if err != nil || cached || len(list) != 1 {
		t.Fatalf("list: %v cached=%v err=%v", list, cached, err)
	}
}

func TestAStoreThatIsDownFailsReads(t *testing.T) {
	svc, st, _ := newService()
	st.Fail = true
	if _, _, err := svc.List(context.Background()); !errors.Is(err, memory.ErrDown) {
		t.Fatalf("list = %v", err)
	}
}

func TestWarmFailsWhenTheCacheCannotBeWritten(t *testing.T) {
	svc, _, ca := newService()
	ctx := context.Background()
	if _, err := svc.Create(ctx, "x"); err != nil {
		t.Fatal(err)
	}
	n, err := svc.Warm(ctx)
	if err != nil || n != 1 || !ca.Filled() {
		t.Fatalf("warm: n=%d err=%v filled=%v", n, err, ca.Filled())
	}
	ca.Fail = true
	if _, err := svc.Warm(ctx); err == nil {
		t.Fatal("warm must report a cache that is down")
	}
}

func TestCleanupDeletesOnlyOldDoneTodos(t *testing.T) {
	svc, st, ca := newService()
	ctx := context.Background()
	clock := time.Now().Add(-60 * 24 * time.Hour)
	st.Now = func() time.Time { return clock }

	oldDone, _ := svc.Create(ctx, "old and done")
	oldOpen, _ := svc.Create(ctx, "old and open")
	yes := true
	if _, err := svc.Update(ctx, oldDone.ID, nil, &yes); err != nil {
		t.Fatal(err)
	}
	st.Now = nil // from here on, "now"
	fresh, _ := svc.Create(ctx, "fresh and done")
	if _, err := svc.Update(ctx, fresh.ID, nil, &yes); err != nil {
		t.Fatal(err)
	}
	if _, _, err := svc.List(ctx); err != nil {
		t.Fatal(err)
	}

	n, err := svc.Cleanup(ctx, 30*24*time.Hour)
	if err != nil || n != 1 {
		t.Fatalf("cleanup deleted %d, err=%v", n, err)
	}
	if ca.Filled() {
		t.Fatal("cleanup must drop the cached list")
	}
	list, _, _ := svc.List(ctx)
	if len(list) != 2 || list[0].ID != oldOpen.ID || list[1].ID != fresh.ID {
		t.Fatalf("wrong todos left: %+v", list)
	}
}

func TestHealthNamesWhoFailed(t *testing.T) {
	svc, st, ca := newService()
	ctx := context.Background()
	if f := svc.Health(ctx); len(f) != 0 {
		t.Fatalf("healthy service reported %v", f)
	}
	ca.Fail = true
	if f := svc.Health(ctx); len(f) != 1 || f[0] != "redis" {
		t.Fatalf("redis down reported %v", f)
	}
	st.Fail = true
	if f := svc.Health(ctx); len(f) != 2 || f[0] != "postgres" || f[1] != "redis" {
		t.Fatalf("both down reported %v", f)
	}
}
