import { LOCALE_STORAGE_KEY, chooseLocale, readChosenLocale, storeLocale } from './locales';

describe('chooseLocale', () => {
  it('takes a stored choice that names a shipped language', () => {
    expect(chooseLocale('ru-RU', ['en-US'])).toBe('ru-RU');
  });

  it('ignores a stored value it does not ship and asks the browser', () => {
    expect(chooseLocale('fr-FR', ['ru-RU'])).toBe('ru-RU');
  });

  it('matches the browser language exactly, then by its language alone', () => {
    expect(chooseLocale(null, ['en-us'])).toBe('en-US');
    expect(chooseLocale(null, ['ru'])).toBe('ru-RU');
    expect(chooseLocale(null, ['ru-BY'])).toBe('ru-RU');
  });

  it('takes the first browser language it has a translation for', () => {
    expect(chooseLocale(null, ['de-DE', 'ru-RU', 'en-US'])).toBe('ru-RU');
  });

  it('falls back to the source when nothing matches', () => {
    expect(chooseLocale(null, ['de-DE', 'fr'])).toBe('en-US');
    expect(chooseLocale(null, [])).toBe('en-US');
  });
});

describe('the stored choice', () => {
  afterEach(() => localStorage.clear());

  it('is read back', () => {
    storeLocale('ru-RU');
    expect(localStorage.getItem(LOCALE_STORAGE_KEY)).toBe('ru-RU');
    expect(readChosenLocale()).toBe('ru-RU');
  });

  it('is optional: blocked storage leaves the browser languages to decide', () => {
    const getItem = vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('blocked');
    });
    const setItem = vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('blocked');
    });
    try {
      expect(() => storeLocale('ru-RU')).not.toThrow();
      expect(readChosenLocale()).toBe(chooseLocale(null, navigator.languages));
    } finally {
      getItem.mockRestore();
      setItem.mockRestore();
    }
  });
});
