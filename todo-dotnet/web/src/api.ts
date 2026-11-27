export type TodoItem = {
  id: string;
  title: string;
  done: boolean;
  created_at: string;
  updated_at: string;
};

export type ListResult = {
  items: TodoItem[];
  source: "hit" | "miss";
  replica?: string;
};

/** Keep int64 id decimal tokens as strings before JSON.parse can coerce them. */
function quoteTodoIds(raw: string): string {
  return raw.replace(/"id"\s*:\s*(-?\d+)/g, '"id":"$1"');
}

function parseTodoItems(raw: string): TodoItem[] {
  const parsed: unknown = JSON.parse(quoteTodoIds(raw));
  if (!Array.isArray(parsed)) {
    throw new Error("expected a todo list");
  }
  return parsed.map((row) => {
    if (!row || typeof row !== "object") {
      throw new Error("expected a todo object");
    }
    const item = row as Record<string, unknown>;
    if (typeof item.id !== "string") {
      throw new Error("todo id must be a string after parse");
    }
    if (typeof item.title !== "string") {
      throw new Error("todo title must be a string");
    }
    if (typeof item.done !== "boolean") {
      throw new Error("todo done must be a boolean");
    }
    if (typeof item.created_at !== "string" || typeof item.updated_at !== "string") {
      throw new Error("todo timestamps must be strings");
    }
    return {
      id: item.id,
      title: item.title,
      done: item.done,
      created_at: item.created_at,
      updated_at: item.updated_at,
    };
  });
}

async function readError(res: Response): Promise<string> {
  const fallback = `${res.status} ${res.statusText}`;
  try {
    const body: unknown = await res.json();
    if (
      body &&
      typeof body === "object" &&
      "error" in body &&
      typeof (body as { error: unknown }).error === "string"
    ) {
      return (body as { error: string }).error;
    }
  } catch {
    // keep fallback
  }
  return fallback;
}

function replicaFrom(res: Response): string | undefined {
  return res.headers.get("X-Served-By") ?? undefined;
}

export async function fetchTodos(signal?: AbortSignal): Promise<ListResult> {
  const res = await fetch("/api/todos", { signal });
  const replica = replicaFrom(res);
  if (!res.ok) {
    throw new Error(await readError(res));
  }
  const raw = await res.text();
  const items = parseTodoItems(raw);
  const source: "hit" | "miss" =
    res.headers.get("X-Cache") === "hit" ? "hit" : "miss";
  return { items, source, replica };
}

export async function addTodo(title: string): Promise<void> {
  const res = await fetch("/api/todos", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ title }),
  });
  if (!res.ok) {
    throw new Error(await readError(res));
  }
}

export async function setDone(id: string, done: boolean): Promise<void> {
  const res = await fetch(`/api/todos/${id}`, {
    method: "PATCH",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ done }),
  });
  if (!res.ok) {
    throw new Error(await readError(res));
  }
}

export async function deleteTodo(id: string): Promise<void> {
  const res = await fetch(`/api/todos/${id}`, { method: "DELETE" });
  if (!res.ok) {
    throw new Error(await readError(res));
  }
  // 204 is bodyless; do not read a body.
}

export async function fetchVersion(): Promise<{
  version: string;
  commit: string;
  replica?: string;
}> {
  const res = await fetch("/version");
  if (!res.ok) {
    throw new Error(await readError(res));
  }
  const body: unknown = await res.json();
  if (!body || typeof body !== "object") {
    throw new Error("invalid version document");
  }
  const doc = body as Record<string, unknown>;
  if (typeof doc.version !== "string" || typeof doc.commit !== "string") {
    throw new Error("version document missing fields");
  }
  return {
    version: doc.version,
    commit: doc.commit,
    replica: typeof doc.replica === "string" ? doc.replica : undefined,
  };
}
