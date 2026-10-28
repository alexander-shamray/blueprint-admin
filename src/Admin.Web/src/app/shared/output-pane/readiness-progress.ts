import { OutputLine } from '../../core/host/host-types';

export type SurfaceState = 'waiting' | 'up' | 'failed';

export interface Surface {
  name: string;
  state: SurfaceState;
}

export interface ReadinessProgress {
  surfaces: Surface[];
  /** The host's own cap, as its marker line wrote it. */
  capSeconds: number;
  /** When the marker line was written, in epoch milliseconds: the host's clock, so a re-attached pane counts from the same moment. */
  startedAt: number;
  /** Whether the wait has said how it ended, `ready:` or `not ready after`. */
  ended: boolean;
}

/**
 * Reset's last step, as the host writes it (`ResetService.WaitForReadinessAsync`): a marker that carries the cap, then
 * `waiting for: a, b` on every poll, then `ready: a, b, c` or `not ready after 120 s: a, b`. Lines before the marker are
 * not the wait's, so a `waiting for:` from any other job is left in its log.
 */
const MARKER = /^== readiness: up to (\d+) s$/;
const WAITING = /^waiting for: (.+)$/;
const READY = /^ready: (.+)$/;
const NOT_READY = /^not ready after \d+ s: (.+)$/;

function names(list: string): string[] {
  return list.split(', ');
}

export class ReadinessTracker {
  private surfaces: Surface[] | null = null;
  private capSeconds = 0;
  private startedAt = NaN;
  private ended = false;

  /** A poll's `waiting for:` is the line the pane keeps behind its toggle; the marker and the verdict stay in the log. */
  static isPoll(text: string): boolean {
    return WAITING.test(text);
  }

  /** Whether the line is part of the wait, which only a line after the marker is. */
  add(line: OutputLine): boolean {
    const marker = MARKER.exec(line.text);
    if (marker) {
      this.surfaces = [];
      this.capSeconds = Number(marker[1]);
      this.startedAt = Date.parse(line.at);
      this.ended = false;
      return true;
    }
    if (!this.surfaces) return false;

    const waiting = WAITING.exec(line.text);
    if (waiting) {
      const still = names(waiting[1]);
      // The first poll names every surface that has not answered; a later one names fewer, and the rest have answered.
      for (const surface of this.surfaces) if (!still.includes(surface.name)) surface.state = 'up';
      for (const name of still)
        if (!this.surfaces.some((s) => s.name === name)) this.surfaces.push({ name, state: 'waiting' });
      return true;
    }

    const ready = READY.exec(line.text);
    if (ready) {
      this.settle(names(ready[1]), []);
      return true;
    }
    const notReady = NOT_READY.exec(line.text);
    if (notReady) {
      this.settle([], names(notReady[1]));
      return true;
    }
    return false;
  }

  snapshot(): ReadinessProgress | null {
    return this.surfaces
      ? {
          surfaces: this.surfaces.map((s) => ({ ...s })),
          capSeconds: this.capSeconds,
          startedAt: this.startedAt,
          ended: this.ended,
        }
      : null;
  }

  private settle(up: string[], failed: string[]): void {
    const surfaces = this.surfaces ?? [];
    for (const name of [...up, ...failed])
      if (!surfaces.some((s) => s.name === name)) surfaces.push({ name, state: 'waiting' });
    for (const surface of surfaces) surface.state = failed.includes(surface.name) ? 'failed' : 'up';
    this.ended = true;
  }
}
