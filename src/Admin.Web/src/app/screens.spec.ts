import { SCREENS } from './screens';

// What the assistant explains each screen from (AI assistant design §6.1): two to five sentences, on every entry.
describe('SCREENS', () => {
  it.each(SCREENS.map((s) => [s.path, s.help] as const))('%s has help of two to five sentences', (_, help) => {
    const sentences = help.split(/[.!?](?:\s|$)/).filter((s) => s.trim() !== '');
    expect(sentences.length).toBeGreaterThanOrEqual(2);
    expect(sentences.length).toBeLessThanOrEqual(5);
  });
});
