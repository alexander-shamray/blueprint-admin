import { OutputLine } from '../../core/host/host-types';

/** A cause waits for its alert to fire, a restore for it to clear. */
export type WatchKind = 'cause' | 'restore';

/** One alert a game-day job told the reader to expect a change in, and when it said so. */
export interface Watch {
  kind: WatchKind;
  alert: string;
  /** When the tool printed the line, in epoch milliseconds: the host's clock, so a re-attached pane counts from the same moment. */
  startedAt: number;
  /** A restore's alert state as the line gave it (`firing`, `inactive`); null for a cause. */
  state: string | null;
  /** Whether the line promised a window: a restore of a phase with no settle time of its own prints none. */
  windowed: boolean;
}

/**
 * The tool's `--cause` says `{runbook}: causing {alert}; it fires within {n}s once caused` and its `--restore` says, per
 * phase, `{alert} is {state} after the restore[; its window clears it within {n}s]` (blueprint-backend
 * `tools/game-day/game_day.py`, `cause_only` and `restore_only`). The window itself is the listing's
 * (`RunbookPhase`), not the number in the line, so nothing here restates a timing. A line that is neither stays in the log.
 */
const CAUSE = /^\S+: causing (\S+); it fires within \d+s once caused$/;
const RESTORE = /^(\S+) is (\S+) after the restore(; its window clears it within \d+s)?$/;

/** The alerts one game-day job is waiting on, in the order it said so. */
export class GameDayLineReader {
  private readonly rows: Watch[] = [];
  private readonly seen = new Set<number>();

  /** Whether the line is one the reader keeps as a watch. A line read twice (a stream resumed) is kept once. */
  add(line: OutputLine): boolean {
    const watch = GameDayLineReader.parse(line);
    if (!watch) return false;
    if (!this.seen.has(line.sequence)) {
      this.seen.add(line.sequence);
      this.rows.push(watch);
    }
    return true;
  }

  snapshot(): Watch[] {
    return [...this.rows];
  }

  static parse(line: OutputLine): Watch | null {
    const startedAt = Date.parse(line.at);
    const cause = CAUSE.exec(line.text);
    if (cause) return { kind: 'cause', alert: cause[1], startedAt, state: null, windowed: true };
    const restore = RESTORE.exec(line.text);
    if (restore) {
      return { kind: 'restore', alert: restore[1], startedAt, state: restore[2], windowed: restore[3] !== undefined };
    }
    return null;
  }

  /** A line is a watch when its words are the tool's; the pane keeps it behind the toggle. */
  static readonly claims = (text: string): boolean =>
    GameDayLineReader.parse({ sequence: 0, at: '', stream: 'Stdout', text }) !== null;
}
