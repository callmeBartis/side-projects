# side-projects

[![Build & Publish](https://github.com/callmeBartis/side-projects/actions/workflows/build.yml/badge.svg)](https://github.com/callmeBartis/side-projects/actions/workflows/build.yml)

A collection of small, self-contained side projects. Each project lives in its
own folder. Server apps ship with a `Dockerfile` and are automatically built and
published as a Docker image by CI; Windows tools are single `.exe` files built
with `dotnet publish`.

> **Completely free & commercially usable.** Everything in this repository may
> be used without restriction — privately, commercially, modified, or
> redistributed. No license fees, no attribution required. Use at your own risk,
> without warranty.

## Table of contents

- [GeoBingo](#geobingo) — real-time multiplayer "find it in Street View"
- [Claude Usage Widget](#claude-usage-widget) — Claude session/weekly usage in the Windows 11 taskbar, with phone alerts
- [WSL Tray](#wsl-tray) — tray tool for WSL2 usage, limits and distros
- [General](#general)
  - [How CI works](#how-ci-works)
  - [Adding a new project](#adding-a-new-project)

---

## GeoBingo

📁 [`geobingo/`](./geobingo) · 🐳 `ghcr.io/callmebartis/geobingo`

A real-time multiplayer game: the host writes a list of things to find
(e.g. *"red door, graffiti, a dog, a fountain"*), shares a room link, and
everyone races to locate each term anywhere on Google Street View. When the
round ends, players vote on each other's finds and a ranking is shown.

**Stack:** a Go backend (API + real-time updates over SSE, SQLite) that embeds a
statically exported Next.js frontend → **a single, self-contained binary /
Docker image**. The only runtime dependency is a Google Maps API key.

### Prerequisite: Google Maps API key

GeoBingo uses Google Street View and therefore needs an API key. Here's how to
get one (Google's free tier is usually more than enough for casual play):

1. Open the [Google Cloud Console](https://console.cloud.google.com/) and create
   a project (or pick an existing one).
2. **Enable billing** — Google requires a payment method even if you stay within
   the free tier.
3. Under **APIs & Services → Library**, enable both of these APIs:
   - **Maps JavaScript API**
   - **Geocoding API**
4. Under **APIs & Services → Credentials → Create credentials → API key**,
   create a key.
5. **Lock the key down** (important, since the key is exposed in the browser):
   - *Application restrictions* → **HTTP referrers** → add your domain(s)
     (e.g. `http://localhost:10200/*` and/or `https://your-domain.tld/*`).
   - *API restrictions* → limit it to the two APIs listed above.
6. Set the key as the `MAPS_API_KEY` environment variable (see below).

### Setup with `docker run`

```bash
docker run -d --name geobingo \
  -p 10200:10200 \
  -e MAPS_API_KEY=your_google_maps_key \
  -v geobingo-data:/data \
  ghcr.io/callmebartis/geobingo:latest
```

Then open **http://localhost:10200** in your browser.

- `-v geobingo-data:/data` keeps the SQLite database (rooms) across restarts.
  Without a volume everything is ephemeral — perfectly fine for a quick game.
- `-p 10200:10200` maps the port; the container port can be changed via
  `-e PORT=...`.

### Setup with Docker Compose

`docker-compose.yml`:

```yaml
services:
  geobingo:
    image: ghcr.io/callmebartis/geobingo:latest
    # Alternatively build from source instead of pulling the image:
    # build: ./geobingo
    ports:
      - "10200:10200"
    environment:
      MAPS_API_KEY: ${MAPS_API_KEY}   # from your .env or shell
      PORT: "10200"
    volumes:
      - geobingo-data:/data
    restart: unless-stopped

volumes:
  geobingo-data:
```

Start it:

```bash
export MAPS_API_KEY=your_google_maps_key   # Windows PowerShell: $env:MAPS_API_KEY="..."
docker compose up -d
```

### Build it yourself (no registry)

```bash
docker build -t geobingo ./geobingo
docker run --rm -p 10200:10200 -e MAPS_API_KEY=your_key geobingo
```

More details (local development, configuration, how to play) are in the
[GeoBingo README](./geobingo/README.md).

---

## Claude Usage Widget

📁 [`claude-usage-widget/`](./claude-usage-widget) · Windows 11 · C# / WinForms

Shows your Claude plan usage (5-hour session and weekly limit) as a small widget
inside the Windows 11 taskbar, and pushes alerts to your phone via
[ntfy](https://ntfy.sh) at configurable thresholds. An iOS shortcut and a
Scriptable home screen widget can show the same numbers on the phone.

Needs Claude Code installed and logged in. Download the `.exe`, double-click,
done: it installs itself per user, no admin rights. Details in the
[README](./claude-usage-widget/README.md).

---

## WSL Tray

📁 [`wsl-tray/`](./wsl-tray) · Windows · C# / WinForms

A tray tool that shows what WSL2 is actually consuming (CPU, RAM, disk per
distro and Docker container), edits `.wslconfig` limits safely with automatic
rollback, and starts, stops, installs, clones and exports distros. Details in the
[README](./wsl-tray/README.md).
---

## General

### How CI works

The workflow [`.github/workflows/build.yml`](./.github/workflows/build.yml) is
shared by every project:

1. On each push/PR it **detects which top-level folders changed** and contain a
   `Dockerfile`.
2. For each changed project it **builds the Docker image**.
3. On pushes to `main` it **publishes** the image to the GitHub Container
   Registry: `ghcr.io/callmebartis/<project>:latest` (and `:<commit-sha>`).
   Pull requests only build to verify — they never publish.

The detection is fully dynamic — there is no per-project list to maintain.

### Adding a new project

1. Create a new top-level folder, e.g. `my-project/`.
2. Add a `Dockerfile` that builds and runs the app.
3. Commit and push. CI picks it up automatically and publishes
   `ghcr.io/callmebartis/my-project`.

That's it — no workflow changes needed.

> **Note (public repo):** secrets belong in `.env` files (git-ignored
> repo-wide) — never commit real keys; each project provides a `.env.example`
> template instead. Databases (`*.db`), build artifacts, and `node_modules/` are
> ignored too.
