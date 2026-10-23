/** A stored list of names, or `[]` (every one) for anything that is not a list of strings. */
export function stringList(value: unknown): string[] {
  return Array.isArray(value) && value.every((v) => typeof v === 'string') ? [...value] : [];
}

/** Whether `name` is shown by a list where `[]` means every name. */
export function shows(list: readonly string[], name: string): boolean {
  return list.length === 0 || list.includes(name);
}

/** What a settings field shows for the value a config holds: a list as names with commas, a missing value as empty. */
export function fieldText(kind: 'names' | 'text', value: unknown): string {
  if (kind === 'names') return stringList(value).join(', ');
  return typeof value === 'string' ? value : '';
}

/** The value a settings field's text stands for: names split on commas (none is `[]`), text trimmed (none is null). */
export function fieldValue(kind: 'names' | 'text', text: string): string[] | string | null {
  if (kind === 'names') {
    return text
      .split(',')
      .map((name) => name.trim())
      .filter((name) => name !== '');
  }
  const trimmed = text.trim();
  return trimmed === '' ? null : trimmed;
}
