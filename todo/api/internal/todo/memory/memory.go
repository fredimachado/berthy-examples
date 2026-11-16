// Package memory holds in-memory stand-ins for the store and the cache, for
// tests that should not need Postgres or Redis.
package memory

import (
	"context"
	"errors"
	"sync"
	"time"

	"github.com/fredimachado/berthy-examples/todo/api/internal/todo"
)

// ErrDown is what a stand-in returns once Fail is set.
var ErrDown = errors.New("dependency is down")

// Store is an in-memory todo.Store.
type Store struct {
	mu    sync.Mutex
	next  int64
	todos []todo.Todo
	// Fail makes every call return ErrDown.
	Fail bool
	// Lists counts List calls, so a test can see a cache hit.
	Lists int
	// Now is the clock for created_at and updated_at. It defaults to time.Now.
	Now func() time.Time
}

func (s *Store) now() time.Time {
	if s.Now != nil {
		return s.Now()
	}
	return time.Now()
}

func (s *Store) List(context.Context) ([]todo.Todo, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.Lists++
	if s.Fail {
		return nil, ErrDown
	}
	return append([]todo.Todo{}, s.todos...), nil
}

func (s *Store) Create(_ context.Context, title string) (todo.Todo, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.Fail {
		return todo.Todo{}, ErrDown
	}
	s.next++
	t := todo.Todo{ID: s.next, Title: title, CreatedAt: s.now(), UpdatedAt: s.now()}
	s.todos = append(s.todos, t)
	return t, nil
}

func (s *Store) Update(_ context.Context, id int64, title *string, done *bool) (todo.Todo, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.Fail {
		return todo.Todo{}, ErrDown
	}
	for i := range s.todos {
		if s.todos[i].ID != id {
			continue
		}
		if title != nil {
			s.todos[i].Title = *title
		}
		if done != nil {
			s.todos[i].Done = *done
		}
		s.todos[i].UpdatedAt = s.now()
		return s.todos[i], nil
	}
	return todo.Todo{}, todo.ErrNotFound
}

func (s *Store) Delete(_ context.Context, id int64) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.Fail {
		return ErrDown
	}
	for i := range s.todos {
		if s.todos[i].ID == id {
			s.todos = append(s.todos[:i], s.todos[i+1:]...)
			return nil
		}
	}
	return todo.ErrNotFound
}

func (s *Store) DeleteDoneBefore(_ context.Context, cutoff time.Time) (int64, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.Fail {
		return 0, ErrDown
	}
	kept := s.todos[:0]
	var n int64
	for _, t := range s.todos {
		if t.Done && t.UpdatedAt.Before(cutoff) {
			n++
			continue
		}
		kept = append(kept, t)
	}
	s.todos = kept
	return n, nil
}

func (s *Store) Ping(context.Context) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.Fail {
		return ErrDown
	}
	return nil
}

// Cache is an in-memory todo.Cache with no expiry.
type Cache struct {
	mu     sync.Mutex
	todos  []todo.Todo
	filled bool
	// Fail makes every call return ErrDown.
	Fail bool
}

func (c *Cache) Get(context.Context) ([]todo.Todo, bool, error) {
	c.mu.Lock()
	defer c.mu.Unlock()
	if c.Fail {
		return nil, false, ErrDown
	}
	if !c.filled {
		return nil, false, nil
	}
	return append([]todo.Todo{}, c.todos...), true, nil
}

func (c *Cache) Set(_ context.Context, todos []todo.Todo) error {
	c.mu.Lock()
	defer c.mu.Unlock()
	if c.Fail {
		return ErrDown
	}
	c.todos, c.filled = append([]todo.Todo{}, todos...), true
	return nil
}

func (c *Cache) Invalidate(context.Context) error {
	c.mu.Lock()
	defer c.mu.Unlock()
	if c.Fail {
		return ErrDown
	}
	c.todos, c.filled = nil, false
	return nil
}

func (c *Cache) Ping(context.Context) error {
	c.mu.Lock()
	defer c.mu.Unlock()
	if c.Fail {
		return ErrDown
	}
	return nil
}

// Filled reports whether the cache holds a list.
func (c *Cache) Filled() bool {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.filled
}
