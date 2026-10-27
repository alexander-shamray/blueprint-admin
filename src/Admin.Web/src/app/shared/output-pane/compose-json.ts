/**
 * One line of `docker compose --progress json`: a resource and where it stands. A layer names its image in `parent_id`,
 * and a layer downloading carries its bytes so far and its size, which Compose's plain lines leave out.
 */
export interface ComposeEvent {
  /** Absent on a failure Compose reports with a message alone. */
  id?: string;
  parent_id?: string;
  status?: string;
  text?: string;
  details?: string;
  current?: number;
  total?: number;
  /** Set on a line Compose writes for a failure rather than for a resource. */
  error?: boolean;
  message?: string;
}

/** The line's event, or null for a line that is not Compose's JSON: a step the console wrote, or plain output. */
export function composeEvent(text: string): ComposeEvent | null {
  if (!text.startsWith('{')) return null;
  try {
    const event: unknown = JSON.parse(text);
    if (typeof event !== 'object' || event === null) return null;
    const { id, message } = event as Partial<ComposeEvent>;
    return typeof id === 'string' || typeof message === 'string' ? (event as ComposeEvent) : null;
  } catch {
    return null;
  }
}

/** An event as Compose's plain output writes it, ` Container commerce-sql-1  Healthy`, so the log reads as it always did. */
export function composePlain(event: ComposeEvent): string {
  if (typeof event.id !== 'string') return event.message ?? '';
  const details = event.details && event.details !== '0B' ? ` ${event.details}` : '';
  return ` ${event.id}  ${event.text ?? event.message ?? ''}${details}`;
}

/** Whether Compose reported the event as a failure. */
export function composeFailed(event: ComposeEvent): boolean {
  return event.error === true || event.status === 'Error';
}
