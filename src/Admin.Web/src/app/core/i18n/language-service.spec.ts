import { DOCUMENT } from '@angular/common';
import { LOCALE_ID } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { LanguageService } from './language-service';
import { LOCALE_STORAGE_KEY } from './locales';

describe('LanguageService', () => {
  let reload: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    reload = vi.fn();
    TestBed.configureTestingModule({
      providers: [
        { provide: LOCALE_ID, useValue: 'en-US' },
        { provide: DOCUMENT, useValue: { location: { reload } } },
      ],
    });
  });

  afterEach(() => localStorage.clear());

  it('stores another language and reloads, since translations load before bootstrap', () => {
    TestBed.inject(LanguageService).switchTo('ru-RU');

    expect(localStorage.getItem(LOCALE_STORAGE_KEY)).toBe('ru-RU');
    expect(reload).toHaveBeenCalledOnce();
  });

  it('does nothing for the language already loaded', () => {
    TestBed.inject(LanguageService).switchTo('en-US');

    expect(localStorage.getItem(LOCALE_STORAGE_KEY)).toBeNull();
    expect(reload).not.toHaveBeenCalled();
  });
});
