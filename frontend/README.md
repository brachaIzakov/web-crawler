# Web Crawler UI

React + TypeScript + Vite single-page app: submit a URL, watch the crawl progress live (SSE) and browse the resulting page tree and link ratios.

- `npm run dev` — dev server on http://localhost:5173; `/api` is proxied to http://localhost:8080
- `npm run build` — type-check and produce the production bundle in `dist/` (served by nginx in the Docker image)
