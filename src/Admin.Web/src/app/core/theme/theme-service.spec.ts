import { TestBed } from '@angular/core/testing';
import { THEME_STORAGE_KEY, ThemeService } from './theme-service';

describe('ThemeService', () => {
  afterEach(() => {
    localStorage.clear();
    document.documentElement.removeAttribute('data-theme');
  });

  it('leaves the attribute off for system', () => {
    const theme = TestBed.inject(ThemeService);
    TestBed.tick();

    expect(theme.choice()).toBe('system');
    expect(document.documentElement.hasAttribute('data-theme')).toBe(false);
  });

  it('sets data-theme and remembers a light or dark choice', () => {
    const theme = TestBed.inject(ThemeService);
    theme.set('dark');
    TestBed.tick();

    expect(document.documentElement.getAttribute('data-theme')).toBe('dark');
    expect(localStorage.getItem(THEME_STORAGE_KEY)).toBe('dark');

    theme.set('system');
    TestBed.tick();
    expect(document.documentElement.hasAttribute('data-theme')).toBe(false);
  });

  it('reads the stored choice and ignores a value it does not know', () => {
    localStorage.setItem(THEME_STORAGE_KEY, 'light');
    expect(TestBed.inject(ThemeService).choice()).toBe('light');

    TestBed.resetTestingModule();
    localStorage.setItem(THEME_STORAGE_KEY, 'sepia');
    expect(TestBed.inject(ThemeService).choice()).toBe('system');
  });

  it('still applies a choice when storage throws', () => {
    const theme = TestBed.inject(ThemeService);
    const setItem = vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('blocked');
    });
    try {
      theme.set('dark');
      TestBed.tick();
      expect(document.documentElement.getAttribute('data-theme')).toBe('dark');
    } finally {
      setItem.mockRestore();
    }
  });
});
