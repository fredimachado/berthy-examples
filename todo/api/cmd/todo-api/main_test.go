package main

import (
	"bytes"
	"context"
	"net"
	"net/http"
	"net/http/httptest"
	"strconv"
	"strings"
	"testing"
	"time"
)

func TestResolve(t *testing.T) {
	cases := []struct {
		argv     []string
		wantName string
		wantArgs []string
	}{
		{[]string{"/app/bin/todo-api"}, "serve", nil},
		{[]string{"/app/bin/migrate", "up"}, "migrate", []string{"up"}},
		{[]string{"/app/bin/migrate", "down-to", "1"}, "migrate", []string{"down-to", "1"}},
		{[]string{"/app/bin/warm-cache"}, "warm-cache", nil},
		{[]string{"/app/bin/cleanup", "--done-older-than", "30d"}, "cleanup", []string{"--done-older-than", "30d"}},
		{[]string{"/app/bin/healthcheck"}, "healthcheck", nil},
		{[]string{"todo-api", "migrate", "version"}, "migrate", []string{"version"}},
		{[]string{"todo-api", "healthcheck"}, "healthcheck", nil},
		{[]string{"todo-api", "version"}, "version", nil},
		{[]string{"todo-api", "bogus"}, "bogus", nil},
	}
	for _, tc := range cases {
		name, args := resolve(tc.argv)
		if name != tc.wantName || strings.Join(args, " ") != strings.Join(tc.wantArgs, " ") {
			t.Errorf("resolve(%v) = %q %v, want %q %v", tc.argv, name, args, tc.wantName, tc.wantArgs)
		}
	}
}

func TestParseAge(t *testing.T) {
	good := map[string]time.Duration{
		"30d":  30 * 24 * time.Hour,
		"1d":   24 * time.Hour,
		"12h":  12 * time.Hour,
		"90m":  90 * time.Minute,
		"720h": 720 * time.Hour,
	}
	for in, want := range good {
		if got, err := parseAge(in); err != nil || got != want {
			t.Errorf("parseAge(%q) = %v, %v; want %v", in, got, err, want)
		}
	}
	for _, in := range []string{"", "0d", "-1d", "d", "30", "1.5d", "abc", "0s", "99999999d"} {
		if _, err := parseAge(in); err == nil {
			t.Errorf("parseAge(%q) succeeded, want an error", in)
		}
	}
}

func envOf(m map[string]string) env {
	return env{getenv: func(k string) string { return m[k] }, stdout: &bytes.Buffer{}}
}

func TestCommandsFailFastNamingMissingVariables(t *testing.T) {
	for _, tc := range []struct {
		name string
		args []string
		want string
	}{
		{"serve", nil, "DB_HOST"},
		{"migrate", []string{"version"}, "POSTGRES_PASSWORD"},
		{"warm-cache", nil, "REDIS_HOST"},
		{"cleanup", []string{"--done-older-than", "30d"}, "REDIS_PASSWORD"},
	} {
		err := commands[tc.name](context.Background(), tc.args, envOf(nil))
		if err == nil || !strings.Contains(err.Error(), tc.want) {
			t.Errorf("%s: error = %v, want one naming %s", tc.name, err, tc.want)
		}
	}
}

func TestCleanupRejectsBadFlagsBeforeConnecting(t *testing.T) {
	for _, args := range [][]string{{"--done-older-than", "soon"}, {"--bogus"}, {"extra"}} {
		if err := cleanup(context.Background(), args, envOf(nil)); err == nil || strings.Contains(err.Error(), "DB_HOST") {
			t.Errorf("cleanup %v: error = %v", args, err)
		}
	}
}

func TestMigrateNeedsACommand(t *testing.T) {
	if err := runMigrate(context.Background(), nil, envOf(nil)); err == nil || !strings.Contains(err.Error(), "usage") {
		t.Fatalf("error = %v", err)
	}
}

func portOf(t *testing.T, srv *httptest.Server) string {
	t.Helper()
	_, port, err := net.SplitHostPort(strings.TrimPrefix(srv.URL, "http://"))
	if err != nil {
		t.Fatal(err)
	}
	if _, err := strconv.Atoi(port); err != nil {
		t.Fatal(err)
	}
	return port
}

func TestHealthcheckExitCodeFollowsTheHealthEndpoint(t *testing.T) {
	status := http.StatusOK
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/health" {
			http.NotFound(w, r)
			return
		}
		w.WriteHeader(status)
	}))
	defer srv.Close()
	e := envOf(map[string]string{"PORT": portOf(t, srv)})

	if err := healthcheck(context.Background(), nil, e); err != nil {
		t.Fatalf("healthy: %v", err)
	}
	status = http.StatusServiceUnavailable
	if err := healthcheck(context.Background(), nil, e); err == nil {
		t.Fatal("503 must fail the check")
	}
	srv.Close()
	if err := healthcheck(context.Background(), nil, e); err == nil {
		t.Fatal("a server that is gone must fail the check")
	}
}

func TestVersionCommandPrintsTheStampedBuild(t *testing.T) {
	e := envOf(nil)
	if err := printVersion(context.Background(), nil, e); err != nil {
		t.Fatal(err)
	}
	if got := e.stdout.(*bytes.Buffer).String(); got != "dev unknown\n" {
		t.Fatalf("output = %q", got)
	}
}
