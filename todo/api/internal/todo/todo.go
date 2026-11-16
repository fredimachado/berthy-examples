// Package todo holds the todo model and the service that ties the database
// and the cache together. It knows nothing about HTTP, pgx or go-redis.
package todo

import (
	"context"
	"errors"
	"fmt"
	"log/slog"
	"strings"
	"sync"
	"time"
	"unicode/utf8"
)

// Todo is one item.
type Todo struct {
	ID        int64     `json:"id"`
	Title     string    `json:"title"`
	Done      bool      `json:"done"`
	CreatedAt time.Time `json:"created_at"`
	UpdatedAt time.Time `json:"updated_at"`
}

// ErrNotFound means no todo has the given id.
var ErrNotFound = errors.New("todo not found")

// InvalidError is a rejected input value. Its message is safe to show a client.
type InvalidError struct{ Msg string }

func (e *InvalidError) Error() string { return e.Msg }

// MaxTitleLength is the longest title, in characters.
const MaxTitleLength = 200

// CleanTitle trims a title and checks its length (1 to 200 characters).
func CleanTitle(s string) (string, error) {
	s = strings.TrimSpace(s)
	n := utf8.RuneCountInString(s)
	if n < 1 || n > MaxTitleLength {
		return "", &InvalidError{Msg: fmt.Sprintf("title must be 1 to %d characters", MaxTitleLength)}
	}
	return s, nil
}

// Store is the durable list of todos.
type Store interface {
	List(ctx context.Context) ([]Todo, error)
	Create(ctx context.Context, title string) (Todo, error)
	// Update changes the fields that are not nil. It returns ErrNotFound for an unknown id.
	Update(ctx context.Context, id int64, title *string, done *bool) (Todo, error)
	Delete(ctx context.Context, id int64) error
	// DeleteDoneBefore deletes done todos last changed before cutoff and returns how many.
	DeleteDoneBefore(ctx context.Context, cutoff time.Time) (int64, error)
	Ping(ctx context.Context) error
}

// Cache holds the list for a short time. Get reports whether it had one.
type Cache interface {
	Get(ctx context.Context) ([]Todo, bool, error)
	Set(ctx context.Context, todos []Todo) error
	Invalidate(ctx context.Context) error
	Ping(ctx context.Context) error
}

// Service is the todo list, read through the cache and written to the store.
type Service struct {
	store Store
	cache Cache
	log   *slog.Logger
}

// NewService builds a Service.
func NewService(store Store, cache Cache, log *slog.Logger) *Service {
	return &Service{store: store, cache: cache, log: log}
}

// List returns the todos and whether they came from the cache. A cache that
// fails is logged and skipped: the database is the source of truth.
func (s *Service) List(ctx context.Context) ([]Todo, bool, error) {
	if todos, ok, err := s.cache.Get(ctx); err != nil {
		s.log.WarnContext(ctx, "cache read failed", "error", err)
	} else if ok {
		return todos, true, nil
	}
	todos, err := s.store.List(ctx)
	if err != nil {
		return nil, false, err
	}
	if err := s.cache.Set(ctx, todos); err != nil {
		s.log.WarnContext(ctx, "cache fill failed", "error", err)
	}
	return todos, false, nil
}

// Create adds a todo. The title is cleaned and checked first.
func (s *Service) Create(ctx context.Context, title string) (Todo, error) {
	title, err := CleanTitle(title)
	if err != nil {
		return Todo{}, err
	}
	t, err := s.store.Create(ctx, title)
	if err != nil {
		return Todo{}, err
	}
	s.invalidate(ctx)
	return t, nil
}

// Update changes the title and/or the done flag.
func (s *Service) Update(ctx context.Context, id int64, title *string, done *bool) (Todo, error) {
	if title == nil && done == nil {
		return Todo{}, &InvalidError{Msg: "give a title, done or both"}
	}
	if title != nil {
		clean, err := CleanTitle(*title)
		if err != nil {
			return Todo{}, err
		}
		title = &clean
	}
	t, err := s.store.Update(ctx, id, title, done)
	if err != nil {
		return Todo{}, err
	}
	s.invalidate(ctx)
	return t, nil
}

// Delete removes a todo.
func (s *Service) Delete(ctx context.Context, id int64) error {
	if err := s.store.Delete(ctx, id); err != nil {
		return err
	}
	s.invalidate(ctx)
	return nil
}

// Warm reads the list from the database and puts it in the cache. Unlike a
// request, it fails when the cache cannot be written: that is its whole job.
func (s *Service) Warm(ctx context.Context) (int, error) {
	todos, err := s.store.List(ctx)
	if err != nil {
		return 0, fmt.Errorf("read todos: %w", err)
	}
	if err := s.cache.Set(ctx, todos); err != nil {
		return 0, fmt.Errorf("fill cache: %w", err)
	}
	return len(todos), nil
}

// Cleanup deletes done todos not changed for olderThan, then drops the cached list.
func (s *Service) Cleanup(ctx context.Context, olderThan time.Duration) (int64, error) {
	n, err := s.store.DeleteDoneBefore(ctx, time.Now().Add(-olderThan))
	if err != nil {
		return 0, fmt.Errorf("delete old todos: %w", err)
	}
	if n > 0 {
		if err := s.cache.Invalidate(ctx); err != nil {
			return n, fmt.Errorf("deleted %d todos but could not clear the cache: %w", n, err)
		}
	}
	return n, nil
}

// HealthTimeout is how long Postgres and Redis get to answer a health check.
const HealthTimeout = 2 * time.Second

// Health pings Postgres and Redis together and returns the names of those that
// did not answer within HealthTimeout. An empty result means healthy.
func (s *Service) Health(ctx context.Context) []string {
	ctx, cancel := context.WithTimeout(ctx, HealthTimeout)
	defer cancel()
	checks := []struct {
		name string
		ping func(context.Context) error
	}{
		{"postgres", s.store.Ping},
		{"redis", s.cache.Ping},
	}
	var (
		wg     sync.WaitGroup
		failed = make([]bool, len(checks))
	)
	for i, c := range checks {
		wg.Add(1)
		go func() {
			defer wg.Done()
			if err := c.ping(ctx); err != nil {
				s.log.WarnContext(ctx, "health check failed", "dependency", c.name, "error", err)
				failed[i] = true
			}
		}()
	}
	wg.Wait()
	var names []string
	for i, f := range failed {
		if f {
			names = append(names, checks[i].name)
		}
	}
	return names
}

func (s *Service) invalidate(ctx context.Context) {
	if err := s.cache.Invalidate(ctx); err != nil {
		// The list can be stale for at most the cache TTL.
		s.log.WarnContext(ctx, "cache invalidation failed", "error", err)
	}
}
