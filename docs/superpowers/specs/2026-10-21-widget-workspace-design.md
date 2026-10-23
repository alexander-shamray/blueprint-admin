# Widget workspace: pages of resizable widgets, live and static diagrams

Design note for #378. It turns the console's eleven fixed screens into
widgets the operator places on pages of their own, adds three kinds of
diagram, and gives the console a component library and a theme. It amends
the console's design (`2026-09-14-blueprint-admin-design.md`, "the spec"
below) where it says so; everything it does not mention stands, §1's three
properties and §8's posture first.

| | |
|---|---|
| **Status** | Draft for review, 2026-10-21 |
| **Asked for** | The operator, 2026-10-21: a more modern look; diagrams; configurable widgets grouped on arbitrary pages; flexible widget sizes; positions kept between sessions. Diagrams of all three kinds below, "dynamic where it is needed, static otherwise", with the choices left to this note |
| **Absorbs** | #375 (inputs survive a screen switch) and #376 (a Scenario run and a Logs follow survive a screen switch): both fall out of §4's state model, and are closed by the phases that land it |
| **Waits on** | #366, whose Angular bump to 22.2 is what the component library's pin needs (§7); #368, which reshapes the Stack screen that phase 2 takes apart |

## 1. Intent

The console answers "what did the platform do?" across services, but it
answers one screen at a time. The questions an operator actually has are
cross-screen: *is the queue this Scenario feeds draining while the run
waits?*, *which hop of this order's trace is slow, and what did that
service log?*, *what does the broker look like while the load run drives
it?* Today each is a sequence of tab switches, and every switch loses the
screen left (#375, #376).

The workspace makes the screen's parts into widgets and the screens into
pages. The operator composes the views those questions need, side by side,
and they stay as composed. Diagrams are added because a topology and a
request path are read faster drawn than tabulated.

**Success** is the operator building, say, a "Scenario debugging" page —
the Scenario runner, the queue depths of the queues it crosses, the live
platform diagram and the run's request sequence — once, and finding it
exactly so after a reload, a host restart and a reboot.

## 2. Goals, non-goals, anti-goals

**Goals**

1. Every capability of today's eleven screens is available as one or more
   widgets (§5). Nothing the console can do today is lost.
2. Pages: any number, named, ordered by the operator, each a grid of widget
   instances. A widget type may appear on several pages and several times on
   one page, each instance with its own configuration (a Logs widget for
   `ordering-api`, another for the gateway).
3. Widgets move by drag and resize from their edges and corners on a grid;
   the layout is the operator's, not a template's.
4. The layout, pages and widget configurations persist across reloads, host
   restarts and reboots, on the host (§6), and can be exported and imported
   as one JSON file.
5. Three diagram kinds (§8): the live platform diagram and the request
   sequence diagram are dynamic; diagrams and notes the operator writes are
   static.
6. A component library and theme (§7): a consistent, current look, light
   and dark, readable at a glance.
7. The eleven screens survive as **built-in pages**: a fresh workspace opens
   with them, so an operator who never customises sees what they see today,
   in the new look.

**Non-goals**

- Sharing a workspace with others, syncing it anywhere, or more than one
  operator (spec §13: the console is single-user and loopback-only).
- Operator-written code: no widget runs script the operator supplies. A
  static diagram is Mermaid or Markdown text, rendered, never executed.
- Free-form metrics charting. The golden-signal queries stay
  `GoldenSignals.cs`'s; Grafana remains the place for ad-hoc PromQL (spec
  §13, "Replacing Grafana").
- A mobile layout. The console is a desktop tool (spec §13); the grid must
  not break at a narrow window, nothing more.

**Anti-goals** — this fails, however well it works otherwise, if:

- a safety confirmation (`down -v`, `bff-replay --reset`, `--execute`, a
  runbook's typed name or second press) is ever restored from saved state,
  or a destructive action becomes one click because a widget sits beside
  another;
- a widget on a page nobody is looking at keeps polling the host: the Stack
  screen's `docker compose ps` every 3 s is already measurable on the
  workstation, and twelve hidden copies of it would not be;
- the saved workspace holds a secret: a password, a token, a minted
  credential;
- the console stops being a client of its siblings: the workspace adds no
  platform fact, no command `run-locally.md` does not have, and no new
  surface the host reaches, except the AI assistant's provider (AI assistant
  note §11).

## 3. Constraints

- Everything in spec §8 holds. The workspace's endpoints are `/api` routes
  under the same loopback binding, `Sec-Fetch-Site` rule and framing denial.
- FakePlatform is first-class (`CLAUDE.md`): every widget renders against
  the fakes, the store works there (§6), and the Playwright smoke covers the
  workspace.
- Exact pins only (`Directory.Packages.props`, `package.json`), each version
  at least two weeks old when pinned, the lockfile in the same commit.
- Angular 22 standalone, zoneless, signals, `inject()` at field level, as
  `docs/working-rules.md` has it. A library that needs Zone.js is out.
- The SPA keeps one origin and no third-party network fetch at run time:
  fonts and icons are bundled, not loaded from a CDN, because the console
  must work with the workstation offline.

## 4. The model

### 4.1 Workspace, page, widget instance

```ts
interface Workspace {
  version: 1;                 // the schema; §6.3 migrates older ones
  pages: Page[];              // in the operator's order
  activePageId: string | null;
}

interface Page {
  id: string;                 // stable, generated
  title: string;
  builtIn: string | null;     // 'stack', 'logs', … for the eleven, else null
  hidden: boolean;            // a built-in page hides rather than deletes
  widgets: WidgetInstance[] | null; // null: a built-in page as shipped, §5.3
}

interface WidgetInstance {
  id: string;
  type: string;               // a key of the widget registry, §5.1
  x: number; y: number;       // grid cell of the top-left corner
  cols: number; rows: number; // size in grid cells
  config: Record<string, unknown>; // the type's own, validated by it
}
```

The grid is 24 columns wide with a fixed row height; each widget type
declares a minimum and a default size. Positions are cells, not pixels, so
a layout survives a window of another width.

### 4.2 Where state lives

Three kinds of state, three owners. This is what dissolves #375 and #376.

| Kind | Example | Owner | Survives |
|---|---|---|---|
| **Layout and configuration** | a widget's place and size; a Logs widget's services; a Queue widget's queue list | the `Workspace`, saved on the host (§6) | everything |
| **Operator input in flight** | a request body being written; a filter typed; an opened message | the widget instance's **session state**, in a root `WidgetSessionStore` keyed by instance id | page switches; not a reload |
| **Running work and its data** | a Scenario run; a Logs follow; a job's output; the broker's queue reads | a root **data service** per source (`ScenarioRunner`, `LogFollowService`, `BrokerFeed`, `StackFeed`, …) | page switches; ends as it ends today |

A widget is a view: it reads its data service and its session state and
writes back to them, and holds nothing that matters itself. Destroying a
widget, which leaving its page does, loses nothing.

**Confirmations are never state, and neither are the gates before them.** A
typed confirmation lives in the widget's own component and nowhere else, so
it is empty on every render. The same holds for every step that unlocks a
destructive action:

- the dead-letter workbench's "a dry run of this message and verb has
  started", which is what offers the real run;
- a runbook cause's first press;
- the armed state of any two-press button.

Each of these resets when the widget is re-created, so leaving a page and
coming back always starts the sequence again. Each widget with one has a test
that proves it (§11).

### 4.3 Polling follows visibility

A data service polls only while at least one widget that reads it is on the
active page and the browser tab is visible (`document.visibilityState`).
Widgets subscribe; the service reference-counts its subscribers and starts
or stops its timer. Two widgets reading the same source share one poll.
Intervals stay the ones today's screens use (the Stack screen's 3 s, the
signals' 15 s, the Map's 5 s), owned by the service.

Running work is not polling: a Scenario run, a Logs follow and a job stream
continue while no widget shows them, as #376 asks, and a small indicator in
the shell (§7.2) says so and links to a page that shows them.

A refresh that is opt-in today stays opt-in. The Map's and the Broker's
auto-refresh are off until switched on, so the per-instance `autoRefresh`
config of a Platform map, Queues, Exchanges, Bindings or Permissions widget
defaults to off and is saved. The live platform diagram (§8.1) is the
exception: its refresh is the point of it, so it defaults to on. It still
runs only while the diagram is visible.

### 4.4 One host resource, several widgets

Some sources are one thing on the host however many widgets show them:

- **Logs.** The host keeps one `logs -f` (`LogFollower`; spec §5.10), and
  that rule stays. `LogFollowService` runs one follow over the union of the
  services its visible-or-following widgets ask for. Each Logs widget
  filters that one stream to its own services, text and correlation id in
  the browser. When the union changes, the service restarts the follow with
  the new set: a new follow replaces the old, as today. Its `--tail 200`
  replays the last lines of each service, so the buffer starts afresh;
  keeping the old lines would show up to 200 of them twice.
- **Scenario.** One `ScenarioRunner` holds the run, because the stack has one
  lease. Every Scenario and Saga strip widget is a view of that one run, and
  Run is refused while it is going, as the palette's second request is
  today. Two Scenario widgets are allowed and show the same run.
- **Jobs.** A Job output widget shows a job by id; several may show the same
  one, each with its own SSE stream, as two tabs can today.

## 5. Widgets

### 5.1 The registry

One list, `WIDGETS` in `src/app/workspace/widgets.ts`, read by the "Add
widget" picker, the grid and the built-in pages. Each entry: `type`, title, the component
(lazy-loaded), default and minimum size, a config schema with its defaults,
and the data services it reads. A widget type is not done until it renders
against FakePlatform and the smoke covers it.

### 5.2 The catalogue

Today's screens, cut along the lines they already have. The **Default page**
column is the built-in page that places the widget.

| Widget | From today's | Config | Default page |
|---|---|---|---|
| Workstation doctor | Stack | — | Stack |
| Stack controls (Up, Down, wipe, Reset, with #368's progress) | Stack | — | Stack |
| Compose services | Stack | services shown | Stack |
| Reachability | Stack | surfaces shown | Stack |
| Golden signals | Stack | services shown | Stack |
| Alerts | Stack | firing only | Stack |
| Load run | Stack | — | Stack |
| Reference client (start, stop, device lane) | Stack | — | Stack |
| Job output | every screen's output pane | a job id, or "last job started from this page" | Stack |
| Logs | Logs | services, text filter, correlation id | Logs |
| Queues | Broker | queue filter, parked only | Broker |
| Exchanges / Bindings / Permissions | Broker | filter | Broker |
| BFF projection | Broker | — | Broker |
| Dead-letter workbench | Broker | queue | Broker |
| Platform map (table) | Map | — | Map |
| Erasure | Erasure | subject id | Erasure |
| Mail | Mail | correlation id filter | Mail |
| API request | API | operation, identity | API |
| API history | API | — | API |
| Trace timeline | Trace | correlation id, window, or "follow the last Trace link" | Trace |
| Scenario | Scenario | script, identity | Scenario |
| Saga strip | Scenario | — | Scenario |
| Runbooks | Runbooks | runbook | Runbooks |
| Transcript | Transcript | — | Transcript |
| **Live platform diagram** | new, §8.1 | services shown, layout | Map |
| **Request sequence** | new, §8.2 | correlation id, or "follow the last Trace link" | Trace |
| **Note / diagram** | new, §8.3 | Mermaid or Markdown source | — |
| **Assistant** | new, AI assistant note | — (one conversation per tab, AI assistant note §2) | — |

"Follow the last Trace link" lets a Trace or Sequence widget on a page
re-point itself when another widget's correlation-id link is clicked, so a
debugging page does not need its id typed. Clicking such a link with no
following widget on the page opens the built-in Trace page, as today.

### 5.3 Built-in pages

Each of the eleven screens is a page with `builtIn` set, built from the
widgets above in roughly today's arrangement. A built-in page can be
rearranged like any other, and **Reset page** returns it to its default; a
built-in page can be hidden but not deleted, so the defaults are always one
click away.

**A built-in page the operator has not changed is not saved.** Its entry in
the workspace keeps its place, title and visibility, with `widgets: null`,
and the SPA builds it from the defaults the console ships. So when a phase
changes a default, for example when the Stack page's legacy widget gives way
to the Stack widgets, every untouched built-in page picks the new default
up, and no migration is needed for it. A built-in page the operator has
edited is saved whole, and is theirs.

**Legacy widgets in a customised page.** The legacy screen widget type (§10,
phase 1) stays registered until phase 6, so a customised page keeps working
through every phase. Phase 6 retires the type with a `version` bump. Its
migration replaces each remaining legacy instance (type `legacy`, its
screen in `config.screen`) with that
screen's default widgets, placed inside the rectangle the legacy widget
occupied, stacked in their default order, and the operator is told once
which pages it changed. The old routes keep working: `/stack`, `/trace/:correlationId`,
`/mail?correlationId=`, … open the built-in page and hand the parameter to
its widgets. The palette (Ctrl+K) lists every page and every built-in
action.

## 6. Persistence

### 6.1 The store

`WorkspaceStore` on the host, beside `OpenApiBaselines`: one file,
`<Admin:DataDir>/workspace.json`, written atomically (temp file, then
rename) under a lock. Against FakePlatform it lives in a temp directory, as
the dead-letter records do, so a smoke run never touches a real one.

Host-side, not `localStorage`, because goal 4 says *between sessions*, and
browser storage is per browser and can be cleared from under the operator.
The host already keeps operator state in `DataDir`, so this adds no new
kind of thing.

### 6.2 Endpoints

| Method and path | Does |
|---|---|
| `GET /api/workspace` | the saved workspace, or the default (§5.3) when there is none, with an `etag` |
| `PUT /api/workspace` | body: a whole `Workspace`, with `If-Match` the etag it was based on. 200 with the new etag; 412 when another tab saved first, with the current one, which the SPA offers to load or to save this tab's over, and saves nothing until the operator chooses; 400 for a document that fails the schema; 413 past 1 MiB |
| `GET /api/workspace/export` | the same document as a download |
| `POST /api/workspace/import` | body: an exported document, validated as `PUT`; replaces the workspace after the SPA asks |

The SPA saves on change, debounced (about 1 s after the last drag or edit),
and says when a save failed. Nothing is saved while a drag is in progress.

### 6.3 Schema and migration

`version` is the schema. The registry is TypeScript (`WIDGETS`, §5.1), so
**the host validates structure and leaves each widget type's own rules to
the SPA**. The host checks:

- the shape of §4.1 and that ids are unique;
- grid bounds (`0 ≤ x`, `x + cols ≤ 24`, `1 ≤ rows ≤` a fixed maximum);
- each `config`'s serialised size, and the secret-named keys of §6.4;
- the document's total size.

It does not know widget types, so a type unknown to this console is kept,
rendered by the SPA as "unknown widget", and saved back untouched. A
document written by a newer console is therefore not destroyed by an older
one. Each widget type validates its own `config` when it renders, and falls
back to its defaults for anything it does not accept. A change to the schema bumps `version` and adds
a migration the host runs on read; a test holds each migration.

### 6.4 What is never saved

Passwords and tokens: an API or Scenario widget saves the identity's
username only, and a custom identity's password is session state that is
never written. Typed confirmations. Job output, logs, trace results and
other data: data is read again, so a page always shows what the platform
says now. The validator refuses a configuration key named like a secret
(`password`, `token`, `secret`, `authorization`, ignoring case), so a widget
cannot start saving one by accident.

## 7. Look and feel

### 7.1 Component library

**Angular Material 3 with the CDK**, at the Angular release the console
pins. It is the Angular team's, it moves with the framework, which keeps
#366's grouped bump one PR, it supports zoneless and signals, and its
design tokens give light and dark themes from one set. The CDK is wanted
anyway, for overlays, focus management and drag-and-drop. Its 22.2 line
needs Angular 22.2, which is #366's bump.

Considered: PrimeNG, which is larger with a theming model of its own;
Taiga UI and Spartan, which are less mature on Angular 22; and hand-written
CSS, which is today's state and the reason for the request. Material is
enough for an operator tool and the cheapest to keep current.

Fonts and icons are bundled (§3): the system font stack for text, a
monospace stack for output, and Material Symbols as the npm-bundled font, so
that `<mat-icon>` can name a ligature (settled in phase 1a, §12).

### 7.2 The shell

- A side navigation listing the pages, built-in and the operator's, in the
  operator's order: add, rename, reorder by drag, hide, delete (a built-in
  page hides), duplicate.
- A top bar: the page title, **Edit layout**, **Add widget**, the theme
  switch (system, light, dark), the backend directory, and the palette key.
- **Edit mode.** The layout is locked by default, so a click on a widget
  never moves it. Edit layout unlocks dragging and resizing, shows each
  widget's configure and remove controls, and Done locks it again.
- A running-work indicator: a Scenario run, a Logs follow or a job going,
  each linking to a page that shows it (§4.3).
- Each widget has a header (title, its own refresh where it has one,
  configure, maximise to the page), and a body that scrolls on its own.

### 7.3 Grid

**`angular-gridster2`** (22.x, peer `@angular/core ^22`): Angular-native,
drag and resize from edges and corners, a cell grid that serialises to
exactly §4.1's `x, y, cols, rows`, and push and compact behaviour. Phase 1
proves it under zoneless change detection before anything depends on it.
**Gridstack.js** (14.x, with its Angular wrapper) is the fallback if it
does not hold up. Either way the grid is wrapped in one `WorkspaceGrid`
component, so the choice is replaceable. **Proved in phase 1c (#383):** the
version `src/Admin.Web/package.json` pins renders, drags and resizes under
zoneless change detection against
FakePlatform, and a resize survives a reload, so the fallback was not
needed.

## 8. Diagrams

### 8.1 Live platform diagram — dynamic

The question it answers: *what is happening now, and where is it stuck?*

- **Data**: `GET /api/platform/map` (spec §5.15: services, the gateway routes
  to each, the queues each consumes with depth and bindings), `GET /api/stack`
  for each surface's reachability, and the rate the Map screen already
  derives from two reads. No new host read.
- **Drawing**: **Cytoscape.js** with the **dagre** layout. Nodes: the gateway,
  each service, each queue; parked twins are folded into their queue's node
  and shown as a badge. Edges: gateway route → service; queue → consuming
  service; exchange binding → queue, labelled with the exchange.
- **Live**: refreshed on the Map's 5 s, while visible (§4.3).
  - **Service nodes.** A service with a probed URL is coloured by its
    reachability (`PlatformProbe`, which covers `PlatformServices.WithUrl`
    only). A worker with no URL, such as Shipping or Notifications, is
    coloured by its Compose service's state and health from the same
    `GET /api/stack`. A service with neither is drawn neutral, never green.
  - **Queue nodes** are coloured by state: empty, holding, growing between
    two reads, or parked messages present. The edge into a queue carries its
    depth and rate.
- **Interaction**: click a service to point a Logs widget on the page at it,
  or open the Logs page filtered to it; click a queue to open it in a Queues
  widget, or the Broker page; a parked queue opens the dead-letter workbench.
  Nodes can be dragged; positions are part of the widget's config, so the
  operator's arrangement persists, and **Re-layout** discards it.
- **Disagreements** (spec §5.15) are listed under the diagram, as on the Map
  screen, and never drawn as if resolved.

Cytoscape over the alternatives: Mermaid re-renders the whole SVG on each
refresh and cannot keep the operator's node positions, and a flow-editor
library (ngx-vflow) is built for editing graphs, which this is not.
Cytoscape is lazy-loaded with the widget, so it costs nothing until used.

### 8.2 Request sequence — dynamic

The question it answers: *where did this request go, and which hop was
slow?*

- **Data**: the Trace timeline the Trace screen already reads
  (`GET /api/trace/{correlationId}`, spec §5.9), with no new host read.
- **Drawing**: a **Mermaid `sequenceDiagram`**, generated from the
  `TraceView` events: participants are the services in order of first
  appearance, plus the broker; each HTTP span is an arrow with its duration;
  each publish and consume through the broker is an arrow to and from the
  broker; the saga's steps are notes. A failed span is drawn as such.
- **Live** on demand: re-drawn on the widget's Reload, or when a "follow the
  last Trace link" widget is re-pointed; a refetch says what changed, as the
  Trace screen does. It is not polled, because a trace does not change once
  written, except while its request is still running.
- Each arrow links to the span in Grafana Explore, as the timeline's rows do.

### 8.3 Notes and diagrams — static

- A **Note / diagram** widget holds Markdown text, in which ` ```mermaid `
  blocks render as diagrams. The operator edits it in place in edit mode.
- **Presets**: the two diagrams in the workspace's `admin-konsol.md` (the
  architecture and the work order of the tabs) ship as preset notes the
  picker offers. That file sits outside this repository, so the presets are
  copied into `src/Admin.Web` as source, not read at run time.
- **Safety**: Mermaid runs with `securityLevel: 'strict'` and no HTML labels;
  Markdown is rendered to elements with no raw HTML, as the Runbooks screen
  renders a runbook (spec §5.14). Nothing the operator writes executes.
- Mermaid is lazy-loaded, shared by §8.2 and §8.3.

## 9. Security

The workspace is the operator's own file on their own machine. The rules
that matter:

- `PUT` and `import` are `/api` writes under §8's `Sec-Fetch-Site` rule, so
  another origin cannot rewrite the operator's pages.
- The validator (§6.3) bounds the document's size and shape, and refuses
  secret-named configuration keys (§6.4).
- Rendering the operator's Markdown and Mermaid never yields script (§8.3).
- No widget can reach a surface the host does not already reach: widgets are
  views over existing `/api` reads and actions.

## 10. Phases

Each phase is one or more PRs and leaves the console working and complete.

| Phase | Delivers | Needs |
|---|---|---|
| 1 Foundation | Material theme and the new shell (§7.2); `WorkspaceStore` and its endpoints with FakePlatform; the grid with edit mode; the Note / diagram widget with the presets; one **legacy screen** widget that renders any of today's eleven screen components whole, so every built-in page exists from day one as one big widget | #366 |
| 2 Stack | the Stack data service and its widgets (§5.2), replacing its legacy widget on the Stack page; Job output | #368 merged |
| 3 Observe | Logs (with `LogFollowService`, #376's half), Queues, Exchanges, Bindings, Permissions, BFF projection, Dead-letter workbench, Platform map, Mail, Erasure | 2 |
| 4 Act | API request and history, Trace timeline, Scenario with `ScenarioRunner` (#376's other half), Saga strip, Runbooks, Transcript; `WidgetSessionStore` for every widget (#375) | 2 |
| 5 Diagrams | Live platform diagram (§8.1), Request sequence (§8.2) | 3, 4 |
| 6 Retire | the legacy screen widget and the screen components it wrapped, once nothing uses them; spec §6's screens table rewritten as the widget catalogue; README | 5 |

From phase L of the AI assistant note on, a phase is not done until its
widgets' strings are in every shipped language (AI assistant note §5.5). A
legacy-screen widget stays English until the phase that replaces it.

Phases 3 and 4 can run in parallel: they share the registry, but neither
edits the other's widgets. The legacy widget of phase 1 is what lets every
later phase move one screen at a time without a page ever missing.

## 11. Testing

- **Host (xunit)**: `WorkspaceStore` — atomic write, a concurrent save, a
  corrupt file read as the default with the file kept aside, each migration;
  the endpoints — the etag and 412, schema refusals, 413, secret-named keys
  refused, an unknown widget type round-tripped untouched, the
  `Sec-Fetch-Site` rule.
- **SPA (Vitest)**: each widget against a mocked host client, as each screen
  is today; each data service's reference-counted polling (no subscriber, no
  timer; a hidden tab, no timer); each widget with a confirmation renders it
  empty after being destroyed and re-created; the session store keeps input
  across a destroy; the registry has a component, sizes and a FakePlatform
  rendering for every type; every widget's strings pass the
  missing-translation check (AI assistant note §5.5).
- **Playwright (FakePlatform)**: create a page, add three widgets, resize one,
  reload, and find them as left; switch pages during a Scenario run and see it
  finish; the live diagram renders the fake map and a queue click opens it;
  every built-in page renders, which replaces today's "every screen renders".

## 12. Left to the builder

- ~~Row height, the grid's breakpoint behaviour, each widget's default and
  minimum size.~~ Settled in phase 1c (#383) for what exists so far: the row
  height is `ROW_HEIGHT_PX` in `workspace-grid.ts`, gridster's own mobile
  breakpoint stacks the widgets, and each type's default and minimum size
  live in its `WIDGETS` entry.
- Settled in phase 2 (#385), for the Stack page and what later phases reuse:
  - a polled source is a `PolledFeed` (`workspace/polled-feed.ts`), which a
    data service such as `StackFeed` owns and each widget attaches to;
  - the Compose job the Stack controls start is running work in its own
    `ComposeRun`, followed to its exit while no widget shows it;
  - a Job output widget's "last job started from this page" is `jobId: null`,
    and follows `PageJobs`, which also takes a job a Show output button opens;
  - each widget learns its instance and page from `WIDGET_CONTEXT`, which the
    grid provides;
  - the legacy Stack screen is those same widgets in a column, so their logic
    has one owner until phase 6.
- Settled in phase 4a (#389):
  - the shell's running-work indicator (§4.3, §7.2) reads `RunningWork`
    (`workspace/running-work.ts`), to which each source of running work
    reports itself when it is first created, so the shell loads none of their
    code: today `ScenarioRunner` and `ComposeRun`, each linking to its page;
  - the Scenario widget and the Saga strip are views of `ScenarioRunner`, and
    the legacy Scenario screen is the two side by side;
  - neither Scenario widget saves a script or an identity yet: the runner
    holds the one run, its script and its user, and a per-instance choice
    waits for its `fields` (the #446 bullet below).
- Settled in #437, the shell's controls (§6.2, §7.2):
  - **Configure** (edit mode only) offers a type's `presets` as a menu and
    marks the one the widget holds; it shows only for a type with more than
    one, or with `fields` (the #446 bullet below). The Note widget (§8.3)
    edits in place.
  - **Maximise** shows one widget across the page it was on, and restore
    brings the grid back. The other widgets are not rendered meanwhile, so
    what they keep is what `WidgetSessionStore` holds (§4.2).
  - **Export** is a link to `GET /api/workspace/export`, enabled once the
    layout is saved, since the host's copy is what downloads. **Import**
    reads a file in the browser, asks, and only then posts it; the host's
    400 says why a document was refused. A saved-over workspace is not
    kept: the question is the only undo.
- Settled in phase 1d (#384), the Note widget (§8.3):
  - Its Markdown is read by the Runbooks screen's reader
    (`runbook-markdown.ts`), which now keeps a fence's language, and drawn
    as elements; a `mermaid` fence is a diagram, any other fence is code.
  - A drawing passes through two walls: Mermaid's strict level with no HTML
    labels (`MERMAID_CONFIG`), then `safeSvg`, which drops script,
    `foreignObject`, event handlers and links that leave the drawing.
  - A note edits in place as a textarea while the layout is edited, and
    saves through the same debounce as any config. Its presets are starting
    points, so the header's configure control does not offer them
    (`configurable: false`); they are the two diagrams of `admin-konsol.md`,
    copied with their Russian labels, a blank note, and an empty one.
  - Mermaid loads on the first diagram (`mermaid-loader.ts`), which the
    sequence diagram (§8.2) reuses.
- Settled in #446, the configure control's settings editor (§7.2): a type
  whose settings a preset list cannot hold declares `fields` in its
  `WidgetType` (`names`, a list written with commas, or `text`), and the
  configure menu then ends in "Edit settings…", which opens a form under the
  header. It saves through the type's own `config`, like a preset. A type
  with fields is configurable even with one preset; the `listing` types and
  Job output declare theirs, and the Scenario widget's script and identity
  declare theirs when they exist.
- Settled in phase 3a (#386), the Logs widget and `LogFollowService`
  (`features/logs`, with the Logs half of #376):
  - the follow state machine of the old Logs screen moved into the root
    service unchanged; what ended it changed. A follow is ended by Stop and
    by nothing else: leaving the page, or destroying every Logs widget,
    leaves it following, and the shell's running-work indicator ("Logs:
    following", linking to the Logs page) says so. A page reload forgets the
    job, and the host's one-follow rule ends it at the next Follow;
  - a Logs widget tells the service which services it shows (`ask`, and
    `withdraw` when destroyed). The follow covers the union, or every
    service when any widget asks for every one; a change to the union while
    a follow runs, or is being asked for, replaces it, as §4.4 says; the new
    follow replays each service's last 200 lines, so the buffer starts afresh.
    With no widget left the follow keeps the set it had;
  - a line belongs to the service named before its first `|` (without
    Compose's `-1` suffix); a line with no prefix is shown only by a widget
    over every service. Clear empties the one shared buffer, not a widget's
    view;
  - the Logs built-in page is one Logs widget over every service; the
    legacy Logs screen is that widget in a thin component, kept until the
    legacy screens retire (phase 6). A widget's services are edited with the
    settings editor (#446).
- Settled in phase 3b (#387), the Broker widgets (`features/broker`):
  - `BrokerFeed` holds the four reads (queues, exchanges, bindings,
    permissions), each its own `PolledFeed` with its own error, and the two
    tools' plans. `PolledFeed` gained `refresh()` (one read now, none while
    one is out) and `attach()` now returns the function that ends the
    subscription, so a widget polls only while its own `autoRefresh` is on;
    a widget with it off reads once when it appears and on Refresh;
  - Queues, Exchanges, Bindings and Permissions each have a text `filter`
    and `autoRefresh` (off by default, saved); Queues also `parkedOnly`.
    These are `fields` of the settings editor (#446), whose kinds gain
    `flag`, a switch;
  - a Queues widget's Open on a parked queue is offered only while a
    Dead-letter workbench is on the page, and asks it to open the queue
    through `BrokerFeed.openRequest`; the workbench's own `queue` setting
    shows an Open button and reads nothing by itself, since the tool puts
    each message back flagged redelivered. A re-created workbench starts
    closed: the messages, the "dry run started" gate and the typed
    `--execute` live in the component and are empty on every render, and so
    is the BFF projection's typed rebuild confirmation (§4.2);
  - an event that changes the queues (a projection run starting or ending, a
    real move sent) reads them again, owing the read when one is out, and the
    BFF projection polls the queues while its own run goes, so its depth line
    follows the run;
  - the Broker built-in page is the six widgets (Queues, BFF projection,
    Dead letters, Exchanges, Permissions, Bindings); the legacy Broker screen
    is the same six one under another, until phase 6, with a Refresh on each
    and no polling, since auto-refresh is a setting of a widget on a
    workspace page.
- The visual design within Material 3: palette, density, spacing.
- Whether a widget's refresh button and a page-wide refresh both exist.
- The exact Mermaid mapping of each `TraceView` event kind in §8.2, within
  the rule that nothing is drawn that the timeline does not hold.
- ~~Whether Material Symbols ships as a font or as SVGs (§7.1).~~ Settled in
  phase 1a (#381): the npm-bundled `@fontsource/material-symbols-outlined`
  (the Latin 400 face, a 324 kB woff2 the browser fetches from the host once),
  because `<mat-icon>` names a ligature and a widget can then add an icon
  without registering an SVG. The theme is `theme.scss` (azure/blue palettes,
  `color-scheme` for light and dark), and the screens' status colours are the
  `--app-*` tokens in `styles.css`.

## 13. Calls this note makes

The operator handed these choices over ("your recommendations") rather than
deciding them; each is this note's call and open to their review:

- host-side persistence in `DataDir`, not `localStorage` (§6.1);
- Angular Material 3 as the component library (§7.1);
- `angular-gridster2` for the grid, with Gridstack.js as the fallback (§7.3);
- Cytoscape.js with dagre for the live diagram, Mermaid for the sequence and
  the static diagrams (§8);
- the eleven screens kept as built-in pages, reset-able and never deleted
  (§5.3);
- the layout locked outside edit mode (§7.2);
- the phase order, with a legacy screen widget bridging phases 1 to 6 (§10);
- one host follow shared by every Logs widget, and one Scenario run shown by
  every Scenario widget, with no host change (§4.4);
- untouched built-in pages saved as references, not layouts, and phase 6's
  migration of legacy widgets (§5.3);
- structural validation on the host, per-type validation in the SPA (§6.3);
- the dry-run and two-press gates reset like confirmations (§4.2);
- opt-in refresh staying opt-in, except on the live diagram (§4.3).
