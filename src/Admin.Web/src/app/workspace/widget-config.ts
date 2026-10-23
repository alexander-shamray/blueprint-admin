/** A stored list of names, or `[]` (every one) for anything that is not a list of strings. */
export function stringList(value: unknown): string[] {
  return Array.isArray(value) && value.every((v) => typeof v === 'string') ? [...value] : [];
}

/** Whether `name` is shown by a list where `[]` means every name. */
export function shows(list: readonly string[], name: string): boolean {
  return list.length === 0 || list.includes(name);
}

/**
 * What a settings field edits: `names` is a list written with commas (empty is "every one"), `text` is one value
 * (empty is none), `flag` is a switch, off unless stored as true.
 */
export type FieldKind = 'names' | 'text' | 'flag';

/** What a settings field shows for the value a config holds: a list as names with commas, a missing value as empty. */
export function fieldText(kind: FieldKind, value: unknown): string {
  if (kind === 'names') return stringList(value).join(', ');
  if (kind === 'flag') return value === true ? 'true' : 'false';
  return typeof value === 'string' ? value : '';
}

/** The value a settings field's text stands for: names split on commas (none is `[]`), text trimmed (none is null). */
export function fieldValue(kind: FieldKind, text: string): string[] | string | boolean | null {
  if (kind === 'flag') return text === 'true';
  if (kind === 'names') {
    return text
      .split(',')
      .map((name) => name.trim())
      .filter((name) => name !== '');
  }
  const trimmed = text.trim();
  return trimmed === '' ? null : trimmed;
}
