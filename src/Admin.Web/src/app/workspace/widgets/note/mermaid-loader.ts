import type { MermaidConfig } from 'mermaid';

/** The keys Mermaid's own default config already refuses to a `%%{init}%%` directive or front matter. */
const MERMAID_SECURE_DEFAULTS = [
  'secure',
  'securityLevel',
  'startOnLoad',
  'maxTextSize',
  'suppressErrorRendering',
  'maxEdges',
] as const;

/**
 * The keys a diagram's own source may not set, beyond Mermaid's defaults: the styling ones, which feed the `<style>`
 * that `safeSvg` keeps, and the ones that would turn HTML labels back on or loosen DOMPurify. Strict mode refuses none
 * of them. Mermaid 12 already refuses a directive's `themeCSS` whose `}` runs ahead of its `{` and nests the rest under
 * the diagram's id, so this is defence in depth (#618): the console does not rest on that sanitiser and that nesting
 * staying as they are in every version, nor on what an unnested at-rule could still do.
 */
const MERMAID_SECURE_EXTRA = [
  'themeCSS',
  'themeVariables',
  'theme',
  'fontFamily',
  'altFontFamily',
  'htmlLabels',
  'flowchart',
  'dompurifyConfig',
] as const;

/**
 * Mermaid's settings (widget-workspace spec §8.3): the strict security level, which escapes the text of a label and
 * refuses click handlers, no HTML in a label, so a label is only ever text, and a `secure` list no directive in a
 * diagram's source can override.
 */
export const MERMAID_CONFIG: MermaidConfig = {
  startOnLoad: false,
  securityLevel: 'strict',
  secure: [...MERMAID_SECURE_DEFAULTS, ...MERMAID_SECURE_EXTRA],
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
  // A chunk that failed to load is tried again by the next diagram, not remembered as failed until a reload.
  loaded.catch(() => (loaded = undefined));
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
