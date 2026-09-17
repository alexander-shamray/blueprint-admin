# Style guide

This repository has two dialects and does not duplicate the siblings' full
guides.

**C#** is [`.editorconfig`](../.editorconfig), copied from
`blueprint-backend` so this host reads like the code it operates. The prose
version of each rule — including the four `var` carve-outs — is that
repository's `docs/style-guide.md`. Three rules fail the build: IDE0055,
IDE0065, IDE0161. `.cs` files are CRLF (`.gitattributes`).

**TypeScript / Angular** follows `blueprint-frontend`'s house forms where they
apply: standalone components, `inject()` at field level, signals for state,
exact pins in `src/Admin.Web/package.json`. There is no Ionic and no Capacitor
here.

**Prose** wraps at 80 columns (tables, links and fences may exceed it),
British spelling, identifiers keep their real spelling. A comment says why
and cites the owner.

`/style-pass` records a newly settled form in **this** file, then in
`.editorconfig` or the SPA linters, in one change.
