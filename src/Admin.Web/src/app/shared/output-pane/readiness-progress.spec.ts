import { OutputLine } from '../../core/host/host-types';
import { ReadinessTracker } from './readiness-progress';

const line = (seconds: number, text: string): OutputLine => ({
  sequence: seconds,
  at: new Date(Date.UTC(2026, 9, 28, 10, 0, seconds)).toISOString(),
  stream: 'Stdout',
  text,
});

const states = (tracker: ReadinessTracker) => tracker.snapshot()?.surfaces.map((s) => `${s.name} ${s.state}`);

describe('ReadinessTracker', () => {
  it('reads nothing before the marker, so another job’s waiting-for line stays in its log', () => {
    const tracker = new ReadinessTracker();

    expect(tracker.add(line(0, 'waiting for: gateway'))).toBe(false);
    expect(tracker.snapshot()).toBeNull();
  });

  it('takes the cap and the start from the marker, and ticks a surface when a later poll no longer names it', () => {
    const tracker = new ReadinessTracker();
    tracker.add(line(0, '== readiness: up to 120 s'));
    tracker.add(line(3, 'waiting for: gateway, ordering, bff'));
    expect(states(tracker)).toEqual(['gateway waiting', 'ordering waiting', 'bff waiting']);

    tracker.add(line(6, 'waiting for: ordering, bff'));
    tracker.add(line(9, 'waiting for: bff'));

    expect(states(tracker)).toEqual(['gateway up', 'ordering up', 'bff waiting']);
    expect(tracker.snapshot()).toMatchObject({
      capSeconds: 120,
      startedAt: Date.UTC(2026, 9, 28, 10, 0, 0),
      ended: false,
    });
  });

  it('ticks every surface on ready, including ones no poll named', () => {
    const tracker = new ReadinessTracker();
    tracker.add(line(0, '== readiness: up to 120 s'));
    tracker.add(line(3, 'waiting for: bff'));

    expect(tracker.add(line(6, 'ready: gateway, bff'))).toBe(true);

    expect(states(tracker)).toEqual(['bff up', 'gateway up']);
    expect(tracker.snapshot()?.ended).toBe(true);
  });

  it('marks the surfaces the cap named failed and the rest up', () => {
    const tracker = new ReadinessTracker();
    tracker.add(line(0, '== readiness: up to 120 s'));
    tracker.add(line(3, 'waiting for: ordering, bff'));
    tracker.add(line(6, 'waiting for: ordering'));

    tracker.add(line(120, 'not ready after 120 s: ordering'));

    expect(states(tracker)).toEqual(['ordering failed', 'bff up']);
    expect(tracker.snapshot()?.ended).toBe(true);
  });

  it('keeps only the polls behind the toggle; the marker and the verdict stay in the log', () => {
    expect(ReadinessTracker.isPoll('waiting for: ordering')).toBe(true);
    expect(ReadinessTracker.isPoll('== readiness: up to 120 s')).toBe(false);
    expect(ReadinessTracker.isPoll('ready: gateway')).toBe(false);
    expect(ReadinessTracker.isPoll('not ready after 120 s: ordering')).toBe(false);
  });
});
