import { OutputLine } from '../../core/host/host-types';
import { composeEvent, composePlain } from './compose-json';

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
 * Compose writes its progress as `Container <name>  <Verb>` lines, many per container, or with `--progress json` as
 * the same pair in a JSON line; the last verb a container printed is where it stands. Only containers are listed: a
 * volume or a network is not a platform service. A line that does not name a container is not parsed, because an Up
 * that pulls writes thousands of layer lines.
 */
export function composeProgress(lines: readonly OutputLine[]): ComposeProgress {
  const latest = new Map<string, string>();
  for (const line of lines) {
    if (!line.text.includes('Container ')) continue;
    const event = composeEvent(line.text);
    const match = CONTAINER_LINE.exec(event ? composePlain(event) : line.text);
    if (match) {
      latest.set(match[1], match[2]);
    }
  }

  const services = [...latest].map(([name, verb]) => ({ name, verb }));
  return { services, settled: services.filter((s) => SETTLED.has(s.verb)).length };
}
