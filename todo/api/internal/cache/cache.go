// Package cache keeps the todo list in Redis.
package cache

import (
	"context"
	"encoding/json"
	"errors"
	"time"

	"github.com/redis/go-redis/v9"

	"github.com/fredimachado/berthy-examples/todo/api/internal/todo"
)

// Key is the Redis key of the cached list.
const Key = "todo:list"

// TTL is how long a cached list lives.
const TTL = 60 * time.Second

// Redis implements todo.Cache.
type Redis struct {
	client *redis.Client
	key    string
	ttl    time.Duration
}

// New builds a cache that stores the list under key for ttl.
func New(client *redis.Client, key string, ttl time.Duration) *Redis {
	return &Redis{client: client, key: key, ttl: ttl}
}

// Get returns the cached list. A missing or unreadable entry is a miss.
func (r *Redis) Get(ctx context.Context) ([]todo.Todo, bool, error) {
	data, err := r.client.Get(ctx, r.key).Bytes()
	if errors.Is(err, redis.Nil) {
		return nil, false, nil
	}
	if err != nil {
		return nil, false, err
	}
	var todos []todo.Todo
	if err := json.Unmarshal(data, &todos); err != nil {
		return nil, false, nil
	}
	if todos == nil {
		todos = []todo.Todo{}
	}
	return todos, true, nil
}

// Set stores the list.
func (r *Redis) Set(ctx context.Context, todos []todo.Todo) error {
	data, err := json.Marshal(todos)
	if err != nil {
		return err
	}
	return r.client.Set(ctx, r.key, data, r.ttl).Err()
}

// Invalidate drops the cached list.
func (r *Redis) Invalidate(ctx context.Context) error {
	return r.client.Del(ctx, r.key).Err()
}

// Ping checks that Redis answers.
func (r *Redis) Ping(ctx context.Context) error { return r.client.Ping(ctx).Err() }
