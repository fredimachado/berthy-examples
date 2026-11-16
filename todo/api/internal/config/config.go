// Package config reads the settings the Berthy manifest passes as
// environment variables.
package config

import (
	"fmt"
	"log/slog"
	"net"
	"net/url"
	"strconv"
	"strings"
)

// Needs says which services a command talks to, so a command only demands the
// variables it uses.
type Needs uint8

const (
	NeedDB Needs = 1 << iota
	NeedRedis
)

// Config is the process configuration.
type Config struct {
	Port     int
	LogLevel slog.Level

	DBHost     string
	DBPort     int
	DBName     string
	DBUser     string
	DBPassword string

	RedisHost     string
	RedisPort     int
	RedisPassword string
}

// Load reads the configuration with getenv (os.Getenv in production). It
// reports every missing or invalid variable at once, by name. An error never
// contains a password.
func Load(getenv func(string) string, needs Needs) (Config, error) {
	l := loader{getenv: getenv}
	c := Config{
		Port:     l.port("PORT", 8080),
		LogLevel: l.level("LOG_LEVEL"),
	}
	if needs&NeedDB != 0 {
		c.DBHost = l.required("DB_HOST")
		c.DBPort = l.port("DB_PORT", 5432)
		c.DBName = l.required("DB_NAME")
		c.DBUser = l.required("DB_USER")
		c.DBPassword = l.required("POSTGRES_PASSWORD")
	}
	if needs&NeedRedis != 0 {
		c.RedisHost = l.required("REDIS_HOST")
		c.RedisPort = l.port("REDIS_PORT", 6379)
		c.RedisPassword = l.required("REDIS_PASSWORD")
	}
	if len(l.missing) > 0 || len(l.invalid) > 0 {
		var parts []string
		if len(l.missing) > 0 {
			parts = append(parts, "missing environment variables: "+strings.Join(l.missing, ", "))
		}
		parts = append(parts, l.invalid...)
		return Config{}, fmt.Errorf("%s", strings.Join(parts, "; "))
	}
	return c, nil
}

// PostgresURL builds the connection URL. The password is escaped, so any
// character is safe in it. Do not log the result.
func (c Config) PostgresURL() string {
	u := url.URL{
		Scheme:   "postgres",
		User:     url.UserPassword(c.DBUser, c.DBPassword),
		Host:     net.JoinHostPort(c.DBHost, strconv.Itoa(c.DBPort)),
		Path:     "/" + c.DBName,
		RawQuery: "sslmode=disable", // the database is on the private shared network
	}
	return u.String()
}

// RedisAddr is host:port of Redis.
func (c Config) RedisAddr() string {
	return net.JoinHostPort(c.RedisHost, strconv.Itoa(c.RedisPort))
}

type loader struct {
	getenv  func(string) string
	missing []string
	invalid []string
}

func (l *loader) required(name string) string {
	v := strings.TrimSpace(l.getenv(name))
	if v == "" {
		l.missing = append(l.missing, name)
	}
	return v
}

func (l *loader) port(name string, def int) int {
	v := strings.TrimSpace(l.getenv(name))
	if v == "" {
		return def
	}
	n, err := strconv.Atoi(v)
	if err != nil || n < 1 || n > 65535 {
		l.invalid = append(l.invalid, fmt.Sprintf("%s must be a port number from 1 to 65535", name))
		return def
	}
	return n
}

func (l *loader) level(name string) slog.Level {
	v := strings.TrimSpace(l.getenv(name))
	if v == "" {
		return slog.LevelInfo
	}
	var lvl slog.Level
	if err := lvl.UnmarshalText([]byte(v)); err != nil {
		l.invalid = append(l.invalid, name+" must be debug, info, warn or error")
		return slog.LevelInfo
	}
	return lvl
}
