# GeoBingo — frontend

The Next.js frontend for GeoBingo. It is statically exported (`output: 'export'`)
and embedded into the Go binary at build time — see the
[project README](../README.md) for how to build and run the whole app.

```bash
npm install
npm run dev     # http://localhost:3000 (API must run separately on :10200)
npm run build   # produces ./out, which the Go server embeds
```
