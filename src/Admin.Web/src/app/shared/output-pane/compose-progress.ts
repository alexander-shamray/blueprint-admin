import { OutputLine } from '../../core/host/host-types';
import { composeEvent, composeFailed, composePlain } from './compose-json';

export interface ComposeService {
  name: string;
  /** The last verb Compose printed for the container: Waiting, Healthy, Stopped, Removed, Exited, Error. */
  verb: string;
  failed: boolean;
  settled: boolean;
  /** From the container's first line to its settled one, in whole seconds, by the job's own timestamps; null while it moves. */
  seconds: number | null;
}

export interface ComposeProgress {
  services: ComposeService[];
  /** Containers with nothing more to wait for: settled, or failed. */
  settled: number;
  /** How many lines the containers wrote, which the log leaves out until asked. */
  lines: number;
}

/** Compose's `Container <name>  <Verb>` progress line; the name carries no spaces and the verb is one word. */
const CONTAINER_LINE = /^\s*Container\s+(\S+)\s+(\w+)\s*$/;

/**
 * The verbs after which a container has nothing more to wait for; `Started` is not one, because `--wait` goes on to
 * Healthy. A migrator or an init job ends `Exited`, which is settled and not a failure.
 */
const SETTLED = new Set(['Healthy', 'Removed', 'Stopped', 'Exited']);

interface Container {
  name: string;
  verb: string;
  failed: boolean;
  startedAt: number;
  settledAt: number | null;
}

/**
 * The containers a `docker compose` job names, read a line at a time. Compose writes its progress as
 * `Container <name>  <Verb>` lines, many per container, or with `--progress json` as the same pair in a JSON line; the
 * last verb a container printed is where it stands. An Up reads from Waiting to Healthy, a Down from Stopping to
 * Removed; a container named again after it settled starts over, as in a Reset, whose Up follows its Down. Only
 * containers are listed: a volume or a network is not a platform service. A line that does not name a container is not
 * parsed, because an Up that pulls writes thousands of layer lines.
 */
export class ComposeTracker {
  private readonly containers = new Map<string, Container>();
  private lines = 0;

  /** Reads one line, and says whether it was a container's, which the log then leaves out. A failure never is. */
  add(line: OutputLine): boolean {
    if (!line.text.includes('Container ')) return false;
    const event = composeEvent(line.text);
    const match = CONTAINER_LINE.exec(event ? composePlain(event) : line.text);
    if (!match) return false;

    const [, name, verb] = match;
    const failed = verb === 'Error' || (event !== null && composeFailed(event));
    const at = Date.parse(line.at);
    let container = this.containers.get(name);
    if (!container || container.settledAt !== null) {
      container = { name, verb, failed, startedAt: at, settledAt: null };
      this.containers.set(name, container);
    }
    container.verb = verb;
    container.failed = failed;
    if (failed || SETTLED.has(verb)) container.settledAt = at;

    if (failed) return false;
    this.lines++;
    return true;
  }

  /** Where every container stands. */
  snapshot(): ComposeProgress {
    const services = [...this.containers.values()].map((c) => ({
      name: c.name,
      verb: c.verb,
      failed: c.failed,
      settled: c.settledAt !== null && !c.failed,
      seconds: c.settledAt === null ? null : this.seconds(c),
    }));
    return { services, settled: services.filter((s) => s.settled || s.failed).length, lines: this.lines };
  }

  private seconds(c: Container): number | null {
    const seconds = Math.round(((c.settledAt ?? NaN) - c.startedAt) / 1000);
    return Number.isNaN(seconds) ? null : Math.max(0, seconds);
  }
}

/** The containers of the lines so far; the Stack controls tile counts them for the running Compose job. */
export function composeProgress(lines: readonly OutputLine[]): ComposeProgress {
  const tracker = new ComposeTracker();
  for (const line of lines) tracker.add(line);
  return tracker.snapshot();
}
