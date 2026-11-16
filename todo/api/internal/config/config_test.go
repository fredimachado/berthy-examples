package config

import (
	"log/slog"
	"net/url"
	"strings"
	"testing"
)

func env(m map[string]string) func(string) string {
	return func(k string) string { return m[k] }
}

func full() map[string]string {
	return map[string]string{
		"DB_HOST":           "todo-db",
		"DB_NAME":           "todo",
		"DB_USER":           "todo",
		"POSTGRES_PASSWORD": "s3cret/with:odd@chars",
		"REDIS_HOST":        "todo-redis",
		"REDIS_PASSWORD":    "r3dis",
	}
}

func TestLoadDefaults(t *testing.T) {
	c, err := Load(env(full()), NeedDB|NeedRedis)
	if err != nil {
		t.Fatal(err)
	}
	if c.Port != 8080 || c.DBPort != 5432 || c.RedisPort != 6379 {
		t.Fatalf("defaults wrong: %+v", c)
	}
	if c.LogLevel != slog.LevelInfo {
		t.Fatalf("level = %v", c.LogLevel)
	}
	if got := c.RedisAddr(); got != "todo-redis:6379" {
		t.Fatalf("redis addr = %q", got)
	}
}

func TestLoadNamesEveryMissingVariable(t *testing.T) {
	_, err := Load(env(map[string]string{"DB_HOST": "x"}), NeedDB|NeedRedis)
	if err == nil {
		t.Fatal("expected an error")
	}
	for _, name := range []string{"DB_NAME", "DB_USER", "POSTGRES_PASSWORD", "REDIS_HOST", "REDIS_PASSWORD"} {
		if !strings.Contains(err.Error(), name) {
			t.Errorf("error %q does not name %s", err, name)
		}
	}
	if strings.Contains(err.Error(), "DB_HOST") {
		t.Errorf("error %q names a variable that is set", err)
	}
}

func TestLoadOnlyDemandsWhatACommandUses(t *testing.T) {
	m := map[string]string{"DB_HOST": "h", "DB_NAME": "n", "DB_USER": "u", "POSTGRES_PASSWORD": "p"}
	if _, err := Load(env(m), NeedDB); err != nil {
		t.Fatalf("db only: %v", err)
	}
	if _, err := Load(env(nil), 0); err != nil {
		t.Fatalf("healthcheck needs nothing: %v", err)
	}
}

func TestLoadRejectsBadValuesWithoutLeakingPasswords(t *testing.T) {
	m := full()
	m["PORT"] = "http"
	m["DB_PORT"] = "70000"
	m["LOG_LEVEL"] = "loud"
	_, err := Load(env(m), NeedDB|NeedRedis)
	if err == nil {
		t.Fatal("expected an error")
	}
	for _, want := range []string{"PORT", "DB_PORT", "LOG_LEVEL"} {
		if !strings.Contains(err.Error(), want) {
			t.Errorf("error %q does not mention %s", err, want)
		}
	}
	for _, secret := range []string{m["POSTGRES_PASSWORD"], m["REDIS_PASSWORD"]} {
		if strings.Contains(err.Error(), secret) {
			t.Errorf("error leaks a password: %q", err)
		}
	}
}

func TestPostgresURLEscapesThePassword(t *testing.T) {
	c, err := Load(env(full()), NeedDB)
	if err != nil {
		t.Fatal(err)
	}
	u, err := url.Parse(c.PostgresURL())
	if err != nil {
		t.Fatal(err)
	}
	pw, _ := u.User.Password()
	if pw != "s3cret/with:odd@chars" || u.User.Username() != "todo" || u.Host != "todo-db:5432" || u.Path != "/todo" {
		t.Fatalf("round trip failed: %s", u.Redacted())
	}
}
