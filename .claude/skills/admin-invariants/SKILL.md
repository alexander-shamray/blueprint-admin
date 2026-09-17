---
name: admin-invariants
description: Use when adding a Host endpoint, SPA screen, Compose/npm command, Playwright spec, or when a platform fact (port, CORS, realm, route, queue) looks wrong. The console copies run-locally.md; FakePlatform is required; never edit sibling clones.
---

# Admin invariants

Three rules. Cite the owner; do not restate values.

## 1. Commands come from `run-locally.md`

The host runs a line that document already has (Compose in the backend
clone, `npm start` in the frontend clone, `rabbitmqctl`, curl examples).
If the console needs an operation that document does not do, change that
document's owner **first**, then copy. Owner of example bodies here:
`src/Admin.Host/Api/RunLocallyExamples.cs`.

## 2. FakePlatform is part of done

A new `/api` surface or screen is not done until:

- it works with `--Admin:FakePlatform=true` (recordings in
  `src/Admin.Host/Fakes/`);
- the Playwright smoke in `src/Admin.Web/e2e/` covers the screen.

Do not require Docker for the unit suite. `docs/testing.md` owns how to run
e2e locally.

## 3. This clone does not own the platform

Nothing in `../blueprint-backend` or `../blueprint-frontend` is edited from
here. Raise a missing fact there. Ports and sibling paths:
`src/Admin.Host/Config/AdminOptions.cs`. Gateway copies:
`src/Admin.Host/Api/GatewayRoutes.cs`. Loopback and no login: spec §8.

401/403/409/422 pass through as the gateway returned them. Inventory 502 is
expected until that service exists.
