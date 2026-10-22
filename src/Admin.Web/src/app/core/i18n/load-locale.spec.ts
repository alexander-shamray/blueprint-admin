import { formatDate, formatNumber } from '@angular/common';
import { clearTranslations } from '@angular/localize';
import { loadLocale } from './load-locale';

/** A fetch that answers one translation file, recording what it was asked for. */
function serving(body: unknown, status = 200) {
  const asked: string[] = [];
  const fetcher = (async (url: string) => {
    asked.push(url);
    return new Response(JSON.stringify(body), { status });
  }) as unknown as typeof fetch;
  return { asked, fetcher };
}

describe('loadLocale', () => {
  afterEach(() => clearTranslations());

  it('fetches nothing for the source language', async () => {
    const { asked, fetcher } = serving({});

    expect(await loadLocale('en-US', fetcher)).toBe('en-US');
    expect(asked).toEqual([]);
    expect($localize`Undo`).toBe('Undo');
  });

  it("hands the host's translations to $localize, keyed by the extracted id", async () => {
    const { asked, fetcher } = serving({ locale: 'ru-RU', translations: { '5775346636203685655': 'Отменить' } });

    expect(await loadLocale('ru-RU', fetcher)).toBe('ru-RU');
    expect(asked).toEqual(['locale/messages.ru-RU.json']);
    expect($localize`Undo`).toBe('Отменить');
  });

  it('formats a date and a number in each language', async () => {
    await loadLocale('ru-RU', serving({ locale: 'ru-RU', translations: {} }).fetcher);
    const day = new Date(2026, 9, 22, 14, 5);

    expect(formatNumber(1234.5, 'en-US')).toBe('1,234.5');
    expect(formatNumber(1234.5, 'ru-RU')).toBe('1 234,5');
    expect(formatDate(day, 'mediumDate', 'en-US')).toBe('Oct 22, 2026');
    expect(formatDate(day, 'mediumDate', 'ru-RU')).toBe('22 окт. 2026 г.');
  });

  it('starts in the source language when the host cannot serve the translations', async () => {
    const error = vi.spyOn(console, 'error').mockImplementation(() => undefined);
    try {
      expect(await loadLocale('ru-RU', serving('missing', 404).fetcher)).toBe('en-US');
      expect($localize`Undo`).toBe('Undo');
      expect(error).toHaveBeenCalled();
    } finally {
      error.mockRestore();
    }
  });
});
