// Package migrate applies the embedded SQL migrations.
//
// Migrations are numbered file pairs, NNNN_name.up.sql and NNNN_name.down.sql,
// numbered 1, 2, 3 without gaps. The applied numbers live in the
// schema_migrations table. Berthy runs this as its own deploy step
// (migrate version, migrate up, migrate down-to N), never at app start.
package migrate

import (
	"context"
	"embed"
	"errors"
	"fmt"
	"io/fs"
	"regexp"
	"sort"
	"strconv"

	"github.com/jackc/pgx/v5"
)

//go:embed migrations/*.sql
var embedded embed.FS

// Migration is one numbered step with its two scripts.
type Migration struct {
	Version int
	Name    string
	Up      string
	Down    string
}

var fileName = regexp.MustCompile(`^(\d{4})_([a-z0-9_]+)\.(up|down)\.sql$`)

// Embedded returns the migrations compiled into the binary.
func Embedded() ([]Migration, error) {
	sub, err := fs.Sub(embedded, "migrations")
	if err != nil {
		return nil, err
	}
	return Load(sub)
}

// Load reads and checks the migrations in fsys: every number has an up and a
// down file, and the numbers run 1, 2, 3 without gaps.
func Load(fsys fs.FS) ([]Migration, error) {
	entries, err := fs.ReadDir(fsys, ".")
	if err != nil {
		return nil, err
	}
	byVersion := map[int]*Migration{}
	for _, e := range entries {
		m := fileName.FindStringSubmatch(e.Name())
		if m == nil {
			return nil, fmt.Errorf("migration file %q does not match NNNN_name.up.sql or NNNN_name.down.sql", e.Name())
		}
		version, _ := strconv.Atoi(m[1])
		body, err := fs.ReadFile(fsys, e.Name())
		if err != nil {
			return nil, err
		}
		mig := byVersion[version]
		if mig == nil {
			mig = &Migration{Version: version, Name: m[2]}
			byVersion[version] = mig
		} else if mig.Name != m[2] {
			return nil, fmt.Errorf("migration %04d has two names: %q and %q", version, mig.Name, m[2])
		}
		if m[3] == "up" {
			mig.Up = string(body)
		} else {
			mig.Down = string(body)
		}
	}
	list := make([]Migration, 0, len(byVersion))
	for _, m := range byVersion {
		if m.Up == "" || m.Down == "" {
			return nil, fmt.Errorf("migration %04d_%s needs both an up and a down file", m.Version, m.Name)
		}
		list = append(list, *m)
	}
	sort.Slice(list, func(i, j int) bool { return list[i].Version < list[j].Version })
	for i, m := range list {
		if m.Version != i+1 {
			return nil, fmt.Errorf("migration numbers must run 1, 2, 3 without gaps: found %04d where %04d belongs", m.Version, i+1)
		}
	}
	return list, nil
}

// Migrator applies migrations on one connection.
type Migrator struct {
	conn       *pgx.Conn
	migrations []Migration
}

// New returns a Migrator that uses conn and migrations.
func New(conn *pgx.Conn, migrations []Migration) *Migrator {
	return &Migrator{conn: conn, migrations: migrations}
}

// lockKey is the Postgres advisory lock that keeps two migrators apart.
const lockKey = 7_461_209_315

// Latest is the highest version this binary knows.
func (m *Migrator) Latest() int {
	if len(m.migrations) == 0 {
		return 0
	}
	return m.migrations[len(m.migrations)-1].Version
}

// Version returns the highest applied migration, or 0 for an empty database.
// It changes nothing.
func (m *Migrator) Version(ctx context.Context) (int, error) {
	var exists bool
	if err := m.conn.QueryRow(ctx, "SELECT to_regclass('schema_migrations') IS NOT NULL").Scan(&exists); err != nil {
		return 0, err
	}
	if !exists {
		return 0, nil
	}
	var v int
	err := m.conn.QueryRow(ctx, "SELECT COALESCE(MAX(version), 0) FROM schema_migrations").Scan(&v)
	return v, err
}

// Up applies every migration above the current version, in order, each in its
// own transaction. Running it again applies nothing. It returns the versions it applied.
func (m *Migrator) Up(ctx context.Context) ([]int, error) {
	var applied []int
	err := m.locked(ctx, func() error {
		if err := m.ensureTable(ctx); err != nil {
			return err
		}
		current, err := m.Version(ctx)
		if err != nil {
			return err
		}
		for _, mig := range m.migrations {
			if mig.Version <= current {
				continue
			}
			if err := m.apply(ctx, mig.Up, "INSERT INTO schema_migrations (version) VALUES ($1)", mig.Version); err != nil {
				return fmt.Errorf("migration %04d_%s up: %w", mig.Version, mig.Name, err)
			}
			applied = append(applied, mig.Version)
		}
		return nil
	})
	return applied, err
}

// DownTo reverts migrations, newest first, until the version is target.
// It returns the versions it reverted. A target of 0 reverts everything.
func (m *Migrator) DownTo(ctx context.Context, target int) ([]int, error) {
	if target < 0 || target > m.Latest() {
		return nil, fmt.Errorf("target %d is outside 0 to %d", target, m.Latest())
	}
	var reverted []int
	err := m.locked(ctx, func() error {
		current, err := m.Version(ctx)
		if err != nil {
			return err
		}
		if current > m.Latest() {
			return fmt.Errorf("the database is at %d, newer than this binary knows (%d): run down-to from the newer image", current, m.Latest())
		}
		for i := current - 1; i >= target; i-- {
			mig := m.migrations[i]
			if err := m.apply(ctx, mig.Down, "DELETE FROM schema_migrations WHERE version = $1", mig.Version); err != nil {
				return fmt.Errorf("migration %04d_%s down: %w", mig.Version, mig.Name, err)
			}
			reverted = append(reverted, mig.Version)
		}
		return nil
	})
	return reverted, err
}

func (m *Migrator) ensureTable(ctx context.Context) error {
	_, err := m.conn.Exec(ctx, `CREATE TABLE IF NOT EXISTS schema_migrations (
		version    INTEGER     PRIMARY KEY,
		applied_at TIMESTAMPTZ NOT NULL DEFAULT now()
	)`)
	return err
}

// apply runs a script and records the change, in one transaction.
func (m *Migrator) apply(ctx context.Context, script, record string, version int) error {
	tx, err := m.conn.Begin(ctx)
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	if _, err := tx.Exec(ctx, script); err != nil {
		return err
	}
	if _, err := tx.Exec(ctx, record, version); err != nil {
		return err
	}
	return tx.Commit(ctx)
}

func (m *Migrator) locked(ctx context.Context, fn func() error) (err error) {
	if _, err := m.conn.Exec(ctx, "SELECT pg_advisory_lock($1)", int64(lockKey)); err != nil {
		return fmt.Errorf("take the migration lock: %w", err)
	}
	defer func() {
		// Use a fresh context: ctx may be the reason we are leaving.
		if _, uerr := m.conn.Exec(context.WithoutCancel(ctx), "SELECT pg_advisory_unlock($1)", int64(lockKey)); uerr != nil {
			err = errors.Join(err, fmt.Errorf("release the migration lock: %w", uerr))
		}
	}()
	return fn()
}
