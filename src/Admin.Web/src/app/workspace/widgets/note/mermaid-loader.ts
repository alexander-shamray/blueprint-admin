import type { MermaidConfig } from 'mermaid';

/**
 * Mermaid's settings (widget-workspace spec §8.3): the strict security level, which escapes the text of a label and
 * refuses click handlers, and no HTML in a label, so a label is only ever text.
 */
export const MERMAID_CONFIG: MermaidConfig = {
  startOnLoad: false,
  securityLevel: 'strict',
  htmlLabels: false,
  flowchart: { htmlLabels: false },
  theme: 'neutral',
};

let loaded: Promise<typeof import('mermaid').default> | undefined;
let counter = 0;

/** Mermaid, fetched on the first diagram and initialised once; the sequence diagram (§8.2) shares it. */
function mermaid(): Promise<typeof import('mermaid').default> {
  loaded ??= import('mermaid').then((m) => {
    m.default.initialize(MERMAID_CONFIG);
    return m.default;
  });
  return loaded;
}

/** The SVG text of a diagram's source. Rejects with Mermaid's own message when the source is not a diagram. */
export async function renderDiagram(source: string): Promise<string> {
  const id = `note-diagram-${++counter}`;
  try {
    return (await (await mermaid()).render(id, source)).svg;
  } catch (e) {
    // A source Mermaid refuses leaves its scratch element on the page; take it off with the error.
    document.getElementById(`d${id}`)?.remove();
    throw e;
  }
}
