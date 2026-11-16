# Build on the machine's own architecture and cross-compile, so a multi-arch
# image needs no emulation.
FROM --platform=$BUILDPLATFORM golang:1.25.8-alpine AS build
WORKDIR /src

COPY go.mod go.sum ./
RUN go mod download

COPY . .

ARG TARGETOS=linux
ARG TARGETARCH
ARG VERSION=dev
ARG COMMIT=unknown
ENV CGO_ENABLED=0
RUN mkdir -p /out/app/bin \
 && GOOS=$TARGETOS GOARCH=$TARGETARCH go build -trimpath \
      -ldflags "-s -w \
        -X github.com/fredimachado/berthy-examples/todo/api/internal/version.Version=${VERSION} \
        -X github.com/fredimachado/berthy-examples/todo/api/internal/version.Commit=${COMMIT}" \
      -o /out/app/bin/todo-api ./cmd/todo-api \
 # One binary, several names. The distroless image has no shell, so the links are made here.
 && for name in migrate warm-cache cleanup healthcheck; do ln -s todo-api "/out/app/bin/$name"; done

# Distroless: no shell, no package manager, no curl. It runs as user 65532.
FROM gcr.io/distroless/static-debian12:nonroot

# This label links the GitHub Container Registry package to the repository.
LABEL org.opencontainers.image.source="https://github.com/fredimachado/berthy-examples" \
      org.opencontainers.image.description="Todo API for trying Berthy: Go, Postgres and Redis"

COPY --from=build /out/app /app
WORKDIR /app
ENV PORT=8080
EXPOSE 8080

# Berthy runs its own commands (migrate, warm-cache, cleanup) by replacing CMD,
# so there is no ENTRYPOINT: an entrypoint would receive them as arguments.
CMD ["/app/bin/todo-api"]

HEALTHCHECK --interval=15s --timeout=5s --start-period=10s --retries=3 \
  CMD ["/app/bin/healthcheck"]
