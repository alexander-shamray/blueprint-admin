/** A stored list of names, or `[]` (every one) for anything that is not a list of strings. */
export function stringList(value: unknown): string[] {
  return Array.isArray(value) && value.every((v) => typeof v === 'string') ? [...value] : [];
}

/** Whether `name` is shown by a list where `[]` means every name. */
export function shows(list: readonly string[], name: string): boolean {
  return list.length === 0 || list.includes(name);
}
