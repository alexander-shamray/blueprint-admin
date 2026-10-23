/**
 * The console's screens, in their default order: the routes, the built-in pages and the host's `WorkspaceDefaults`
 * (held to this list by a drift test) all follow it. The side navigation and the palette list the operator's pages
 * instead (widget-workspace spec §7.2).
 *
 * `help` is what the assistant explains each screen from (AI assistant design §6.1): what it shows, what it can do and
 * what it is not for. It is this registry's own text, sent in the source language; the model translates it as it
 * answers. A spec fails an entry without it.
 */
export const SCREENS = [
  {
    path: 'stack',
    label: 'Stack',
    help:
      'Runs the platform as run-locally.md does: the workstation doctor, the Compose services and their health, Up, ' +
      'Down and Reset, the frontend dev server, the golden signals, the firing alerts and a load run. Start, stop or ' +
      'wipe the backend here. It shows no log lines or traces; Logs and Trace do.',
  },
  {
    path: 'logs',
    label: 'Logs',
    help:
      "Follows the services' log output live, as docker compose logs prints it, for the services chosen, with a text " +
      'filter. Use it to watch what the platform is doing now. To follow one request across services, open Trace ' +
      'with its correlation id instead.',
  },
  {
    path: 'broker',
    label: 'Broker',
    help:
      "The RabbitMQ broker: each queue's depth, consumers and parked messages, the exchanges, bindings and " +
      "permissions, the dead letters, and the BFF projection's rebuild. Inspect a dead-letter queue and replay or " +
      'discard its messages here. It publishes no message of its own.',
  },
  {
    path: 'map',
    label: 'Map',
    help:
      'A map of the platform: its services and what each one reaches through the gateway, the broker and its ' +
      'stores. Use it to see what talks to what. It shows structure, not live traffic; Trace follows one request.',
  },
  {
    path: 'erasure',
    label: 'Erasure',
    help:
      "Raises a privacy erasure request for a customer and follows each personal-data holder's answer to it. Use it " +
      'to check that every holder erased the subject. It does not read the customer data itself.',
  },
  {
    path: 'mail',
    label: 'Mail',
    help:
      'The mail Notifications sent, as the local mail sink caught it: the list, each message, and a filter by ' +
      "correlation id. Use it to see an order's mail. It sends nothing.",
  },
  {
    path: 'requests',
    label: 'API',
    help:
      "Sends a request to the platform through the gateway as a realm user, from each service's OpenAPI " +
      'operations, and keeps a history of them. Pick an operation, an identity and a body, and see the status and the ' +
      'response exactly as the gateway returned them. This is how to call the platform by hand.',
  },
  {
    path: 'trace',
    label: 'Trace',
    help:
      "Follows one request across the services by its correlation id: Tempo's spans, the log lines and the events " +
      'on one timeline, and the same trace as a sequence diagram. Start from a correlation id, from a response, a ' +
      'log line or a mail. It does not list traces.',
  },
  {
    path: 'scenario',
    label: 'Scenario',
    help:
      'Runs the end-to-end scenario: places an order and follows it through the saga, payment, stock and shipping, ' +
      'checking each step, and shows the order saga as it moves. Use it to check the platform works as a whole. It ' +
      'holds the stack while it runs.',
  },
  {
    path: 'runbooks',
    label: 'Runbooks',
    help:
      "The backend's runbooks, read from its clone, each step with its command and the console's way to run it. " +
      'Use it during an incident or a game day. A step that changes the platform asks to be confirmed first.',
  },
  {
    path: 'transcript',
    label: 'Transcript',
    help:
      'Every command and request the console ran, as the line that would repeat it: docker compose, npm, and a curl ' +
      'for each platform request. Use it to see or copy what was done. It changes nothing.',
  },
] as const;

export type ScreenPath = (typeof SCREENS)[number]['path'];

export function isScreenPath(value: unknown): value is ScreenPath {
  return typeof value === 'string' && SCREENS.some((s) => s.path === value);
}
