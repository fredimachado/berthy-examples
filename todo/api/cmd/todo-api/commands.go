package main

import (
	"context"
	"errors"
	"flag"
	"fmt"
	"io"
	"log/slog"
	"net/http"
	"os"
	"regexp"
	"strconv"
	"time"

	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgxpool"
	"github.com/redis/go-redis/v9"

	"github.com/fredimachado/berthy-examples/todo/api/internal/cache"
	"github.com/fredimachado/berthy-examples/todo/api/internal/config"
	"github.com/fredimachado/berthy-examples/todo/api/internal/migrate"
	"github.com/fredimachado/berthy-examples/todo/api/internal/server"
	"github.com/fredimachado/berthy-examples/todo/api/internal/store"
	"github.com/fredimachado/berthy-examples/todo/api/internal/todo"
	"github.com/fredimachado/berthy-examples/todo/api/internal/version"
)

// shutdownTimeout stays under the 30s drain_timeout of the Berthy manifest.
const shutdownTimeout = 25 * time.Second

func newLogger(w io.Writer, level slog.Level) *slog.Logger {
	return slog.New(slog.NewJSONHandler(w, &slog.HandlerOptions{Level: level}))
}

// openService connects to Postgres and Redis. Connections are lazy: an
// unreachable dependency shows in /health, it does not stop the process.
func openService(cfg config.Config, log *slog.Logger) (*todo.Service, func(), error) {
	pool, err := pgxpool.New(context.Background(), cfg.PostgresURL())
	if err != nil {
		return nil, nil, fmt.Errorf("configure postgres: %w", err)
	}
	rdb := redis.NewClient(&redis.Options{
		Addr:         cfg.RedisAddr(),
		Password:     cfg.RedisPassword,
		DialTimeout:  2 * time.Second,
		ReadTimeout:  2 * time.Second,
		WriteTimeout: 2 * time.Second,
	})
	closeAll := func() {
		pool.Close()
		_ = rdb.Close()
	}
	return todo.NewService(store.New(pool), cache.New(rdb, cache.Key, cache.TTL), log), closeAll, nil
}

func serve(ctx context.Context, args []string, e env) error {
	if len(args) > 0 {
		return fmt.Errorf("serve takes no arguments\n%s", usage)
	}
	cfg, err := config.Load(e.getenv, config.NeedDB|config.NeedRedis)
	if err != nil {
		return err
	}
	log := newLogger(e.stdout, cfg.LogLevel)
	svc, closeAll, err := openService(cfg, log)
	if err != nil {
		return err
	}
	defer closeAll()

	srv := &http.Server{
		Addr:              fmt.Sprintf(":%d", cfg.Port),
		Handler:           server.New(svc, server.BuildInfo{Version: version.Version, Commit: version.Commit}, log),
		ReadHeaderTimeout: 5 * time.Second,
		ReadTimeout:       15 * time.Second,
		WriteTimeout:      30 * time.Second,
		IdleTimeout:       60 * time.Second,
	}
	errc := make(chan error, 1)
	go func() { errc <- srv.ListenAndServe() }()
	log.Info("listening", "addr", srv.Addr, "version", version.Version, "commit", version.Commit)

	select {
	case err := <-errc:
		return err
	case <-ctx.Done():
	}
	log.Info("shutting down")
	shutdownCtx, cancel := context.WithTimeout(context.Background(), shutdownTimeout)
	defer cancel()
	if err := srv.Shutdown(shutdownCtx); err != nil {
		return fmt.Errorf("shutdown: %w", err)
	}
	return nil
}

const migrateUsage = "usage: migrate version | up | down-to <n>"

func runMigrate(ctx context.Context, args []string, e env) error {
	if len(args) == 0 {
		return errors.New(migrateUsage)
	}
	cfg, err := config.Load(e.getenv, config.NeedDB)
	if err != nil {
		return err
	}
	migrations, err := migrate.Embedded()
	if err != nil {
		return err
	}
	conn, err := pgx.Connect(ctx, cfg.PostgresURL())
	if err != nil {
		return fmt.Errorf("connect to postgres at %s:%d: %w", cfg.DBHost, cfg.DBPort, err)
	}
	defer conn.Close(context.WithoutCancel(ctx))
	m := migrate.New(conn, migrations)
	log := newLogger(os.Stderr, cfg.LogLevel)

	switch args[0] {
	case "version":
		v, err := m.Version(ctx)
		if err != nil {
			return err
		}
		// Berthy stores this line as the schema state and passes it back to down-to.
		fmt.Fprintln(e.stdout, v)
	case "up":
		applied, err := m.Up(ctx)
		if err != nil {
			return err
		}
		v, err := m.Version(ctx)
		if err != nil {
			return err
		}
		log.Info("migrated", "applied", applied, "version", v)
		fmt.Fprintf(e.stdout, "version %d (applied %d)\n", v, len(applied))
	case "down-to":
		if len(args) != 2 {
			return errors.New(migrateUsage)
		}
		target, err := strconv.Atoi(args[1])
		if err != nil {
			return fmt.Errorf("down-to needs a number, got %q", args[1])
		}
		reverted, err := m.DownTo(ctx, target)
		if err != nil {
			return err
		}
		log.Info("reverted", "reverted", reverted, "version", target)
		fmt.Fprintf(e.stdout, "version %d (reverted %d)\n", target, len(reverted))
	default:
		return fmt.Errorf("unknown migrate command %q\n%s", args[0], migrateUsage)
	}
	return nil
}

func warmCache(ctx context.Context, args []string, e env) error {
	if len(args) > 0 {
		return errors.New("usage: warm-cache")
	}
	cfg, err := config.Load(e.getenv, config.NeedDB|config.NeedRedis)
	if err != nil {
		return err
	}
	log := newLogger(os.Stderr, cfg.LogLevel)
	svc, closeAll, err := openService(cfg, log)
	if err != nil {
		return err
	}
	defer closeAll()
	n, err := svc.Warm(ctx)
	if err != nil {
		return err
	}
	fmt.Fprintf(e.stdout, "cache warmed with %d todos\n", n)
	return nil
}

func cleanup(ctx context.Context, args []string, e env) error {
	fs := flag.NewFlagSet("cleanup", flag.ContinueOnError)
	fs.SetOutput(io.Discard)
	olderThan := fs.String("done-older-than", "30d", "delete done todos not changed for this long (30d, 12h)")
	if err := fs.Parse(args); err != nil || fs.NArg() > 0 {
		return errors.New("usage: cleanup [--done-older-than 30d]")
	}
	age, err := parseAge(*olderThan)
	if err != nil {
		return err
	}
	cfg, err := config.Load(e.getenv, config.NeedDB|config.NeedRedis)
	if err != nil {
		return err
	}
	log := newLogger(os.Stderr, cfg.LogLevel)
	svc, closeAll, err := openService(cfg, log)
	if err != nil {
		return err
	}
	defer closeAll()
	n, err := svc.Cleanup(ctx, age)
	if err != nil {
		return err
	}
	fmt.Fprintf(e.stdout, "deleted %d done todos older than %s\n", n, *olderThan)
	return nil
}

var days = regexp.MustCompile(`^(\d+)d$`)

// parseAge reads "30d" (days) or any time.ParseDuration value such as "12h".
func parseAge(s string) (time.Duration, error) {
	var d time.Duration
	if m := days.FindStringSubmatch(s); m != nil {
		n, err := strconv.Atoi(m[1])
		if err != nil || n > 36500 {
			return 0, fmt.Errorf("--done-older-than %q is out of range", s)
		}
		d = time.Duration(n) * 24 * time.Hour
	} else {
		var err error
		if d, err = time.ParseDuration(s); err != nil {
			return 0, fmt.Errorf("--done-older-than %q is not a duration such as 30d or 12h", s)
		}
	}
	if d <= 0 {
		return 0, fmt.Errorf("--done-older-than %q must be greater than zero", s)
	}
	return d, nil
}

// healthcheck asks the local server for /health. It exists so the image
// needs no curl.
func healthcheck(ctx context.Context, args []string, e env) error {
	if len(args) > 0 {
		return errors.New("usage: healthcheck")
	}
	cfg, err := config.Load(e.getenv, 0)
	if err != nil {
		return err
	}
	ctx, cancel := context.WithTimeout(ctx, 3*time.Second)
	defer cancel()
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, fmt.Sprintf("http://127.0.0.1:%d/health", cfg.Port), nil)
	if err != nil {
		return err
	}
	res, err := http.DefaultClient.Do(req)
	if err != nil {
		return err
	}
	defer res.Body.Close()
	_, _ = io.Copy(io.Discard, io.LimitReader(res.Body, 1<<16))
	if res.StatusCode != http.StatusOK {
		return fmt.Errorf("health returned %s", res.Status)
	}
	return nil
}
