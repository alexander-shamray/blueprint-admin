/** Fills `{name}` placeholders (URL-encoded) and appends the query values that are not empty. */
export function buildUrl(template: string, path: Record<string, string>, query: Record<string, string>): string {
  const filled = template.replace(/\{([^}]+)\}/g, (whole, name: string) =>
    path[name] ? encodeURIComponent(path[name]) : whole,
  );
  const search = new URLSearchParams(Object.entries(query).filter(([, value]) => value !== ''));
  const qs = search.toString();
  return qs ? `${filled}${filled.includes('?') ? '&' : '?'}${qs}` : filled;
}

/**
 * One `Name: value` per line; blank lines are skipped. Any other line is reported, and so is a line
 * whose name repeats an earlier one ignoring case (the host refuses the pair).
 */
export function parseHeaders(text: string): { headers: Record<string, string>; invalid: string[] } {
  const headers: Record<string, string> = {};
  const invalid: string[] = [];
  const seen = new Set<string>();
  for (const raw of text.split('\n')) {
    const line = raw.trim();
    if (!line) continue;
    const colon = line.indexOf(':');
    const name = line.slice(0, colon).trim();
    if (colon <= 0 || seen.has(name.toLowerCase())) {
      invalid.push(line);
      continue;
    }
    seen.add(name.toLowerCase());
    headers[name] = line.slice(colon + 1).trim();
  }
  return { headers, invalid };
}

/**
 * The platform keys idempotency on `commandId` (Common.Application IdempotencyBehavior), so a
 * resend with the same one is the same intent. Each Send is a new intent (spec §6).
 */
export function withFreshCommandId(body: string, uuid: string): string {
  try {
    const parsed: unknown = JSON.parse(body);
    if (parsed && typeof parsed === 'object' && !Array.isArray(parsed) && 'commandId' in parsed) {
      return JSON.stringify({ ...parsed, commandId: uuid }, null, 2);
    }
  } catch {
    // Not JSON: sent as typed.
  }
  return body;
}

export function pretty(body: string): string {
  try {
    return body ? JSON.stringify(JSON.parse(body), null, 2) : body;
  } catch {
    return body;
  }
}
