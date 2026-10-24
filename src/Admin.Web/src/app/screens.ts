/**
 * The console's screens, in their default order: the routes, the built-in pages and the host's `WorkspaceDefaults`
 * (held to this list by a drift test) all follow it. The side navigation and the palette list the operator's pages
 * instead (widget-workspace spec §7.2).
 *
 * `help` is what the assistant explains each screen from (AI assistant design §6.1). The widgets own what each one shows
 * and does (`WidgetType.help`), so a screen's text says what its page is for and names the widgets it holds, which a
 * spec holds to the page's built-in layout. It is this registry's own text, sent in the source language; the model
 * translates it as it answers. A spec fails an entry without it.
 */
export const SCREENS = [
  {
    path: 'stack',
    label: 'Stack',
    help:
      'The page that runs the platform as run-locally.md does. It holds the Workstation doctor, Compose services, ' +
      'Stack controls, Alerts, Job output, Reachability, Golden signals, Load run and Reference client widgets. It ' +
      'shows no log lines or traces; the Logs and Trace pages do.',
  },
  {
    path: 'logs',
    label: 'Logs',
    help:
      "The page for the services' live log output. It holds the Logs widget. To follow one request across services, " +
      'open the Trace page with its correlation id instead.',
  },
  {
    path: 'broker',
    label: 'Broker',
    help:
      'The page for the RabbitMQ broker. It holds the Queues, BFF projection, Dead-letter workbench, Exchanges, ' +
      'Permissions and Bindings widgets. It publishes no message of its own.',
  },
  {
    path: 'map',
    label: 'Map',
    help:
      'The page that shows the platform as a table of services, routes and queues. It holds the Platform map widget. ' +
      'It shows structure, not live traffic; the Trace page follows one request.',
  },
  {
    path: 'erasure',
    label: 'Erasure',
    help:
      'The page for privacy erasure requests. It holds the Erasure widget. It does not read the customer data ' +
      'itself.',
  },
  {
    path: 'mail',
    label: 'Mail',
    help: 'The page for the mail Notifications sent. It holds the Mail widget. It sends nothing.',
  },
  {
    path: 'requests',
    label: 'API',
    help:
      'The page for calling the platform by hand through the gateway. It holds the API request and API history ' +
      'widgets.',
  },
  {
    path: 'trace',
    label: 'Trace',
    help:
      'The page for following one request across the services by its correlation id. It holds the Trace widget, and ' +
      'a correlation id in a response, a log line or a mail opens it here. It does not list traces.',
  },
  {
    path: 'scenario',
    label: 'Scenario',
    help:
      'The page for the end-to-end scenario. It holds the Scenario and Saga strip widgets, and it holds the stack ' +
      'while the scenario runs.',
  },
  {
    path: 'runbooks',
    label: 'Runbooks',
    help:
      "The page for the backend's runbooks, for an incident or a game day. It holds the Runbooks widget. A step " +
      'that changes the platform asks to be confirmed first.',
  },
  {
    path: 'transcript',
    label: 'Transcript',
    help:
      'The page for what the console ran, as the lines that would repeat it. It holds the Transcript widget. It ' +
      'changes nothing.',
  },
] as const;

export type ScreenPath = (typeof SCREENS)[number]['path'];

export function isScreenPath(value: unknown): value is ScreenPath {
  return typeof value === 'string' && SCREENS.some((s) => s.path === value);
}
