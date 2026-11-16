# The todo app

A small todo list you can run on your machine and deploy with
[Berthy](https://github.com/fredimachado/berthy). It is the app the Berthy
[user guide](https://github.com/fredimachado/berthy/blob/main/docs/user-guide.md)
describes: three Berthy apps on one Docker Swarm server per environment.

| Berthy app | Kind | What it is |
| --- | --- | --- |
| `api` | `image` | The Go API and web page, from the private image `ghcr.io/fredimachado/todo-api`. |
| `postgres` | `compose`, `stateful` | PostgreSQL 17, reachable as `todo-db`. |
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
| `GET /` | A small web page to add, tick and delete todos. It shows the version and whether the list came from the cache or from the database. |
| `GET /health` | `200 {"status":"ok"}` when Postgres and Redis both answer within 2 seconds, else `503` with the name of the one that failed. |
| `GET /version` | The build version and commit. |
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
explains every step. This is the short path for this app. It uses the `dev`
environment; for `prod`, change `-e dev` to `-e prod`.

**You need** a Linux server with systemd, a private address on it (this README
uses `10.0.0.5`), an account that is root or has `sudo` without a password, and
the `berthy` CLI on your machine.

1. **Make a token that can pull the image.** In GitHub, make a personal access
   token (classic) with only the `read:packages` scope. A fine-grained token
   cannot read packages. Berthy uses it to pull the private image. Keep it out
   of your shell history.

2. **Set up the server.** Work from the manifests' folder, so Berthy finds them:

   ```bash
   cd todo/berthy
   mkdir -p ~/.config/berthy
   berthy setup -e dev --host ops@10.0.0.5 --listen 10.0.0.5:7710 \
     --config ~/.config/berthy/config.json
   export CFG=~/.config/berthy/config.json
   export BERTHY_DEV_TOKEN=<the admin token setup printed>
   ```

   Save the admin token and the server's encryption key as the setup output
   says. `berthy setup --dry-run` shows what it would do first.

3. **Set the secrets.** Each value is read from standard input and is never
   printed:

   ```bash
   read -rs GHCR_PAT         # paste the token from step 1, then Enter
   printf '%s' "$GHCR_PAT" | berthy secret set GHCR_TOKEN --registry ghcr.io -e dev --config $CFG
   unset GHCR_PAT
   openssl rand -hex 24 | berthy secret set POSTGRES_PASSWORD -e dev --config $CFG
   openssl rand -hex 24 | berthy secret set REDIS_PASSWORD    -e dev --config $CFG
   ```

   `GHCR_TOKEN` must carry `--registry ghcr.io`; the other two must not.

4. **Check the manifests**, with no server needed:

   ```bash
   berthy config validate -e dev
   berthy config validate -e prod
   ```

   Both print `manifest valid`.

5. **Deploy Redis, then Postgres, then the API.** The API needs both.

   ```bash
   berthy deploy redis    -e dev --config $CFG
   berthy deploy postgres -e dev --config $CFG
   berthy deploy api      -e dev --config $CFG --version v1.0.1
   ```

   Use the newest version from the [releases](https://github.com/fredimachado/berthy-examples/releases)
   page instead of `v1.0.1`. Add `--dry-run` to any deploy to see its plan.
   The API deploy pulls the image, runs `migrate version` and `migrate up`,
   starts the new containers beside the old ones, waits for the health check,
   and then runs the `warm-cache` hook.

   **Prod is protected**, so it takes the image digest, not a tag. Copy the
   command from the release notes:

   ```bash
   berthy deploy api -e prod --config $CFG --version sha256:<digest from the release notes>
   ```

6. **Look at it.**

   ```bash
   berthy ps -e dev --config $CFG
   berthy logs api -e dev --config $CFG --follow
   berthy exec api -e dev --config $CFG -- /app/bin/migrate version
   berthy ui -e dev --config $CFG
   ```

   `berthy ui` is the full-screen live view. The `cleanup` job shows in
   `berthy jobs`, and `berthy job run dump-db` dumps the database.

7. **Open the app.** The API has no public address. It is on the Docker network
   `todo` as `todo-api-web:8080`. To use it from a browser, either publish it
   with Berthy's managed ingress (a Cloudflare Tunnel: uncomment `ingress` and
   `hostnames` in `berthy/berthy.prod.yml` and follow step 7 of the guide), or
   look at it through an SSH tunnel with a throwaway container on the server:

   ```bash
   ssh -L 8080:127.0.0.1:8080 ops@10.0.0.5
   # on the server:
   docker run -d --name todo-peek --network todo -p 127.0.0.1:8080:8080 \
     alpine/socat TCP-LISTEN:8080,fork,reachable TCP:todo-api-web:8080
   # then open http://localhost:8080 on your machine; when done:
   docker rm -f todo-peek
   ```

The prod overlay leaves out what needs services of your own: backups to
S3-compatible storage, Cloudflare ingress, probes, Telegram alerts and
telemetry. `berthy/berthy.prod.yml` says which blocks and where to find them.
The guide's steps 7 to 10 add them.
