import { expect, test, type Page } from "@playwright/test";

async function waitForListReady(page: Page) {
  await expect(page.getByRole("heading", { name: "Todo" })).toBeVisible();
  await expect(page.locator("#source")).not.toHaveText("...");
}

test("UserCanManageATodo", async ({ page }) => {
  const title = `e2e-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;

  await page.goto("/");
  await waitForListReady(page);

  await page.getByPlaceholder("What needs doing?").fill(title);
  await page.getByRole("button", { name: "Add" }).click();

  const row = page.locator("li", { hasText: title });
  await expect(row).toBeVisible();
  await expect(page.getByPlaceholder("What needs doing?")).toHaveValue("");
  await expect(row.locator('input[type="checkbox"]')).not.toBeChecked();

  await row.locator('input[type="checkbox"]').click();
  await expect(row.locator('input[type="checkbox"]')).toBeChecked();

  await page.reload();
  await waitForListReady(page);
  const afterReload = page.locator("li", { hasText: title });
  await expect(afterReload.locator('input[type="checkbox"]')).toBeChecked();

  await afterReload.getByRole("button", { name: "Delete" }).click();
  await expect(page.locator("li", { hasText: title })).toHaveCount(0);

  await page.reload();
  await waitForListReady(page);
  await expect(page.locator("li", { hasText: title })).toHaveCount(0);
});

test("PageReportsSourceVersionAndErrors", async ({ page }) => {
  const title = `src-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;

  await page.goto("/");
  await waitForListReady(page);

  await page.getByPlaceholder("What needs doing?").fill(title);
  await page.getByRole("button", { name: "Add" }).click();
  await expect(page.locator("li", { hasText: title })).toBeVisible();

  // Successful invalidation → the refresh after write is a miss from Postgres.
  await expect(page.locator("#source")).toHaveText("database (Postgres)");
  await expect(page.locator("#source")).toHaveClass(/miss/);

  // Next load hits Redis.
  await page.reload();
  await waitForListReady(page);
  await expect(page.locator("#source")).toHaveText("cache (Redis)");
  await expect(page.locator("#source")).toHaveClass(/hit/);

  const versionRes = await page.request.get("/version");
  expect(versionRes.ok()).toBeTruthy();
  const versionDoc = (await versionRes.json()) as {
    version: string;
    commit: string;
    replica?: string;
  };
  const shortCommit = versionDoc.commit.slice(0, 7);
  await expect(page.locator("#version")).toHaveText(
    `version ${versionDoc.version} (${shortCommit})`,
  );
  if (typeof versionDoc.replica === "string" && versionDoc.replica.length > 0) {
    await expect(page.locator("#replica")).toHaveText(versionDoc.replica);
  } else {
    await expect(page.locator("#replica")).toHaveText("...");
  }

  // Validation error must stay visible and keep the existing list.
  await page.getByPlaceholder("What needs doing?").fill("   ");
  await page.getByRole("button", { name: "Add" }).click();
  await expect(page.locator("#error")).toContainText(
    "title must be 1 to 200 characters",
  );
  await expect(page.locator("li", { hasText: title })).toBeVisible();

  await page.locator("li", { hasText: title }).getByRole("button", { name: "Delete" }).click();
  await expect(page.locator("li", { hasText: title })).toHaveCount(0);
});

test("EmptyListMessageOnlyAfterSuccessfulEmptyGet", async ({ page }) => {
  type GetOutcome = "hold" | "fail" | "empty";
  let outcome: GetOutcome = "hold";
  let releaseHold!: () => void;
  const hold = new Promise<void>((resolve) => {
    releaseHold = resolve;
  });

  await page.route(/\/api\/todos\/?$/, async (route) => {
    if (route.request().method() !== "GET") {
      await route.continue();
      return;
    }
    if (outcome === "hold") {
      await hold;
    }
    if (outcome === "fail") {
      await route.fulfill({
        status: 500,
        contentType: "application/json",
        body: JSON.stringify({ error: "internal error" }),
      });
      return;
    }
    await route.fulfill({
      status: 200,
      contentType: "application/json",
      headers: { "X-Cache": "miss" },
      body: "[]",
    });
  });

  await page.route("**/version", async (route) => {
    await route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({ version: "dev", commit: "unknown" }),
    });
  });

  await page.goto("/");
  await expect(page.getByRole("heading", { name: "Todo" })).toBeVisible();
  await expect(page.locator("#list .empty")).toHaveCount(0);

  outcome = "fail";
  releaseHold();
  await expect(page.locator("#error")).toContainText("internal error");
  await expect(page.locator("#list .empty")).toHaveCount(0);

  outcome = "empty";
  await page.reload();
  await expect(page.locator("#list .empty")).toHaveText("Nothing to do.");
});

test("AddClearsDraftOnlyWhenUnchanged", async ({ page }) => {
  const submitted = `draft-a-${Date.now()}`;
  const newer = `draft-b-${Date.now()}`;

  let releasePost!: () => void;
  const postGate = new Promise<void>((resolve) => {
    releasePost = resolve;
  });
  let signalPostStarted!: () => void;
  const postStarted = new Promise<void>((resolve) => {
    signalPostStarted = resolve;
  });

  await page.route(/\/api\/todos\/?$/, async (route) => {
    if (route.request().method() === "POST") {
      signalPostStarted();
      await postGate;
      await route.continue();
      return;
    }
    await route.continue();
  });

  await page.goto("/");
  await waitForListReady(page);

  await page.getByPlaceholder("What needs doing?").fill(submitted);
  await page.getByRole("button", { name: "Add" }).click();
  await postStarted;

  await page.getByPlaceholder("What needs doing?").fill(newer);
  releasePost();

  await expect(page.locator("li", { hasText: submitted })).toBeVisible();
  await expect(page.getByPlaceholder("What needs doing?")).toHaveValue(newer);

  await page.locator("li", { hasText: submitted }).getByRole("button", { name: "Delete" }).click();
  await expect(page.locator("li", { hasText: submitted })).toHaveCount(0);
});

test("LargeIdsAddressTheCorrectRow", async ({ page }) => {
  const idLow = "9007199254740993";
  const idHigh = "9007199254740994";
  const now = "2026-01-01T00:00:00Z";

  await page.route(/\/api\/todos\/?$/, async (route) => {
    if (route.request().method() === "GET") {
      // Wire format keeps id as a JSON number (beyond Number.MAX_SAFE_INTEGER).
      await route.fulfill({
        status: 200,
        contentType: "application/json",
        headers: { "X-Cache": "miss", "X-Served-By": "e2e-replica" },
        body:
          `[{"id":${idLow},"title":"low","done":false,"created_at":"${now}","updated_at":"${now}"},` +
          `{"id":${idHigh},"title":"high","done":false,"created_at":"${now}","updated_at":"${now}"}]`,
      });
      return;
    }
    await route.continue();
  });

  await page.route("**/version", async (route) => {
    await route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({ version: "dev", commit: "unknown", replica: "e2e-replica" }),
    });
  });

  const patchPaths: string[] = [];
  const deletePaths: string[] = [];

  page.on("request", (request) => {
    const method = request.method();
    const path = new URL(request.url()).pathname;
    if (method === "PATCH" && path.startsWith("/api/todos/")) {
      patchPaths.push(path);
    }
    if (method === "DELETE" && path.startsWith("/api/todos/")) {
      deletePaths.push(path);
    }
  });

  await page.route(`**/api/todos/${idLow}`, async (route) => {
    if (route.request().method() === "PATCH") {
      await route.fulfill({
        status: 200,
        contentType: "application/json",
        headers: { "X-Served-By": "e2e-replica" },
        body: `{"id":${idLow},"title":"low","done":true,"created_at":"${now}","updated_at":"${now}"}`,
      });
      return;
    }
    if (route.request().method() === "DELETE") {
      await route.fulfill({ status: 204, headers: { "X-Served-By": "e2e-replica" } });
      return;
    }
    await route.continue();
  });

  await page.route(`**/api/todos/${idHigh}`, async (route) => {
    if (route.request().method() === "PATCH") {
      await route.fulfill({
        status: 200,
        contentType: "application/json",
        headers: { "X-Served-By": "e2e-replica" },
        body: `{"id":${idHigh},"title":"high","done":true,"created_at":"${now}","updated_at":"${now}"}`,
      });
      return;
    }
    if (route.request().method() === "DELETE") {
      await route.fulfill({ status: 204, headers: { "X-Served-By": "e2e-replica" } });
      return;
    }
    await route.continue();
  });

  await page.goto("/");
  await waitForListReady(page);

  await page.locator("li", { hasText: "low" }).locator('input[type="checkbox"]').click();
  await page.locator("li", { hasText: "high" }).getByRole("button", { name: "Delete" }).click();

  await expect.poll(() => patchPaths).toContain(`/api/todos/${idLow}`);
  await expect.poll(() => deletePaths).toContain(`/api/todos/${idHigh}`);
  expect(patchPaths).not.toContain(`/api/todos/${idHigh}`);
  expect(deletePaths).not.toContain(`/api/todos/${idLow}`);
});
