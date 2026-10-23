import { TraceEvent, TraceEventKind, TraceView } from '../../../core/host/host-types';
import { label, sequenceOf } from './sequence-source';

function event(kind: TraceEventKind, service: string, summary: string, extra: Partial<TraceEvent> = {}): TraceEvent {
  return {
    at: '2026-09-16T09:43:00.000Z',
    source: 'tempo',
    service,
    kind,
    summary,
    traceId: 't1',
    link: null,
    ...extra,
  };
}

function view(events: TraceEvent[]): TraceView {
  return {
    correlationId: 'c1',
    window: '15m',
    reachable: true,
    error: null,
    traceIds: ['t1'],
    tracesTruncated: false,
    warning: null,
    events,
  };
}

describe('sequenceOf', () => {
  it('draws a request that crosses the broker: services in order of first appearance, the broker where it first appears', () => {
    const drawn = sequenceOf(
      view([
        event('HttpIn', 'Gateway.Api', 'POST /orders (12.5 ms)'),
        event('Outbox', 'Ordering.Api', 'INSERT outbox (1 ms)'),
        event('Publish', 'Ordering.Api', 'send OrderPlaced (2 ms)'),
        event('Consume', 'Inventory.Api', 'Inventory reserves stock: process (4 ms)'),
        event('Saga', 'Ordering.Api', "Ordering's fulfilment saga, from Initial to Reserved: process (3 ms)"),
        event('Queued', 'RabbitMQ', 'ordering: 0 messages (drained).', { source: 'broker', traceId: null }),
      ]),
    );

    expect(drawn?.source.split('\n')).toEqual([
      'sequenceDiagram',
      'autonumber',
      'participant C as Caller',
      'participant S0 as Gateway.Api',
      'participant S1 as Ordering.Api',
      'participant B as RabbitMQ',
      'participant S2 as Inventory.Api',
      'C->>S0: POST /orders (12.5 ms)',
      'Note over S1: INSERT outbox (1 ms)',
      'S1->>B: send OrderPlaced (2 ms)',
      'B->>S2: Inventory reserves stock: process (4 ms)',
      "Note over S1: Ordering's fulfilment saga, from Initial to Reserved: process (3 ms)",
      'Note over B: ordering: 0 messages (drained).',
    ]);
  });

  it('numbers the arrows only, and hands back the events behind them in that order', () => {
    const http = event('HttpIn', 'Gateway.Api', 'GET /x (1 ms)');
    const span = event('Span', 'Gateway.Api', 'SELECT (2 ms)');
    const saga = event('Saga', 'Ordering.Api', 'a step');

    const drawn = sequenceOf(view([http, saga, span]));

    expect(drawn?.arrows).toEqual([http, span]);
    expect(drawn?.source).toContain('S0->>S0: SELECT (2 ms)');
  });

  it('draws a failed event as a note that says so in words, on a band', () => {
    const drawn = sequenceOf(
      view([event('HttpIn', 'Gateway.Api', 'GET /x (1 ms)'), event('Error', 'Ordering.Api', 'boom (3 ms)')]),
    );

    expect(drawn?.source).toContain('rect rgb(255, 224, 224)\nNote over S1: failed: boom (3 ms)\nend');
    expect(drawn?.arrows).toHaveLength(1);
  });

  it('draws nothing that is not in the timeline: no broker without a broker event, no caller without an inbound request', () => {
    const drawn = sequenceOf(view([event('Span', 'Catalog.Api', 'SELECT (2 ms)')]));

    expect(drawn?.source).not.toContain('Caller');
    expect(drawn?.source).not.toContain('participant B');
    expect(drawn?.source).toBe(
      ['sequenceDiagram', 'autonumber', 'participant S0 as Catalog.Api', 'S0->>S0: SELECT (2 ms)'].join('\n'),
    );
  });

  it('draws a service only a log names as nothing, and has no diagram for logs and the broker line alone', () => {
    expect(sequenceOf(view([event('Log', 'Gateway.Api', 'a line', { source: 'loki' })]))).toBeNull();
    expect(
      sequenceOf(
        view([
          event('Log', 'Gateway.Api', 'a line', { source: 'loki' }),
          event('Queued', 'RabbitMQ', 'depth', { source: 'broker' }),
        ]),
      ),
    ).toBeNull();
    expect(sequenceOf(view([]))).toBeNull();
  });

  it('keeps a hostile service name or summary to one label on one line', () => {
    const drawn = sequenceOf(
      view([event('HttpIn', 'Evil\nparticipant X as Y', 'a; Note over S0: b # c %% d\nS0->>S0: injected')]),
    );

    const lines = drawn?.source.split('\n') ?? [];
    expect(lines).toHaveLength(5);
    expect(lines[2]).toBe('participant C as Caller');
    expect(lines[3]).toBe('participant S0 as Evil participant X as Y');
    expect(lines[4]).toBe('C->>S0: a, Note over S0: b , c % d S0->>S0: injected');
  });
});

describe('label', () => {
  it('cuts a long line and says it did', () => {
    const cut = label('x'.repeat(200));

    expect(cut).toHaveLength(90);
    expect(cut.endsWith('…')).toBe(true);
  });
});
