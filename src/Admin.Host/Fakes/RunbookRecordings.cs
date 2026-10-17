namespace Admin.Host.Fakes;

/// <summary>
/// The game day (run-locally.md, "Game day") in FakePlatform mode. <see cref="Catalogue"/> is four entries of
/// <c>game_day.py --list --json</c> as the backend printed it on 2026-10-17, one for each confirmation the tool names
/// (<c>confirm</c>, a runbook with two phases, <c>typed</c>) and one runbook with no script. The runbook texts are three of the
/// backend's <c>docs/runbooks/</c> files as they stood that day. A cause puts its alert in <see cref="FakeStack.CausedAlerts"/>, which
/// <see cref="FakeGrafana.Rules"/> answers firing, and a restore takes it back: the tool waits for the alert on a stack and
/// the fake answers at once.
/// </summary>
public static class RunbookRecordings
{
    /// <summary>The arguments before the runbook, as <see cref="Runbooks.GameDayService.Spec"/> spells them.</summary>
    public const string Prefix = "-3.12 tools/game-day/game_day.py ";

    public static readonly string Catalogue = """""
[
  {
    "runbook": "contact-refused",
    "alerts": [
      "ContactReadRefused"
    ],
    "script": true,
    "phases": [
      {
        "alert": "ContactReadRefused",
        "impact": "data",
        "confirmation": "typed",
        "sustained": false,
        "fires_within_seconds": 300,
        "clears_within_seconds": 1980
      }
    ]
  },
  {
    "runbook": "error-rate",
    "alerts": [
      "ErrorRateGateway",
      "ErrorRateService"
    ],
    "script": true,
    "phases": [
      {
        "alert": "ErrorRateGateway",
        "impact": "outage",
        "confirmation": "confirm",
        "sustained": true,
        "fires_within_seconds": 480,
        "clears_within_seconds": 480
      },
      {
        "alert": "ErrorRateService",
        "impact": "outage",
        "confirmation": "confirm",
        "sustained": true,
        "fires_within_seconds": 480,
        "clears_within_seconds": 480
      }
    ]
  },
  {
    "runbook": "outbox-growth",
    "alerts": [
      "OutboxGrowth"
    ],
    "script": true,
    "phases": [
      {
        "alert": "OutboxGrowth",
        "impact": "outage",
        "confirmation": "confirm",
        "sustained": true,
        "fires_within_seconds": 780,
        "clears_within_seconds": 780
      }
    ]
  },
  {
    "runbook": "stuck-saga",
    "alerts": [
      "StuckSaga"
    ],
    "script": false,
    "reason": "its rule is in awaiting-signal.yaml, which the Compose Prometheus does not load (§13.6)"
  }
]
""""";

    /// <summary>The recorded runbook, or null for one the fake holds no text of: the host then says so, as it does for a missing file.</summary>
    public static string? Markdown(string name) => name switch
    {
        "outbox-growth" => OutboxGrowth.ReplaceLineEndings("\n"),
        "error-rate" => ErrorRate.ReplaceLineEndings("\n"),
        "contact-refused" => ContactRefused.ReplaceLineEndings("\n"),
        _ => null,
    };

    /// <summary>
    /// What the scripted <c>py</c> prints for <c>RUNBOOK --cause [--phase ALERT]</c> and <c>RUNBOOK --restore</c>, after putting the
    /// alerts in the state the tool would. The first phase is the default, as it is in the tool.
    /// </summary>
    public static IEnumerable<string> Run(IReadOnlyList<string> arguments, FakeStack stack)
    {
        string runbook = arguments.Count > 0 ? arguments[0] : "";
        string[] alerts = PhasesOf(runbook);

        if (arguments.Contains("--cause", StringComparer.Ordinal))
        {
            int at = arguments.ToList().IndexOf("--phase");
            string? chosen = at >= 0 && at + 1 < arguments.Count ? arguments[at + 1] : alerts.FirstOrDefault();

            if (chosen is null || !alerts.Contains(chosen))
            {
                return [$"FINDING: {runbook} has no phase {chosen}", "game day: cause FAILED in 0m01s"];
            }

            stack.Cause([chosen]);

            return [$"{chosen} is inactive; causing it", "game day: cause OK in 0m05s"];
        }

        stack.Restore(alerts);

        return [$"restored {runbook}", "game day: restore OK in 0m04s"];
    }

    private static string[] PhasesOf(string runbook) => runbook switch
    {
        "outbox-growth" => ["OutboxGrowth"],
        "error-rate" => ["ErrorRateGateway", "ErrorRateService"],
        "contact-refused" => ["ContactReadRefused"],
        _ => [],
    };

    private const string OutboxGrowth = """""
# Runbook — outbox backlog growing

| | |
|---|---|
| Alert | `OutboxGrowth`, in `deploy/observability/alerts/platform-alerts.yaml` |
| Condition | pending rows > 1000 **and rising** over 10 minutes, replicas deduplicated |
| Signal | `OutboxMetrics`, `Ordering.Infrastructure/Observability` ([§13.6](../backend-architecture/13-observability.md)) |
| Owner | The service team ([§13.8](../backend-architecture/13-observability.md)) |

## What it means

Either lane is not keeping up. This is a *ticket* rather than a page, and the
severity is doing real work: the backlog is draining more slowly than it fills,
which is a capacity problem hours away from being an outage, not an outage.

**Both halves of the condition matter.** A backlog of ten thousand rows that is
falling needs nobody — it is a recovery in progress after
[`outbox-broker.md`](outbox-broker.md), and paging on the count alone would page
during every successful recovery. The `deriv(...) > 0` half is what keeps this
alert about growth.

## Why this is not the age alert

`outbox.oldest.age` catches a lane that has **stopped**; this catches one that
is **falling behind**. Neither substitutes for the other, which is why §13.6
ships both: a single stuck row pins the age gauge at hours while the count stays
at one, and a backlog of ten thousand rows all seconds old leaves the age gauge
flat.

So check the age gauge first — if it is also high, this is a stall and you want
[`outbox-broker.md`](outbox-broker.md) or
[`projection-lag.md`](projection-lag.md) instead.

```promql
max by (service_name, lane) (outbox_oldest_age_seconds)
max by (service_name, lane) (outbox_pending_count)
```

The `lane` label says which side is growing, and they have different answers.

**`max` per lane, then sum the lanes — never `sum` over the raw series.** These
gauges read the database, so every replica exports the same number and Ordering
runs three; a plain sum reports three times the backlog and would trip a
thousand-row threshold at about 334 real rows. The alert aggregates the same
way, which is why its condition above is written in words rather than copied
from the rule.

## Is the dispatcher alive and claiming?

Count the dispatcher's delivery failures over the last ten minutes in Grafana's
Explore, on the Loki data source; the hosts log through OpenTelemetry alone
([§13.4](../backend-architecture/13-observability.md)), so `kubectl logs` shows
nothing. The dispatcher logs `Outbox message …` only when a delivery fails, so a
count above zero says it is running and failing, and zero says nothing either
way: a healthy dispatcher is silent, and so is a dead one.

```logql
count_over_time({service_name="Ordering.Api"} |~ "Outbox message" [10m])
```

```bash
kubectl -n <ns> get pods -l app=ordering
```

A dispatcher that is not running produces a backlog that rises in a straight
line with no errors at all. `AddHostedService<OutboxDispatcher>()` is registered
in `AddOrderingInfrastructure`; nothing resolves it at startup, so a lost
registration is silent and the service still reports ready.

## Batch size against arrival rate

The claim takes `TOP (100)` per pass and the loop ticks twice a second, so one
replica's ceiling is roughly 200 rows a second **if every delivery succeeds
immediately**. Compare that with what is arriving:

```promql
deriv(sum by (service_name) (max by (service_name, lane) (outbox_pending_count))[10m:])
```

If arrivals genuinely exceed the drain rate, the fixes in order of preference
are: add replicas (the claim uses `UPDLOCK, READPAST` precisely so replicas do
not collide); then raise the batch size; and only then question the tick. Note
that §9.4 treats the batch size and tick as **constants rather than
configuration** ([§15.4](../backend-architecture/15-cicd-deployment.md)
argues why), so changing either is a code change and a pull request, not a
values file.

## A slow deliverer rather than a large batch

More often the batch is fine and each delivery is slow. Look at where the time
goes:

- **Broker lane** — publish latency to RabbitMQ. A broker under memory pressure
  throttles publishers rather than refusing them, which reads as slowness.
- **Local lane** — the projection handler's own SQL. `projection.lag` is the
  instrument, and a handler doing a row-at-a-time write inside a `MERGE` loop is
  the usual finding.

```promql
histogram_quantile(0.95, sum by (le) (rate(projection_lag_seconds_bucket[10m])))
```

## Has the retention purge failed?

This is the cause people miss, because it produces a large count with a *low*
age. `RetentionPurgeService` deletes processed outbox rows past the window
(§9.4, §9.5). If it has stopped, the table grows without any delivery problem at
all — and `outbox.pending.count` counts only unprocessed rows, so a purge
failure alone should **not** move this gauge.

That makes the check diagnostic rather than corrective: if pending is high but
the table is enormous, you have two problems.

```sql
SELECT
    Pending   = SUM(CASE WHEN ProcessedAt IS NULL THEN 1 ELSE 0 END),
    Processed = SUM(CASE WHEN ProcessedAt IS NOT NULL THEN 1 ELSE 0 END),
    Oldest    = MIN(OccurredAt)
FROM ordering.OutboxMessages;
```

A large `Processed` count is a purge that is not running — check the hosted
service's logs, and remember it is registered last and therefore stopped first
on every deploy, so a crash-looping pod may never reach a purge pass.

## Mitigation

Add replicas. It is the one lever that needs no code change, and the claim
statement was written to make it safe.

Do **not** delete unprocessed rows to bring the number down. They are the only
copy of those events, and §9.4's retention predicate refuses to delete them for
exactly that reason.

## Closing it

The alert clears when the count drops below 1000 or stops rising. Confirm the
*derivative* went negative rather than the count merely plateauing at a high
level — a flat backlog of 5000 rows is not resolved, it is a queue nobody is
draining.
""""";

    private const string ErrorRate = """""
# Runbook — error rate

| | |
|---|---|
| Alerts | `ErrorRateGateway` and `ErrorRateService`, in `deploy/observability/alerts/platform-alerts.yaml` — one condition, split by §13.8's ownership |
| Condition | 5xx > 1% of requests over 5 minutes, per service |
| Signal | `http.server.request.duration`, ASP.NET Core instrumentation ([§13.2](../backend-architecture/13-observability.md)) |
| Owner | Platform for the gateway, the service team for a service ([§13.8](../backend-architecture/13-observability.md)) |

## What it means

One request in a hundred is failing with a 5xx. Users are seeing errors **now**,
which is what makes this a page rather than a ticket.

**A 4xx is not this alert and must not be added to it.** [§10.5](../backend-architecture/10-api-gateway.md)
maps `Error.Rule` to 422 and `ValidationException` to 400, both of which are the
system correctly refusing a request. Widening the condition to `4..|5..` is the
fastest way to make this pager ignorable.

## First, decide whether it is the edge or a service

The alert is grouped by `service_name`, so the label already says. The
distinction matters because the gateway has no domain and no database
([§10.1](../backend-architecture/10-api-gateway.md)) — a 5xx there is routing,
auth, or a backend that is not answering, never business logic.

```promql
sum by (service_name, http_route) (
  rate(http_server_request_duration_seconds_count{http_response_status_code=~"5.."}[5m])
)
```

If `service_name` is the gateway **and** a backend is also alerting, work the
backend first: the edge is reporting its downstream, and fixing the downstream
clears both.

## Then correlate to a deploy

Most 5xx spikes start at a rollout.

```bash
kubectl -n <ns> rollout history deploy/<workload>
kubectl -n <ns> get events --sort-by=.lastTimestamp | tail -30
```

A spike that begins within a minute or two of a rollout is that rollout until
proven otherwise. `kubectl rollout undo deploy/<workload>` is the shortest path
back, and the canary policy of [§15.5](../backend-architecture/15-cicd-deployment.md)
should already have caught it — if it did not, that is a second finding to
raise after the incident.

## Read one failing request end to end

Correlation IDs are assigned at the edge and flow through every log and span
([§10.4](../backend-architecture/10-api-gateway.md), §13.4), so one identifier
is enough to reconstruct the path.

1. Take an `X-Correlation-Id` from a failing response, or pick any from the
   error logs.
2. Query traces for it, and read the waterfall for the first span that faults.
3. Query logs for the same value across **all** services, not just the one that
   alerted — the exception is often thrown one hop in.

**The exception message is in the log, never in the response.** §10.5's
ProblemDetails deliberately carries no stack trace and no internal detail, so
reading the response body will not tell you what broke.

## The usual causes, in the order they occur

- **A dependency is down.** Check readiness for the service and its neighbours:
  a database that has gone away shows up as `/health/ready` failing
  ([§13.5](../backend-architecture/13-observability.md)) and the pod leaving the
  Service's endpoints. If readiness is green and 5xx is high, the dependency the
  failing path uses is *not* one readiness covers — say so in the postmortem,
  because that is a gap worth closing.
- **A migration ran and the code did not.** See
  [`migration-failure.md`](migration-failure.md); the reverse case — code ahead
  of schema — surfaces here as `SqlException` naming a missing column.
- **An unhandled exception on a new path.** The trace names the method. This is
  the case where a fix ships rather than a rollback, but roll back first and fix
  second.
- **Saturation.** Check CPU throttling and the connection pool before
  concluding it is a bug: a pool exhausted by a slow dependency raises
  timeouts, and timeouts surface as 5xx.

## Mitigation before diagnosis

In order of preference: roll back the deploy; scale out if the trace shows
saturation rather than a defect; shed load at the edge by tightening the
relevant §10.3 rate-limit policy. Only the first of those is a fix — the other
two buy time and must be reverted in the same incident.

## Closing it

The alert clears on its own once the ratio drops below 1% for five minutes.
Before closing, check that the errors stopped rather than the *traffic* stopped:
a service nobody can reach has an excellent error rate.

```promql
sum by (service_name) (rate(http_server_request_duration_seconds_count[5m]))
```

If that has collapsed too, this is [`business-volume.md`](business-volume.md)
and the incident is not over.
""""";

    private const string ContactRefused = """""
# Runbook — contact reads refused

| | |
|---|---|
| Alert | `ContactReadRefused`, in `deploy/observability/alerts/platform-alerts.yaml` |
| Condition | Any `notifications.contact.refused` in the alert's window, which outlasts a row's longest backoff |
| Signal | `ContactMetrics`, `src/Services/Notifications/Notifications.Infrastructure/Contacts` ([§13.6](../backend-architecture/13-observability.md)) |
| Owner | The team that owns Notifications ([§13.8](../backend-architecture/13-observability.md)) |

## What it means

Notifications' send worker reads a customer's mailbox and language from
Keycloak's admin API under its own client credential
([ADR-052](../backend-architecture/adr/ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)),
and that read was refused: by the identity provider, by the worker's own check
of the grant on the token it was issued, or by Keycloak's admin API. **Every
notification whose customer has no fresh stored contact backs off and is not
sent**, and no retry changes the answer, because the cause is a credential or
a grant. A refused read is never answered from a stale stored contact, as an
outage is: the refusal is the deployment's to fix, not a reason to send to an
address the owner may have changed.

## What is *not* affected

- **Orders, payments and shipments.** Notifications publishes nothing
  ([§3.2](../backend-architecture/03-bounded-contexts.md)); no other service
  waits on it.
- **A notification whose customer's contact is fresh.** It is sent from the
  stored row without asking Keycloak (`ContactOptions.Freshness`).
- **A row younger than its give-up age.** It backs off and is claimed again
  on a later pass. **A row that reaches `DeliveryOptions.GiveUpAge` while
  reads are refused is not**: the worker marks it `Undeliverable` with the
  reason `gave_up`, and that customer is never told.

## Telling it from an outage

Both hold rows on the `contact` step of `notifications.waiting`. An outage —
Keycloak unreachable, slow or answering 5xx — counts nothing here, serves a
stale contact where one is stored, and clears when Keycloak does. A refusal
counts here and clears only when somebody fixes it.

```promql
sum(rate(notifications_contact_refused_total[10m]))

max by (step) (notifications_waiting)
```

## Find the cause

The worker logs each refused row as `ContactRefused` — *"The contact read for
notification … was refused over this host's credential; the row backs off"* —
with the refusal as its exception. The line itself does not carry it: read the
entry's `exception_message` field in Grafana's log view, and `kubectl logs`
shows nothing, since the hosts log through OpenTelemetry alone (§13.4). Its
message names which of four it is:

| The exception says | The cause |
|---|---|
| *did not issue this host a usable token* | The identity provider refused the `notifications-worker` client: its secret is wrong or rotated on one side only, or the client is disabled |
| *realm-management role(s) where ADR-052 names* | The token no longer carries exactly the grant on `realm-management`: the `service-account-notifications-worker` user lost `view-users`, or the `notifications-worker` client's cap moved — its scope mapping changed, or full scope was turned back on ([ADR-077](../backend-architecture/adr/ADR-077-a-workers-token-is-capped-by-its-clients-scope-and-the-realm-gate-reads-the-cap.md)) |
| *a token that is not a JWT* | The realm is issuing opaque tokens to this client |
| *Keycloak refused this host's token with …* | The admin API rejected a token the worker accepted: `401` is a token it does not accept for this realm, `403` a grant it does not find |

The worker's own check refuses before the admin API is asked, so the first
three never reach it.

## Fix it

- **A secret.** The worker reads it from the Secret its chart names under
  `identity.clientSecretRef` (`deploy/helm/notifications/values.yaml`). Make
  it match the client's secret in the realm by
  [`docs/secrets.md`](../secrets.md)'s client-secret procedure, then restart
  the worker so it reads the new value.
- **A grant.** Give the service account back exactly the grant
  [`docs/secrets.md`](../secrets.md) names for it, and nothing more, and the
  client back its cap: full scope off and that role its one scope mapping
  (ADR-077). A wider grant is refused too, deliberately: the worker holding
  more than it reads is the failure ADR-052 exists to prevent.
- **An issuer or realm.** Compare `identity.authority` with
  `contactSource.baseUrl` and `contactSource.realm`; the render refuses a
  realm that is not the authority's, so a mismatch here is a base URL pointing
  at another Keycloak.

The backed-off rows are claimed again on their own, so once the rate falls to
zero, watch the `contact` step drain and the alert resolve. **A waiting set
that falls is not by itself recovery**: rows given up during the refusal leave
it too. Look for rows in `notifications.NotificationLog` marked
`Undeliverable` with `gave_up` since the refusal began; each is a customer who
was not told, and whether to tell them by hand is the business's decision.
""""";
}
