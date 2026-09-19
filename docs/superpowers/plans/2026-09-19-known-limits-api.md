# Known limits: lasting API history and additive realm users — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Lift two lines from README's "Known limits". First, the API
screen's history survives leaving the screen and coming back; only a reload
of the page clears it. Second, users configured under `Admin:Users` are added
to demo/demo and browser/browser instead of replacing both, and a configured
user named like a default gives that default another password.

**Architecture:** The history moves out of `ApiPage` into a root-provided
signal store, `ApiHistory`, in `src/app/core/history/`. This is the pattern
`IdentityState` already uses so that the identity choice outlives the
screen. The store also numbers entries, so `track h.seq` stays unique across
visits. On the host, `RealmUsers.Of` merges the configured list over the
realm export's two instead of choosing one list or the other. Every consumer
already goes through it: the picker, the token lookup, the proxy's
validation and the catalog's document fetch. `ApiCatalog.BearerAsync` loses
the fallback that only a list without demo could reach.

**Tech Stack:** as phase 5: the .NET SDK `global.json` pins, minimal APIs,
xunit.v3, Shouldly, `Microsoft.AspNetCore.Mvc.Testing`; Angular standalone
with signals, Vitest via `ng test`, Playwright. Versions are
`global.json`'s, `Directory.Packages.props`' and
`src/Admin.Web/package.json`'s, not this plan's. No new packages.

**Spec:** `docs/superpowers/specs/2026-09-14-blueprint-admin-design.md`:
§2.4 (identities), §5.1 (the `Users` row), §5.6 (Identity), §6 (the API
screen's history list), §8 (no credential store). README's "Known limits"
lines, as of `e8e4f57`:

> - API history is kept only while the screen is open.
> - The identity picker offers demo/demo and browser/browser; configuring any user
>   replaces both: `--Admin:Users:0:Username=ops --Admin:Users:0:Password=…`.

---

## Measured facts (read from the code on 2026-09-19; cite, do not restate elsewhere)

**F1: Configured users replace the defaults by an explicit choice, not by the
binder.** `src/Admin.Host/Identity/RealmUser.cs`:
`RealmUsers.Of(options) => options.Users.Count > 0 ? options.Users : Defaults`.
`AdminOptions.Users` is initialised to `[]` on purpose. The comment on
`RealmUsers.Of` says why: "the configuration binder appends to an existing
list instead of replacing it". So a list initialiser holding the two defaults
would have *appended* `--Admin:Users:0:…` rather than overwritten element 0.
The replace is the `Count > 0` ternary, and
`TokenServiceTests.Configured_users_replace_the_defaults` pins it. No
binding experiment is needed; the fix is in `Of`, and `AdminOptions.Users`
keeps its empty initialiser.

**F2: Every reader of the user list goes through `RealmUsers.Of`.** The
readers:
- `IdentityEndpoints` (`GET /api/identity/users`, usernames only);
- `TokenService.ForAsync` (the password for a username sent alone);
- `RequestProxy`'s validation ("is not a configured realm user");
- `ApiCatalog.BearerAsync` (the identity the OpenAPI documents are fetched
  as: demo, else the first user).

Nothing reads `AdminOptions.Users` directly (`grep -rn "\.Users"
src/Admin.Host`). One change in `Of` therefore reaches all four.

**F3: With the merge, demo is always present, so `BearerAsync`'s other
branches become unreachable.** `users.Count > 0 ? users[0] : null` and "No
realm user is configured to fetch the document with." can no longer happen.
`ApiCatalogTests.The_catalog_identity_is_the_first_configured_user_when_there_is_no_demo`
configures only `ops` and asserts `username=ops&password=pw`; after the merge
the list is demo, browser, ops, and that test fails by design.

**F4: History lives and dies with the component.** `ApiPage` declares
`readonly history = signal<HistoryEntry[]>([])` and a `private seq = 0`
(`api-page.ts:66,83`), writes one entry per answered send, newest first,
capped by `MAX_HISTORY = 50` (`:36,263`), and restores by `restore(entry)`.
The route is lazy (`app.routes.ts`, `path: 'requests'`), so leaving the
screen destroys the component and its signal. `IdentityState`
(`core/identity/identity-state.ts`) is already `providedIn: 'root'` for
exactly this reason, per its doc comment: "so the choice survives leaving and
returning to the screen".

**F5: A history entry holds no credential the host could have given out, but
it does hold whatever the platform returned.** At `api-page.ts:263` the entry
takes these from the send:
- `headersText` through `withoutCredentialLines`;
- the request with `identity: null` and headers through
  `withoutCredentialHeaders` (`Authorization`, `Cookie`);
- the result through `withoutSetCookie`;
- `sentAs` through `redact`, which drops a custom password.

The existing specs pin each of these: "history keeps no pasted Authorization
header…", "history keeps no Cookie sent or Set-Cookie received…". What
remains is the response body and its other headers, which are the platform's
and unbounded in what they may carry.

**F6: The shell navigates without a reload.** `app.html` holds
`routerLink`s: "Stack", "Logs", "Broker", "API" (`/requests`), "Trace",
"Scenario". A Playwright test that clicks them keeps the one page, and so
keeps the root injector. `page.goto` would reload and clear it.

## Decisions this plan makes where the spec is silent

- **History is kept in memory only; no `sessionStorage`, no
  `localStorage`.** F5: an entry's response body is whatever the gateway or
  the BFF answered. Writing it to browser storage would put platform
  responses on disk under the console's origin, where they outlive the tab
  and are readable by any later page on `127.0.0.1:5300`. Spec §8 says the
  console holds the realm passwords and adds no credential store. A disk
  copy of arbitrary responses is the same kind of thing. `at` is also a
  `Date`, which does not survive JSON without a reviver. What the limit
  costs a user is a lost history on leaving the screen, and that is exactly
  what a root store fixes. A reload clearing it is stated in README as the
  remaining limit.
- **The cap stays at 50**, now owned by `ApiHistory` as `MAX_HISTORY`.
- **The store numbers entries.** `ApiPage` restarted `seq` at 0 on each
  construction. With a store that outlives the page, a new visit would
  number from 1 again, and `@for (h of history(); track h.seq)` would see
  duplicate keys. `ApiHistory.add` takes an entry without `seq` and assigns
  it.
- **A send still in flight when the screen is left is dropped, as today.**
  `ApiPage`'s `DestroyRef` unsubscribes `sends`, so its answer reaches
  neither the pane nor the history. Moving the subscription into the store
  would change what "leaving the screen" does to a request, which neither
  limit asks for. Named in README.
- **Merge order: the realm export's two first, in their order; each
  configured user either replaces the default of the same username in place
  or is appended, in configuration order.** Usernames compare ordinally, as
  `TokenService` and `RequestProxy` already compare them with `==`. A name
  configured twice takes the later password. A default cannot be removed.
  demo and browser are in the realm export (§2.4), so an entry for them is
  never false. This is stated in README.
- **`ApiCatalog.BearerAsync` fetches as demo, always** (F3). A configured
  demo changes only the password it fetches with. The dead fallback and its
  error string are deleted, not left unreachable, and the test that pinned
  "the first configured user" is replaced by one that pins the configured
  demo password.

## Global Constraints

- .NET SDK pinned by `global.json` with `rollForward: disable`.
  `TreatWarningsAsErrors`; IDE0055, IDE0065, IDE0161 fail the build. **No
  column alignment** of `=` or `=>`. No `#pragma`.
- Every `.cs` file is CRLF. The Write tool emits LF, so after creating or
  editing `.cs` files, and before every `dotnet build`/`dotnet test`, run
  `dotnet format whitespace BlueprintAdmin.slnx` from the repo root. Run
  `dotnet format BlueprintAdmin.slnx --verify-no-changes` before each commit.
- Test names are sentences with underscores (C#), or sentences (Vitest,
  Playwright).
- A comment says why and cites the owner by file or symbol; no history, no
  PR named.
- `inject()` at field level, never a constructor parameter; standalone
  components; signals for state.
- The host listens on loopback only; never add a listener, CORS header or
  credential store (spec §8). No browser storage for history (decision
  above).
- FakePlatform is first-class. `FakeKeycloak` (`src/Admin.Host/Fakes/FakeKeycloak.cs`)
  knows demo/demo and browser/browser, and the endpoint test below relies on
  it refusing any other demo password.
- British spelling; prose wraps at 80 columns.
- Commands run from the repository root; `npm` commands from `src/Admin.Web`.
  Before claiming a task done: `bash .claude/scripts/host-checks.sh all`
  green for host tasks, `bash .claude/scripts/npm-checks.sh all` green for SPA
  tasks.
- `npm ci`, never `npm install`. No package changes in this plan.
- Commits are semantic and present tense, and the body argues the change.
  Before every commit, `git branch --show-current` must not print `main`.
  End each message with the attribution trailer the session provides.
- Nothing in `../blueprint-backend` or `../blueprint-frontend` is edited.

---

## File structure

```
src/Admin.Host/Identity/RealmUser.cs                          Task 1  RealmUsers.Of merges
src/Admin.Host/Config/AdminOptions.cs                         Task 1  Users doc comment
src/Admin.Host/Api/ApiCatalog.cs                              Task 1  BearerAsync as demo
tests/Admin.Host.Tests/Identity/TokenServiceTests.cs          Task 1
tests/Admin.Host.Tests/Identity/IdentityEndpointTests.cs      Task 1
tests/Admin.Host.Tests/Api/ApiCatalogTests.cs                 Task 1
src/Admin.Web/src/app/core/history/api-history.ts             Task 2  root store (new)
src/Admin.Web/src/app/core/history/api-history.spec.ts        Task 2  (new)
src/Admin.Web/src/app/features/api/api-page.ts                Task 2  reads and writes the store
src/Admin.Web/src/app/features/api/api-page.spec.ts           Task 2
src/Admin.Web/e2e/api.spec.ts                                 Task 2
docs/superpowers/specs/2026-09-14-blueprint-admin-design.md   Task 3  §5.1, §5.6, §6
README.md                                                     Task 3  screen text, known limits
```

---

### Task 1: Configured realm users extend demo and browser

**Files:**
- Modify: `src/Admin.Host/Identity/RealmUser.cs` (`RealmUsers.Of` and its comment)
- Modify: `src/Admin.Host/Config/AdminOptions.cs` (the `Users` summary)
- Modify: `src/Admin.Host/Api/ApiCatalog.cs` (`BearerAsync`, currently lines 74–92)
- Test: `tests/Admin.Host.Tests/Identity/TokenServiceTests.cs`
- Test: `tests/Admin.Host.Tests/Identity/IdentityEndpointTests.cs`
- Test: `tests/Admin.Host.Tests/Api/ApiCatalogTests.cs`

**Interfaces:**
- Consumes: nothing from other tasks.
- Produces: `RealmUsers.Of(AdminOptions) : IReadOnlyList<RealmUser>`. The
  signature is unchanged; it now returns demo, browser, then any other
  configured users. `GET /api/identity/users` keeps its wire shape,
  `[{"username":"…"}]`.

- [ ] **Step 1: Replace the test that pins the old behaviour with two that pin the new one**

In `tests/Admin.Host.Tests/Identity/TokenServiceTests.cs`, replace the whole
`Configured_users_replace_the_defaults` fact with:

```csharp
    [Fact]
    public void Configured_users_extend_the_realm_defaults()
    {
        RealmUsers.Of(new AdminOptions()).Select(u => u.Username).ShouldBe(["demo", "browser"]);
        RealmUsers.Of(new AdminOptions { Users = [new RealmUser { Username = "ops", Password = "x" }] })
            .Select(u => u.Username)
            .ShouldBe(["demo", "browser", "ops"]);
    }

    [Fact]
    public void A_configured_user_named_like_a_default_gives_it_another_password_in_place()
    {
        IReadOnlyList<RealmUser> users = RealmUsers.Of(new AdminOptions
        {
            Users =
            [
                new RealmUser { Username = "browser", Password = "rotated" },
                new RealmUser { Username = "ops", Password = "first" },
                new RealmUser { Username = "ops", Password = "second" },
            ],
        });

        users.Select(u => $"{u.Username}/{u.Password}").ShouldBe(["demo/demo", "browser/rotated", "ops/second"]);
    }
```

- [ ] **Step 2: Add the endpoint test that proves the binding and the override end to end**

In `tests/Admin.Host.Tests/Identity/IdentityEndpointTests.cs`, add these to
the `using` block, keeping it sorted:

```csharp
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
```

Add this fact after `Users_lists_usernames_and_never_passwords`:

```csharp
    [Fact]
    public async Task Configured_users_are_listed_after_the_defaults_and_a_configured_demo_password_is_the_one_sent()
    {
        // FakeKeycloak knows demo/demo only, so a 401 for demo proves the configured password went out.
        HttpClient configured = factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration(config =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Admin:Users:0:Username"] = "ops",
                ["Admin:Users:0:Password"] = "ops-pw",
                ["Admin:Users:1:Username"] = "demo",
                ["Admin:Users:1:Password"] = "rotated",
            }))).CreateClient();

        string users = await configured.GetStringAsync("/api/identity/users", Token);
        HttpResponseMessage demo = await configured.PostAsJsonAsync("/api/identity/token", new { username = "demo" }, Token);

        users.ShouldBe("""[{"username":"demo"},{"username":"browser"},{"username":"ops"}]""");
        demo.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
```

- [ ] **Step 3: Replace the catalog-identity test that F3 makes wrong**

In `tests/Admin.Host.Tests/Api/ApiCatalogTests.cs`, replace the whole
`The_catalog_identity_is_the_first_configured_user_when_there_is_no_demo`
fact with:

```csharp
    [Fact]
    public async Task The_catalog_identity_is_demo_with_the_password_configuration_gives_it()
    {
        ScriptedHandler handler = Platform(Both);
        AdminOptions options = new()
        {
            Users = [new RealmUser { Username = "ops", Password = "pw" }, new RealmUser { Username = "demo", Password = "rotated" }],
        };

        await Catalog(handler, options).GetAsync(Token);

        handler.Requests.Single(r => r.Body != null).Body!.ShouldContain("username=demo&password=rotated");
    }
```

- [ ] **Step 4: Run the three to see them fail**

Run: `dotnet format whitespace BlueprintAdmin.slnx; dotnet test BlueprintAdmin.slnx --filter "FullyQualifiedName~Configured_users|FullyQualifiedName~A_configured_user_named_like_a_default|FullyQualifiedName~The_catalog_identity_is_demo"`

Expected: 4 tests, 3 FAIL and 1 PASS.
- `Configured_users_extend_the_realm_defaults` fails: `["ops"]` should be
  `["demo","browser","ops"]`.
- `A_configured_user_named_like_a_default…` fails: it gets
  `browser/rotated, ops/first, ops/second`.
- The endpoint test fails: it lists only `ops` and `demo`.
- `The_catalog_identity_is_demo…` already passes. The configured list holds
  a demo, so today's "demo, else the first" picks it. The test is here to
  pin the rule that Steps 5 and 6 must keep once the fallback is gone.

- [ ] **Step 5: Merge in `RealmUsers.Of`**

In `src/Admin.Host/Identity/RealmUser.cs`, replace the summary and body of
`Of` (the ternary) with:

```csharp
    /// <summary>
    /// The realm export's two users, then the configured ones: a configured user named like one of the two gives it
    /// that password in place, and any other is appended in configuration order, a name given twice taking the later
    /// password. The defaults are not a list initializer on <see cref="AdminOptions.Users"/> because the
    /// configuration binder appends to an existing list instead of replacing it.
    /// </summary>
    public static IReadOnlyList<RealmUser> Of(AdminOptions options)
    {
        List<RealmUser> users = [.. Defaults];
        foreach (RealmUser configured in options.Users)
        {
            // Ordinal, as TokenService and RequestProxy look a username up.
            int at = users.FindIndex(u => u.Username == configured.Username);
            if (at >= 0)
            {
                users[at] = configured;
            }
            else
            {
                users.Add(configured);
            }
        }

        return users;
    }
```

In `src/Admin.Host/Config/AdminOptions.cs`, replace the `Users` summary with:

```csharp
    /// <summary>Realm users added to demo/demo and browser/browser in the identity picker, or giving one of them another password (see <c>Identity/RealmUser.cs</c>).</summary>
```

- [ ] **Step 6: Fetch the documents as demo, always**

In `src/Admin.Host/Api/ApiCatalog.cs`, replace `BearerAsync`'s summary and its
first statements, from `IReadOnlyList<RealmUser> users = RealmUsers.Of(o);`
through the closing brace of `if (user is null) { … }`, with:

```csharp
    /// <summary>A token for demo, which <see cref="RealmUsers.Of"/> always holds, with any configured password; or why there is none.</summary>
    private async Task<(string? Bearer, string? Error)> BearerAsync(AdminOptions o, CancellationToken cancellationToken)
    {
        RealmUser user = RealmUsers.Of(o).First(u => u.Username == "demo");

        return await tokens.GetAsync(user.Username, user.Password, cancellationToken) switch
```

Leave the `switch` arms below unchanged.

- [ ] **Step 7: Run the host suite**

Run: `dotnet format whitespace BlueprintAdmin.slnx; bash .claude/scripts/host-checks.sh all`

Expected: build clean, every test PASS. The run includes the four above and
`Users_lists_usernames_and_never_passwords`, which still reads
`[{"username":"demo"},{"username":"browser"}]` because the factory configures
no users.

- [ ] **Step 8: Commit**

```bash
dotnet format BlueprintAdmin.slnx --verify-no-changes
git branch --show-current
git add src/Admin.Host/Identity/RealmUser.cs src/Admin.Host/Config/AdminOptions.cs src/Admin.Host/Api/ApiCatalog.cs tests/Admin.Host.Tests/Identity/TokenServiceTests.cs tests/Admin.Host.Tests/Identity/IdentityEndpointTests.cs tests/Admin.Host.Tests/Api/ApiCatalogTests.cs
git commit -F - <<'EOF'
feat(identity): add configured realm users to demo and browser instead of replacing them

RealmUsers.Of chose one list or the other: any Admin:Users entry dropped
demo/demo and browser/browser from the picker, the token lookup, the
proxy's validation and the catalog's document fetch at once. Adding ops
meant typing demo and browser back in. It now starts from the realm
export's two and lays the configured list over them: a user named like a
default gives it that password in place, any other is appended in
configuration order. The binder note stays true, which is why the defaults
are still not a list initializer.

demo is therefore always present, so ApiCatalog fetches the documents as
demo with whatever password configuration gives it. The fallback to the
first user and its "no realm user" error could no longer be reached, and
are deleted with the test that pinned them.

<attribution trailer>
EOF
```

---

### Task 2: The API history outlives the screen

**Files:**
- Create: `src/Admin.Web/src/app/core/history/api-history.ts`
- Create: `src/Admin.Web/src/app/core/history/api-history.spec.ts`
- Modify: `src/Admin.Web/src/app/features/api/api-page.ts`
- Modify: `src/Admin.Web/src/app/features/api/api-page.spec.ts`
- Modify: `src/Admin.Web/e2e/api.spec.ts`

**Interfaces:**
- Consumes: `ProxyRequest`, `ProxyResult` from `core/host/host-types.ts`;
  `IdentityChoice` is not needed.
- Produces:
  - `HistoryEntry` and `SentAs`, moved verbatim from `api-page.ts`.
  - `ApiHistory`, which is `providedIn: 'root'`, with
    `entries: Signal<HistoryEntry[]>` (newest first, at most `MAX_HISTORY`)
    and `add(entry: Omit<HistoryEntry, 'seq'>): void`.
  - `ApiPage.history` stays a readonly signal of `HistoryEntry[]`, so the
    template and every existing `page.history()` in the spec are unchanged.

- [ ] **Step 1: Write the store's failing spec**

Create `src/Admin.Web/src/app/core/history/api-history.spec.ts`:

```ts
import { TestBed } from '@angular/core/testing';
import { ApiHistory, HistoryEntry, MAX_HISTORY } from './api-history';

function entry(name: string): Omit<HistoryEntry, 'seq'> {
  return {
    at: new Date('2026-09-19T08:00:00Z'), name, operationId: null, hasCommandId: false, headersText: '',
    urlTemplate: 'http://localhost:5000/api/v1/catalog/products/', pathValues: {}, queryValues: {},
    identity: 'anonymous', sentAs: { kind: 'anonymous' },
    request: { method: 'GET', url: 'http://localhost:5000/api/v1/catalog/products/', headers: {}, body: null, identity: null, correlationId: null },
    result: { outcome: 'responded', status: 200, headers: {}, body: '', bodyTruncated: false, bodyError: null, elapsedMs: 1, correlationId: 'c' },
  };
}

describe('ApiHistory', () => {
  it('keeps entries newest first and numbers each one itself', () => {
    const history = TestBed.inject(ApiHistory);
    history.add(entry('first'));
    history.add(entry('second'));

    expect(history.entries().map((e) => e.name)).toEqual(['second', 'first']);
    expect(history.entries().map((e) => e.seq)).toEqual([2, 1]);
  });

  it('keeps at most MAX_HISTORY entries, dropping the oldest', () => {
    const history = TestBed.inject(ApiHistory);
    for (let i = 1; i <= MAX_HISTORY + 1; i++) history.add(entry(`call ${i}`));

    expect(history.entries()).toHaveLength(MAX_HISTORY);
    expect(history.entries()[0].name).toBe(`call ${MAX_HISTORY + 1}`);
    expect(history.entries().at(-1)?.name).toBe('call 2');
  });
});
```

- [ ] **Step 2: Write the page's failing spec**

In `src/Admin.Web/src/app/features/api/api-page.spec.ts`, add this directly
after `'keeps a history, newest first, and restores a past response when clicked'`:

```ts
  it('keeps the history when the screen is left and opened again, numbering the new visit after it', () => {
    host.proxy.mockReturnValueOnce(of(responded)).mockReturnValueOnce(of({ ...responded, status: 403, body: '', correlationId: 'corr-2' }));
    const first = render();
    click(first, 'Send');
    first.destroy();

    const second = render();
    let rows = Array.from(second.nativeElement.querySelectorAll('.history li button')) as HTMLButtonElement[];
    expect(rows.map((r) => r.textContent)).toEqual([expect.stringContaining('200')]);

    click(second, 'Send');
    rows = Array.from(second.nativeElement.querySelectorAll('.history li button')) as HTMLButtonElement[];
    expect(rows.map((r) => r.textContent)).toEqual([expect.stringContaining('403'), expect.stringContaining('200')]);
    expect(new Set(second.componentInstance.history().map((h) => h.seq)).size).toBe(2);

    rows[1].click();
    second.detectChanges();
    expect(second.nativeElement.querySelector('.response .correlation')?.textContent).toContain('corr-1');
  });
```

- [ ] **Step 3: Run the two specs to see them fail**

Run (in `src/Admin.Web`): `npx ng test --watch=false --include src/app/core/history/api-history.spec.ts --include src/app/features/api/api-page.spec.ts`

Expected: FAIL. The build cannot resolve `./api-history`. Once Step 4
creates the file and before Step 5 runs, the page spec's new case still
fails at its first `toEqual`: the second component starts with an empty
history.

- [ ] **Step 4: Create the store**

Create `src/Admin.Web/src/app/core/history/api-history.ts`:

```ts
import { Injectable, signal } from '@angular/core';
import { ProxyRequest, ProxyResult } from '../host/host-types';

export interface HistoryEntry {
  seq: number;
  at: Date;
  name: string;
  /** The operation selected when sent, so a restored entry resends with that operation's commandId handling. */
  operationId: string | null;
  /** Whether Send mints a fresh commandId, kept so a restore still does after a reload drops the operation. */
  hasCommandId: boolean;
  headersText: string;
  /** The URL template and parameter values as entered, so a restored entry's inputs still build its URL. */
  urlTemplate: string;
  pathValues: Record<string, string>;
  queryValues: Record<string, string>;
  identity: string;
  /** Who it went out as, without a custom password: history is not a credential store, so a restore asks for it again. */
  sentAs: SentAs;
  /** As sent, with the identity and any credential header (Authorization, Cookie) left out: none is kept, so a restore asks again. */
  request: ProxyRequest;
  result: ProxyResult;
}

export type SentAs = { kind: 'anonymous' } | { kind: 'user'; username: string } | { kind: 'custom'; username: string };

export const MAX_HISTORY = 50;

/**
 * The API screen's calls, newest first. Root-scoped, as IdentityState is, so the list survives leaving and
 * returning to the screen. Memory only: an entry keeps the platform's response body, and browser storage would
 * put that on disk under the console's origin, which spec §8 keeps free of anything credential-like.
 */
@Injectable({ providedIn: 'root' })
export class ApiHistory {
  /** Numbered here rather than per screen, so `track h.seq` never meets a number a previous visit used. */
  private seq = 0;
  private readonly all = signal<HistoryEntry[]>([]);

  readonly entries = this.all.asReadonly();

  add(entry: Omit<HistoryEntry, 'seq'>): void {
    this.all.update((all) => [{ ...entry, seq: ++this.seq }, ...all].slice(0, MAX_HISTORY));
  }
}
```

- [ ] **Step 5: Make the page read and write the store**

In `src/Admin.Web/src/app/features/api/api-page.ts`:

1. Delete the `HistoryEntry` interface, the `SentAs` type and
   `const MAX_HISTORY = 50;`, which is lines 13–36 up to but not including
   `const CUSTOM = 'custom';`.
2. Add the import after the `IdentityState` import:

   ```ts
   import { ApiHistory, HistoryEntry, SentAs } from '../../core/history/api-history';
   ```

3. Delete the field `private seq = 0;`.
4. After `private readonly router = inject(Router);` add:

   ```ts
     private readonly apiHistory = inject(ApiHistory);
   ```

5. Replace `readonly history = signal<HistoryEntry[]>([]);` with:

   ```ts
     /** Outlives the screen (core/history/api-history.ts); a reload of the page clears it. */
     readonly history = this.apiHistory.entries;
   ```

6. In `send()`, replace the `this.history.update((all) => [{ seq: ++this.seq, … }, ...all].slice(0, MAX_HISTORY));`
   line with:

   ```ts
        this.apiHistory.add({ at: new Date(), name, operationId, hasCommandId, headersText: withoutCredentialLines(headersText), urlTemplate, pathValues, queryValues, identity, sentAs, request: { ...request, headers: withoutCredentialHeaders(request.headers), identity: null }, result: withoutSetCookie(result) });
   ```

`restore(entry: HistoryEntry)`, `historyLabel(entry: HistoryEntry)` and
`redact(…): SentAs` are unchanged, and now use the imported types.

- [ ] **Step 6: Run the SPA checks**

Run: `bash .claude/scripts/npm-checks.sh all`

Expected: lint clean, and every Vitest spec PASS, including both new store
cases, the new page case, and the existing history cases ("keeps a history,
newest first…", "history keeps no pasted Authorization header…", "history
keeps no Cookie sent or Set-Cookie received…"). Each existing case gets a
fresh root injector from `TestBed`, so none sees another's entries. Build
succeeds.

- [ ] **Step 7: Cover it in the Playwright smoke**

In `src/Admin.Web/e2e/api.spec.ts`, add after
`'the api screen lists operations and sends as each identity'`:

```ts
// History is root-scoped and memory-only (src/app/core/history/api-history.ts): the shell's links change
// screens without a reload, so leaving and coming back keeps it, and a reload clears it. The last assertion
// is the boundary: a later change that persisted or rehydrated the history would fail here.
test('the api history survives leaving the screen and coming back, and not a reload', async ({ page }) => {
  await page.goto('/requests');

  await page.locator('button.op', { hasText: 'GetProducts' }).click();
  await page.getByLabel('Identity').selectOption('anonymous');
  await page.getByRole('button', { name: 'Send' }).click();
  await expect(page.locator('.response .status')).toHaveText('200');

  await page.getByRole('link', { name: 'Stack' }).click();
  await expect(page).toHaveURL(/\/stack$/);
  await page.getByRole('link', { name: 'API' }).click();

  await expect(page.locator('.history li')).toHaveCount(1);
  await expect(page.locator('.history li').first()).toContainText('200 GetProducts as anonymous');

  await page.reload();
  await expect(page.locator('button.op', { hasText: 'GetProducts' })).toBeVisible();
  await expect(page.locator('.history li')).toHaveCount(0);
});
```

Run (in `src/Admin.Web`, with the host under FakePlatform as `docs/testing.md`
describes): `npm run e2e -- e2e/api.spec.ts`

Expected: 5 passed.

- [ ] **Step 8: Commit**

```bash
git branch --show-current
git add src/Admin.Web/src/app/core/history/api-history.ts src/Admin.Web/src/app/core/history/api-history.spec.ts src/Admin.Web/src/app/features/api/api-page.ts src/Admin.Web/src/app/features/api/api-page.spec.ts src/Admin.Web/e2e/api.spec.ts
git commit -F - <<'EOF'
feat(api): keep the request history when the screen is left

The history was a signal on ApiPage, and the route is lazy, so going to
the Trace screen from "Trace this call" and back emptied it: the one move
the screen invites. It now lives in ApiHistory, root-scoped as
IdentityState already is for the same reason, and the page reads and
writes it.

The store numbers entries, because a page that restarted its counter on
each visit would hand `track h.seq` a key the retained entries already
use. It keeps the cap of 50.

It stays in memory. An entry keeps the platform's response body, and
browser storage would put that on disk under the console's origin, which
spec §8 keeps free of anything credential-like; a reload of the page
clears it, and README says so.

<attribution trailer>
EOF
```

---

### Task 3: Spec and README say what the console now does

**Files:**
- Modify: `docs/superpowers/specs/2026-09-14-blueprint-admin-design.md` (§5.1 table row, §5.6 paragraph, §6 API row)
- Modify: `README.md` (the screen paragraph; "Known limits")

**Interfaces:**
- Consumes: the behaviour of Tasks 1 and 2.
- Produces: nothing code reads.

- [ ] **Step 1: Amend spec §5.1**

Replace the `Users` row:

```markdown
| `Users` | `demo`/`demo`, `browser`/`browser` | the realm user table shown in the identity picker |
```

with:

```markdown
| `Users` | empty | realm users added to `demo`/`demo` and `browser`/`browser` (§2.4) in the identity picker; one named `demo` or `browser` gives it that password (`RealmUsers.Of`) |
```

- [ ] **Step 2: Amend spec §5.6**

After the sentence ending "`GET /identity/users` returns usernames only."
add:

```markdown
The users are the realm export's two with `Users` laid over them: a
configured name that matches one replaces its password in place, any other
is appended. The OpenAPI documents are fetched as `demo`.
```

- [ ] **Step 3: Amend spec §6**

In the **API** row of the screen table, replace `a history list` with:

```markdown
a history list that outlives the screen and not a reload, held in memory only (§8)
```

- [ ] **Step 4: Amend README**

In the screen paragraph, replace `a history of this visit's calls` with
`a history of the calls since the page was loaded`.

In "Known limits", replace the two lines

```markdown
- API history is kept only while the screen is open.
- The identity picker offers demo/demo and browser/browser; configuring any user
  replaces both: `--Admin:Users:0:Username=ops --Admin:Users:0:Password=…`.
```

with:

```markdown
- API history is kept in memory: reloading the page clears it, and a request
  still in flight when the screen is left is not recorded.
- Configured users are added to demo/demo and browser/browser
  (`--Admin:Users:0:Username=ops --Admin:Users:0:Password=…`); configuring
  `demo` or `browser` changes its password, and neither can be removed.
```

- [ ] **Step 5: Check nothing else restates the old rule**

Run: `git grep -n -e "replaces both" -e "this visit's calls" -e "only while the screen is open" -- ':!docs/superpowers/plans/'`

Expected: no output. `plans/` is the frozen delivery record and keeps the
phase 3 wording.

- [ ] **Step 6: Run everything once more**

Run: `bash .claude/scripts/host-checks.sh all; bash .claude/scripts/npm-checks.sh all`

Expected: both green. The Playwright suite ran in Task 2 Step 7. CI's
`smoke` job runs it again on the PR.

- [ ] **Step 7: Commit**

```bash
git branch --show-current
git add docs/superpowers/specs/2026-09-14-blueprint-admin-design.md README.md
git commit -F - <<'EOF'
docs: say that API history outlives the screen and configured users add to the defaults

Spec §5.1 described Users as a table whose default was the two realm
users, and README listed replacing them as a limit; §5.6 now says how the
configured list is laid over them and that the documents are fetched as
demo. §6 and README say the API history survives leaving the screen and
is cleared by a reload, which, with a send dropped when the screen is
left, is what remains of that limit.

<attribution trailer>
EOF
```

---

## Self-review

- **Coverage.** Limit 1: Task 2 (store, page, unit and e2e) and Task 3
  (§6, README). Limit 2: Task 1 (merge, the four readers through `Of`,
  catalog identity, endpoint proof against FakeKeycloak) and Task 3 (§5.1,
  §5.6, README). The wire contract of `GET /identity/users` is
  unchanged and asserted in Task 1 Step 2.
- **Names.** `ApiHistory`, `entries`, `add`, `MAX_HISTORY`, `HistoryEntry`,
  `SentAs` are the same in the store, its spec, the page and the page spec.
  `RealmUsers.Of` keeps its signature.
- **What a reviewer may reject independently.** Task 1 without Task 2, and
  the reverse; Task 3 follows whichever lands.
