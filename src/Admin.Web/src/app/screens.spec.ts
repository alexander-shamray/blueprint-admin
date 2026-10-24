import { builtInWidgets } from './workspace/built-in-pages';
import { SCREENS } from './screens';
import { widgetType } from './workspace/widgets';

// What the assistant explains each screen from (AI assistant design §6.1): two to five sentences, on every entry.
describe('SCREENS', () => {
  // The widgets own their help, so a screen names the ones its built-in page holds and does not describe them.
  it.each(SCREENS.map((s) => [s.path, s.help] as const))('%s names every widget its page holds', (path, help) => {
    const titles = builtInWidgets(path).map((w) => widgetType(w.type)!.title);

    expect(titles.length).toBeGreaterThan(0);
    for (const title of titles) expect(help).toContain(title);
  });

  it.each(SCREENS.map((s) => [s.path, s.help] as const))('%s has help of two to five sentences', (_, help) => {
    const sentences = help.split(/[.!?](?:\s|$)/).filter((s) => s.trim() !== '');
    expect(sentences.length).toBeGreaterThanOrEqual(2);
    expect(sentences.length).toBeLessThanOrEqual(5);
  });
});
