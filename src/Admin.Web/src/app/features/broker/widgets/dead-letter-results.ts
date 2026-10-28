/** What the backend's dead-letter tool says became of one message. */
export type MoveOutcome =
  | 'replayed'
  | 'discarded'
  | 'would-replay'
  | 'would-discard'
  | 'would-refuse'
  | 'refused'
  | 'failed'
  | 'not-found'
  | 'not-reached';

export interface MoveResult {
  messageId: string;
  outcome: MoveOutcome;
  /** Where a replay put the message, or the queue a failed one went back to; null when the line names none. */
  destination: string | null;
  /** The tool's parenthesised reason, if the line has one. */
  reason: string | null;
}

/**
 * The tool's `finish` words each action as `{action:<14} {message id}[ -> {destination}][ ({reason})]`, and a message
 * it could not find or reach as `not found      {id}` and `not reached    {id} (the run stopped first)`
 * (blueprint-backend `tools/dead-letters/dead_letters.py`, `finish`). The action words are the tool's own; a line
 * whose word is not one of these is not a result and stays in the log, as does the `truncated` line and the closing
 * dry-run note.
 */
const ACTION =
  /^(replayed|discarded|would-replay|would-discard|would-refuse|refused|failed) +(\S+)(?: -> (\S+))?(?: \((.*)\))?$/;
const ABSENT = /^(not found|not reached) +(\S+)(?: \((.*)\))?$/;

/** The messages one replay or discard job reported, in the order it reported them. */
export class MoveResultReader {
  private readonly rows: MoveResult[] = [];

  /** Whether the line is one of the tool's per-message results; if so it is kept as a row. */
  add(text: string): boolean {
    const result = MoveResultReader.parse(text);
    if (!result) return false;
    this.rows.push(result);
    return true;
  }

  snapshot(): MoveResult[] {
    return [...this.rows];
  }

  static parse(text: string): MoveResult | null {
    const action = ACTION.exec(text);
    if (action) {
      return {
        messageId: action[2],
        outcome: action[1] as MoveOutcome,
        destination: action[3] ?? null,
        reason: action[4] ?? null,
      };
    }
    const absent = ABSENT.exec(text);
    if (absent) {
      return {
        messageId: absent[2],
        outcome: absent[1] === 'not found' ? 'not-found' : 'not-reached',
        destination: null,
        reason: absent[3] ?? null,
      };
    }
    return null;
  }

  /** A line is a result when its words are the tool's; the pane keeps it behind the toggle. */
  static readonly claims = (text: string): boolean => MoveResultReader.parse(text) !== null;
}

/** Whether the job was a dry run: the tool words a dry run's actions with `would-`. */
export function isDryRun(rows: readonly MoveResult[]): boolean {
  return rows.some((r) => r.outcome.startsWith('would-'));
}
