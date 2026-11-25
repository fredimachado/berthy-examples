# The todo app

A small todo list you can run on your machine and deploy with
[Berthy](https://github.com/fredimachado/berthy). It is the app the Berthy
[user guide](https://github.com/fredimachado/berthy/blob/main/docs/user-guide.md)
describes: one project with three Berthy apps. Dev and prod can share one
agent; each environment has its own overlay network (`todo-dev`, `todo-prod`).

This walkthrough uses [Berthy v0.8.0](https://github.com/fredimachado/berthy/releases/tag/v0.8.0).
The manifests need v0.8.0 or later because they name the server.

| Berthy app | Kind | What it is |
| --- | --- | --- |
| `api` | `image` | The Go API and web page, from the private image `ghcr.io/fredimachado/todo-api`. Swarm service `todo-<environment>-api-web`, network alias `api-web`. |
| `postgres` | `compose`, `stateful` | PostgreSQL 17, reachable as `todo-db` on the environment network. |
| `redis` | `compose` | Redis 7, reachable as `todo-redis`. It caches the todo list. |

```
todo/
  api/                Go module: the API, its migrations, tests and Dockerfile
  berthy/             the Berthy manifests: berthy.yml, berthy.dev.yml, berthy.prod.yml, deploy/*.yml
  compose.dev.yml     run everything on your machine with Docker Compose
```

## The API

One Go binary (standard library, `pgx` and `go-redis`), no framework. It reads
its settings from the environment, which is how the Berthy manifest passes them:

| Variable | Meaning | Default |
| --- | --- | --- |
| `DB_HOST`, `DB_NAME`, `DB_USER`, `POSTGRES_PASSWORD` | Postgres | required |
| `DB_PORT` | Postgres port | `5432` |
| `REDIS_HOST`, `REDIS_PASSWORD` | Redis | required |
| `REDIS_PORT` | Redis port | `6379` |
| `PORT` | The API's port | `8080` |
| `LOG_LEVEL` | `debug`, `info`, `warn` or `error` | `info` |

A missing variable stops the process at start with a message that names it. A
password never appears in a message or a log.

| Request | What it does |
| --- | --- |
| `GET /` | A small web page to add, tick and delete todos. It shows the version, whether the list came from the cache or from the database, and which replica answered. |
| `GET /health` | `200 {"status":"ok"}` when Postgres and Redis both answer within 2 seconds, else `503` with the name of the one that failed. |
| `GET /version` | The build version, the commit and the replica (the container that answered). |
| `GET /api/todos` | The list. It is served from Redis (key `todo:list`, 60 seconds) and filled from Postgres on a miss. The `X-Cache` header says `hit` or `miss`. |
| `POST /api/todos` | `{"title": "..."}` creates a todo. The title has 1 to 200 characters. |
| `PATCH /api/todos/{id}` | `{"title": "...", "done": true}`, either field, changes a todo. |
| `DELETE /api/todos/{id}` | Deletes a todo. |

Every write drops the cached list. Errors are JSON: `{"error": "..."}`.

The image holds one binary, `/app/bin/todo-api`, and four more names linked to
it. These are the commands the Berthy manifest runs:

| Command | Used for |
| --- | --- |
| `/app/bin/todo-api` | The server. This is the image's default command. |
| `/app/bin/migrate version`, `up`, `down-to <n>` | The Berthy `migrations` step. `version` prints the number of the last applied migration. The SQL files are in `api/internal/migrate/migrations`. |
| `/app/bin/warm-cache` | The `post_deploy` hook: fills the Redis cache. |
| `/app/bin/cleanup --done-older-than 30d` | The scheduled `cleanup` job: deletes done todos not changed for 30 days. |
| `/app/bin/healthcheck` | The container health check. The image has no `curl`. |
| `/app/bin/todo-api version` | Prints the build version and commit. |

## Run it on your machine

You need Docker with Compose. From the root of this repository:

```bash
docker compose -f todo/compose.dev.yml up --build
```

This builds the image, starts Postgres and Redis, runs the migrations in a
one-off `migrate` service, then starts the API on <http://localhost:8080>. Open
it in a browser, or:

```bash
curl -s localhost:8080/health
curl -s -X POST localhost:8080/api/todos -d '{"title":"buy milk"}'
curl -si localhost:8080/api/todos        # X-Cache: miss, then hit
curl -s -X PATCH localhost:8080/api/todos/1 -d '{"done":true}'
curl -s -X DELETE localhost:8080/api/todos/1
```

Stop it and delete its data with `docker compose -f todo/compose.dev.yml down -v`.
Set `API_PORT` to use another port than 8080. The passwords in
`compose.dev.yml` are for your laptop only.

Run the tests from `todo/api`:

```bash
go test -race ./...                       # unit tests, no services needed
TEST_POSTGRES_URL='postgres://postgres:pw@localhost:5432/postgres?sslmode=disable' \
TEST_REDIS_ADDR=localhost:6379 \
  go test -race -tags integration ./...   # also the tests that use real Postgres and Redis
```

The integration tests drop and recreate the todo tables: use a throwaway database.

## Versions and releases

Every push to `main` that changes something other than Markdown runs the tests
and, when they pass, publishes a release named **`v1.0.N`**, where `N` is the
number of that run of the Release workflow (`v1.0.1`, `v1.0.2`, ...). Nobody
tags by hand. The workflow:

- builds the image for `linux/amd64` and `linux/arm64` and pushes
  `ghcr.io/fredimachado/todo-api` with the tags `v1.0.N`, `latest` and
  `sha-<short commit>`;
- stamps `v1.0.N` and the commit into the binary, so `/version` and
  `todo-api version` report it;
- creates the git tag `v1.0.N` on the commit it built and a GitHub release
  with the same name. The release notes carry the **image digest** and the two
  deploy commands for that version.

One release is published at a time, in push order. The run number counts every
run of the workflow, so a run that failed leaves a gap in the versions.
The image is private. The package is linked to this repository, so it belongs
to it in GitHub's package list.

## Deploy it with Berthy

The [Berthy user guide](https://github.com/fredimachado/berthy/blob/main/docs/user-guide.md)
explains every step. This is the short path for the `dev` environment.
`BERTHY_ENV` sets the default for the commands below. Pass `-e prod` to
target prod for one command.

**You need** a Linux server with systemd, a private address on it (this README
uses `10.0.0.5`), an account that is root or has `sudo` without a password, and
the `berthy` CLI on your machine.

1. **Make a token that can pull the image.** In GitHub, make a personal access
   token (classic) with only the `read:packages` scope. A fine-grained token
   cannot read packages. Berthy uses it to pull the private image. Keep it out
   of your shell history.

2. **Set up the server.** First edit `servers.web1` in `berthy.dev.yml` and
   `berthy.prod.yml` to your server (both environments share one server in this
   walkthrough). Then run setup from the manifests' folder so Berthy finds the
   project and can ask for secrets.

   ```bash
   cd todo/berthy
   export BERTHY_ENV=dev
   berthy setup
   ```

   Add `--dry-run` to preview the setup. Berthy writes the CLI configuration and
   saves the admin token in your local credential store. If setup cannot confirm
   the save, keep the printed token privately and set `BERTHY_TODO_DEV_TOKEN` in
   your shell.

   To add prod on the same server, run `berthy setup -e prod`. It creates the
   `todo-prod` environment. The fallback token variable is
   `BERTHY_TODO_PROD_TOKEN`. `--host USER@SERVER --listen ADDRESS:7710` still
   works without a `servers` entry.

   After each setup run, check `cli_key` for the saved file under `~/.berthy/keys`.
   Setup names it `berthy.todo.dev.<key-id>.key` or `berthy.todo.prod.<key-id>.key`.
   If `cli_key` says `not saved`, follow the first instruction under `next steps`.
   See the [key backup instructions](https://github.com/fredimachado/berthy/blob/main/docs/user-guide.md#save-three-things).

3. **Finish setting the secrets.** On a fresh environment, setup generates the
   Postgres and Redis passwords and asks for the GitHub token from step 1 at a
   hidden prompt. To finish later, run `berthy secret set` in a terminal.
   `berthy secret generate` generates only the declared passwords. Berthy keeps
   stored passwords and generates a missing one only before an app that uses it
   has been deployed. For an existing database or cache, enter its current password.

4. **Check the manifests**, with no server needed:

   ```bash
   berthy config validate -e dev
   berthy config validate -e prod
   ```

   Both print `manifest valid`.

5. **Deploy the apps.** One command is enough; `depends_on` in `berthy.yml`
   makes Berthy deploy postgres, then redis, then the api:

   ```bash
   berthy deploy --version v1.0.1
   ```

   Or one at a time:

   ```bash
   berthy deploy redis
   berthy deploy postgres
   berthy deploy api --version v1.0.1
   ```

   Use the newest version from the [releases](https://github.com/fredimachado/berthy-examples/releases)
   page instead of `v1.0.1`. Add `--dry-run` to any deploy to see its plan.
   The API deploy pulls the image, runs `migrate version` and `migrate up`,
   starts the new containers beside the old ones, waits for the health check,
   and then runs the `warm-cache` hook.

   **Prod is protected.** The agent admits only a digest. At a terminal with a
   human admin token and a clean committed checkout, list the tags, preview,
   then confirm (default no):

   ```bash
   berthy versions api -e prod
   berthy deploy api -e prod --version v1.0.1 --dry-run
   berthy deploy api -e prod --version v1.0.1
   ```

   The CLI resolves the tag once, shows the full digest and plan, and asks
   before admission. Commit any local changes to the manifests first; a dirty
   checkout is refused.

   In CI and scripts, give the digest yourself. Copy it from the release notes
   (still from `todo/berthy`):

   ```bash
   berthy deploy api -e prod --version sha256:<digest from the release notes>
   ```

   That explicit digest command also works against a prod agent still on
   v0.5.0. Tag listing and the interactive tag preview need a v0.6.0 agent.

   When prod should run what already works in `dev`:

   ```bash
   berthy promote api --from dev -e prod
   ```

6. **Look at it.**

   ```bash
   berthy ps
   berthy logs api --follow
   berthy exec api -- /app/bin/migrate version
   berthy ui
   ```

   `berthy ui` is the full-screen live view. The `cleanup` job shows in
   `berthy jobs`, and `berthy job run dump-db` dumps the database.

7. **Open the app.** The API has no public address. On the `todo-dev` network
   it answers as `api-web:8080` (Swarm service `todo-dev-api-web`). To use it
   from a browser, either publish it with Berthy's managed ingress (a
   Cloudflare Tunnel: uncomment `ingress` and `hostnames` in
   `berthy/berthy.prod.yml` and follow step 7 of the guide), or look at it
   through an SSH tunnel with a throwaway container on the server. When the
   selected manifest enables managed ingress, setup also asks for the
   Cloudflare API token.

   To use the SSH tunnel:

   ```bash
   ssh -L 8080:127.0.0.1:8080 ops@10.0.0.5
   # on the server:
   docker run -d --name todo-peek --network todo-dev -p 127.0.0.1:8080:8080 \
     alpine/socat TCP-LISTEN:8080,fork,reachable TCP:api-web:8080
   # then open http://localhost:8080 on your machine; when done:
   docker rm -f todo-peek
   ```

8. **Destroy.** Confirm with the hosted environment name. Stateful apps need
   `--allow-recreate`:

   ```bash
   berthy destroy --project todo -e prod --confirm todo/prod --allow-recreate --dry-run
   ```

The prod overlay leaves out what needs services of your own: backups to
S3-compatible storage, Cloudflare ingress, probes, Telegram alerts and
telemetry. `berthy/berthy.prod.yml` says which blocks and where to find them.
The guide's steps 7 to 10 add them.

## See which replica answered

When the API runs more than one replica, each response says which container
served it. The page's footer shows it as **served by** and updates it on every
request. Every response also carries the name in the `X-Served-By` header, and
`/version` reports it as `replica`. The name is the container's short id.

To see the spread, scale the API to three replicas and ask for `/version` ten
times. Set `BERTHY_ENV=dev` as in the deploy steps and replace
`https://todo.example.com` with your app's public address.

```bash
berthy scale api --replicas 3 --role web
for i in $(seq 10); do curl -s https://todo.example.com/version; echo; done
```

`scale` changes the running replicas only. The manifest stays the source of
truth, and a deploy applies its `replicas`, so change the manifest too.

Through the Cloudflare tunnel, the connector reuses connections to the service,
and Swarm balances per connection. One browser tab may keep landing on the same
replica. Separate `curl` runs, each a new connection, or several clients show
the spread.
