// Command todo-api is the todo API and its helper commands in one binary.
//
// The image installs it as /app/bin/todo-api and links the other names to it,
// so Berthy can run /app/bin/migrate, /app/bin/warm-cache, /app/bin/cleanup
// and /app/bin/healthcheck. The command comes from the name the binary was
// started as, or from the first argument: todo-api migrate version.
package main

import (
	"context"
	"fmt"
	"io"
	"os"
	"os/signal"
	"path/filepath"
	"strings"
	"syscall"
)

const usage = `usage: todo-api [serve]
       todo-api migrate version | up | down-to <n>
       todo-api warm-cache
       todo-api cleanup [--done-older-than 30d]
       todo-api healthcheck

Settings come from the environment: PORT, LOG_LEVEL, DB_HOST, DB_PORT, DB_NAME,
DB_USER, POSTGRES_PASSWORD, REDIS_HOST, REDIS_PORT, REDIS_PASSWORD.`

var commands = map[string]func(ctx context.Context, args []string, env env) error{
	"serve":       serve,
	"migrate":     runMigrate,
	"warm-cache":  warmCache,
	"cleanup":     cleanup,
	"healthcheck": healthcheck,
}

// env is what a command reads from its surroundings, so tests can replace it.
type env struct {
	getenv func(string) string
	stdout io.Writer
}

func main() {
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()

	name, args := resolve(os.Args)
	run, ok := commands[name]
	if !ok {
		fmt.Fprintf(os.Stderr, "todo-api: unknown command %q\n%s\n", name, usage)
		os.Exit(2)
	}
	if err := run(ctx, args, env{getenv: os.Getenv, stdout: os.Stdout}); err != nil {
		fmt.Fprintf(os.Stderr, "%s: %v\n", name, err)
		os.Exit(1)
	}
}

// resolve picks the command and its arguments from argv. The name the binary
// was started as wins when it is a command; otherwise the first argument is
// the command, and "serve" is the default.
func resolve(argv []string) (string, []string) {
	base := filepath.Base(argv[0])
	if _, ok := commands[base]; ok {
		return base, argv[1:]
	}
	if len(argv) > 1 && !strings.HasPrefix(argv[1], "-") {
		return argv[1], argv[2:]
	}
	return "serve", argv[1:]
}
