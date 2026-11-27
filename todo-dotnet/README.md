# The todo-dotnet app

A .NET todo list beside the Go [`todo/`](../todo/README.md) example. Run it
locally with [Aspire](https://aspire.dev): Postgres 17, Redis 7, an ASP.NET Core
API, and a React page through Vite. Publish one directory that contains the API
and the built static site. This example is Aspire-first; Berthy manifests for it
are not part of this walkthrough (the Berthy
[user guide](https://github.com/fredimachado/berthy/blob/main/docs/user-guide.md)
still deploys the Go `todo/` app).

```
todo-dotnet/
  Api/                 ASP.NET Core 11 API, EF Core migrations, published wwwroot
  AppHost/             Scoped C# Aspire AppHost (Postgres, Redis, migrate, api, web)
  Tests/               xUnit contract and integration tests
  web/                 React + Vite SPA and Playwright suite
  scripts/             smoke.ps1 and verify.ps1 drivers
```

## Prerequisites

- .NET SDK 11
- Aspire CLI 17 (`aspire --version`)
- Node.js 24+ and npm (for the SPA and Playwright)
- Docker (Postgres and Redis containers)
- PowerShell 7 (`pwsh`)

## The API

The API reads Aspire-injected connection strings and optional build identity:

| Setting | Meaning | Default |
| --- | --- | --- |
| `ConnectionStrings:tododb` | Postgres (Npgsql) | required for list/CRUD and migrate |
| `ConnectionStrings:redis` | Redis | required for caching; CRUD still works if Redis is down |
| `BuildVersion` | Reported by `/version` | `dev` |
| `BuildCommit` | Reported by `/version` | `unknown` |
| `HOSTNAME` | Replica identity (`replica` field and `X-Served-By`) | machine hostname; empty omits both |

`migrate` names a missing `ConnectionStrings:tododb` in its failure message (the
value is never logged). The running API reports missing or unhealthy dependencies
via `GET /health`.

| Request | What it does |
| --- | --- |
| `GET /` | The React page (Vite in Aspire; Kestrel `wwwroot` after publish). On the published Kestrel endpoint, a configured replica adds `X-Served-By` (Aspire's Vite server does not). |
| `GET /health` | `200 {"status":"ok"}` when Postgres and Redis both answer within 2 seconds, else `503` with the failed dependency names. |
| `GET /version` | Build version, commit, and optional replica. |
| `GET /api/todos` | The list from Redis key `todo:list` (60s TTL) or Postgres on a miss. `X-Cache` is `hit` or `miss`. |
| `POST /api/todos` | `{"title":"..."}` creates a todo. Title length is 1–200 Unicode scalars after trim. |
| `PATCH /api/todos/{id}` | Partial update of `title` and/or `done`. |
| `DELETE /api/todos/{id}` | Deletes one todo. |

Every committed write attempts to delete `todo:list`. If that `DEL` fails while
the old key survives, the write still succeeds and a stale `X-Cache: hit` is
allowed until the key's original TTL expires. Unknown routes return
`404 {"error":"not found"}` with `Content-Type: application/json` (no HTML SPA
fallback). The published executable also accepts `migrate` to apply EF migrations
and exit.

## Run it with Aspire

From the repository root:

```powershell
aspire start --apphost todo-dotnet/AppHost/apphost.cs --isolated --non-interactive
aspire wait api --status healthy --apphost todo-dotnet/AppHost/apphost.cs
aspire wait web --status healthy --apphost todo-dotnet/AppHost/apphost.cs
aspire describe web --format Json --apphost todo-dotnet/AppHost/apphost.cs
```

Open the `web` HTTP URL from `aspire describe`. Stop only the AppHost you
started (`aspire stop --pid <appHostPid> --apphost todo-dotnet/AppHost/apphost.cs`).
Aspire data for an isolated run is transient to that process; stopping the host
tears down its containers.

HTTP smoke against the API endpoint:

```powershell
pwsh -NoProfile -File todo-dotnet/scripts/verify.ps1 -Target Api
```

## Browser checks

Install the locked frontend toolchain and Chromium once per machine:

```powershell
npm --prefix todo-dotnet/web ci
Push-Location todo-dotnet/web
npx --no-install playwright install --with-deps chromium
Pop-Location
```

Against the Aspire Vite origin (AppHost must be stopped afterward by the driver):

```powershell
pwsh -NoProfile -File todo-dotnet/scripts/verify.ps1 -Target AspireWeb
```

Publish the API with the Vite assets in `wwwroot`, then run the same Playwright
suite against Kestrel (Vite is not running):

```powershell
$todoPublishDir = Join-Path ([System.IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString())
dotnet publish todo-dotnet/Api/Todo.Api.csproj --configuration Release --output $todoPublishDir
pwsh -NoProfile -File todo-dotnet/scripts/verify.ps1 -Target PublishedWeb -PublishDir $todoPublishDir
```

`PublishedWeb` starts its own Postgres and Redis containers, runs
`dotnet Todo.Api.dll migrate`, starts the published API on `http://127.0.0.1:0`,
sets `TODO_BASE_URL` for Playwright, checks the published `/` headers and assets,
and releases only the processes and containers it owns.

## Tests

Node-free .NET tests (Testcontainers for Postgres and Redis):

```powershell
dotnet test todo-dotnet/Tests/Todo.Tests.csproj --configuration Release
```

Ordinary `dotnet build` / `dotnet test` do not require Node or browsers. The
frontend install and Vite build run at `dotnet publish` time (or via the npm
commands above).

## Releases and the Go image

Pushes to `main` that change non-Markdown files still run the repository
[release workflow](../.github/workflows/release.yml), which publishes the Go
`todo-api` image. A .NET-only change can therefore trigger a Go image release.
Restricting that trigger is a separate decision. The
[todo-dotnet workflow](../.github/workflows/todo-dotnet.yml) covers this example
on its own paths.
