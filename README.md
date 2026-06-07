# side-projects

A monorepo of small, self-contained side projects. Each project lives in its
own top-level folder, ships with its own `Dockerfile`, and is built and
published independently by CI.

## Projects

| Project | Description | Stack |
| --- | --- | --- |
| [`geobingo`](./geobingo) | Real-time multiplayer "find it in Street View" bingo game | Go · Next.js · SQLite · Google Maps |

## How CI works

The workflow at [`.github/workflows/build.yml`](./.github/workflows/build.yml)
is shared by every project:

1. On each push/PR it **detects which top-level folders changed** and contain a
   `Dockerfile`.
2. For each changed project it **builds the Docker image**.
3. On pushes to `main` it **publishes** the image to the GitHub Container
   Registry: `ghcr.io/callmebartis/<project>:latest` (and `:<commit-sha>`).
   Pull requests only build to verify — they never publish.

The detection is fully dynamic: there is no per-project list to maintain.

## Adding a new project

1. Create a new top-level folder, e.g. `my-app/`.
2. Add a `Dockerfile` to it that builds and runs the app.
3. Commit and push. CI picks it up automatically and publishes
   `ghcr.io/callmebartis/my-app`.

That's it — no workflow changes needed.

## Local development

Each project has its own `README.md` with setup and run instructions.

```bash
docker build -t geobingo ./geobingo
docker run --rm -p 10200:10200 -e MAPS_API_KEY=your_key geobingo
```

## Security

This is a **public** repository.

- Secrets live in `.env` files, which are git-ignored repo-wide. Never commit
  real keys — each project provides a `.env.example` template instead.
- Databases (`*.db`), build artifacts, and `node_modules/` are git-ignored.
- If you ever expose a key by accident, **rotate it** — git history is forever.
