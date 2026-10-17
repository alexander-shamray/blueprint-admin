import { QueuesView, TraceView } from '../../core/host/host-types';
import { SagaStrip, pairingText, sagaText } from './scenario-run';
import type { StepView } from './scenario-page';

/** The most characters of one step's body the document carries; the Scenario page has the rest. */
export const BODY_LIMIT = 4000;

/** The most trace events one correlation id contributes. */
export const EVENT_LIMIT = 60;

/** What the section says of a script with no `mail` step: the export's own line, not a guess at the sink's contents (spec §5.13). */
export const MAIL_OMITTED =
  "Omitted: this script does not count the mail the run caused; the deliver script's `mail` step does.";

/** Why a trace could not be read, in place of its timeline, or the view that was. */
export type TraceRead = TraceView | string;

export interface ExportInput {
  title: string;
  run: string;
  username: string;
  exportedAt: string;
  steps: readonly StepView[];
  strip: SagaStrip | null;
  /** The host's rendered, scrubbed transcript script; null when it could not be read. */
  transcript: string | null;
  /** Whether the transcript starts where the run did, or is the host's whole session because the starting point was not read. */
  transcriptScope: 'run' | 'session';
  /** By correlation id. */
  traces: Readonly<Record<string, TraceRead>>;
  /** The broker's queues at the drain step, and when the export was made; null where there was no read. */
  queuesBefore: QueuesView | null;
  queuesAfter: QueuesView | null;
}

/** The `mail` step's own count, as the page said it, or the line that says the script does not count one. */
function mailSection(steps: readonly StepView[]): string[] {
  const step = steps.find((s) => s.key === 'mail');
  if (!step) return [MAIL_OMITTED];
  // A step that never ran has no count; its detail is the run's reason for stopping, not the sink's answer.
  return step.state === 'skipped' || step.state === 'pending'
    ? ['Not counted: the mail step did not run.']
    : [`${step.state}: ${step.detail}`];
}

/** A fence longer than any run of backticks in the text, so a body that holds one cannot close it early. */
function fence(text: string, language = ''): string {
  const longest = Math.max(2, ...(text.match(/`+/g) ?? []).map((run) => run.length));
  const ticks = '`'.repeat(longest + 1);
  return `${ticks}${language}\n${text}\n${ticks}`;
}

function cell(text: string): string {
  return text.replace(/\|/g, '\\|').replace(/\r?\n/g, ' ');
}

function pretty(body: string): string {
  try {
    return JSON.stringify(JSON.parse(body), null, 2);
  } catch {
    return body;
  }
}

function clock(ms: number): string {
  return new Date(ms).toISOString();
}

function outcome(steps: readonly StepView[]): string {
  const failed = steps.find((s) => s.state === 'failed');
  if (failed) return `stopped at "${failed.title}": ${failed.detail || 'no detail'}`;
  if (steps.every((s) => s.state === 'ok')) return 'every step succeeded';
  const open = steps.find((s) => s.state === 'running' || s.state === 'pending');
  return open ? `not finished: "${open.title}" had not completed` : 'finished with steps not run';
}

function stepsSection(steps: readonly StepView[]): string[] {
  const lines = [
    '## Steps',
    '',
    '| # | Step | State | HTTP | Correlation id |',
    '|---|---|---|---|---|',
  ];
  steps.forEach((s, i) => {
    lines.push(
      `| ${i + 1} | ${cell(s.title)} | ${s.state} | ${s.status ?? ''} | ${s.correlationId ? `\`${s.correlationId}\`` : ''} |`,
    );
  });
  steps.forEach((s, i) => {
    lines.push('', `### ${i + 1}. ${s.title}`, '');
    if (s.request) lines.push(`Request: \`${s.request}\``, '');
    if (s.detail) lines.push(s.detail, '');
    if (s.regrant) lines.push(s.regrant, '');
    if (s.body) {
      const shown = pretty(s.body);
      const cut = shown.length > BODY_LIMIT;
      lines.push(fence(cut ? shown.slice(0, BODY_LIMIT) : shown, 'json'));
      if (cut) lines.push('', `Body cut at ${BODY_LIMIT} of ${shown.length} characters.`);
    }
  });
  return lines;
}

function sagaSection(strip: SagaStrip | null): string[] {
  const lines = ['## The order, as the platform reported it', ''];
  if (strip === null) return [...lines, 'No order read was made, so there is no state to report.'];
  lines.push(
    `The last read answered \`${strip.status}\` at ${clock(strip.readAt)} on the console's clock, the BFF knowing it as of ${strip.asOf}; ${strip.reads} read${strip.reads === 1 ? '' : 's'} in all. This is the last state read, not the order's state now.`,
  );
  if (strip.saga !== null && strip.pairing !== null) {
    lines.push(
      '',
      '| | Saga (Ordering) | Buyer (BFF) |',
      '|---|---|---|',
      `| State | ${sagaText(strip.saga)} | \`${strip.status}\` |`,
      `| Last changed | ${strip.sagaSeenAt === null ? '' : `first seen so at ${clock(strip.sagaSeenAt)} (the console's clock; the saga stores no time of entering a state)`} | ${strip.statusChangedAt ?? 'no stamp on the order read'} (the platform's clock) |`,
      '',
      `${strip.pairing === 'differs' ? '**[disagree]** ' : strip.pairing === 'ahead' ? '[ahead] ' : ''}${pairingText(strip.pairing)}`,
    );
  }
  if (strip.transitions.length > 0) {
    lines.push('', '| From | To | Seconds |', '|---|---|---|');
    for (const t of strip.transitions)
      lines.push(`| ${t.from} | ${t.to} | ${t.seconds ?? 'unreadable'} |`);
  }
  if (strip.stalled) {
    lines.push(
      '',
      `Stalled: the status stood at \`${strip.status}\` for ${Math.round(strip.unchangedForMs / 1000)} s across the reads.`,
    );
  }
  return lines;
}

function traceSection(
  steps: readonly StepView[],
  traces: Readonly<Record<string, TraceRead>>,
): string[] {
  const lines = ['## Trace timeline', ''];
  const ids = [...new Set(steps.flatMap((s) => (s.correlationId ? [s.correlationId] : [])))];
  if (ids.length === 0)
    return [...lines, 'No step sent a request, so there is no correlation id to read.'];
  for (const id of ids) {
    const read = traces[id];
    lines.push(`### \`${id}\``, '');
    if (read === undefined) {
      lines.push('Not read.', '');
    } else if (typeof read === 'string') {
      lines.push(`Not read: ${read}`, '');
    } else if (!read.reachable) {
      lines.push(`Grafana did not answer: ${read.error ?? 'no reason given'}.`, '');
    } else if (read.events.length === 0) {
      lines.push(`No events in the last ${read.window}.`, '');
    } else {
      for (const e of read.events.slice(0, EVENT_LIMIT)) {
        lines.push(`- ${e.at} · ${e.service} · ${e.kind} · ${e.summary.replace(/\r?\n/g, ' ')}`);
      }
      if (read.events.length > EVENT_LIMIT)
        lines.push(`- … ${read.events.length - EVENT_LIMIT} more events on the Trace screen`);
      if (read.warning) lines.push('', `Warning: ${read.warning}`);
      lines.push('');
    }
  }
  return lines;
}

function queueSection(before: QueuesView | null, after: QueuesView | null): string[] {
  const lines = ['## Queue depths', ''];
  if (!before?.reachable && !after?.reachable) {
    return [...lines, 'The broker was not read, before or after.'];
  }
  const depth = (view: QueuesView | null, name: string): string => {
    if (!view?.reachable) return 'not read';
    const q = view.queues.find((x) => x.name === name);
    return q === undefined ? 'absent' : (q.messages?.toString() ?? 'unknown');
  };
  const names = [
    ...new Set([...(before?.queues ?? []), ...(after?.queues ?? [])].map((q) => q.name)),
  ].sort();
  lines.push(
    'Before is the first read of the drain step; after is the read made when this document was written.',
    '',
  );
  lines.push('| Queue | Before | After |', '|---|---|---|');
  for (const name of names)
    lines.push(`| \`${name}\` | ${depth(before, name)} | ${depth(after, name)} |`);
  return lines;
}

/**
 * A finished run as one Markdown document to paste into an issue (spec §5.13), in the order the issue (#70) asks: the
 * operator's transcript, the steps and their statuses, the correlation ids, the trace timeline, the queue depths and the
 * mail. A section whose source is not there says so in one line rather than going missing. The text is unscrubbed here: the
 * host's scrubber has the last word before it is downloaded.
 */
export function buildExport(input: ExportInput): string {
  const out = [
    `# Scenario run ${input.run}: ${input.title}`,
    '',
    `- Run as: ${input.username}`,
    `- Exported: ${input.exportedAt}`,
    `- Outcome: ${outcome(input.steps)}`,
    '',
    '## Operator transcript',
    '',
    input.transcriptScope === 'run'
      ? 'The commands and requests the host kept from when this run began.'
      : "The host's whole session: the point where this run began could not be read.",
    '',
    input.transcript === null
      ? 'Omitted: the host would not render the transcript.'
      : input.transcript.trim() === ''
        ? 'The transcript is empty.'
        : fence(input.transcript, 'bash'),
    '',
    ...stepsSection(input.steps),
    '',
    ...sagaSection(input.strip),
    '',
    '## Correlation ids',
    '',
    ...(input.steps.some((s) => s.correlationId)
      ? input.steps
          .filter((s) => s.correlationId)
          .map((s) => `- \`${s.correlationId}\` — ${s.title}`)
      : ['No step sent a request.']),
    '',
    ...traceSection(input.steps, input.traces),
    '',
    ...queueSection(input.queuesBefore, input.queuesAfter),
    '',
    '## Mail the run caused',
    '',
    ...mailSection(input.steps),
    '',
  ];
  return out.join('\n');
}
