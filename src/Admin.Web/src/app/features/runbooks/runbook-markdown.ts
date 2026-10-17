/** One run of text inside a line: plain, `code` or **strong**. A link keeps its words and drops its target, which is a path in the backend clone. */
export interface Span {
  kind: 'text' | 'code' | 'strong';
  text: string;
}

/**
 * The blocks of the backend's runbooks, which use headings, paragraphs, lists, fenced commands and pipe tables and nothing
 * else. Rendered by the template as elements and never as HTML, so a runbook cannot put markup on the page.
 */
export type Block =
  | { kind: 'heading'; level: number; spans: Span[] }
  | { kind: 'paragraph'; spans: Span[] }
  | { kind: 'list'; items: Span[][] }
  | { kind: 'code'; text: string }
  | { kind: 'table'; header: Span[][]; rows: Span[][][] }
  | { kind: 'rule' };

const INLINE = /(`[^`]+`|\*\*[^*]+\*\*|\[[^\]]+\]\([^)]*\))/;
const LINK = /^\[([^\]]+)\]\([^)]*\)$/;
const HEADING = /^(#{1,6})\s+(.*)$/;
const ITEM = /^(\s*)(?:[-*]|\d+\.)\s+(.*)$/;
const TABLE_RULE = /^\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?$/;

/** A line's spans, in order. */
export function inline(text: string): Span[] {
  return text
    .split(INLINE)
    .filter((part) => part !== '')
    .map((part): Span => {
      if (part.length > 2 && part.startsWith('`') && part.endsWith('`')) return { kind: 'code', text: part.slice(1, -1) };
      if (part.length > 4 && part.startsWith('**') && part.endsWith('**')) return { kind: 'strong', text: part.slice(2, -2) };
      const link = LINK.exec(part);
      return { kind: 'text', text: link ? link[1] : part };
    });
}

function cells(line: string): string[] {
  const trimmed = line.trim().replace(/^\|/, '').replace(/\|$/, '');
  return trimmed.split('|').map((c) => c.trim());
}

/** The runbook as blocks. A line that fits no block is a paragraph's, so nothing in the text is ever dropped. */
export function blocks(markdown: string): Block[] {
  const lines = markdown.replace(/\r\n/g, '\n').split('\n');
  const out: Block[] = [];
  let paragraph: string[] = [];

  const endParagraph = (): void => {
    if (paragraph.length > 0) out.push({ kind: 'paragraph', spans: inline(paragraph.join(' ')) });
    paragraph = [];
  };

  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];

    if (line.trimStart().startsWith('```')) {
      endParagraph();
      const code: string[] = [];
      for (i++; i < lines.length && !lines[i].trimStart().startsWith('```'); i++) code.push(lines[i]);
      out.push({ kind: 'code', text: code.join('\n') });
      continue;
    }

    if (line.trim() === '') {
      endParagraph();
      continue;
    }

    const heading = HEADING.exec(line);
    if (heading) {
      endParagraph();
      out.push({ kind: 'heading', level: heading[1].length, spans: inline(heading[2]) });
      continue;
    }

    if (/^-{3,}$/.test(line.trim())) {
      endParagraph();
      out.push({ kind: 'rule' });
      continue;
    }

    if (line.trimStart().startsWith('|') && i + 1 < lines.length && TABLE_RULE.test(lines[i + 1].trim())) {
      endParagraph();
      const header = cells(line).map(inline);
      const rows: Span[][][] = [];
      for (i += 2; i < lines.length && lines[i].trimStart().startsWith('|'); i++) rows.push(cells(lines[i]).map(inline));
      i--;
      out.push({ kind: 'table', header, rows });
      continue;
    }

    const item = ITEM.exec(line);
    if (item) {
      endParagraph();
      const items: string[] = [item[2]];
      for (; i + 1 < lines.length; i++) {
        const next = lines[i + 1];
        if (ITEM.test(next) && !/^\s/.test(next)) {
          items.push(ITEM.exec(next)![2]);
        } else if (/^\s+\S/.test(next) && !ITEM.test(next)) {
          items[items.length - 1] += ' ' + next.trim();
        } else {
          break;
        }
      }
      out.push({ kind: 'list', items: items.map(inline) });
      continue;
    }

    paragraph.push(line.trim());
  }

  endParagraph();
  return out;
}
