import { TraceEvent, TraceEventKind, TraceView } from '../../../core/host/host-types';

/** What a timeline draws as: the Mermaid source, and the events behind its numbered arrows, in the order they are numbered. */
export interface Sequence {
  source: string;
  arrows: TraceEvent[];
}

/** The longest label an arrow or note carries; the timeline beside it holds the whole line. */
const LABEL_MAX = 90;

const CALLER = 'C';
const BROKER = 'B';

/**
 * Text that is safe to put after a colon or an `as` in a sequence diagram. `;` ends a statement, `#` opens an entity
 * code and `%%` a comment, and a newline ends the line, so none of them survives; what is left is only ever a label.
 */
export function label(text: string): string {
  const flat = text
    .replace(/[\r\n\t]+/g, ' ')
    .replace(/[;#]/g, ',')
    .replace(/%%/g, '%')
    .replace(/\s+/g, ' ')
    .trim();
  return flat.length > LABEL_MAX ? `${flat.slice(0, LABEL_MAX - 1)}…` : flat;
}

/**
 * The Mermaid `sequenceDiagram` of a timeline (widget-workspace spec §8.2). Nothing is drawn that the timeline does not
 * hold: a participant is a service one of its drawn events names (the caller of an inbound request is not named, so it
 * is only "Caller"), and an arrow is only ever a hop the events say happened.
 *
 * | Event | Drawn as |
 * |---|---|
 * | `HttpIn` | an arrow from the caller to the service, with the span's duration in its words |
 * | `Span` | an arrow of the service to itself |
 * | `Publish` | an arrow from the service to the broker |
 * | `Consume` | an arrow from the broker to the service |
 * | `Saga`, `Outbox` | a note over the service |
 * | `Queued` | a note over the broker |
 * | `Error` | a note over the service that begins "failed", on a shaded band |
 * | `Log` | nothing: a log line is not a hop |
 *
 * Participants are declared first-appearance order with an id of their own, never the service's name, so a name cannot
 * break out of its line. Returns null when the timeline holds nothing but logs and the broker's closing line.
 */
export function sequenceOf(view: TraceView): Sequence | null {
  const drawn = view.events.filter((e) => e.kind !== 'Log');
  if (!drawn.some((e) => e.kind !== 'Queued')) return null;

  const names: string[] = [];
  const seen = new Set<string>();
  const ids = new Map<string, string>();
  const declare = (id: string, name: string): string => {
    if (!seen.has(id)) {
      seen.add(id);
      names.push(`participant ${id} as ${label(name)}`);
    }
    return id;
  };
  const brokerName = view.events.find((e) => e.kind === 'Queued')?.service ?? 'Broker';
  const service = (name: string): string => {
    let id = ids.get(name);
    if (id === undefined) {
      id = `S${ids.size}`;
      ids.set(name, id);
    }
    return declare(id, name);
  };

  const lines: string[] = [];
  const arrows: TraceEvent[] = [];
  const arrow = (from: string, to: string, event: TraceEvent): void => {
    arrows.push(event);
    lines.push(`${from}->>${to}: ${label(event.summary)}`);
  };
  const note = (over: string, text: string): void => {
    lines.push(`Note over ${over}: ${label(text)}`);
  };

  for (const event of drawn) {
    const kind: TraceEventKind = event.kind;
    switch (kind) {
      case 'HttpIn': {
        const from = declare(CALLER, 'Caller');
        arrow(from, service(event.service), event);
        break;
      }
      case 'Span': {
        const id = service(event.service);
        arrow(id, id, event);
        break;
      }
      case 'Publish':
        arrow(service(event.service), declare(BROKER, brokerName), event);
        break;
      case 'Consume':
        arrow(declare(BROKER, brokerName), service(event.service), event);
        break;
      case 'Saga':
      case 'Outbox':
        note(service(event.service), event.summary);
        break;
      case 'Queued':
        note(declare(BROKER, brokerName), event.summary);
        break;
      case 'Error': {
        const id = service(event.service);
        lines.push('rect rgb(255, 224, 224)');
        note(id, `failed: ${event.summary}`);
        lines.push('end');
        break;
      }
    }
  }

  return { source: ['sequenceDiagram', 'autonumber', ...names, ...lines].join('\n'), arrows };
}
