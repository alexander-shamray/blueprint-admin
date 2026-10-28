import { OutputLine } from '../../core/host/host-types';
import { ComposeTracker, composeProgress } from './compose-progress';

const lines = (...texts: string[]): OutputLine[] =>
  texts.map((text, sequence) => ({ sequence, at: '', stream: 'Stderr', text }));

const at = (seconds: number, text: string, sequence = seconds): OutputLine => ({
  sequence,
  at: new Date(Date.UTC(2026, 9, 28, 10, 0, seconds)).toISOString(),
  stream: 'Stderr',
  text,
});

describe('composeProgress', () => {
  it('keeps the last verb a container printed, in first-seen order, and counts the settled ones', () => {
    const progress = composeProgress(
      lines(
        ' Container a-1  Creating',
        ' Container b-1  Creating',
        ' Container a-1  Created',
        ' Container a-1  Healthy',
        ' Container b-1  Waiting',
      ),
    );

    expect(progress.services.map((s) => [s.name, s.verb, s.settled])).toEqual([
      ['a-1', 'Healthy', true],
      ['b-1', 'Waiting', false],
    ]);
    expect(progress.settled).toBe(1);
    expect(progress.lines).toBe(5);
  });

  it('reads the same pair from a --progress json line, and passes over the layer lines', () => {
    const progress = composeProgress(
      lines(
        '{"id":"3c5e70de2899","parent_id":"Image grafana/otel-lgtm:0.37.0","status":"Working","text":"Downloading"}',
        '{"id":"Container a-1","status":"Working","text":"Waiting"}',
        '{"id":"Container a-1","status":"Done","text":"Healthy"}',
        '{"id":"Network n","status":"Done","text":"Created"}',
      ),
    );

    expect(progress.services.map((s) => [s.name, s.verb])).toEqual([['a-1', 'Healthy']]);
    expect(progress.settled).toBe(1);
  });

  it('counts Started as not settled, because --wait goes on to Healthy', () => {
    expect(composeProgress(lines(' Container a-1  Started')).settled).toBe(0);
  });

  it('settles a Down at Stopped and Removed, and an exited migrator without calling it failed', () => {
    const progress = composeProgress(
      lines(
        ' Container a-1  Stopping',
        ' Container a-1  Stopped',
        ' Container a-1  Removing',
        ' Container a-1  Removed',
        ' Container migrate-1  Exited',
      ),
    );

    expect(progress.services.map((s) => [s.name, s.verb, s.settled, s.failed])).toEqual([
      ['a-1', 'Removed', true, false],
      ['migrate-1', 'Exited', true, false],
    ]);
  });

  it('turns a container whose line says Error to failed, which is settled for the count and not for the row', () => {
    const tracker = new ComposeTracker();
    const folded = [
      tracker.add(at(0, '{"id":"Container a-1","status":"Working","text":"Starting"}')),
      tracker.add(at(1, '{"id":"Container a-1","status":"Error","text":"Error"}')),
      tracker.add(at(2, ' Container b-1  Error')),
    ];

    expect(folded).toEqual([true, false, false]);
    const progress = tracker.snapshot();
    expect(progress.services.map((s) => [s.name, s.failed, s.settled])).toEqual([
      ['a-1', true, false],
      ['b-1', true, false],
    ]);
    expect(progress.settled).toBe(2);
  });

  it('times a container from its first line to its settled one, and starts over when a settled one is named again', () => {
    const tracker = new ComposeTracker();
    tracker.add(at(0, ' Container a-1  Stopping'));
    tracker.add(at(3, ' Container a-1  Removed'));
    expect(tracker.snapshot().services[0].seconds).toBe(3);

    tracker.add(at(10, ' Container a-1  Waiting'));
    expect(tracker.snapshot().services[0].seconds).toBeNull();
    tracker.add(at(24, ' Container a-1  Healthy'));
    expect(tracker.snapshot().services[0].seconds).toBe(14);
  });

  it('lists neither volumes nor networks nor prose', () => {
    const progress = composeProgress(
      lines(' Volume v  Created', ' Network n  Created', 'exited with something', ' Container a-1 is unhealthy'),
    );

    expect(progress.services).toEqual([]);
  });
});
