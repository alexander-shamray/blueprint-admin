# Admin.Web

The console's Angular SPA. How to build, run, test and smoke it is in the
[repository README](../../README.md); the design is
`docs/superpowers/specs/2026-09-14-blueprint-admin-design.md`.

In short, from this directory: `npm run build` writes into the host's
`wwwroot`, which the host serves; `npm start` runs the dev server with `/api`
proxied to the host; `npm test` runs Vitest; `npm run e2e` runs the Playwright
smoke against a FakePlatform host. Output path and dev-server port are
`angular.json`, the proxy target is `proxy.conf.json`.
