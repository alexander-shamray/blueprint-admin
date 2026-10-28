import { OutputLine } from '../../core/host/host-types';
import { StepTracker } from './step-progress';

const line = (seconds: number, text: string, stream: OutputLine['stream'] = 'Stdout'): OutputLine => ({
  sequence: seconds,
  at: new Date(Date.UTC(2026, 9, 28, 10, 0, seconds)).toISOString(),
  stream,
  text,
});

describe('StepTracker', () => {
  it('shows every step pending before a marker, then moves each on at its marker line', () => {
    const tracker = new StepTracker(['k6', 'prometheus', 'run']);
    expect(tracker.snapshot().map((s) => s.state)).toEqual(['pending', 'pending', 'pending']);

    tracker.add(line(0, '== k6 (job j-1)'));
    tracker.add(line(2, 'k6 v1.0'));
    tracker.add(line(5, '== prometheus (job j-2)'));

    expect(tracker.snapshot()).toEqual([
      { name: 'k6', state: 'done', seconds: 5, detail: null },
      { name: 'prometheus', state: 'running', seconds: null, detail: null },
      { name: 'run', state: 'pending', seconds: null, detail: null },
    ]);
  });

  it('reads the stress run’s settle and summary markers, which carry no job id', () => {
    const tracker = new StepTracker(['up', 'settle', 'run', 'summary']);
    tracker.add(line(0, '== up (job j-1)'));
    tracker.add(line(30, '== settle: the stack is left idle for 60 s, as run-locally.md asks'));
    tracker.add(line(90, '== run (job j-2)'));
    tracker.add(line(400, '== summary (artifacts/stress/x.json)'));
    tracker.finish(0);

    expect(tracker.snapshot().map((s) => [s.name, s.state])).toEqual([
      ['up', 'done'],
      ['settle', 'done'],
      ['run', 'done'],
      ['summary', 'done'],
    ]);
  });

  it('starts a step at a $ line that contains its name, as Reset and the projection rebuild write', () => {
    const tracker = new StepTracker(['down -v', 'up -d --wait', 'readiness']);
    tracker.add(line(0, '$ docker compose -f deploy/compose/docker-compose.yml down -v'));
    tracker.add(line(4, '$ docker compose -f deploy/compose/docker-compose.yml --progress json up -d --wait'));
    tracker.add(line(60, '== readiness'));
    tracker.add(line(65, 'waiting for: gateway'));

    expect(tracker.snapshot().map((s) => s.state)).toEqual(['done', 'done', 'running']);
  });

  it('fails the step the job stopped in, naming its stops-here line, and skips the steps it never reached', () => {
    const tracker = new StepTracker(['k6', 'prometheus', 'run']);
    tracker.add(line(0, '== k6 (job j-1)'));
    tracker.add(line(3, '== prometheus (job j-2)'));
    tracker.add(line(8, 'prometheus said no; the load run stops here.', 'Stderr'));
    tracker.finish(1);

    expect(tracker.snapshot()).toEqual([
      { name: 'k6', state: 'done', seconds: 3, detail: null },
      { name: 'prometheus', state: 'failed', seconds: 5, detail: 'prometheus said no; the load run stops here.' },
      { name: 'run', state: 'skipped', seconds: null, detail: null },
    ]);
  });

  it('ignores a marker for an earlier step, a stray $ line, and prose that is no marker', () => {
    const tracker = new StepTracker(['k6', 'run']);
    tracker.add(line(0, 'before the first marker'));
    tracker.add(line(1, '== run (job j-2)'));
    tracker.add(line(2, '== k6 (job j-1)'));
    tracker.add(line(3, '$ nothing of the steps'));

    expect(tracker.snapshot().map((s) => s.state)).toEqual(['pending', 'running']);
  });
});
