# data-proxy

A Cloudflare Worker that lets containers running outside Cloudflare (e.g. on a
VPS) keep their SQLite data in **D1** and their files in **R2**, so the
containers themselves are stateless and can run as several replicas on any host.

The D1 REST API is not meant for this: it shares the account-wide limit of
1,200 requests per 5 minutes. This Worker talks to D1 and R2 through bindings
instead, which have no such limit.

```
container (Go, any host) --HTTPS + bearer token--> data-proxy --binding--> D1 / R2
```

## API

Base URL: `https://data-proxy.<subdomain>.workers.dev/v1/<service>`
Header: `Authorization: Bearer <service token>`

| Method | Path | Body | Response |
|---|---|---|---|
| POST | `/query` | `{"sql": "...", "params": [...]}` | `{"columns": [...], "rows": [[...]]}` |
| POST | `/batch` | `{"statements": [{"sql", "params"}, ...]}` | `{"results": [{"changes", "lastRowId"}]}` |
| PUT | `/files/<key>` | raw bytes + `content-type` | 204 |
| GET | `/files/<key>` | | file with stored `content-type`, 404 if missing |
| DELETE | `/files/<key>` | | 204 |
| GET | `/files?prefix=&cursor=` | | `{"objects": [{"key", "size", "uploaded"}], "cursor"}` |

- `/batch` runs as **one transaction**: if any statement fails, none apply.
- `params` may be strings, numbers or `null`.
- Errors come back as HTTP 400 `{"error": "D1_ERROR: ..."}`.
- Each service token only reaches that service's own database and bucket.

## Adding a service

For a service `foo`:

1. `npx wrangler d1 create foo --location weur`
2. `npx wrangler r2 bucket create foo-files --location weur` (only if needed)
3. Add `FOO_DB` / `FOO_FILES` bindings to `wrangler.jsonc`
4. `openssl rand -hex 32 | tr -d '\r\n' | npx wrangler secret put FOO_TOKEN`
5. `npm run deploy`

## Deploy

```bash
npm ci
export CLOUDFLARE_API_TOKEN=...  CLOUDFLARE_ACCOUNT_ID=...
npm run deploy
```

The API token needs: Workers Scripts Edit, D1 Edit, Workers R2 Storage Edit,
Account Settings Read, plus User Details Read and Memberships Read for wrangler.

## Limits worth knowing

- Every query and every file read is one Worker request. On the free plan
  that is 100k requests/day across all services, so clients should cache.
- One D1 database runs queries one at a time, so use one database per service.
- Integers above 2^53 lose precision (they pass through JavaScript numbers).
