import { useEffect, useRef, useState, type FormEvent } from "react";
import {
  addTodo,
  deleteTodo,
  fetchTodos,
  fetchVersion,
  setDone,
  type ListResult,
  type TodoItem,
} from "./api";

type ListState =
  | { status: "loading" }
  | { status: "error"; error: string; items: TodoItem[]; source?: ListResult["source"]; replica?: string }
  | { status: "ok"; items: TodoItem[]; source: ListResult["source"]; replica?: string };

export default function App() {
  const [list, setList] = useState<ListState>({ status: "loading" });
  const [error, setError] = useState("");
  const [title, setTitle] = useState("");
  const [versionLabel, setVersionLabel] = useState("version ...");
  const [replicaLabel, setReplicaLabel] = useState("...");
  const loadGen = useRef(0);
  const abortRef = useRef<AbortController | null>(null);

  async function refresh() {
    abortRef.current?.abort();
    const controller = new AbortController();
    abortRef.current = controller;
    const gen = ++loadGen.current;

    try {
      const result = await fetchTodos(controller.signal);
      if (gen !== loadGen.current) {
        return;
      }
      if (result.replica) {
        setReplicaLabel(result.replica);
      }
      setList({
        status: "ok",
        items: result.items,
        source: result.source,
        replica: result.replica,
      });
    } catch (e) {
      if (controller.signal.aborted || gen !== loadGen.current) {
        return;
      }
      const message = e instanceof Error ? e.message : String(e);
      setList((prev) => ({
        status: "error",
        error: message,
        items: prev.status === "loading" ? [] : prev.items,
        source: prev.status === "loading" ? undefined : prev.source,
        replica: prev.status === "loading" ? undefined : prev.replica,
      }));
      setError(message);
    }
  }

  async function act(fn: () => Promise<void>) {
    setError("");
    try {
      await fn();
      await refresh();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  }

  useEffect(() => {
    void refresh();
    void fetchVersion()
      .then((v) => {
        setVersionLabel(`version ${v.version} (${v.commit.slice(0, 7)})`);
        if (v.replica) {
          setReplicaLabel(v.replica);
        }
      })
      .catch(() => {
        // version is best-effort chrome, not a hard failure
      });
    return () => {
      abortRef.current?.abort();
    };
  }, []);

  function onSubmit(ev: FormEvent) {
    ev.preventDefault();
    const value = title;
    void act(async () => {
      await addTodo(value);
      // Clear only if the draft is still the submitted title (do not wipe a newer draft).
      setTitle((current) => (current === value ? "" : current));
    });
  }

  const items = list.status === "loading" ? [] : list.items;
  const source = list.status === "ok" ? list.source : list.status === "error" ? list.source : undefined;
  const sourceLabel =
    source === "hit"
      ? "cache (Redis)"
      : source === "miss"
        ? "database (Postgres)"
        : "...";
  const sourceClass =
    source === "hit" ? "badge hit" : source === "miss" ? "badge miss" : "badge";

  return (
    <>
      <h1>Todo</h1>
      <form id="add" onSubmit={onSubmit}>
        <input
          id="title"
          type="text"
          maxLength={200}
          placeholder="What needs doing?"
          autoComplete="off"
          required
          value={title}
          onChange={(e) => setTitle(e.target.value)}
        />
        <button type="submit">Add</button>
      </form>
      <div id="error" role="alert">
        {error}
      </div>
      <ul id="list">
        {list.status === "ok" && list.items.length === 0 ? (
          <li className="empty">Nothing to do.</li>
        ) : null}
        {(list.status === "ok" || list.status === "error") &&
          items.map((t) => (
            <li key={t.id} className={t.done ? "done" : undefined}>
              <input
                type="checkbox"
                checked={t.done}
                onChange={(e) => {
                  const done = e.target.checked;
                  void act(() => setDone(t.id, done));
                }}
              />
              <span>{t.title}</span>
              <button
                type="button"
                className="link"
                onClick={() => {
                  void act(() => deleteTodo(t.id));
                }}
              >
                Delete
              </button>
            </li>
          ))}
      </ul>
      <div className="meta">
        <span>
          List from: <span id="source" className={sourceClass}>{sourceLabel}</span>
        </span>
        <span id="version">{versionLabel}</span>
        <span>
          served by <span id="replica" className="badge">{replicaLabel}</span>
        </span>
      </div>
    </>
  );
}
