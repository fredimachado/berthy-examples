# berthy-examples

Small, real apps to try [Berthy](https://github.com/fredimachado/berthy) with:
the code, the Berthy manifests, CI and a release pipeline that publishes the
Docker image.

| Example | What it is |
| --- | --- |
| [`todo/`](todo/README.md) | A todo list: a Go API with Postgres and Redis. It is the app the Berthy [user guide](https://github.com/fredimachado/berthy/blob/main/docs/user-guide.md) deploys. Run it locally with Docker Compose, deploy it with Berthy, ship new versions from GitHub Actions. |
| [`todo-dotnet/`](todo-dotnet/README.md) | The same todo list as an Aspire-first ASP.NET Core API with Postgres, Redis, and a React SPA. Run it locally with Aspire; publish one directory that serves the API and static site. |

## How the repository is built and released

- `.github/workflows/ci.yml` runs on every push and pull request: `gofmt`,
  `go vet`, the tests (with Postgres 17 and Redis 7 as services), a Docker
  build for `linux/amd64` and `linux/arm64`, and a check of the Compose files.
- `.github/workflows/release.yml` runs on every push to `main` that is not only
  Markdown. It calls the CI workflow first. When that passes, it publishes
  `ghcr.io/fredimachado/todo-api` as `v1.0.N` (`N` is the workflow run number),
  `latest` and `sha-<commit>`, tags the commit `v1.0.N` and creates a GitHub
  release with the image digest and the `berthy deploy` commands.

To validate the Todo manifests locally, install [Berthy v0.8.0 or later](https://github.com/fredimachado/berthy/releases)
and run `berthy config validate -e dev --manifest-dir todo/berthy`, then repeat
with `-e prod`.

For the user guide, see the
[Berthy repository](https://github.com/fredimachado/berthy/blob/main/docs/user-guide.md).

## License

MIT. See [LICENSE](LICENSE).
