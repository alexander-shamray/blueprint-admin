import { BrokerQueue, QueuesView, TraceView } from '../../core/host/host-types';
import { BODY_LIMIT, ExportInput, MAIL_OMITTED, buildExport } from './scenario-export';
import { StepView } from './scenario-page';

function step(partial: Partial<StepView> & { key: StepView['key'] }): StepView {
  return {
    title: partial.key,
    state: 'ok',
    correlationId: null,
    request: null,
    status: null,
    detail: '',
    regrant: null,
    body: null,
    ...partial,
  };
}

function queues(...depths: [string, number | null][]): QueuesView {
  return {
    reachable: true,
    error: null,
    queues: depths.map(([name, messages]): BrokerQueue => ({
      name,
      messages,
      consumer: null,
      parked: null,
      runbook: null,
    })),
    projection: { queue: 'p', found: true, messages: 0, drained: true },
  };
}

const trace: TraceView = {
  correlationId: 'scenario-abc-order',
  window: '30m',
  reachable: true,
  error: null,
  traceIds: [],
  tracesTruncated: false,
  warning: null,
  events: [
    {
      at: '2026-10-05T12:00:00Z',
      source: 'tempo',
      service: 'ordering',
      kind: 'Span',
      summary: 'POST /orders',
      traceId: null,
      link: null,
    },
  ],
};

function input(partial: Partial<ExportInput> = {}): ExportInput {
  return {
    title: 'Order to delivered',
    run: 'abc',
    username: 'demo',
    exportedAt: '2026-10-05T13:00:00.000Z',
    steps: [
      step({
        key: 'publish',
        title: 'Publish',
        correlationId: 'scenario-abc-publish',
        request: 'POST http://x/p',
        status: 200,
        body: '{"a":1}',
      }),
      step({ key: 'order', title: 'Order', correlationId: 'scenario-abc-order', status: 200 }),
    ],
    strip: null,
    transcript: 'curl -X POST http://x/p',
    transcriptScope: 'run',
    traces: { 'scenario-abc-order': trace },
    queuesBefore: queues(['orders', 3]),
    queuesAfter: queues(['orders', 0], ['mail', 1]),
    ...partial,
  };
}

describe('buildExport', () => {
  it("carries the issue's sections in its order, ending with the mail it cannot yet count", () => {
    const md = buildExport(input());
    const at = [
      '## Operator transcript',
      '## Steps',
      '## Correlation ids',
      '## Trace timeline',
      '## Queue depths',
      '## Mail the run caused',
    ].map((h) => md.indexOf(h));

    expect(at.every((i) => i >= 0)).toBe(true);
    expect(at).toEqual([...at].sort((a, b) => a - b));
    expect(md).toContain(MAIL_OMITTED);
    expect(md).toContain('- Outcome: every step succeeded');
  });

  it('lists each step with its status and correlation id, and pretty-prints a JSON body', () => {
    const md = buildExport(input());

    expect(md).toContain('| 1 | Publish | ok | 200 | `scenario-abc-publish` |');
    expect(md).toContain('"a": 1');
    expect(md).toContain('- `scenario-abc-order` — Order');
  });

  it('writes the trace timeline per id, and says so for an id that was not read', () => {
    const md = buildExport(input());

    expect(md).toContain('- 2026-10-05T12:00:00Z · ordering · Span · POST /orders');
    expect(md).toContain('### `scenario-abc-publish`\n\nNot read.');
  });

  it('says why a trace could not be read, in place of its timeline', () => {
    const md = buildExport(input({ traces: { 'scenario-abc-order': 'The host did not answer.' } }));

    expect(md).toContain('Not read: The host did not answer.');
  });

  it('shows the queue depths before and after, with a queue one side never had', () => {
    const md = buildExport(input());

    expect(md).toContain('| `orders` | 3 | 0 |');
    expect(md).toContain('| `mail` | absent | 1 |');
  });

  it('says the broker was not read when neither read answered', () => {
    const md = buildExport(input({ queuesBefore: null, queuesAfter: null }));

    expect(md).toContain('The broker was not read, before or after.');
  });

  it('names the failed step as the outcome', () => {
    const md = buildExport(
      input({
        steps: [step({ key: 'order', title: 'Order', state: 'failed', detail: 'Answered 403.' })],
      }),
    );

    expect(md).toContain('- Outcome: stopped at "Order": Answered 403.');
  });

  it("says when the transcript is the whole session because the run's start was not read", () => {
    expect(buildExport(input({ transcriptScope: 'session' }))).toContain("The host's whole session");
    expect(buildExport(input())).toContain('from when this run began');
  });

  it('omits the transcript with a line when the host would not render it', () => {
    expect(buildExport(input({ transcript: null }))).toContain(
      'Omitted: the host would not render the transcript.',
    );
  });

  it('cuts a long body and says how much, and fences a body that holds backticks with a longer fence', () => {
    const long = JSON.stringify({ text: 'x'.repeat(BODY_LIMIT * 2) });
    const md = buildExport(
      input({ steps: [step({ key: 'order', body: long }), step({ key: 'quote', body: '"```"' })] }),
    );

    expect(md).toContain(`Body cut at ${BODY_LIMIT} of`);
    expect(md).toContain('````json\n"```"\n````');
  });

  it("reports the strip's last read as the last state read, not the order's state now", () => {
    const strip = {
      status: 'confirmed',
      readAt: Date.parse('2026-10-05T12:00:05Z'),
      asOf: '2026-10-05T12:00:04Z',
      reads: 3,
      transitions: [{ from: 'placed' as const, to: 'confirmed' as const, seconds: 1 }],
      unchangedForMs: 40_000,
      stalled: true,
      statusChangedAt: '2026-10-05T12:00:01Z',
      saga: null,
      sagaSeenAt: null,
      pairing: null,
    };
    const md = buildExport(input({ strip }));

    expect(md).toContain("not the order's state now");
    expect(md).toContain('| placed | confirmed | 1 |');
    expect(md).toContain('Stalled: the status stood at `confirmed` for 40 s');
    expect(md).not.toContain('Saga (Ordering)');
  });

  it("sets the saga's word beside the buyer's, and marks a disagreement in words", () => {
    const strip = {
      status: 'delivered',
      readAt: Date.parse('2026-10-05T12:00:05Z'),
      asOf: '2026-10-05T12:00:04Z',
      reads: 1,
      transitions: [],
      unchangedForMs: 0,
      stalled: false,
      statusChangedAt: '2026-10-05T12:00:04Z',
      saga: { kind: 'row' as const, state: 'AwaitingStock', startedAt: '2026-10-05T12:00:00Z', cancelReason: null },
      sagaSeenAt: Date.parse('2026-10-05T12:00:05Z'),
      pairing: 'differs' as const,
    };
    const md = buildExport(input({ strip }));

    expect(md).toContain('| | Saga (Ordering) | Buyer (BFF) |');
    expect(md).toContain('| State | AwaitingStock | `delivered` |');
    expect(md).toContain('2026-10-05T12:00:04Z (the platform');
    expect(md).toContain('**[disagree]**');
  });
});
