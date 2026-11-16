package migrate

import (
	"strings"
	"testing"
	"testing/fstest"
)

func pair(n, name string) map[string]*fstest.MapFile {
	return map[string]*fstest.MapFile{
		n + "_" + name + ".up.sql":   {Data: []byte("SELECT 1;")},
		n + "_" + name + ".down.sql": {Data: []byte("SELECT 2;")},
	}
}

func merge(parts ...map[string]*fstest.MapFile) fstest.MapFS {
	out := fstest.MapFS{}
	for _, p := range parts {
		for k, v := range p {
			out[k] = v
		}
	}
	return out
}

func TestEmbeddedMigrationsAreValid(t *testing.T) {
	list, err := Embedded()
	if err != nil {
		t.Fatal(err)
	}
	if len(list) < 2 {
		t.Fatalf("expected at least two migrations, got %d", len(list))
	}
	for i, m := range list {
		if m.Version != i+1 || strings.TrimSpace(m.Up) == "" || strings.TrimSpace(m.Down) == "" {
			t.Errorf("migration %d is malformed: %+v", i, m)
		}
	}
}

func TestLoadOrdersByNumber(t *testing.T) {
	list, err := Load(merge(pair("0002", "b"), pair("0001", "a"), pair("0003", "c")))
	if err != nil {
		t.Fatal(err)
	}
	if len(list) != 3 || list[0].Name != "a" || list[1].Name != "b" || list[2].Name != "c" {
		t.Fatalf("wrong order: %+v", list)
	}
}

func TestLoadRejectsBadSets(t *testing.T) {
	missingDown := fstest.MapFS{"0001_a.up.sql": {Data: []byte("SELECT 1;")}}
	twoNames := merge(pair("0001", "a"), map[string]*fstest.MapFile{"0001_b.up.sql": {Data: []byte("x")}})
	cases := map[string]struct {
		fs   fstest.MapFS
		want string
	}{
		"missing down": {missingDown, "both an up and a down"},
		"gap":          {merge(pair("0001", "a"), pair("0003", "c")), "without gaps"},
		"starts at 2":  {pair("0002", "a"), "without gaps"},
		"two names":    {twoNames, "two names"},
		"bad name":     {fstest.MapFS{"notes.txt": {Data: []byte("hi")}}, "does not match"},
	}
	for name, tc := range cases {
		t.Run(name, func(t *testing.T) {
			_, err := Load(tc.fs)
			if err == nil || !strings.Contains(err.Error(), tc.want) {
				t.Fatalf("error = %v, want one containing %q", err, tc.want)
			}
		})
	}
}

func TestLatest(t *testing.T) {
	if got := (&Migrator{}).Latest(); got != 0 {
		t.Fatalf("empty latest = %d", got)
	}
	list, _ := Load(merge(pair("0001", "a"), pair("0002", "b")))
	if got := (&Migrator{migrations: list}).Latest(); got != 2 {
		t.Fatalf("latest = %d", got)
	}
}
