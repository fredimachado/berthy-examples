// Package store keeps todos in Postgres.
package store

import (
	"context"
	"errors"
	"time"

	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgxpool"

	"github.com/fredimachado/berthy-examples/todo/api/internal/todo"
)

// Postgres implements todo.Store.
type Postgres struct {
	pool *pgxpool.Pool
}

// New wraps a connection pool.
func New(pool *pgxpool.Pool) *Postgres { return &Postgres{pool: pool} }

const columns = "id, title, done, created_at, updated_at"

func scan(row pgx.Row) (todo.Todo, error) {
	var t todo.Todo
	err := row.Scan(&t.ID, &t.Title, &t.Done, &t.CreatedAt, &t.UpdatedAt)
	return t, err
}

// List returns every todo, oldest first.
func (p *Postgres) List(ctx context.Context) ([]todo.Todo, error) {
	rows, err := p.pool.Query(ctx, "SELECT "+columns+" FROM todos ORDER BY id")
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	todos := []todo.Todo{}
	for rows.Next() {
		t, err := scan(rows)
		if err != nil {
			return nil, err
		}
		todos = append(todos, t)
	}
	return todos, rows.Err()
}

// Create inserts a todo.
func (p *Postgres) Create(ctx context.Context, title string) (todo.Todo, error) {
	return scan(p.pool.QueryRow(ctx,
		"INSERT INTO todos (title) VALUES ($1) RETURNING "+columns, title))
}

// Update changes the fields that are not nil.
func (p *Postgres) Update(ctx context.Context, id int64, title *string, done *bool) (todo.Todo, error) {
	t, err := scan(p.pool.QueryRow(ctx,
		`UPDATE todos
		    SET title = COALESCE($2, title), done = COALESCE($3, done), updated_at = now()
		  WHERE id = $1
		  RETURNING `+columns, id, title, done))
	if errors.Is(err, pgx.ErrNoRows) {
		return todo.Todo{}, todo.ErrNotFound
	}
	return t, err
}

// Delete removes a todo.
func (p *Postgres) Delete(ctx context.Context, id int64) error {
	tag, err := p.pool.Exec(ctx, "DELETE FROM todos WHERE id = $1", id)
	if err != nil {
		return err
	}
	if tag.RowsAffected() == 0 {
		return todo.ErrNotFound
	}
	return nil
}

// DeleteDoneBefore deletes done todos that were last changed before cutoff.
func (p *Postgres) DeleteDoneBefore(ctx context.Context, cutoff time.Time) (int64, error) {
	tag, err := p.pool.Exec(ctx, "DELETE FROM todos WHERE done AND updated_at < $1", cutoff)
	if err != nil {
		return 0, err
	}
	return tag.RowsAffected(), nil
}

// Ping checks that the database answers.
func (p *Postgres) Ping(ctx context.Context) error { return p.pool.Ping(ctx) }
