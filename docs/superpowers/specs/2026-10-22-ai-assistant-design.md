# AI assistant: a provider-neutral agent in the console, MCP-ready

Status: proposed, 2026-10-22. Builds on the console design
(`2026-09-14-blueprint-admin-design.md`, "the spec" below) and the widget
workspace (`2026-10-21-widget-workspace-design.md`, "the workspace note").
Where this note changes either, §11 says how, and the PR that lands this note
amends them in the same change.

## 1. Intent

The console has grown from one page to eleven screens, and the workspace
(#378) cuts those into some thirty widget types an operator arranges
themselves. What each widget is for, which one answers a question, and where
it lives are now the hard part, not the platform. The operator asked for an
AI agent, reached over an API, that:

- explains what each screen and widget does, in the console's own words;
- takes the operator to the feature that answers a question ("where do I see
  why an order is stuck?" opens the page and points at the widget);
- answers questions about what the platform is doing now, from what the
  console can already read.

Three further asks shape the design more than the first version does:

- **Other agents later.** Claude, reached through Anthropic's API, is the first
  provider; another model, a local one included, must be an adapter, not a
  rewrite.
- **Other clients later.** The same agent core must be hostable behind another
  front end. The operator named it: the platform's own web client
  (`../blueprint-frontend`, served by the backend's BFF). That host is
  deployed, multi-user and signed in, which the console is not, so the core is
  written for many callers from the first phase (§5.2), and nothing in the
  core or the browser client knows it is in the admin console.
- **An MCP route later** (the earlier option C): external agents — Claude
  Code, Claude Desktop, others — reaching the same tools over the Model
  Context Protocol. Not built now; nothing built now may block it.

## 2. Goals, non-goals, anti-goals

**Goals**

1. An assistant panel in the shell, then an Assistant widget type, streamed as
   it is written. There is **one conversation per browser tab**, held by one
   app-wide service: the panel and every Assistant widget are views of it, and
   the panel stays once the widget exists. Its id is that service's state,
   kept in `sessionStorage` so a reload of the tab resumes it (§5.5), and
   never a widget's saved configuration.
2. Explanations drawn from one place: the `help` of each entry in the screen
   and widget registries (§6.1), never from a second copy.
3. Navigation as tool calls the browser carries out: open a page or a screen,
   point at a widget, offer a widget to add — each a view change, never a
   platform change.
4. Answers about current state and live platform data (a payment's status,
   a product's catalog entry, its stock), through read-only tools over reads
   the host already serves (§6.2, §6.5).
5. **Provider-neutral**: a provider is an adapter behind one interface (§8).
   Anthropic and a FakePlatform provider ship first.
6. **Client-neutral**: the core is a library with no reference to the host,
   ASP.NET or any admin type, and the browser client is framework-free
   TypeScript. Each host supplies its tools; each client supplies its UI tools
   and its catalogue (§5, §6).
7. **Multilingual**: the whole console, not only the assistant, is offered in
   several languages, English and Russian first (§5.5). The assistant answers
   in the caller's language, and in any language the model knows, naming the
   UI as it is labelled in that language. Each client localizes its own
   strings, and the protocol carries codes, not sentences (§5.4).
8. **MCP-shaped**: every tool is described in the Model Context Protocol's
   tool shape from the start (§6), so the MCP route of §12 is a transport and
   not a remodel.

**Non-goals**

- Building the MCP transport now (§12 is its outline).
- An agent that operates the platform, **in this note**. No Up, Down, wipe,
  Reset, replay, Scenario, runbook step or write request is a tool in any
  phase here. The operator may decide to add actions later. That is a design
  note of its own, under the conditions of §6.3, and nothing here may make it
  a remodel.
- Keeping conversations across a host restart, exporting them, or saving them
  in the workspace.
- Editing either sibling. A second client is raised in its own repository
  (§13).
- Voice, attachments, image input, more than one operator.

**Anti-goals** — this fails, however well it works otherwise, if:

- the provider's credential reaches the browser, the workspace, a log line,
  the transcript, a fixture, or any configuration file the repo carries;
- a tool that changes the platform can be registered without the operator's
  explicit decision to allow actions — the refusal is structural (§6.3), not a
  sentence in a prompt, and lifting it is a code and spec change, never a
  setting the model or a tool result can reach;
- if actions are ever allowed, one runs without the operator's confirmation in
  the UI, or a typed confirmation (`down -v`, a runbook's typed name) is typed
  by anyone but the operator;
- a realm password, `SqlPassword`, the BFF broker connection's password, the
  dead-letter operator's minted password or a JWT reaches the provider;
- the host reaches the provider without the operator having turned the
  assistant on, or the console loses any function with the assistant off, the
  network down or no credential present;
- a hidden assistant panel or widget keeps the host or the provider busy (the
  workspace note's §4.3 rule).

## 3. Constraints

- The spec's §8 holds except for the one outbound surface §11 adds: the host
  binds loopback, `/api` keeps the `Sec-Fetch-Site` rule and the framing
  denial, and there is no CORS header. The browser never talks to the
  provider; it talks to `/api` only, as the workspace note's §3 requires.
- FakePlatform is first-class: the assistant works with no credential and no
  network against the fake provider, and the Playwright smoke covers the panel.
- Exact pins only; each version at least two weeks old when pinned; the
  lockfile in the same commit. Warnings stop the build; no `#pragma`.
- Angular 22 standalone, zoneless, signals, `inject()` at field level.
- Every fact keeps one owner: the protocol's owner is the core's C# types,
  the TypeScript mirror cites them, and a shared golden file pins the JSON
  both sides read and write (§14). The owner sits in this repository through
  phase 5 and in `blueprint-backend` from phase 6 on (§13).

## 4. Shape

```
Browser (any client)                   Host (any host)                         Provider
─────────────────────                  ──────────────────────────              ─────────────
assistant-client.ts  ──POST/GET /api──► endpoints (Admin.Host/Assistant)
 · protocol types                        │
 · SSE reader                            ▼
 · client-tool registry ◄─client_tool─  AssistantSession  (Admin.Assistant)  ──► IAssistantProvider
   (open_screen, …)    ─tool-results─►   · the loop, limits, cancel             ├ AnthropicProvider
assistant-panel (Angular)                · history (append-only)                 │  (Admin.Assistant.Anthropic)
                                         · redaction of every outbound byte     ├ FakeAssistantProvider
                                         ▼                                       │  (Admin.Host/Fakes)
                                       IToolSource × n  (host's read-only tools) └ … another adapter
```

| Part | Where | Knows about |
|---|---|---|
| Core | `src/Admin.Assistant/` (class library, net10, no package beyond the BCL) | protocol, tools, providers, the loop — nothing of the admin |
| Anthropic adapter | `src/Admin.Assistant.Anthropic/` | the core and the official `Anthropic` NuGet package |
| Host wiring | `src/Admin.Host/Assistant/` | endpoints, options, the admin's tool sources, the redactor |
| Fake provider | `src/Admin.Host/Fakes/FakeAssistantProvider.cs` and its scripts | the core |
| Browser client | `src/Admin.Web/src/app/assistant/` — `protocol.ts`, `assistant-client.ts` and `client-tools.ts` import nothing from Angular; `assistant-panel.ts` is the Angular view | `/api/assistant` only |

The core's names avoid the admin's vocabulary on purpose: it says "client",
"catalogue" and "tool source", never "screen", "widget" or "stack". The
namespace is `Admin.Assistant` because that is the repository's convention.
Renaming it when it moves to `blueprint-backend` (§13) is a find-and-replace,
not a redesign.

## 5. The protocol

### 5.1 Resources

All under `/api/assistant`, all under the spec's §8 rules. The shapes are the
core's records; field names are camelCase JSON.

| Method and path | Does |
|---|---|
| `GET /api/assistant` | Status: `enabled`, `provider`, `model`, and when disabled a `reason` (`off`, `no-credential`, `unreachable`). Never the credential or where it came from. |
| `POST /api/assistant/conversations` | Opens a conversation. Body: `client` (name, version), `locale` (§5.4), `catalogue` (§6.1), `clientTools` (§6), and `hostOptions`, an object the core hands untouched to the host's tool sources in `ToolContext` (the admin reads its `identity`, a username, §6.5). Returns its `id`. |
| `POST /api/assistant/conversations/{id}/turns` | Sends the operator's message and starts a turn. Body: `text`, `context` (what the operator is looking at: the page, the screen, the widgets on it), and optionally a new `locale` or `hostOptions`. `409` if a turn is running. |
| `GET /api/assistant/conversations/{id}` | The conversation as the client shows it: each operator message, each reply's text, each tool call's `name` and `args` with its outcome, each turn's end `reason`, whether a turn is running, and `lastEventId`, the id the event stream continues from. No provider-opaque block, no tool result body. A reloaded client redraws from this and then follows `events` (§5.5). |
| `GET /api/assistant/conversations/{id}/events` | Server-Sent Events for the conversation, with `Last-Event-ID` resume from a bounded ring, as `JobStream` does for a job. |
| `POST /api/assistant/conversations/{id}/tool-results` | The result of a client tool call: `callId`, `ok`, `content`. |
| `POST /api/assistant/conversations/{id}/cancel` | Ends the running turn; a provider stream in flight is cancelled. |
| `DELETE /api/assistant/conversations/{id}` | Forgets the conversation. |
| `POST /api/assistant/conversations/{id}/confirmations` | **Reserved** (§6.3): the operator's answer to a `confirmation_required` event. Until actions are allowed it answers `404`. |

Conversations live in host memory only, bounded in number and idle time (the
limits are `AssistantOptions`', §9), and are gone after a host restart.

**A turn is running work, not polling** (the workspace note's §4.3). It goes
on when the panel or the browser tab is hidden, and the shell's running-work
indicator shows it. The client keeps the event stream open while a turn runs,
so client tool calls are still answered (they only route the SPA, which is
still loaded), and closes it once the conversation is idle and nothing showing
it is visible. A stream reopened later resumes by `Last-Event-ID`. An idle,
hidden assistant holds no stream and no timer.

### 5.2 Callers

Every conversation belongs to a `Caller`, an opaque id and a display name that
the host supplies from its own authentication. The core neither
authenticates nor stores credentials. It only keys things by caller:

- a conversation is visible to its caller alone; any other caller gets `404`,
  not `403`, so ids reveal nothing;
- `MaxConversations` applies per caller, `ConversationTokenBudget` per
  conversation (§9), and `MaxCallers` bounds the whole store;
- every tool call gets a `ToolContext` with the caller and the conversation,
  so a host's tool source can scope its results (the BFF, to the signed-in
  user's own orders) and its redactor can redact per caller.

The admin host has exactly one caller, `operator`, because the console has no
login (spec §8). The rest of this note's admin rules are that host's rules, not
the core's. No security property of the core depends on loopback.

### 5.3 Events

| Event | Data |
|---|---|
| `text` | a delta of the assistant's reply |
| `tool_started` | `callId`, `name`, `side` (`server` or `client`), and `args`: the few input values a label shows (a service, a screen). The client renders the label in its locale (§5.4) |
| `tool_finished` | `callId`, `ok` |
| `client_tool_call` | `callId`, `name`, `input` — the client runs it and posts the result |
| `turn_ended` | `reason`: `done`, `max_steps`, `budget`, `cancelled`, `refused`, `error`; and `usage` (input, output, cached tokens) |
| `error` | an RFC 9457 problem, as the rest of `/api` answers |
| `confirmation_required` | **Reserved** (§6.3): `callId`, `name`, `args`, and the confirmation the console demands, if any. Never emitted until actions are allowed, and a client ignores it until then |

A turn that ends because the provider refused carries `refused` and the
provider's category if one was given; the client says so plainly rather than
showing an empty reply.

### 5.4 Languages

The assistant speaks the caller's language. The protocol carries no prose of
its own, only the model's reply.

- **Which language the reply is in.** Opening a conversation sends a `locale`
  (BCP 47, e.g. `ru-RU`, `en-US`), and a turn may change it. The reply follows
  the language of the caller's latest message. `locale` decides only when
  that message has no language, such as a bare correlation id. The core adds
  this rule to the latest user message, after the history (§7), so a locale
  or language change never invalidates the cached prefix.
- **Names as the caller's UI shows them.** A screen, widget or page is named
  in the reply exactly as the client labels it *in the active locale*, so the
  caller can find what the reply points at. Each catalogue entry carries its
  `label` as displayed for this reason. Service, queue, exchange and
  operation names are platform identifiers and are never translated.
- **Help in the caller's language.** The client sends the catalogue in its
  active locale, with `label` and `help` taken from its translations (§5.5).
  Where an entry has no translation yet, the source-language text is sent, and
  the model translates it as it answers. A translation is the client's own
  fact, owned where its other translations live.
- **No English sentences on the wire.** Status `reason`, `turn_ended`'s
  `reason` and refusal category, a problem's `type` and the tool label's
  `name` + `args` are codes. Each client turns them into words with its own
  i18n. The framework-free client exposes them as codes and never ships text.
- **The panel's own strings** (buttons, the data notice, end reasons, tool
  labels for each code) are ordinary console strings under §5.5. The
  conversation's `locale` is the console's active locale.
- **What stays one language.** The fixed system text is English, and so are
  tool names, descriptions and schemas: one owner each, and every current
  model reads them whatever the caller's language. Tool inputs are ids. Tool
  results are passed through in whatever language the platform wrote them.
  Redaction does not depend on language.

### 5.5 The console in several languages

The operator asked for the console as a whole to be multilingual, so the
assistant panel is one user of a console-wide mechanism, not its owner.

- **Mechanism: `@angular/localize`**, first-party, already pinned in the
  workspace by `blueprint-frontend`. One mechanism across the two Angular
  clients means one way to mark, extract and review strings. Templates use
  `i18n` attributes, and code uses `$localize`. The console's source locale
  is `en-US`.
- **One build, runtime-loaded translations.** The console does not build once
  per locale. Before bootstrap, `main.ts` reads the chosen locale, fetches
  that locale's bundled translation file from the host (same origin, offline,
  as the workspace note's §3 requires) and calls `loadTranslations`, then
  bootstraps. Switching language stores the choice and reloads the page. The
  reload is an ordinary one: the workspace is read again from the host, and
  unsent input in a widget is lost, as on any reload (workspace note §4.2).
  The assistant's conversation is not lost. The client keeps its id in
  `sessionStorage` for the tab and the host still holds it. So the reloaded
  panel reads `GET /api/assistant/conversations/{id}` (§5.1) to redraw the
  whole conversation, then opens the event stream from the `lastEventId` that
  read returns. The picker warns before switching while a turn is running.
  One build keeps `wwwroot`, the host's static serving and the smoke
  unchanged.
- **Choice.** A language picker in the shell, defaulting to
  `navigator.language` when a translation for it exists and to `en-US`
  otherwise. The choice is kept in `localStorage`, per browser. It is not
  part of the workspace document.
- **Files.** `src/Admin.Web/src/locale/messages.<locale>.json`, produced and
  checked with `ng extract-i18n`: `en-US` is the source, `ru-RU` the first
  translation. A check fails the build on a message the source has and a
  shipped translation lacks, so a new string cannot ship half-translated. It
  runs in `npm-checks.sh` and the `web` workflow. Adding a language is
  adding one file and one picker entry.
- **Formats.** Dates, numbers and durations go through Angular's locale data
  for the active locale (`registerLocaleData`), never hand-formatted.
- **What is never translated**, because it is evidence or a command, not UI
  text:
  - the platform's responses, problem bodies, log lines, trace contents and
    command output, shown as the platform produced them (spec §8's
    passthrough);
  - typed confirmations (`down -v`, a runbook's typed name), which stay the
    literal text the operator types in every language;
  - service, queue, route and operation names, and curl lines in the
    transcript;
  - the host's own problem `title`/`detail` text. That stays English in this
    note. The SPA translates a host problem by its `type` where it has a
    translation, and shows the English text otherwise. Keying every host
    message is left to a later change (§16).
- **Sequencing with the workspace.** The workspace (#383–#394) is rewriting
  the eleven screens into widgets. Translating a screen that is about to be
  replaced is wasted work and a merge conflict with the phase replacing it. So
  the foundation (§15, phase L) lands first: the mechanism, the picker, the
  shell, shared components and a lint rule that refuses
  an untranslated literal in a template. The rule runs on an allow-list of
  translated files that grows with each phase, so the screens left English
  until their workspace phase do not fail it, and the final sweep makes it
  repository-wide. **After that, every workspace phase's
  definition of done includes its widgets' strings in every shipped
  language** (amended into the workspace note, §11). A legacy-screen widget
  stays English until the phase that replaces it. Screens the workspace does
  not touch get one sweep PR at the end.

## 6. Tools

A tool is a `ToolDescriptor` in the MCP tool shape — `name`, `title`,
`description`, `inputSchema` (JSON Schema), `annotations` (`readOnlyHint`,
`destructiveHint`, `idempotentHint`, `openWorldHint`) — plus one field of the
core's own: `side`, `server` or `client`. Names are snake_case and unique
across both sides; a client tool cannot shadow a server tool.

### 6.1 The catalogue

What the assistant explains is the client's, so the client sends it when it
opens a conversation: each screen or widget type with its id, title, where it
lives and its `help`. The owner of that text is the registry entry —
`SCREENS` in `src/app/screens.ts` today, `WIDGETS` (the workspace note's §5.1)
once #383 lands — which gains a `help` field: two to five sentences on what
the thing shows, what it can do, and what it is not for. A spec test fails a
registry entry with no `help`.

Each feature has one `help`, and the owner moves as the workspace grows:

- **Until #383**, the `SCREENS` entry owns it.
- **From #383 until a screen is cut into widgets**, the legacy-screen widget
  has no `help` of its own. Its catalogue entry cites the screen's.
- **Once a screen's widgets replace it** (workspace phases 2–4), each widget
  owns its own `help`. The screen's text narrows to "this page holds …",
  naming the widgets and not describing them.
- **At workspace phase 6 (#394)**, `SCREENS` goes. Its remaining text moves
  to the built-in pages' definitions, and `open_screen` becomes an alias of
  `open_page` for the built-in page of the same name.

Phase 1 builds on `SCREENS` knowing it will go: everything it builds there is
the `help` field and one tool name.

The core places the catalogue in the system prompt after the fixed
instructions, so it is part of the cached prefix and costs a cache read, not
a full price, on every turn after the first (§8.2).

### 6.2 Server tools (the admin's)

Read-only, each wrapping one host read the SPA already calls, so the assistant
reaches nothing the console did not already reach. Each source names the
endpoint it mirrors in its doc comment. The first set:

| Tool | Mirrors |
|---|---|
| `stack_status` | the Stack screen's services and reachability reads |
| `firing_alerts` | the alert strip's read |
| `queue_depths` | the Broker screen's queues read (depth, consumers, parked) |
| `recent_logs` | the output of the operator's running log follow, read from the follow job's buffered lines through `GET /api/jobs/{id}` (the Logs screen streams the same job over SSE), with a text filter and a bounded line count. Read only: it never starts, stops or replaces a follow (the host keeps one, `LogFollower`), and with none running it says so, so the model can offer `open_screen` to Logs instead |
| `trace_summary` | the Trace screen's read for one correlation id |
| `list_api_operations` | the API screen's catalogue |
| `list_runbooks` | the Runbooks screen's list |

Each result is cut to a size bound and redacted (§10) before it is added to
the conversation. A tool that fails returns an error result the model reads,
never an exception that ends the turn.

### 6.3 What may not be registered

`ToolRegistry` refuses, at registration, any server tool whose annotations
are not `readOnlyHint: true, destructiveHint: false`, and any tool of either
side whose name matches the admin's action vocabulary (`up`, `down`, `wipe`,
`reset`, `replay`, `discard`, `execute`, `run_`, `send_`, `delete`). Client
tools change the view, never the platform, and are annotated
`readOnlyHint: true` in the platform's sense. The refusal is unit-tested; a
prompt is not what stops an action.

**Room for actions later.** The refusal is this note's policy, not a law of
the core. The core enforces it through one `ToolPolicy` the host passes in.
The admin's policy is read-only, and nothing reads it from configuration. If
the operator decides to allow actions, a later note designs them under these
conditions, which this note fixes now so the protocol is ready:

- **Confirmed by the operator, in the UI, every time.** An action tool's call
  does not run when the model asks. The host emits a reserved
  `confirmation_required` event (`callId`, `name`, `args`, and the
  confirmation the console already demands for that action, if any). The
  client shows the action and its consequences, and the call runs only on
  `POST …/confirmations` with the operator's answer. A typed confirmation
  stays typed by the operator: the model can propose the action, never the
  text. A refusal or a timeout returns an error result. The event name and
  the resource are reserved in phase 1, and the client ignores the event until
  then.
- **The same path as the button.** An action tool calls the endpoint the
  console's own button calls, with the guards it already has (the lease,
  `Sec-Fetch-Site`, the confirmation check). It never gets a second path to
  the platform.
- **Per action, opted in.** The policy allows named tools one at a time, never
  "all writes". Each tool is annotated `destructiveHint` honestly and is
  transcribed as the assistant's.
- **Spec first.** Spec §13's bullet (§11) is amended in the same change.

### 6.4 Client tools (the admin SPA's)

| Tool | Does |
|---|---|
| `open_screen` | routes to a screen by its `SCREENS` path |
| `open_page` | switches to a workspace page by id or title (after #383) |
| `highlight_widget` | scrolls to and outlines a widget instance or the first of a type on the page (after #383) |
| `propose_widget` | shows an "Add *type*" chip in the reply; the operator's click adds it, through the workspace's own add path (after #383) |
| `follow_correlation` | fires the workspace's "follow the last Trace link" for an id (after #390) |

A client tool the client does not answer within the timeout is an error
result, not a hung turn.

### 6.5 Server tools: platform data

The assistant answers from the platform's live data. Examples: *what is the
payment status of order X*, *what does product Y's catalog entry say*, *how
many are in stock*, *what is reserved for this order*, *where is the order's
saga*. It reads that data the way the API screen does: through the gateway,
as a realm identity, with `RequestProxy`.

- **Derived, not copied.** `PlatformReadTools` builds one tool per **GET**
  operation in `ApiCatalog`, which holds both the OpenAPI documents' operations
  and `CuratedOperations`' buyer-order reads. Tool name, description and
  `inputSchema` come from the operation's id, summary and parameters (path,
  query and paging). Nothing in the tool list restates a route, so a new GET
  in the backend's OpenAPI is a new tool, and a removed one disappears. The
  list is sorted by name so the cached prefix is deterministic, and it changes
  only when the catalogue does. Today that gives tools such as `get_product`,
  `get_products`, `get_stock`, `get_reservation`, `get_payment`,
  `get_order_saga_state` and the BFF's order reads. The final list is
  whatever the catalogue holds.
- **GET only, by construction.** The builder takes GET operations and nothing
  else. A tool cannot send a body. §6.3 refuses any descriptor that is not
  read-only, so a misread catalogue cannot slip a write in.
- **Which identity.** For each operation, the first realm user in
  `AdminOptions.Users`, in order, whose token carries the permission the
  route's policy in `GatewayRoutes` requires. The host reads it from the
  token's `permission` claim, as `IdentityEndpoints.Permissions` already
  does. An `authenticated` route takes the first user and an anonymous one
  takes none. With no user holding the permission, the tool says so and
  sends nothing. The panel can pin one user for the conversation instead,
  as the API screen's identity picker holds one choice for the screen.
  Passwords and tokens stay inside `TokenService`. The model sees a username
  at most.
- **What comes back.** Status, the body (cut to the size bound, with
  `truncated` set when cut), and the request's correlation id, all after
  redaction (§10). Never headers. A 401/403/404/409/422 is returned as the
  gateway sent it, so the model reports it and does not guess (spec §8's
  *do not invent platform behaviour*). The correlation id lets the reply
  offer `follow_correlation` to the Trace page.
- **Which services.** `AssistantOptions.PlatformReads` lists the services
  whose GETs become tools. The default is Catalog, Inventory, Ordering,
  Payments and the BFF. Privacy is excluded by default: its reads are about
  erasure subjects, and sending them to a provider should be the operator's
  explicit choice.
- **Audited.** Every read goes through `RequestProxy`, so it lands in the
  operator transcript as the curl that would repeat it, marked as the
  assistant's. The operator can see exactly what the assistant asked the
  platform.
- **FakePlatform.** The fakes already answer these operations for the API
  screen, so the tools work against them unchanged.
- **Another host.** The BFF of §13 derives the same way from its own
  operations. It calls them as the signed-in caller (`ToolContext`, §5.2)
  instead of a realm user, so a customer can only ask about their own
  payment.

## 7. The loop

`AssistantSession` owns it; providers do one turn each (§8).

1. Build the request in cache order: the tools from every source and the
   client, the fixed system text and the catalogue, then the history. This
   turn's client context and the language rule (§5.4) ride in the latest
   user message, after the history, so nothing that changes per turn sits in
   front of a part that could be reused.
2. Stream the provider's turn, relaying `text` as it arrives.
3. If the turn asked for tools: run every server call concurrently with a
   timeout, emit every client call and wait for its result, then add **all**
   results to the history as one message, and go to 2.
4. Stop at the provider's end, at `MaxSteps` tool rounds, at the conversation's
   token budget, on cancel, or on a refusal; emit `turn_ended` with the reason.

The history is **append-only**. Nothing earlier in a conversation is edited,
reordered or trimmed by the core, and every provider-opaque block a provider
returns (Claude's thinking blocks among them) is kept as an opaque payload on
its assistant message and handed back to the same adapter unchanged: current
Claude models reject or drop reasoning whose history was edited. When a
conversation outgrows its budget it ends with `budget`; the client offers a
new one. Compaction is left for later (§15).

## 8. Providers

### 8.1 The interface

```csharp
public interface IAssistantProvider
{
    string Id { get; }
    ValueTask<ProviderStatus> StatusAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<ProviderEvent> TurnAsync(TurnRequest request, CancellationToken cancellationToken);
}
```

`TurnRequest` carries the system text, the catalogue, the tool descriptors,
the history and the limits. `ProviderEvent` is one of `TextDelta`,
`ToolCall` (id, name, input as JSON), `Opaque` (blocks to replay), `Usage`
and `End` (reason, and a refusal category if any). An adapter translates
descriptors to its API's tool format and back; nothing above it sees a
vendor type.

### 8.2 Anthropic

- The official `Anthropic` NuGet package, exact pin, in its own project so the
  core carries no vendor dependency.
- The adapter takes an SDK client its host builds, and never builds one
  itself. The admin host builds it with the SDK's own credential resolution
  (`ANTHROPIC_API_KEY`, or the profile `ant auth login` stores), so the
  console stores no credential and no configuration key holds one. With none
  present, status is `no-credential`. A deployed host builds the client from
  its own secret store.
- Model `claude-opus-5-5` by default, effort `low` for chat; both are
  `AssistantOptions` settings, so a cheaper model such as `claude-haiku-5-5`
  is a configuration change, the operator's to make.
- Streaming always; adaptive thinking (the model's default, so the parameter
  is omitted); `tool_choice` `auto`, since the current models refuse a forced
  one; `strict: true` on every tool.
- A prompt-cache breakpoint after the tools, the system text and the
  catalogue (the part that does not change between turns) and a second at
  the end of the history, so each request reuses the conversation so far.
  Nothing that varies (a time, a page id, the turn's context) comes before
  either. `usage.cache_read_input_tokens` is
  reported in `turn_ended`, so a broken cache shows.
- The server-side refusal fallback on (`fallbacks: "default"`), and a
  `refusal` stop reason mapped to `End(refused)`.

### 8.3 Fake

`FakeAssistantProvider` plays scripts: a message matching a script's pattern
yields its recorded text and tool calls, so e2e can ask "where are the logs?"
and see `open_screen` carried out, and a turn can exercise a server tool
against FakePlatform. Its scripts sit with the other recordings under
`src/Admin.Host/Fakes/`. FakePlatform selects it only when `Provider` is
**unset**. An explicit `off` wins on FakePlatform too, so the fakes can run
with the feature off (§9.1).

### 8.4 Another provider

A project beside `Admin.Assistant.Anthropic`, an `IAssistantProvider`, and one
more value `Admin:Assistant:Provider` accepts. A provider on the workstation
(a local model server on loopback) adds no outbound surface at all.

## 9. Configuration

`AssistantOptions`, nested in `AdminOptions` as `Admin:Assistant`, owns:
`Provider` (`off`, `anthropic` or `fake`; unset by default, which means
`off`, except under FakePlatform, where it means `fake`), `Model`, `Effort`,
`MaxSteps`, `MaxOutputTokens`, `ConversationTokenBudget`,
`ClientToolTimeout`, `ServerToolTimeout`, `MaxConversations`, `MaxCallers`,
`IdleTimeout`, `PlatformReads` (§6.5).
No option holds a credential, and the options validator refuses one named
like a secret, as the workspace validator does (workspace note §6.4).

### 9.1 Off is a configuration switch

The AI feature is optional, and turning it off is configuration alone:
`Admin:Assistant:Provider=off` in `appsettings.json`, an environment
variable (`BLUEPRINT_Admin__Assistant__Provider=off`), or
`--Admin:Assistant:Provider=off` on the command line, as with every other
`Admin:` key. Leaving it unset is off too,
except under FakePlatform, where unset selects the fake provider (§8.3). With
it off:

- **The host reaches no provider and builds none.** No adapter is
  constructed, no SDK client is created, and no credential is resolved. The
  `Admin.Assistant.Anthropic` assembly is never loaded.
- **The endpoints shrink to one.** `GET /api/assistant` answers
  `enabled: false, reason: off`. Every other `/api/assistant/*` route answers
  `404`, so nothing can open a conversation.
- **The UI shows no trace of it.** The SPA reads that status once at startup
  and renders no panel, no shell toggle, no command-palette entry, and no
  Assistant type in the widget picker. An Assistant widget already saved in a
  workspace is kept, not deleted, and renders a one-line "the assistant is
  turned off in the console's configuration" placeholder. Turning the
  feature back on restores it as it was.
- **Nothing else changes.** No screen or widget depends on the assistant.
  `help` in the registries stays, because it is the registries' own text.
  Phase L's languages are a console feature, not part of the AI feature, and
  are unaffected.

Changing the setting takes a host restart, like every other `Admin:` option.
The SPA picks up the new status on its next load.

`ConversationTokenBudget` caps **context size**: the input tokens of the
latest provider request, cached tokens included. A conversation whose next
request would pass it ends with `budget`. It is not a spend cap: spend per turn
is bounded by `MaxSteps` × `MaxOutputTokens` and by the context cap, and
`turn_ended` reports both the turn's usage and the conversation's running
total, so spend is visible.

## 10. Privacy and trust

- **Redaction.** Every byte the core sends a provider — the operator's text and
  every tool result — passes `IRedactor` first. The admin's redactor applies
  `FixtureScrubber`'s rules and then replaces, as exact values, every secret
  the host knows: each realm user's password, `SqlPassword`, the password in
  `BffBrokerConnection`, and the dead-letter operator's password while one is
  minted. A gate test runs every server tool against FakePlatform with known
  secrets planted and fails if one survives.
- **Untrusted results.** Logs, traces and API bodies are data an attacker may
  have written; a seller's product description is exactly that. The system
  text says so, and §6.3 makes the worst a steered model can do a wrong answer
  or a wrong navigation.
- **Notice.** The panel says which provider the conversation goes to, and the
  first message of a session waits on a one-time "questions and the console
  data the assistant reads go to *provider*" acknowledgement, kept in
  `localStorage` per browser.
- **Transcript.** The assistant runs no command. Its platform reads (§6.5)
  pass through `RequestProxy`, which transcribes them as the API screen's are,
  marked as the assistant's. Host logs record each turn's provider, model,
  usage and end reason, never its text.

## 11. Changes to the spec and the workspace note

- Spec §8 gains a paragraph: the assistant is the console's one outbound
  surface beyond the platform, off unless `Admin:Assistant:Provider` names a
  provider (or is left unset under FakePlatform, which then uses the fake
  provider), reached only by the host, carrying redacted data; the browser
  still reaches only the host.
- Spec §13 gains: "An assistant that operates the platform, until the
  operator decides otherwise. The assistant explains, navigates and reads. It
  has no tool that changes the platform (AI assistant note §6.3, which sets
  the conditions for adding one)."
- The workspace note gains, in §10: "From phase L of the AI assistant note on,
  a phase is not done until its widgets' strings are in every shipped
  language (AI assistant note §5.5)", and in §11, the same check under
  SPA tests.
- The workspace note's anti-goal "no new surface the host reaches" gains
  "except the assistant's provider (AI assistant note §11)", and its §5.2
  catalogue gains the Assistant widget row.
- Spec §6's screens table, README and `CLAUDE.md`'s owners table gain the
  assistant's owners as each phase lands.

## 12. The MCP route, later

What it will be, so nothing now blocks it:

- A separate process, `src/Admin.Mcp/`, speaking MCP over **stdio** to the
  agent that launched it and HTTP to the host's `/api` on loopback — a client
  of the host like the SPA. stdio needs no new listener, so the spec's §8
  binding is untouched, and a non-browser request carries no `Sec-Fetch-Site`
  or carries `none`, which `/api` already admits.
- Its tools are the server tools of §6.2 and §6.5, served by a new
  `GET /api/assistant/tools` and `POST /api/assistant/tools/{name}`, through
  the same registry, limits and redactor. Descriptors are already MCP's shape.
- Its resource is the catalogue, which by then needs a host-readable copy:
  the SPA build emits the registry's catalogue as a JSON asset under
  `wwwroot`, and the panel and the MCP route both read that one file.
- Client tools reach a browser only if one is open; whether the MCP route
  offers them at all is that phase's decision.

## 13. Another client, and where the shared code lives

The second consumer is the platform's web client, so the shared code must
live where both the console and the platform can use it. The operator chose
**`blueprint-backend`, and no new repository** (2026-10-22):

| Piece | Lives in | How others get it |
|---|---|---|
| Core and Anthropic adapter, and the protocol: its C# types and §14's golden JSON files | this repository through phase 5, then `blueprint-backend` as building blocks | NuGet packages from GitHub Packages, pinned exactly |
| Browser client (`protocol.ts`, `assistant-client.ts`) | each Angular client keeps its own | copied, cited and drift-checked against the backend's golden files, as `GatewayRoutes` and `PlatformQueues` already are |
| Tool sources, redactor, catalogue, client tools, panel, fake provider | each host and each client | not shared |

The reasons for this split:

- The backend owns the core's production host, the BFF, and holds the same
  C# dialect and analysis policy (ADR-019).
- The console is already a client of the backend. Depending on a package the
  backend publishes points ownership the right way. A package published from
  this repository would make a tool that is never deployed the owner of
  production code.
- The browser client is small, and this repository's copied-and-cited
  pattern already exists. An npm package would add a publishing path for
  three files.
- A new repository would add its own CI, releases and rules, and would buy
  nothing the backend does not already give.

**When it moves.** Phases 1–5 build and change the core here, where the
FakePlatform loop is fastest and the protocol can still move. Before the BFF
uses it:

- the backend takes the core in and publishes it
  (alexander-shamray/blueprint-backend#1346);
- the console then consumes the package and mirrors the browser client
  under the drift gate (phase 6, #435).

From then on, a change to the core is a backend PR and a version bump here,
as the spec's rule against editing a sibling requires.

**The web client's side**:

- the BFF hosts the core for signed-in customers, maps each user to a
  `Caller`, and calls the platform with the customer's own token
  (alexander-shamray/blueprint-backend#1347);
- the web client mirrors the browser client and builds its own panel, UI
  tools and translated catalogue
  (alexander-shamray/blueprint-frontend#326).

That host also needs what the console lacks: authentication on the §5
resources, rate limits, a credential from its secret store, and a decision,
by ADR, on what customer data may go to the provider. Those are its design,
not this note's.

## 14. Testing

- **Core (xunit, new `tests/Admin.Assistant.Tests/`)**: the loop against a
  scripted provider — text relay, a server round, a client round, both in one
  round with all results in one message, `MaxSteps`, budget, cancel, a refusal,
  a client-tool timeout; the registry refusals of §6.3; history append-only
  with opaque blocks returned unchanged.
- **Anthropic adapter**: descriptor translation and event mapping against
  recorded stream bodies, no network.
- **Host**: the endpoints — status with each `reason`, `409` on a second turn,
  SSE resume, the conversation read after more events than the ring holds
  (every message present, `lastEventId` continuing the stream, no opaque
  block or tool body), unknown conversation `404`, the `Sec-Fetch-Site`
  rule; the redaction gate of §10; options refusing a secret-named key; with
  `Provider=off`, on FakePlatform too: status reports `off`, every other
  route is `404`, and no provider or SDK client is resolved from the
  container.
- **Protocol**: one golden JSON file per event and body, read by a host test
  and a Vitest spec, so the C# owner and the TypeScript mirror cannot drift.
- **SPA (Vitest)**: the client parses events, runs a client tool and posts its
  result, resumes; after a reload it redraws from the conversation read and
  follows from its `lastEventId`; the panel streams, shows a refusal,
  cancels, holds no timer when hidden; every registry entry has `help`.
- **Languages**: the language rule and the turn's context ride in the latest
  user message, and a locale change leaves the prefix byte-identical.
  Every code a client receives has a translated message in every shipped
  locale (a Vitest spec walks the protocol's code unions). The extraction
  check of §5.5 fails a missing translation. Each shipped locale bootstraps,
  formats a date and a number, and keeps its typed confirmations literal. The
  fake provider's scripts match Russian and English phrasings.
- **Playwright (FakePlatform, `Provider=off`)**: no panel, toggle, palette
  entry or picker entry; a workspace holding an Assistant widget renders its
  placeholder, and every other widget is unchanged.
- **Playwright (FakePlatform)**: open the panel, ask where the logs are, land
  on Logs; ask the same in Russian ("где логи?"), land on Logs, and see the
  reply name **Logs** as labelled; switch the console to `ru-RU` and see the
  shell and the panel in Russian and the reply name the screen by its Russian
  label; ask about the stack and see a server tool's label.

## 15. Phases

Each phase is one or more PRs and leaves the console working.

| Phase | Delivers | Needs |
|---|---|---|
| L Languages | §5.5's foundation: `@angular/localize` with runtime-loaded translations, the shell's language picker, `messages.en-US.json` and `messages.ru-RU.json`, the shell and shared components translated, the missing-translation check and the template lint rule on its growing allow-list, locale-aware formats | — |
| 1 Core | `Admin.Assistant`, the protocol, the endpoints, `AssistantOptions` (off by default), the fake provider, the redactor, the shell's assistant panel, `help` on `SCREENS`, `open_screen`, locale and the language rule, panel strings under §5.5; e2e | L |
| 2 Anthropic | `Admin.Assistant.Anthropic`, credential status, the notice, prompt caching and usage reporting | 1 |
| 3a Console reads | the server tools of §6.2 and the redaction gate over each | 1 |
| 3b Platform data | `PlatformReadTools` (§6.5): GET operations as tools, identity choice and the panel's override, `PlatformReads`, transcript marking; e2e asking a product's stock and an order's payment status on FakePlatform | 1 |
| 4 Workspace | `help` on `WIDGETS`, the Assistant widget type, `open_page`, `highlight_widget`, `propose_widget`, `follow_correlation` | 1, #383 (and #390 for the last) |
| 5 MCP | §12, as its own decision | 3a, 3b |
| 6 Package | the console consumes the core from `blueprint-backend`'s package and drops its own copy; `protocol.ts` and `assistant-client.ts` become a copied, cited mirror of the backend's golden files under the `drift` job; `CLAUDE.md`'s owners table names the backend (#435) | blueprint-backend#1346 |

Phases 2, 3a and 3b can run in parallel. Compaction of long conversations
waits until use shows it is wanted.

## 16. Left to the builder

- The fixed system text, within §10's rule on untrusted data.
- The default limits in `AssistantOptions`, and the size bound on a tool result.
- The panel's look within Material 3, and where its toggle sits in the shell
  and the command palette.
- The exact read each server tool wraps, within §6.2's rule that it is one the
  SPA already calls.
- The fake provider's script format.
- Translation quality and terminology, and which shared components phase L
  covers beyond the shell.
- Keying the host's own problem messages for translation (§5.5); this note
  leaves them English.

## 17. Calls this note makes

The operator accepted the recommendations wholesale ("все твои рекомендации
принимаются") and chose option B with C to follow; each of these is this
note's call and open to their review:

- the agent loop in the host, not in the browser, so no credential is ever in
  a page (§4);
- a core library with no admin type in it, a vendor adapter in its own
  project, and a framework-free browser client (§4), as the meaning of
  "universal";
- because the second client is `blueprint-frontend` (the operator's answer),
  callers, per-caller limits and a `ToolContext` in the core from phase 1
  (§5.2), and an adapter that takes a client its host builds (§8.2);
- MCP's tool shape for every descriptor from day one, and stdio rather than a
  listener for the MCP route (§6, §12);
- the catalogue sent by the client, its text owned by the registries (§6.1);
- the shared core in `blueprint-backend`, not in a new repository, as the
  operator decided. It is built here through phase 5, then moved and
  consumed as a package. Each Angular client keeps a copied, cited and
  drift-checked mirror of the browser client, not an npm package (§13);
- conversations in host memory only (§5.1);
- read-only now with room for actions later: a host-supplied `ToolPolicy`,
  and a reserved `confirmation_required` event and `confirmations` resource,
  so a later decision to allow actions extends the protocol without remodelling
  it (§6.3);
- platform data as tools derived from `ApiCatalog`'s GET operations, sent as
  the first configured realm user whose token's permission claim satisfies
  the route, transcribed, with Privacy left out
  by default (§6.5);
- multilingual support, as the operator asked, for the whole console:
  `@angular/localize` (the web client's mechanism) with one build and
  translations loaded at startup, a reload to switch; English source and
  Russian first; evidence and typed confirmations never translated; the
  foundation first, with each workspace phase translating its own widgets
  rather than a sweep over screens about to be replaced (§5.5). The reply
  follows the language of the latest message, with `locale` as the fallback,
  and names the UI by its labels in the active locale (§5.4);
- one conversation per browser tab, shared by the panel and every Assistant
  widget (§2);
- a turn is running work that outlives a hidden panel, and an idle hidden
  assistant holds nothing open (§5.1);
- `recent_logs` reads the operator's running follow and never starts one
  (§6.2);
- the token budget caps context size, not spend (§9);
- `help` moving from `SCREENS` to widgets to built-in pages as the workspace
  phases land (§6.1);
- `claude-opus-5-5` at effort `low` as the default, the operator free to pick
  another (§8.2);
- off by default, with a one-time data notice (§9, §10). Off removes the
  feature completely: no provider built, one status route, no UI. An explicit
  `off` beats FakePlatform's default, and saved Assistant widgets are kept as
  placeholders (§9.1), as the operator asked that the feature be optional and
  switchable from configuration.
