/** Where a k6 run is, read off the latest of the progress lines k6 prints once a second when its output is not a terminal. */
export interface K6Progress {
  elapsedSeconds: number;
  users: number;
  maxUsers: number;
  complete: number;
  interrupted: number;
  /** The run's length where exactly one scenario prints it (`elapsed/total`); null where none does or several do. */
  totalSeconds: number | null;
}

/** `running (0m42.0s), 05/10 VUs, 812 complete and 0 interrupted iterations`; the stress run's time omits the minutes below one. */
const RUNNING =
  /^running \((?<elapsed>(?:\d+h)?(?:\d+m)?\d+(?:\.\d+)?s)\), (?<users>\d+)\/(?<max>\d+) VUs, (?<complete>\d+) complete and (?<interrupted>\d+) interrupted iterations$/;

/**
 * A scenario's bar under it: `query     [   1% ] 00/10 VUs  0m00.9s/3m0s  1.00 iters/s`, `command • [   0% ] waiting    09.1s`,
 * `query   ✓ [ 100% ] 00/10 VUs  3m0s  1.00 iters/s`. Only its name and, while it runs, the `elapsed/total` pair are read.
 */
const SCENARIO = /^(?<name>\S+)\s+(?:[✓✗•]\s+)?\[\s*\d+%\s*\](?<rest>.*)$/;
const OF_TOTAL = /(?:\d+h)?(?:\d+m)?\d+(?:\.\d+)?s\/(?<total>(?:\d+h)?(?:\d+m)?\d+(?:\.\d+)?s)/;
const DURATION = /^(?:(\d+)h)?(?:(\d+)m)?(\d+(?:\.\d+)?)s$/;

function seconds(text: string): number {
  const m = DURATION.exec(text);
  return m ? Number(m[1] ?? 0) * 3600 + Number(m[2] ?? 0) * 60 + Number(m[3]) : NaN;
}

/**
 * The load run's and the stress run's k6 lines. The latest `running` line is the whole state; the bars under it name the
 * scenarios, and only a run of one scenario shows how long it will take: a load run's scenarios start at offsets k6 does
 * not print, so its end is not a sum of anything printed. A line that is neither stays in the log.
 */
export class K6Tracker {
  private current: K6Progress | null = null;
  private readonly scenarios = new Map<string, number | null>();

  /** Whether the line is one k6 printed as progress; the pane then keeps it behind its toggle. */
  add(text: string): boolean {
    const running = RUNNING.exec(text);
    if (running?.groups) {
      const g = running.groups;
      this.current = {
        elapsedSeconds: seconds(g['elapsed']),
        users: Number(g['users']),
        maxUsers: Number(g['max']),
        complete: Number(g['complete']),
        interrupted: Number(g['interrupted']),
        totalSeconds: null,
      };
      return true;
    }

    const scenario = SCENARIO.exec(text);
    if (scenario?.groups) {
      const total = OF_TOTAL.exec(scenario.groups['rest'])?.groups?.['total'];
      const name = scenario.groups['name'];
      this.scenarios.set(name, total ? seconds(total) : (this.scenarios.get(name) ?? null));
      return true;
    }
    return false;
  }

  snapshot(): K6Progress | null {
    if (!this.current) return null;
    const totals = [...this.scenarios.values()];
    const total = totals.length === 1 ? totals[0] : null;
    return { ...this.current, totalSeconds: total !== null && !Number.isNaN(total) ? total : null };
  }
}
