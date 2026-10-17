import { ListItem, blocks, inline } from './runbook-markdown';

const text = (item: ListItem): string => item.spans.map((s) => s.text).join('');

describe('runbook markdown', () => {
  it('reads code, strong and a link as spans, keeping the link words and dropping its target', () => {
    expect(inline('Run `kubectl get pods` **now** per [the guide](../x.md).')).toEqual([
      { kind: 'text', text: 'Run ' },
      { kind: 'code', text: 'kubectl get pods' },
      { kind: 'text', text: ' ' },
      { kind: 'strong', text: 'now' },
      { kind: 'text', text: ' per ' },
      { kind: 'text', text: 'the guide' },
      { kind: 'text', text: '.' },
    ]);
  });

  it('reads headings, paragraphs that wrap, fenced commands and rules', () => {
    const result = blocks(['# Runbook — outbox', '', 'One line', 'wrapped.', '', '```bash', 'kubectl get pods', '', 'echo done', '```', '', '---'].join('\n'));

    expect(result).toEqual([
      { kind: 'heading', level: 1, spans: [{ kind: 'text', text: 'Runbook — outbox' }] },
      { kind: 'paragraph', spans: [{ kind: 'text', text: 'One line wrapped.' }] },
      { kind: 'code', text: 'kubectl get pods\n\necho done' },
      { kind: 'rule' },
    ]);
  });

  it('reads a list with its continuation lines, bulleted or numbered', () => {
    const result = blocks(['- first', '  continued', '- second', '', '1. one', '2. two'].join('\n'));

    expect(result.map((b) => b.kind)).toEqual(['list', 'list']);
    const [bullets, numbers] = result as { kind: 'list'; items: ListItem[] }[];
    expect(bullets.items.map(text)).toEqual(['first continued', 'second']);
    expect(numbers.items.map(text)).toEqual(['one', 'two']);
  });

  it('keeps an indented item under the item before it, to any depth, and returns to the outer list after it', () => {
    const result = blocks(['1. drain', '   - check `a`', '   - check `b`', '     - deeper', '2. restore', '   continued'].join('\n'));

    expect(result).toHaveLength(1);
    const [list] = result as { kind: 'list'; items: ListItem[] }[];
    expect(list.items.map(text)).toEqual(['drain', 'restore continued']);
    expect(list.items[0].children.map(text)).toEqual(['check a', 'check b']);
    expect(list.items[0].children[1].children.map(text)).toEqual(['deeper']);
    expect(list.items[1].children).toEqual([]);
  });

  it('reads a pipe table into a header and rows, and a leading pipe line with no rule under it as a paragraph', () => {
    const table = blocks(['| | |', '|---|---|', '| Alert | `OutboxGrowth` |', '| Owner | The service team |'].join('\n'));

    expect(table).toHaveLength(1);
    const first = table[0] as { kind: 'table'; header: unknown[]; rows: { text: string }[][][] };
    expect(first.kind).toBe('table');
    expect(first.rows.map((r) => r.map((c) => c.map((s) => s.text).join('')))).toEqual([
      ['Alert', 'OutboxGrowth'],
      ['Owner', 'The service team'],
    ]);

    expect(blocks('| not a table |').map((b) => b.kind)).toEqual(['paragraph']);
  });

  it('keeps markup in the text as text: nothing is ever HTML', () => {
    const [paragraph] = blocks('<img src=x onerror=alert(1)> and `<b>`');

    expect(paragraph).toEqual({
      kind: 'paragraph',
      spans: [
        { kind: 'text', text: '<img src=x onerror=alert(1)> and ' },
        { kind: 'code', text: '<b>' },
      ],
    });
  });
});
