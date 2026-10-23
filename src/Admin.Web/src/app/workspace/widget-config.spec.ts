import { fieldText, fieldValue } from './widget-config';

describe('a settings field', () => {
  it('shows a list of names with commas and a missing value as empty', () => {
    expect(fieldText('names', ['a', 'b'])).toBe('a, b');
    expect(fieldText('names', 'not a list')).toBe('');
    expect(fieldText('text', 'job-1')).toBe('job-1');
    expect(fieldText('text', null)).toBe('');
  });

  it('reads names split on commas, and text trimmed, with nothing written as every one or none', () => {
    expect(fieldValue('names', ' a, b ,, c')).toEqual(['a', 'b', 'c']);
    expect(fieldValue('names', '  ')).toEqual([]);
    expect(fieldValue('text', ' job-1 ')).toBe('job-1');
    expect(fieldValue('text', '')).toBeNull();
  });
});
