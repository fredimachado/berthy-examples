import { defineConfig, devices } from "@playwright/test";

const rawBaseUrl = process.env.TODO_BASE_URL;
if (!rawBaseUrl || !/^https?:\/\//i.test(rawBaseUrl)) {
  throw new Error(
    "TODO_BASE_URL must be set to an absolute http(s) origin (no implicit localhost).",
  );
}

let baseURL: string;
try {
  const parsed = new URL(rawBaseUrl);
  if (parsed.protocol !== "http:" && parsed.protocol !== "https:") {
    throw new Error("unsupported protocol");
  }
  baseURL = parsed.origin;
} catch {
  throw new Error(
    `TODO_BASE_URL is not a valid absolute http(s) origin: ${rawBaseUrl}`,
  );
}

export default defineConfig({
  testDir: "./e2e",
  fullyParallel: false,
  forbidOnly: !!process.env.CI,
  retries: 0,
  workers: 1,
  reporter: "list",
  use: {
    baseURL,
    trace: "on-first-retry",
  },
  projects: [
    {
      name: "chromium",
      use: { ...devices["Desktop Chrome"] },
    },
  ],
});
