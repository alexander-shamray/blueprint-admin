const SVG_NAMESPACE = 'http://www.w3.org/2000/svg';

/** Elements that can run script or load a document, which a diagram has no use for. */
const FORBIDDEN = new Set(['script', 'foreignobject', 'iframe', 'object', 'embed', 'animate', 'set', 'use', 'image']);

/** An attribute that points somewhere is kept only when it points inside the drawing, as a marker or a gradient does. */
const LINKING = new Set(['href', 'xlink:href', 'src']);

/**
 * The SVG Mermaid drew, parsed and stripped of anything that could run: script, `foreignObject`, event handlers and
 * links out of the drawing. Mermaid's strict level already sanitises its output; this is the second wall (widget
 * workspace spec §8.3), so a Mermaid fault or a source it mis-escapes still cannot put script on the page.
 * Returns null for text that is not one SVG document.
 */
export function safeSvg(svg: string): SVGElement | null {
  const parsed = new DOMParser().parseFromString(svg, 'image/svg+xml');
  const root = parsed.documentElement;
  if (root.localName !== 'svg' || root.namespaceURI !== SVG_NAMESPACE || parsed.querySelector('parsererror')) return null;

  const walk = (element: Element): void => {
    for (const child of [...element.children]) {
      if (FORBIDDEN.has(child.localName.toLowerCase())) child.remove();
      else walk(child);
    }
    for (const attribute of [...element.attributes]) {
      const name = attribute.name.toLowerCase();
      if (name.startsWith('on')) element.removeAttribute(attribute.name);
      else if (LINKING.has(name) && !attribute.value.trim().startsWith('#')) element.removeAttribute(attribute.name);
    }
  };
  walk(root);

  return document.importNode(root, true) as unknown as SVGElement;
}
