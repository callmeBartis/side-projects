# 🗺️ GeoBingo

A real-time multiplayer "find it in Street View" bingo game. The host writes a
list of things to find (e.g. _"red door, graffiti, a dog, a fountain"_), shares
a room link, and everyone races to locate each term anywhere on Google Street
View. When the round ends, players vote on each other's finds and a ranking is
shown.

## Architecture

GeoBingo ships as a **single self-contained binary / Docker image**:

- **Backend** — Go (`main.go`). Serves the JSON API, holds live game state, and
  pushes real-time updates over **Server-Sent Events (SSE)**. Persists rooms in
  **SQLite** (via the pure-Go `modernc.org/sqlite` driver, so no CGO).
- **Frontend** — Next.js (static export) + Tailwind, talking to Google Maps /
  Street View. It is **embedded into the Go binary** at compile time
  (`//go:embed all:frontend/out`), so the server serves both the API and the
  SPA from one process.

```
browser ──HTTP/SSE──> Go server ──> SQLite (geobingo.db)
                          │
                          └── serves embedded Next.js static export
```

## Prerequisites

A **Google Maps API key** with:

- _Maps JavaScript API_ and _Geocoding API_ enabled,
- billing active,
- and (strongly recommended) an **HTTP-referrer restriction** to your domain.

## Quick start (Docker — recommended)

```bash
# from the geobingo/ folder
docker build -t geobingo .

# run (DB persisted in the named volume "geobingo-data")
docker run --rm -p 10200:10200 \
  -e MAPS_API_KEY=your_google_maps_api_key \
  -v geobingo-data:/data \
  geobingo
```

Then open <http://localhost:10200>.

A published image is also available from CI:

```bash
docker run --rm -p 10200:10200 -e MAPS_API_KEY=your_key \
  ghcr.io/callmebartis/geobingo:latest
```

## Local development

The frontend and backend run as one process. For day-to-day work you can either
rebuild the embedded export, or run the Next.js dev server alongside the API.

**Option A — full build (matches production):**

```bash
cd frontend && npm install && npm run build && cd ..
go run .          # serves API + built SPA on :10200
```

**Option B — hot-reloading frontend:**

```bash
# terminal 1 — API
go run .
# terminal 2 — Next.js dev server (proxy /api to :10200 as needed)
cd frontend && npm install && npm run dev
```

Create your env file first:

```bash
cp .env.example .env   # then put your real key in .env (git-ignored)
```

## Configuration

| Variable        | Default | Description                                  |
| --------------- | ------- | -------------------------------------------- |
| `MAPS_API_KEY`  | —       | Google Maps JS API key (required).           |
| `PORT`          | `10200` | Port the HTTP server listens on.             |

Values are read from a `.env` file in the working directory if present,
otherwise from the process environment (which is what the Docker image uses).

## How to play

1. Enter a name.
2. **Create a room** with a comma-separated list of terms and a time limit, or
   **join** an existing room with its code.
3. Share the invite link. The host starts the round.
4. Find each term in Street View and drop a pin (you must be _inside_ Street
   View — drag the little yellow figure onto a blue road).
5. Finish when you've found everything (or when the timer runs out).
6. Everyone votes on each find; the player with the most approved finds wins.

## Security

- `.env` and `*.db` are git-ignored — they never leave your machine.
- The Maps key is necessarily exposed to the browser; lock it down by HTTP
  referrer and API in the Google Cloud Console.
- The SQLite DB lives in `/data` inside the container — mount a volume to keep
  rooms across restarts; it's safe to throw away otherwise.
