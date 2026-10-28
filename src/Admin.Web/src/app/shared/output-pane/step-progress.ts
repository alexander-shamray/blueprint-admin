import { OutputLine } from '../../core/host/host-types';

export type StepState = 'pending' | 'running' | 'done' | 'failed' | 'skipped';

export interface StepRow {
  name: string;
  state: StepState;
  /** From the step's marker line to the next one, or to the job's last line; null while it runs or never ran. */
  seconds: number | null;
  /** The line the job wrote when it stopped here, for a failed step. */
  detail: string | null;
}

interface Step {
  name: string;
  state: StepState;
  startedAt: number;
  endedAt: number;
  detail: string | null;
}

/** What a job writes when a step ends it: `the load run stops here.`, `… exited with 1; the reset stops here.` */
const STOPS = /\bstops here\.?$|\bexited \d+\b/;

/**
 * The steps of a job that runs several under one (`ProcessSpec.Steps`): the load run, the stress run, Reset and the
 * projection rebuild. The host names the steps before the first line; each starts at the marker line the job writes,
 * `== name` or a `$ command line` that contains the name, and a step the job never reaches stays pending until the
 * job ends. Nothing is read that the job does not print.
 */
export class StepTracker {
  private readonly steps: Step[];
  private current = -1;
  private lastAt = NaN;

  constructor(names: readonly string[]) {
    this.steps = names.map((name) => ({ name, state: 'pending', startedAt: NaN, endedAt: NaN, detail: null }));
  }

  /** Whether these are the steps this tracker was made for. */
  is(names: readonly string[]): boolean {
    return names.length === this.steps.length && names.every((name, i) => name === this.steps[i].name);
  }

  add(line: OutputLine): void {
    const at = Date.parse(line.at);
    if (!Number.isNaN(at)) this.lastAt = at;

    const next = this.marked(line.text);
    if (next > this.current) {
      this.end('done', at);
      this.current = next;
      this.steps[next].state = 'running';
      this.steps[next].startedAt = at;
    } else if (this.current >= 0 && line.stream === 'Stderr' && STOPS.test(line.text)) {
      this.steps[this.current].detail = line.text;
    }
  }

  /** The job ended: the step it was in ends with it, failed unless the job exited 0, and the steps it never reached are skipped. */
  finish(exitCode: number): void {
    this.end(exitCode === 0 ? 'done' : 'failed', this.lastAt);
    for (const step of this.steps) if (step.state === 'pending') step.state = 'skipped';
  }

  snapshot(): StepRow[] {
    return this.steps.map((s) => {
      const seconds = Math.round((s.endedAt - s.startedAt) / 1000);
      return {
        name: s.name,
        state: s.state,
        seconds:
          s.state === 'done' || s.state === 'failed' ? (Number.isNaN(seconds) ? null : Math.max(0, seconds)) : null,
        detail: s.detail,
      };
    });
  }

  /** The step a marker line starts, or -1; a step not yet started is the only one it can be. */
  private marked(text: string): number {
    const from = this.current + 1;
    if (text.startsWith('== ')) {
      const name = text.slice(3).split(/[ :]/, 1)[0];
      const index = this.steps.findIndex((s, i) => i >= from && s.name === name);
      return index;
    }
    if (text.startsWith('$ ')) return this.steps.findIndex((s, i) => i >= from && text.includes(s.name));
    return -1;
  }

  private end(state: 'done' | 'failed', at: number): void {
    const step = this.steps[this.current];
    if (step?.state !== 'running') return;
    step.state = state;
    step.endedAt = at;
  }
}
