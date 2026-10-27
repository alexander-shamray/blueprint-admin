import { OutputLine } from '../../core/host/host-types';
import { ComposeEvent, composeEvent, composeFailed } from './compose-json';

export type PullState = 'waiting' | 'downloading' | 'extracting' | 'done' | 'failed';

/** One image Compose is pulling: its layers' bytes so far and sizes, summed. */
export interface ImagePull {
  name: string;
  state: PullState;
  current: number;
  total: number;
  /** False while a layer has not yet said its size: Docker reports a size only once that layer starts downloading. */
  totalKnown: boolean;
}

export interface PullProgress {
  /** Failed first, then the ones still moving, then waiting, then done, each in the order Compose named them. */
  images: ImagePull[];
  /** The images not yet pulled. */
  left: number;
  current: number;
  total: number;
  totalKnown: boolean;
  /** Over the last 10 seconds of the job's own timestamps, while a layer is downloading; null otherwise. */
  bytesPerSecond: number | null;
  /** How many lines the pull wrote, which the log leaves out until asked. */
  lines: number;
}

type LayerState = 'waiting' | 'downloading' | 'downloaded' | 'extracting' | 'done' | 'exists';

interface Layer {
  state: LayerState;
  current: number;
  total: number | null;
}

interface Image {
  name: string;
  order: number;
  pulled: boolean;
  failed: boolean;
  layers: Map<string, Layer>;
}

/** Compose's word for a layer, to where the layer stands; a word not here leaves the layer where it was. */
const LAYER_STATES: Record<string, LayerState> = {
  'Pulling fs layer': 'waiting',
  Waiting: 'waiting',
  Downloading: 'downloading',
  'Verifying Checksum': 'downloaded',
  'Download complete': 'downloaded',
  Extracting: 'extracting',
  'Pull complete': 'done',
  'Already exists': 'exists',
};

const IMAGE = 'Image ';
const WINDOW_MS = 10_000;
const RANK: Record<PullState, number> = { failed: 0, downloading: 1, extracting: 2, waiting: 3, done: 4 };

/**
 * The images a `docker compose --progress json` job pulls, read a line at a time: Compose prints a layer's progress ten
 * times a second, so the job's lines are read once each rather than all again on every new one.
 */
export class PullTracker {
  private readonly images = new Map<string, Image>();
  private readonly samples: { at: number; bytes: number }[] = [];
  private bytes = 0;
  private lastAt = 0;
  private lines = 0;

  /** Reads one line, and says whether it was a pull's progress, which the log then leaves out. A failure never is. */
  add(line: OutputLine): boolean {
    const event = composeEvent(line.text);
    if (!event || composeFailed(event)) {
      if (event) this.fail(event);
      return false;
    }

    const counted = event.parent_id?.startsWith(IMAGE)
      ? this.layer(this.image(event.parent_id), event)
      : event.id?.startsWith(IMAGE) === true && this.imageLine(event.id, event.text);
    if (!counted) return false;

    this.lines++;
    const at = Date.parse(line.at);
    if (!Number.isNaN(at)) this.sample(at);
    return true;
  }

  /** Where every image stands, or null for a job that pulled nothing. */
  snapshot(): PullProgress | null {
    if (this.images.size === 0) return null;

    const images = [...this.images.values()].map((image) => this.pull(image));
    const order = new Map([...this.images.values()].map((image) => [image.name, image.order]));
    images.sort((a, b) => RANK[a.state] - RANK[b.state] || order.get(a.name)! - order.get(b.name)!);
    const downloading = images.some((i) => i.state === 'downloading');

    return {
      images,
      left: images.filter((i) => i.state !== 'done').length,
      current: images.reduce((sum, i) => sum + i.current, 0),
      total: images.reduce((sum, i) => sum + i.total, 0),
      totalKnown: images.every((i) => i.totalKnown),
      bytesPerSecond: downloading ? this.rate() : null,
      lines: this.lines,
    };
  }

  private image(id: string): Image {
    let image = this.images.get(id);
    if (!image) {
      image = {
        name: id.slice(IMAGE.length),
        order: this.images.size,
        pulled: false,
        failed: false,
        layers: new Map(),
      };
      this.images.set(id, image);
    }
    return image;
  }

  /** `Pulling` and `Pulled` are an image's; `Building` and `Built` are an image Compose builds, which the log keeps. */
  private imageLine(id: string, text: string | undefined): boolean {
    if (text !== 'Pulling' && text !== 'Pulled') return false;
    this.image(id).pulled = text === 'Pulled';
    return true;
  }

  private layer(image: Image, event: ComposeEvent): boolean {
    if (typeof event.id !== 'string') return false;
    const layer = image.layers.get(event.id) ?? { state: 'waiting', current: 0, total: null };

    const state = LAYER_STATES[event.text ?? ''] ?? layer.state;
    const total = typeof event.total === 'number' ? event.total : layer.total;
    let current = typeof event.current === 'number' && state === 'downloading' ? event.current : layer.current;
    if ((state === 'downloaded' || state === 'extracting' || state === 'done') && total !== null) current = total;
    if (state === 'exists') current = 0;

    this.bytes += current - layer.current;
    image.layers.set(event.id, { state, current, total: state === 'exists' ? null : total });
    return true;
  }

  private fail(event: ComposeEvent): void {
    const id = event.parent_id?.startsWith(IMAGE) ? event.parent_id : event.id?.startsWith(IMAGE) ? event.id : null;
    if (id) this.image(id).failed = true;
  }

  private pull(image: Image): ImagePull {
    const layers = [...image.layers.values()].filter((l) => l.state !== 'exists');
    const started = layers.some((l) => l.state !== 'waiting');
    const downloaded =
      layers.length > 0 &&
      layers.every((l) => l.state === 'downloaded' || l.state === 'extracting' || l.state === 'done');
    const state: PullState = image.failed
      ? 'failed'
      : image.pulled
        ? 'done'
        : downloaded
          ? 'extracting'
          : started
            ? 'downloading'
            : 'waiting';

    return {
      name: image.name,
      state,
      current: layers.reduce((sum, l) => sum + l.current, 0),
      total: layers.reduce((sum, l) => sum + (l.total ?? 0), 0),
      totalKnown: image.pulled || layers.every((l) => l.total !== null || l.state !== 'waiting'),
    };
  }

  /** A sample a second at most, the last 10 seconds' worth kept, and a rate only once they span two. */
  private sample(at: number): void {
    this.lastAt = at;
    const last = this.samples.at(-1);
    if (!last || at - last.at >= 1000) this.samples.push({ at, bytes: this.bytes });
    while (this.samples.length > 1 && this.samples[0].at < at - WINDOW_MS) this.samples.shift();
  }

  private rate(): number | null {
    const first = this.samples[0];
    const seconds = first ? (this.lastAt - first.at) / 1000 : 0;
    return seconds >= 2 ? Math.max(0, (this.bytes - first.bytes) / seconds) : null;
  }
}
