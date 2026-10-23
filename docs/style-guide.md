# Style guide

This repository has two dialects and does not duplicate the siblings' full
guides.

**C#** is [`.editorconfig`](../.editorconfig), copied from
`blueprint-backend` so this host reads like the code it operates. The prose
version of each rule — including the `var` carve-outs — is that
repository's `docs/style-guide.md`. Three rules fail the build: IDE0055,
IDE0065, IDE0161. `.cs` files are CRLF (`.gitattributes`).

**TypeScript / Angular** follows `blueprint-frontend`'s house forms where they
apply: standalone components, `inject()` at field level, signals for state,
exact pins in `src/Admin.Web/package.json`. There is no Ionic and no Capacitor
here.

**The SPA's layout is Prettier's.** `.ts`, `.html` and `.css` under
`src/Admin.Web/src` and `src/Admin.Web/e2e` are written by
`npm run format` and checked by `npm run format:check`, which `npm run lint`
runs, so the local checks and the CI `web` job fail on a file Prettier would
change. Width, quotes, trailing commas and every wrapping decision in those
files are `src/Admin.Web/.prettierrc`'s, and neither ESLint nor
`.editorconfig` restates the width or a wrapping decision: two tools deciding
one thing disagree sooner or later, and the disagreement surfaces as a lint
failure on a file Prettier just wrote. (`.editorconfig` names single quotes
for `.ts` so an editor types what Prettier then enforces; the two agree, and
Prettier wins if they ever do not.) ESLint keeps what is a rule about the
program (selector prefixes, the template accessibility set); `.editorconfig`
keeps the few keys an editor applies first (indent, charset, final newline).

- **Width is 120**, as the C# is, because the code is written to it; the
  default 100 reflowed lines nobody had found hard to read.
- **`htmlWhitespaceSensitivity` is `strict`.** The default treats the space
  around an inline element as insignificant and rewrites it, which changes
  what a template renders and the text of a translated message (its i18n id
  is computed from it). The cost is Prettier's `><` hugging when an inline
  element wraps, which is ugly and correct.
- **Control-flow blocks are the exception `strict` does not cover.** Prettier
  puts whitespace inside the body of an `@if`, `@case` or `@default`, which a
  browser shows wherever the block sits tight against text or punctuation
  (`Exception : message`, `listed , the newest`, a space between a `code`
  run and the full stop after it). Such a site is written as it must render,
  with `<!-- prettier-ignore -->` before the element (or `// prettier-ignore`
  before a component's `template:`), on its own line: the directive takes no
  trailing text, so the reason goes in a comment beside it. A new block
  beside punctuation is checked in the browser, because the unit tests
  normalise whitespace and will not say.
- **A reformat is its own commit**, so `git blame --ignore-rev` can skip it.

No setting expresses the control-flow rule just above, so it stays a review
matter.

**Prose** wraps at 80 columns (tables, links and fences may exceed it),
British spelling, identifiers keep their real spelling. A comment says why
and cites the owner.

`/style-pass` records a newly settled form in **this** file, then in
`.editorconfig` or the SPA linters, in one change.
