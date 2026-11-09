# Agents

Operating primer: [`CLAUDE.md`](CLAUDE.md), which imports
[`.harness/agent-rules.md`](.harness/agent-rules.md), the rules every
repository on the shared harness holds. Read both before editing.

| | |
|---|---|
| Commands | `.claude/commands/` (`/ship`, `/branch`, `/commit`, `/pr`, reviews) |
| Skills | `.claude/skills/` (`codebase-index`, `admin-invariants`) |
| Contract | [`docs/change-locality.md`](docs/change-locality.md) |
| Harness | [`docs/harness-boundaries.md`](docs/harness-boundaries.md): the section its index names, before touching `.claude/`. A managed file, any path `.harness/manifest.json` lists, is changed in `blueprint-harness`, never here |

Facts that move with a PR are cited from those files, never restated here.
