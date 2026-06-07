# syntax=docker/dockerfile:1

# =============================================================================
#  GeoBingo — single-image build.
#
#  The Go server embeds the Next.js static export (//go:embed all:frontend/out)
#  and serves both the API and the SPA, so the final image is one self-contained
#  static binary. No external services required at runtime except a Google Maps
#  API key passed via the MAPS_API_KEY environment variable.
# =============================================================================

# ---- Stage 1: build the Next.js frontend (static export -> frontend/out) -----
FROM node:22-alpine AS frontend
WORKDIR /app/frontend

# Install deps from lockfile first for better layer caching.
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci

COPY frontend/ ./
RUN npm run build

# ---- Stage 2: build the Go binary (embeds the static export) ----------------
FROM golang:1.25-alpine AS backend
WORKDIR /src

COPY go.mod go.sum ./
RUN go mod download

COPY *.go ./
# The embed directive needs frontend/out to exist at compile time.
COPY --from=frontend /app/frontend/out ./frontend/out

# modernc.org/sqlite is pure Go, so CGO can stay off -> fully static binary.
RUN CGO_ENABLED=0 GOOS=linux go build -trimpath -ldflags="-s -w" -o /out/geobingo .

# ---- Stage 3: minimal runtime ------------------------------------------------
FROM alpine:3.20
RUN apk add --no-cache ca-certificates tzdata wget \
 && adduser -D -u 10001 appuser \
 && mkdir -p /data && chown appuser:appuser /data

COPY --from=backend /out/geobingo /usr/local/bin/geobingo

USER appuser
WORKDIR /data

ENV PORT=10200
EXPOSE 10200

# geobingo.db is created in the working directory (/data).
# Mount a volume here to persist rooms across restarts.
VOLUME ["/data"]

HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3 \
  CMD wget -qO- "http://127.0.0.1:${PORT}/api/health" || exit 1

ENTRYPOINT ["/usr/local/bin/geobingo"]
