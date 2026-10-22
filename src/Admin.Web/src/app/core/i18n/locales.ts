/**
 * The languages the console ships (AI assistant design §5.5): `en-US` is the source, and every other entry has a
 * `src/locale/messages.<id>.json` the host serves beside the app. Adding a language is one file and one entry here.
 * `name` is the language's own name for itself, so an operator who cannot read the active language can still find
 * theirs; it is never translated.
 */
export const LOCALES = [
  { id: 'en-US', name: 'English' },
  { id: 'ru-RU', name: 'Русский' },
] as const;

export type LocaleId = (typeof LOCALES)[number]['id'];

/** The language the templates and `$localize` calls are written in; it needs no translation file. */
export const SOURCE_LOCALE: LocaleId = 'en-US';

/** The browser-local key; like the theme, the choice is per browser and not part of the workspace document. */
export const LOCALE_STORAGE_KEY = 'admin.locale';

/**
 * The stored choice when it names a shipped language; otherwise the first of the browser's languages that has a
 * translation, matched exactly and then by its language alone (`ru` and `ru-BY` both take `ru-RU`); otherwise the
 * source.
 */
export function chooseLocale(stored: string | null, languages: readonly string[]): LocaleId {
  const shipped = LOCALES.find((l) => l.id === stored);
  if (shipped) return shipped.id;
  for (const language of languages) {
    const exact = LOCALES.find((l) => l.id.toLowerCase() === language.toLowerCase());
    if (exact) return exact.id;
    const primary = language.split('-')[0].toLowerCase();
    const same = LOCALES.find((l) => l.id.split('-')[0].toLowerCase() === primary);
    if (same) return same.id;
  }
  return SOURCE_LOCALE;
}

/** What this page loads in: storage can be missing or throw (private window, blocked site data), so it is optional. */
export function readChosenLocale(): LocaleId {
  let stored: string | null = null;
  try {
    stored = localStorage.getItem(LOCALE_STORAGE_KEY);
  } catch {
    // No storage: the browser's languages decide.
  }
  return chooseLocale(stored, navigator.languages ?? [navigator.language]);
}

export function storeLocale(locale: LocaleId): void {
  try {
    localStorage.setItem(LOCALE_STORAGE_KEY, locale);
  } catch {
    // Storage is a convenience; without it the reload falls back to the browser's languages.
  }
}
