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

  it('keeps a projection rebuild’s failed replay failed when the BFF is started after it, and the start done', () => {
    const tracker = new StepTracker(['stop web-bff', 'bff-replay', 'start web-bff']);
    tracker.add(line(0, '$ docker compose -f deploy/compose/docker-compose.yml stop web-bff'));
    tracker.add(line(2, '$ dotnet run --project tools/bff-replay -- --reset'));
    tracker.add(line(7, 'dotnet run --project tools/bff-replay -- --reset exited with 1.', 'Stderr'));
    tracker.add(
      line(7, 'The tool did not finish; the BFF is started again and the projection may be partial.', 'Stderr'),
    );
    tracker.add(line(8, '$ docker compose -f deploy/compose/docker-compose.yml start web-bff'));
    tracker.add(line(10, ' Container web-bff-1  Started', 'Stderr'));
    tracker.finish(1);

    expect(tracker.snapshot()).toEqual([
      { name: 'stop web-bff', state: 'done', seconds: 2, detail: null },
      {
        name: 'bff-replay',
        state: 'failed',
        seconds: 5,
        detail: 'dotnet run --project tools/bff-replay -- --reset exited with 1.',
      },
      { name: 'start web-bff', state: 'done', seconds: 2, detail: null },
    ]);
  });

  it('fails a step whose failure line gives a negative code, and the start after it when it fails too', () => {
    const tracker = new StepTracker(['stop web-bff', 'bff-replay', 'start web-bff']);
    tracker.add(line(0, '$ docker compose stop web-bff'));
    tracker.add(line(1, '$ dotnet run --project tools/bff-replay -- --reset'));
    tracker.add(line(3, 'dotnet run --project tools/bff-replay -- --reset exited with -1.', 'Stderr'));
    tracker.add(line(4, '$ docker compose start web-bff'));
    tracker.add(line(6, 'docker compose start web-bff exited with 1.', 'Stderr'));
    tracker.finish(1);

    expect(tracker.snapshot().map((s) => [s.name, s.state])).toEqual([
      ['stop web-bff', 'done'],
      ['bff-replay', 'failed'],
      ['start web-bff', 'failed'],
    ]);
  });

  it('ends the start done when the replay never wrote its marker, and leaves the replay not run', () => {
    const tracker = new StepTracker(['stop web-bff', 'bff-replay', 'start web-bff']);
    tracker.add(line(0, '$ docker compose stop web-bff'));
    tracker.add(line(2, 'The tool could not run: dotnet was not found', 'Stderr'));
    tracker.add(line(2, '$ docker compose start web-bff'));
    tracker.finish(1);

    expect(tracker.snapshot().map((s) => [s.name, s.state])).toEqual([
      ['stop web-bff', 'done'],
      ['bff-replay', 'skipped'],
      ['start web-bff', 'done'],
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
