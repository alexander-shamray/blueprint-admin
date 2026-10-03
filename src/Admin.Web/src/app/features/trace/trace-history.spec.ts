import { TraceView } from '../../core/host/host-types';
import { KEPT_IDS, KEPT_PER_ID, TraceHistory, diffTimelines } from './trace-history';

function view(correlationId: string, summaries: string[], window = '15m'): TraceView {
  return {
    correlationId,
    window,
    reachable: true,
    error: null,
    traceIds: [],
    tracesTruncated: false,
    warning: null,
    events: summaries.map((summary, i) => ({
      at: `2026-09-16T09:43:0${i}.000Z`,
      source: 'loki',
      service: 'Gateway.Api',
      kind: 'Log' as const,
      summary,
      traceId: null,
      link: null,
    })),
  };
}

describe('TraceHistory', () => {
  it('returns the previous fetch of the same id and nothing for a first fetch', () => {
    const history = new TraceHistory();
    const first = view('a', ['one']);

    expect(history.record(first, new Date(1))).toBeNull();
    expect(history.record(view('a', ['one', 'two']), new Date(2))?.view).toBe(first);
    expect(history.record(view('b', ['one']), new Date(3))).toBeNull();
  });

  it('keeps at most KEPT_PER_ID fetches of one id, dropping the oldest', () => {
    const history = new TraceHistory();

    for (let i = 0; i < KEPT_PER_ID + 2; i++) history.record(view('a', [`fetch ${i}`]), new Date(i));

    const kept = history.fetches('a');
    expect(kept.length).toBe(KEPT_PER_ID);
    expect(kept[0].view.events[0].summary).toBe('fetch 2');
  });

  it('keeps at most KEPT_IDS ids, dropping the one fetched longest ago', () => {
    const history = new TraceHistory();

    for (let i = 0; i < KEPT_IDS; i++) history.record(view(`id-${i}`, ['x']), new Date(i));
    history.record(view('id-0', ['x']), new Date(100));
    history.record(view('new', ['x']), new Date(101));

    expect(history.fetches('id-0').length).toBe(2);
    expect(history.fetches('id-1').length).toBe(0);
    expect(history.fetches('new').length).toBe(1);
  });
});

describe('diffTimelines', () => {
  it('lists what arrived and what left, by everything an event says', () => {
    const diff = diffTimelines(
      { fetchedAt: new Date(1), view: view('a', ['one', 'two']) },
      { fetchedAt: new Date(2), view: view('a', ['one', 'three']) },
    );

    expect(diff.comparable).toBe(true);
    expect(diff.arrived.map((e) => e.summary)).toEqual(['three']);
    expect(diff.gone.map((e) => e.summary)).toEqual(['two']);
  });

  it('reads the broker snapshot by what it says, since its time is new on every fetch', () => {
    const queued = (at: string, summary: string) => ({ at, source: 'broker', service: 'rabbitmq', kind: 'Queued' as const, summary, traceId: null, link: null });
    const fetch = (at: string, summary: string, n: number) => ({ fetchedAt: new Date(n), view: { ...view('a', ['one']), events: [queued(at, summary)] } });

    const same = diffTimelines(fetch('2026-09-16T09:43:03Z', 'ordering-catalog-events: 0 messages', 1), fetch('2026-09-16T09:43:13Z', 'ordering-catalog-events: 0 messages', 2));
    const drained = diffTimelines(fetch('2026-09-16T09:43:03Z', 'ordering-catalog-events: 2 messages', 1), fetch('2026-09-16T09:43:13Z', 'ordering-catalog-events: 0 messages', 2));

    expect(same.arrived).toEqual([]);
    expect(same.gone).toEqual([]);
    expect(drained.arrived.map((e) => e.summary)).toEqual(['ordering-catalog-events: 0 messages']);
    expect(drained.gone.map((e) => e.summary)).toEqual(['ordering-catalog-events: 2 messages']);
  });

  it('compares nothing across two windows', () => {
    const diff = diffTimelines(
      { fetchedAt: new Date(1), view: view('a', ['one']) },
      { fetchedAt: new Date(2), view: view('a', ['one', 'two'], '1h') },
    );

    expect(diff.comparable).toBe(false);
    expect(diff.previousWindow).toBe('15m');
    expect(diff.arrived).toEqual([]);
    expect(diff.gone).toEqual([]);
  });
});
