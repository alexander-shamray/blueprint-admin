/**
 * One run of text inside a line: plain, `code` or **strong**. A link is its words, with its target kept beside them;
 * the parser never makes a link. How a target is drawn is `RunbookSpans`' `targets`: off, as for a runbook, whose
 * targets are paths in the backend clone, it is dropped; on, a console page is routed and anything else is text.
 */
export interface Span {
  kind: 'text' | 'code' | 'strong';
  text: string;
  target?: string;
}

/**
 * One item of a list, numbered or bulleted as its marker was, with the items indented under it. A numbered item keeps
 * its number, so a list a blank line or a fence split goes on counting where it left off.
 */
export interface ListItem {
  spans: Span[];
  children: ListItem[];
  ordered: boolean;
  number?: number;
}

/**
 * The blocks of the backend's runbooks, which use headings, paragraphs, lists, fenced commands and pipe tables and nothing
 * else. Rendered by the template as elements and never as HTML, so a runbook cannot put markup on the page.
 */
export type Block =
  | { kind: 'heading'; level: number; spans: Span[] }
  | { kind: 'paragraph'; spans: Span[] }
  | { kind: 'list'; items: ListItem[] }
  | { kind: 'code'; text: string; lang?: string }
  | { kind: 'table'; header: Span[][]; rows: Span[][][] }
  | { kind: 'rule' };

const INLINE = /(`[^`]+`|\*\*[^*]+\*\*|\[[^\]]+\]\([^)]*\))/;
const LINK = /^\[([^\]]+)\]\(([^)]*)\)$/;
const HEADING = /^(#{1,6})\s+(.*)$/;
const ITEM = /^(\s*)([-*]|\d+\.)\s+(.*)$/;
const TABLE_RULE = /^\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?$/;

/** A line's spans, in order. */
export function inline(text: string): Span[] {
  return text
    .split(INLINE)
    .filter((part) => part !== '')
    .map((part): Span => {
      if (part.length > 2 && part.startsWith('`') && part.endsWith('`'))
        return { kind: 'code', text: part.slice(1, -1) };
      if (part.length > 4 && part.startsWith('**') && part.endsWith('**'))
        return { kind: 'strong', text: part.slice(2, -2) };
      const link = LINK.exec(part);
      if (link && link[2].trim() !== '') return { kind: 'text', text: link[1], target: link[2].trim() };
      return { kind: 'text', text: link ? link[1] : part };
    });
}

function cells(line: string): string[] {
  const trimmed = line.trim().replace(/^\|/, '').replace(/\|$/, '');
  return trimmed.split('|').map((c) => c.trim());
}

/** One list line as read: how far it is indented, its marker (`-`, `*` or a number and a stop), and its text. */
interface Entry {
  indent: number;
  text: string;
  marker: string;
}

/** An entry from a list line's indent, marker and text. */
function entryOf(match: RegExpExecArray): Entry {
  return { indent: match[1].length, marker: match[2], text: match[3] };
}

/** The items of a list, each under the nearest item before it that is indented less. */
function nest(entries: Entry[]): ListItem[] {
  const root: ListItem[] = [];
  const open: { indent: number; item: ListItem }[] = [];
  for (const entry of entries) {
    const ordered = entry.marker !== '-' && entry.marker !== '*';
    const item: ListItem = ordered
      ? { spans: inline(entry.text), children: [], ordered, number: Number.parseInt(entry.marker, 10) }
      : { spans: inline(entry.text), children: [], ordered };
    while (open.length > 0 && open[open.length - 1].indent >= entry.indent) open.pop();
    (open.length > 0 ? open[open.length - 1].item.children : root).push(item);
    open.push({ indent: entry.indent, item });
  }
  return root;
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
      const lang = line.trimStart().slice(3).trim().split(/\s+/)[0].toLowerCase();
      const code: string[] = [];
      for (i++; i < lines.length && !lines[i].trimStart().startsWith('```'); i++) code.push(lines[i]);
      out.push(lang === '' ? { kind: 'code', text: code.join('\n') } : { kind: 'code', text: code.join('\n'), lang });
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
      for (i += 2; i < lines.length && lines[i].trimStart().startsWith('|'); i++)
        rows.push(cells(lines[i]).map(inline));
      i--;
      out.push({ kind: 'table', header, rows });
      continue;
    }

    const item = ITEM.exec(line);
    if (item) {
      endParagraph();
      const entries: Entry[] = [entryOf(item)];
      for (; i + 1 < lines.length; i++) {
        const next = lines[i + 1];
        const nextItem = ITEM.exec(next);
        // A fence indented under an item is a code block of its own, never words of the item's.
        if (next.trimStart().startsWith('```')) break;
        if (nextItem) {
          entries.push(entryOf(nextItem));
        } else if (/^\s+\S/.test(next)) {
          entries[entries.length - 1].text += ' ' + next.trim();
        } else {
          break;
        }
      }
      out.push({ kind: 'list', items: nest(entries) });
      continue;
    }

    paragraph.push(line.trim());
  }

  endParagraph();
  return out;
}
