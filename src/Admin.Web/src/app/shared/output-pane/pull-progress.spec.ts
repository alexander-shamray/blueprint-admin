import { OutputLine } from '../../core/host/host-types';
import { PullTracker } from './pull-progress';

let sequence = 0;
const line = (event: object, at = '2026-10-27T13:00:00Z'): OutputLine => ({
  sequence: sequence++,
  at,
  stream: 'Stderr',
  text: JSON.stringify(event),
});
const image = (name: string, text: string) => line({ id: `Image ${name}`, status: 'Working', text });
const layer = (id: string, name: string, text: string, current?: number, total?: number, at?: string) =>
  line({ id, parent_id: `Image ${name}`, status: 'Working', text, current, total }, at);

const LGTM = 'grafana/otel-lgtm:0.37.0';
const REDIS = 'redis:7.4.11-alpine';

describe('PullTracker', () => {
  it('pulled nothing until Compose names an image, and leaves a container line to the log', () => {
    const tracker = new PullTracker();

    expect(tracker.add(line({ id: 'Container a-1', status: 'Done', text: 'Healthy' }))).toBe(false);
    expect(tracker.add({ sequence: 0, at: '', stream: 'Stdout', text: '$ docker compose up' })).toBe(false);
    expect(tracker.snapshot()).toBeNull();
  });

  it('sums each image’s layers, and says the size is not known while a layer has not started', () => {
    const tracker = new PullTracker();
    tracker.add(image(LGTM, 'Pulling'));
    tracker.add(layer('a', LGTM, 'Pulling fs layer'));
    tracker.add(layer('b', LGTM, 'Pulling fs layer'));
    tracker.add(layer('a', LGTM, 'Downloading', 100, 900));

    let pull = tracker.snapshot()!.images[0];
    expect(pull).toEqual({ name: LGTM, state: 'downloading', current: 100, total: 900, totalKnown: false });

    tracker.add(layer('b', LGTM, 'Downloading', 5, 20));
    pull = tracker.snapshot()!.images[0];
    expect(pull).toMatchObject({ current: 105, total: 920, totalKnown: true });
  });

  it('counts a layer whole once downloaded, leaves out one already on the machine, and is done when Pulled', () => {
    const tracker = new PullTracker();
    tracker.add(image(REDIS, 'Pulling'));
    tracker.add(layer('x', REDIS, 'Already exists'));
    tracker.add(layer('y', REDIS, 'Downloading', 8, 17));
    tracker.add(layer('y', REDIS, 'Download complete'));

    expect(tracker.snapshot()!.images[0]).toMatchObject({
      state: 'extracting',
      current: 17,
      total: 17,
      totalKnown: true,
    });

    tracker.add(layer('y', REDIS, 'Pull complete'));
    tracker.add(image(REDIS, 'Pulled'));
    expect(tracker.snapshot()).toMatchObject({ left: 0, current: 17, total: 17 });
    expect(tracker.snapshot()!.images[0].state).toBe('done');
  });

  it('lists the moving images before the waiting ones and the done ones last', () => {
    const tracker = new PullTracker();
    tracker.add(image('done:1', 'Pulling'));
    tracker.add(image('waits:1', 'Pulling'));
    tracker.add(layer('w', 'waits:1', 'Pulling fs layer'));
    tracker.add(layer('m', 'moves:1', 'Downloading', 1, 2));
    tracker.add(image('done:1', 'Pulled'));

    expect(tracker.snapshot()!.images.map((i) => [i.name, i.state])).toEqual([
      ['moves:1', 'downloading'],
      ['waits:1', 'waiting'],
      ['done:1', 'done'],
    ]);
    expect(tracker.snapshot()!.left).toBe(2);
  });

  it('marks an image failed and keeps the failure line for the log', () => {
    const tracker = new PullTracker();
    tracker.add(image(LGTM, 'Pulling'));

    expect(tracker.add(line({ id: `Image ${LGTM}`, status: 'Error', text: 'Error', details: 'toomanyrequests' }))).toBe(
      false,
    );
    expect(tracker.snapshot()!.images[0].state).toBe('failed');
  });

  it('reads the rate from the job’s own timestamps, over two seconds at least, and only while downloading', () => {
    const tracker = new PullTracker();
    tracker.add(layer('a', LGTM, 'Downloading', 0, 10_000_000, '2026-10-27T13:00:00Z'));
    tracker.add(layer('a', LGTM, 'Downloading', 1_000_000, 10_000_000, '2026-10-27T13:00:01Z'));
    expect(tracker.snapshot()!.bytesPerSecond).toBeNull();

    tracker.add(layer('a', LGTM, 'Downloading', 4_000_000, 10_000_000, '2026-10-27T13:00:04Z'));
    expect(tracker.snapshot()!.bytesPerSecond).toBe(1_000_000);

    tracker.add(layer('a', LGTM, 'Download complete', undefined, undefined, '2026-10-27T13:00:05Z'));
    expect(tracker.snapshot()!.bytesPerSecond).toBeNull();
  });

  it('counts the lines the pull wrote, which the log leaves out', () => {
    const tracker = new PullTracker();
    tracker.add(image(LGTM, 'Pulling'));
    tracker.add(layer('a', LGTM, 'Downloading', 1, 2));
    tracker.add(layer('a', LGTM, 'Downloading', 1, 2));
    tracker.add(image('commerce-rabbitmq', 'Building'));

    expect(tracker.snapshot()!.lines).toBe(3);
  });
});
