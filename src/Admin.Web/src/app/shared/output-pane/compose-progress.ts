import { OutputLine } from '../../core/host/host-types';

export interface ComposeService {
  name: string;
  verb: string;
}

export interface ComposeProgress {
  services: ComposeService[];
  settled: number;
}

/** Compose's `Container <name>  <Verb>` progress line; the name carries no spaces and the verb is one word. */
const CONTAINER_LINE = /^\s*Container\s+(\S+)\s+(\w+)\s*$/;

/** The verbs after which a container has nothing more to wait for; `Started` is not one, because `--wait` goes on to Healthy. */
const SETTLED = new Set(['Healthy', 'Removed', 'Stopped', 'Exited']);

/**
 * Compose writes its progress as `Container <name>  <Verb>` lines, many per container; the last verb a container
 * printed is where it stands. Only containers are listed: a volume or a network is not a platform service.
 */
export function composeProgress(lines: readonly OutputLine[]): ComposeProgress {
  const latest = new Map<string, string>();
  for (const line of lines) {
    const match = CONTAINER_LINE.exec(line.text);
    if (match) {
      latest.set(match[1], match[2]);
    }
  }

  const services = [...latest].map(([name, verb]) => ({ name, verb }));
  return { services, settled: services.filter((s) => SETTLED.has(s.verb)).length };
}
