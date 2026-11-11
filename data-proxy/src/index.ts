// data-proxy: lets containers that run outside Cloudflare use D1 (SQLite) and
// R2 through Worker bindings. The D1 REST API is rate limited account-wide and
// meant for admin use, so app traffic goes through here instead.
//
// One Worker serves every service. A service "foo" needs:
//   FOO_TOKEN  secret, the bearer token for that service only
//   FOO_DB     D1 binding (optional)
//   FOO_FILES  R2 binding (optional)
// A token only ever reaches its own service's bindings.

type Param = string | number | null;
type Statement = { sql: string; params?: Param[] };

const SERVICE = /^[a-z][a-z0-9_]{0,31}$/;

export default {
  async fetch(req: Request, env: Record<string, unknown>): Promise<Response> {
    try {
      return await handle(req, env);
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      // Clients retry 5xx but treat 400 as final, so only errors that a retry
      // cannot fix are 400: bad request JSON and SQL errors (SQLITE_*, type
      // and column errors). Everything else (D1/R2 overloaded, network lost,
      // storage timeouts) is 503.
      const permanent = e instanceof SyntaxError || /SQLITE_|D1_TYPE_ERROR|D1_COLUMN_NOTFOUND/.test(msg);
      return json({ error: msg }, permanent ? 400 : 503);
    }
  },
};

async function handle(req: Request, env: Record<string, unknown>): Promise<Response> {
  const url = new URL(req.url);
  // /v1/<service>/<op>[/<key...>]
  const [v, svc, op, ...rest] = url.pathname.split("/").filter(Boolean);
  if (v !== "v1" || !svc || !SERVICE.test(svc) || !op) return json({ error: "not found" }, 404);

  const prefix = svc.toUpperCase();
  const token = env[`${prefix}_TOKEN`];
  if (typeof token !== "string" || !(await authorized(req, token))) {
    return json({ error: "unauthorized" }, 401);
  }

  if (op === "query" || op === "batch") {
    const db = env[`${prefix}_DB`] as D1Database | undefined;
    if (!db) return json({ error: "no database for service" }, 404);
    if (req.method !== "POST") return json({ error: "method not allowed" }, 405);

    if (op === "query") {
      const s = (await req.json()) as Statement;
      const rows = await db.prepare(s.sql).bind(...(s.params ?? [])).raw({ columnNames: true });
      const [columns = [], ...data] = rows;
      return json({ columns, rows: data });
    }

    // Writes only. D1 runs a batch as one transaction, which is what the
    // client's buffered database/sql Tx relies on.
    const { statements } = (await req.json()) as { statements: Statement[] };
    if (!Array.isArray(statements) || statements.length === 0) {
      return json({ error: "statements required" }, 400);
    }
    const res = await db.batch(statements.map((s) => db.prepare(s.sql).bind(...(s.params ?? []))));
    return json({
      results: res.map((r) => ({ changes: r.meta.changes, lastRowId: r.meta.last_row_id })),
    });
  }

  if (op === "files") {
    const bucket = env[`${prefix}_FILES`] as R2Bucket | undefined;
    if (!bucket) return json({ error: "no bucket for service" }, 404);
    const key = rest.map(decodeURIComponent).join("/");

    if (!key) {
      if (req.method !== "GET") return json({ error: "method not allowed" }, 405);
      const list = await bucket.list({
        prefix: url.searchParams.get("prefix") ?? undefined,
        cursor: url.searchParams.get("cursor") ?? undefined,
        limit: 1000,
      });
      return json({
        objects: list.objects.map((o) => ({ key: o.key, size: o.size, uploaded: o.uploaded.getTime() })),
        cursor: list.truncated ? list.cursor : null,
      });
    }

    switch (req.method) {
      case "GET": {
        const obj = await bucket.get(key);
        if (!obj) return json({ error: "not found" }, 404);
        const h = new Headers();
        obj.writeHttpMetadata(h);
        h.set("etag", obj.httpEtag);
        return new Response(obj.body, { headers: h });
      }
      case "PUT":
        await bucket.put(key, req.body, {
          httpMetadata: { contentType: req.headers.get("content-type") ?? "application/octet-stream" },
        });
        return new Response(null, { status: 204 });
      case "DELETE":
        await bucket.delete(key);
        return new Response(null, { status: 204 });
    }
    return json({ error: "method not allowed" }, 405);
  }

  return json({ error: "not found" }, 404);
}

async function authorized(req: Request, token: string): Promise<boolean> {
  const got = req.headers.get("authorization") ?? "";
  // Hash both sides so timingSafeEqual always compares equal-length buffers.
  const enc = new TextEncoder();
  const [a, b] = await Promise.all([
    crypto.subtle.digest("SHA-256", enc.encode(got)),
    crypto.subtle.digest("SHA-256", enc.encode(`Bearer ${token}`)),
  ]);
  return crypto.subtle.timingSafeEqual(a, b);
}

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "content-type": "application/json" },
  });
}
