import { K6Tracker } from './k6-progress';

describe('K6Tracker', () => {
  it('reads the latest running line as the whole state, in the load run’s and the stress run’s time forms', () => {
    const tracker = new K6Tracker();
    expect(tracker.snapshot()).toBeNull();

    expect(tracker.add('running (0m42.0s), 05/10 VUs, 812 complete and 0 interrupted iterations')).toBe(true);
    expect(tracker.snapshot()).toEqual({
      elapsedSeconds: 42,
      users: 5,
      maxUsers: 10,
      complete: 812,
      interrupted: 0,
      totalSeconds: null,
    });

    tracker.add('running (1m10.0s), 050/400 VUs, 9 complete and 3 interrupted iterations');
    expect(tracker.snapshot()).toMatchObject({
      elapsedSeconds: 70,
      users: 50,
      maxUsers: 400,
      complete: 9,
      interrupted: 3,
    });

    tracker.add('running (01.0s), 05/50 VUs, 0 complete and 0 interrupted iterations');
    expect(tracker.snapshot()?.elapsedSeconds).toBe(1);
  });

  it('knows the length of a run only where one scenario prints it, and never sums several', () => {
    const stress = new K6Tracker();
    stress.add('running (1m10.0s), 012/400 VUs, 9 complete and 0 interrupted iterations');
    expect(stress.add('journey   [  10% ] 012/400 VUs  1m10.0s/17m30s')).toBe(true);
    expect(stress.snapshot()?.totalSeconds).toBe(17 * 60 + 30);

    const load = new K6Tracker();
    load.add('running (0m01.0s), 00/20 VUs, 1 complete and 0 interrupted iterations');
    load.add('query     [   1% ] 00/10 VUs  0m00.9s/3m0s  1.00 iters/s');
    load.add('command • [   0% ] waiting    09.1s');
    expect(load.snapshot()?.totalSeconds).toBeNull();
  });

  it('keeps a finished scenario’s name when its bar stops printing the length', () => {
    const tracker = new K6Tracker();
    tracker.add('running (3m09.0s), 00/20 VUs, 717 complete and 0 interrupted iterations');
    tracker.add('query     [  99% ] 00/10 VUs  2m59.0s/3m0s  1.00 iters/s');
    tracker.add('query   ✓ [ 100% ] 00/10 VUs  3m0s  1.00 iters/s');

    expect(tracker.snapshot()?.totalSeconds).toBe(180);
  });

  it('leaves a line that is not k6’s progress in the log', () => {
    const tracker = new K6Tracker();

    expect(tracker.add('k6 exit: 0')).toBe(false);
    expect(tracker.add('running the sampler')).toBe(false);
    expect(tracker.add('Stress run: 4 of 5 steps measured')).toBe(false);
    expect(tracker.snapshot()).toBeNull();
  });
});
