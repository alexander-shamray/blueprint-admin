const SVG_NAMESPACE = 'http://www.w3.org/2000/svg';

/**
 * The elements a Mermaid drawing is made of, lower-cased. Anything else (script, foreignObject, animation, a link, an
 * element of another namespace, a name this list has never heard of) is dropped with what it holds.
 */
const ALLOWED = new Set([
  'svg',
  'g',
  'defs',
  'title',
  'desc',
  'style',
  'symbol',
  'marker',
  'clippath',
  'mask',
  'pattern',
  'path',
  'rect',
  'circle',
  'ellipse',
  'line',
  'polyline',
  'polygon',
  'text',
  'tspan',
  'lineargradient',
  'radialgradient',
  'stop',
]);

/** An attribute that points somewhere is kept only when it points inside the drawing, as a marker or a gradient does. */
const LINKING = new Set(['href', 'src']);

/** A `url(...)` in CSS that is not a reference to an id inside the drawing, which would fetch or run something. */
const OUTSIDE_URL = /url\(\s*(?!['"]?#)/i;
const OUTSIDE_IMPORT = /@import/i;
const IMAGE_SET = /image-set\(/i;
/**
 * What reaches past the shadow root the drawing is drawn under (`mermaid-diagram.ts`): a rule for its host, and the
 * at-rules some engines register for the whole document. Mermaid writes none of them.
 */
const PAST_THE_SHADOW = /:host|@font-face|@property/i;
const COMMENT = /\/\*[\s\S]*?(\*\/|$)/g;

/** CSS read the way a browser reads it: `u\72l(` is `url(`. Applied before any of the tests above. */
function unescapeCss(value: string): string {
  return value
    .replace(/\\([0-9a-f]{1,6})[ \t\r\n\f]?/gi, (_, hex: string) =>
      String.fromCodePoint(Math.min(parseInt(hex, 16), 0x10ffff)),
    )
    .replace(/\\(.)/g, '$1');
}

/** Whether a value, as CSS, would fetch or import something from outside the drawing. */
function reachesOutside(value: string): boolean {
  const css = unescapeCss(value);
  return OUTSIDE_URL.test(css) || OUTSIDE_IMPORT.test(css) || IMAGE_SET.test(css);
}

/**
 * The SVG Mermaid drew, parsed and reduced to an allow-list of shapes and text: no script, no `foreignObject`, no
 * animation, no link, no event handler, no reference that leaves the drawing. Mermaid's strict level already
 * sanitises its output; this is the second wall (widget-workspace spec §8.3), so a Mermaid fault or a source it
 * mis-escapes still cannot put script on the page. Names are judged by namespace and local name, so a prefix cannot
 * disguise one. Returns null for text that is not one SVG document.
 */
export function safeSvg(svg: string): SVGElement | null {
  const parsed = new DOMParser().parseFromString(svg, 'image/svg+xml');
  const root = parsed.documentElement;
  if (root.localName !== 'svg' || root.namespaceURI !== SVG_NAMESPACE || parsed.querySelector('parsererror'))
    return null;

  const walk = (element: Element): void => {
    for (const child of [...element.children]) {
      if (child.namespaceURI !== SVG_NAMESPACE || !ALLOWED.has(child.localName.toLowerCase())) child.remove();
      else walk(child);
    }
    const css = element.textContent ?? '';
    // A browser drops a comment before it reads a token, so `:/**/host` is `:host`.
    const read = unescapeCss(css.replace(COMMENT, ''));
    if (element.localName.toLowerCase() === 'style' && (reachesOutside(css) || PAST_THE_SHADOW.test(read)))
      element.textContent = '';
    for (const attribute of [...element.attributes]) {
      const name = attribute.localName.toLowerCase();
      const outside = !attribute.value.trim().startsWith('#');
      if (name.startsWith('on')) element.removeAttributeNode(attribute);
      else if (LINKING.has(name) && outside) element.removeAttributeNode(attribute);
      else if (reachesOutside(attribute.value)) element.removeAttributeNode(attribute);
    }
  };
  walk(root);

  return document.importNode(root, true) as unknown as SVGElement;
}
