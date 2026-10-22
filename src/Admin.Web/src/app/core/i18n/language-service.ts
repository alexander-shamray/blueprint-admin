import { DOCUMENT } from '@angular/common';
import { Injectable, LOCALE_ID, inject } from '@angular/core';
import { LOCALES, LocaleId, storeLocale } from './locales';

/**
 * The language picker's half of AI assistant design §5.5: translations load once, before bootstrap, so switching
 * stores the choice and reloads the page. The reload is an ordinary one; the caller decides whether it may happen now.
 */
@Injectable({ providedIn: 'root' })
export class LanguageService {
  private readonly location = inject(DOCUMENT).location;
  readonly current = inject(LOCALE_ID) as LocaleId;
  readonly choices = LOCALES;

  switchTo(locale: LocaleId): void {
    if (locale === this.current) return;
    storeLocale(locale);
    this.location.reload();
  }
}
