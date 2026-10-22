import { registerLocaleData } from '@angular/common';
import localeRu from '@angular/common/locales/ru';
import { loadTranslations } from '@angular/localize';
import { LocaleId, SOURCE_LOCALE, TranslatedLocale } from './locales';

/**
 * Angular's formats for each translated language; `en-US` is built in. Registered under the language, `ru` for
 * `ru-RU`. A full record, so a language added to `LOCALES` without its data does not compile: without it every
 * `number` and `date` pipe would throw once the page renders.
 */
const LOCALE_DATA: Record<TranslatedLocale, unknown[]> = { 'ru-RU': localeRu };

/** The shape `ng extract-i18n --format json` writes, and the shape each translation file keeps. */
export interface TranslationFile {
  locale: string;
  translations: Record<string, string>;
}

/**
 * Before bootstrap (AI assistant design §5.5): fetch the chosen language's translations from the host, same origin,
 * and hand them to `$localize`, then register its dates and numbers. A translation the host cannot serve leaves the
 * console in the source language rather than not starting it: the answer is the language this page actually loaded in.
 */
export async function loadLocale(locale: LocaleId, fetcher: typeof fetch = fetch): Promise<LocaleId> {
  if (locale === SOURCE_LOCALE) return locale;
  try {
    const response = await fetcher(`locale/messages.${locale}.json`);
    if (!response.ok) throw new Error(`${response.status} ${response.statusText}`);
    const file = (await response.json()) as TranslationFile;
    loadTranslations(file.translations);
  } catch (e) {
    console.error(`The ${locale} translations could not be loaded, so the console is in ${SOURCE_LOCALE}.`, e);
    return SOURCE_LOCALE;
  }
  registerLocaleData(LOCALE_DATA[locale]);
  return locale;
}
