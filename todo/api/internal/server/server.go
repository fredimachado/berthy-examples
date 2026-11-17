// Package server is the HTTP API and the small web page.
package server

import (
	"context"
	_ "embed"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log/slog"
	"net/http"
	"strconv"
	"time"

	"github.com/fredimachado/berthy-examples/todo/api/internal/todo"
)

//go:embed web/index.html
var indexHTML []byte

// Todos is what the handlers need from the todo service.
type Todos interface {
	List(ctx context.Context) ([]todo.Todo, bool, error)
	Create(ctx context.Context, title string) (todo.Todo, error)
	Update(ctx context.Context, id int64, title *string, done *bool) (todo.Todo, error)
	Delete(ctx context.Context, id int64) error
	// Health returns the names of the dependencies that did not answer.
	Health(ctx context.Context) []string
}

// BuildInfo is what /version reports.
type BuildInfo struct {
	Version string `json:"version"`
	Commit  string `json:"commit"`
	// Replica names the container that answers: its host name, which Docker sets to the short container id. Each replica
	// of a scaled service has its own, so it shows which one served a request.
	Replica string `json:"replica,omitempty"`
}

// maxBody is the largest request body: a title is at most 200 characters.
const maxBody = 8 << 10

// New returns the HTTP handler.
func New(todos Todos, build BuildInfo, log *slog.Logger) http.Handler {
	s := &server{todos: todos, build: build, log: log}
	mux := http.NewServeMux()
	mux.HandleFunc("GET /{$}", s.index)
	mux.HandleFunc("GET /health", s.health)
	mux.HandleFunc("GET /version", s.version)
	mux.HandleFunc("GET /api/todos", s.list)
	mux.HandleFunc("POST /api/todos", s.create)
	mux.HandleFunc("PATCH /api/todos/{id}", s.update)
	mux.HandleFunc("DELETE /api/todos/{id}", s.delete)
	mux.HandleFunc("/", func(w http.ResponseWriter, r *http.Request) {
		writeError(w, http.StatusNotFound, "not found")
	})
	return s.recoverer(s.logRequests(s.servedBy(mux)))
}

// servedBy sets X-Served-By to the replica name on every response, so a client
// can see which container of a scaled service answered. It sets the header
// before the handler writes anything.
func (s *server) servedBy(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if s.build.Replica != "" {
			w.Header().Set("X-Served-By", s.build.Replica)
		}
		next.ServeHTTP(w, r)
	})
}

type server struct {
	todos Todos
	build BuildInfo
	log   *slog.Logger
}

func (s *server) index(w http.ResponseWriter, r *http.Request) {
	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	w.Header().Set("Content-Security-Policy", "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'")
	w.Header().Set("X-Content-Type-Options", "nosniff")
	_, _ = w.Write(indexHTML)
}

func (s *server) version(w http.ResponseWriter, r *http.Request) {
	writeJSON(w, http.StatusOK, s.build)
}

func (s *server) health(w http.ResponseWriter, r *http.Request) {
	failed := s.todos.Health(r.Context())
	if len(failed) == 0 {
		writeJSON(w, http.StatusOK, map[string]string{"status": "ok"})
		return
	}
	writeJSON(w, http.StatusServiceUnavailable, map[string]any{"status": "unavailable", "failed": failed})
}

func (s *server) list(w http.ResponseWriter, r *http.Request) {
	todos, cached, err := s.todos.List(r.Context())
	if err != nil {
		s.fail(w, r, err)
		return
	}
	if cached {
		w.Header().Set("X-Cache", "hit")
	} else {
		w.Header().Set("X-Cache", "miss")
	}
	writeJSON(w, http.StatusOK, todos)
}

func (s *server) create(w http.ResponseWriter, r *http.Request) {
	var in struct {
		Title string `json:"title"`
	}
	if !decode(w, r, &in) {
		return
	}
	t, err := s.todos.Create(r.Context(), in.Title)
	if err != nil {
		s.fail(w, r, err)
		return
	}
	w.Header().Set("Location", "/api/todos/"+strconv.FormatInt(t.ID, 10))
	writeJSON(w, http.StatusCreated, t)
}

func (s *server) update(w http.ResponseWriter, r *http.Request) {
	id, ok := parseID(w, r)
	if !ok {
		return
	}
	var in struct {
		Title *string `json:"title"`
		Done  *bool   `json:"done"`
	}
	if !decode(w, r, &in) {
		return
	}
	t, err := s.todos.Update(r.Context(), id, in.Title, in.Done)
	if err != nil {
		s.fail(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, t)
}

func (s *server) delete(w http.ResponseWriter, r *http.Request) {
	id, ok := parseID(w, r)
	if !ok {
		return
	}
	if err := s.todos.Delete(r.Context(), id); err != nil {
		s.fail(w, r, err)
		return
	}
	w.WriteHeader(http.StatusNoContent)
}

// fail maps a service error to a status. Only validation and not-found
// messages reach the client; anything else is logged and hidden.
func (s *server) fail(w http.ResponseWriter, r *http.Request, err error) {
	var invalid *todo.InvalidError
	switch {
	case errors.As(err, &invalid):
		writeError(w, http.StatusUnprocessableEntity, invalid.Msg)
	case errors.Is(err, todo.ErrNotFound):
		writeError(w, http.StatusNotFound, "todo not found")
	default:
		s.log.ErrorContext(r.Context(), "request failed", "method", r.Method, "path", r.URL.Path, "error", err)
		writeError(w, http.StatusInternalServerError, "internal error")
	}
}

func parseID(w http.ResponseWriter, r *http.Request) (int64, bool) {
	id, err := strconv.ParseInt(r.PathValue("id"), 10, 64)
	if err != nil || id < 1 {
		writeError(w, http.StatusBadRequest, "id must be a positive number")
		return 0, false
	}
	return id, true
}

// decode reads one JSON object. It rejects unknown fields, trailing data and
// bodies over maxBody, and writes the 400 itself.
func decode(w http.ResponseWriter, r *http.Request, v any) bool {
	dec := json.NewDecoder(http.MaxBytesReader(w, r.Body, maxBody))
	dec.DisallowUnknownFields()
	err := dec.Decode(v)
	if err == nil {
		if _, extra := dec.Token(); !errors.Is(extra, io.EOF) {
			err = errors.New("unexpected data after the JSON object")
		}
	}
	if err != nil {
		var tooBig *http.MaxBytesError
		if errors.As(err, &tooBig) {
			writeError(w, http.StatusRequestEntityTooLarge, "request body too large")
			return false
		}
		writeError(w, http.StatusBadRequest, "invalid JSON: "+jsonProblem(err))
		return false
	}
	return true
}

func jsonProblem(err error) string {
	var syntax *json.SyntaxError
	var typeErr *json.UnmarshalTypeError
	switch {
	case errors.Is(err, io.EOF):
		return "the body is empty"
	case errors.As(err, &syntax):
		return "syntax error"
	case errors.As(err, &typeErr):
		return fmt.Sprintf("field %q has the wrong type", typeErr.Field)
	default:
		return err.Error()
	}
}

func writeJSON(w http.ResponseWriter, status int, v any) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(v)
}

func writeError(w http.ResponseWriter, status int, msg string) {
	writeJSON(w, status, map[string]string{"error": msg})
}

type statusWriter struct {
	http.ResponseWriter
	status int
}

func (w *statusWriter) WriteHeader(code int) {
	w.status = code
	w.ResponseWriter.WriteHeader(code)
}

func (s *server) logRequests(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		start := time.Now()
		sw := &statusWriter{ResponseWriter: w, status: http.StatusOK}
		next.ServeHTTP(sw, r)
		// The health check runs every few seconds: keep it out of the info logs.
		level := slog.LevelInfo
		if r.URL.Path == "/health" && sw.status < 500 {
			level = slog.LevelDebug
		}
		s.log.Log(r.Context(), level, "request",
			"method", r.Method, "path", r.URL.Path, "status", sw.status,
			"duration_ms", time.Since(start).Milliseconds())
	})
}

func (s *server) recoverer(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		defer func() {
			if v := recover(); v != nil {
				s.log.ErrorContext(r.Context(), "panic in handler", "panic", fmt.Sprint(v))
				writeError(w, http.StatusInternalServerError, "internal error")
			}
		}()
		next.ServeHTTP(w, r)
	})
}
