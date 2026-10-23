import { safeSvg } from './svg-safe';

const wrap = (inner: string, attributes = '') =>
  `<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" ${attributes}>${inner}</svg>`;

describe('safeSvg', () => {
  it('keeps a drawing as it was', () => {
    const svg = safeSvg(wrap('<g><rect width="10" height="5"/><text>label</text></g><marker id="m"/>'))!;
    expect(svg.querySelectorAll('rect, text, marker').length).toBe(3);
    expect(svg.textContent).toBe('label');
  });

  it('drops a script, an event handler and a foreignObject, wherever they sit', () => {
    const svg = safeSvg(
      wrap(
        '<script>alert(1)</script><g onclick="alert(2)"><rect onmouseover="alert(3)"/></g>' +
          '<foreignObject><div xmlns="http://www.w3.org/1999/xhtml" onclick="alert(4)">x</div></foreignObject>',
        'onload="alert(5)"',
      ),
    )!;
    const html = svg.outerHTML;
    expect(html).not.toMatch(/script|onclick|onmouseover|onload|foreignObject|alert/i);
    expect(svg.querySelector('rect')).not.toBeNull();
  });

  it('drops a link, an animation, a use and an element it does not know, with what they hold', () => {
    const svg = safeSvg(
      wrap(
        '<a href="javascript:alert(1)"><text>x</text></a><a xlink:href="https://example.com"><text>y</text></a>' +
          '<use href="#a"/><animate attributeName="href" to="javascript:alert(2)"/><set attributeName="onclick" to="alert(3)"/>' +
          '<image href="https://example.com/x.png"/><madeup><text>z</text></madeup><rect id="kept"/>',
      ),
    )!;
    expect(svg.outerHTML).not.toMatch(/javascript|example\.com|<a|use|animate|set|image|madeup|>z</);
    expect(svg.querySelector('#kept')).not.toBeNull();
  });

  it('judges a name by its namespace, so a prefix cannot disguise an element or an attribute', () => {
    const svg = safeSvg(
      '<svg xmlns="http://www.w3.org/2000/svg" xmlns:x="http://www.w3.org/1999/xlink" xmlns:h="http://www.w3.org/1999/xhtml">' +
        '<h:script>alert(1)</h:script><h:div onclick="alert(2)">no</h:div>' +
        '<marker x:href="https://example.com" h:onclick="alert(3)" id="m"/></svg>',
    )!;
    expect(svg.outerHTML).not.toMatch(/script|div|onclick|example\.com|alert/);
    expect(svg.querySelector('marker')).not.toBeNull();
  });

  it('keeps a reference to an id inside the drawing and drops css that reaches outside it', () => {
    const svg = safeSvg(
      wrap(
        '<style>.a{fill:url(#g)} .b{background:url(https://example.com/x)} @import "https://example.com/y.css";</style>' +
          '<rect id="r" style="fill:url(#g)"/><rect id="s" style="fill:url(https://example.com/z)"/><marker href="#m"/>',
      ),
    )!;
    expect(svg.querySelector('style')?.textContent).toBe('');
    expect(svg.querySelector('#r')?.getAttribute('style')).toBe('fill:url(#g)');
    expect(svg.querySelector('#s')?.hasAttribute('style')).toBe(false);
    expect(svg.querySelector('marker')?.getAttribute('href')).toBe('#m');
  });

  it('drops a reference that leaves the drawing from any attribute, and from CSS written with escapes', () => {
    const svg = safeSvg(
      wrap(
        '<rect id="a" fill="url(https://example.com/x)"/><rect id="b" filter="url(#f)"/>' +
          '<rect id="c" style="fill:u\\72l(https://example.com/y)"/><rect id="d" style="background:image-set(\'https://example.com/z\' 1x)"/>' +
          '<style>.a{fill:\\75rl(https://example.com/w)}</style>',
      ),
    )!;
    expect(svg.outerHTML).not.toMatch(/example\.com/);
    expect(svg.querySelector('#a')?.hasAttribute('fill')).toBe(false);
    expect(svg.querySelector('#b')?.getAttribute('filter')).toBe('url(#f)');
  });

  it('answers null for text that is not one SVG document', () => {
    expect(safeSvg('<div>no</div>')).toBeNull();
    expect(safeSvg('<svg')).toBeNull();
    expect(safeSvg('<html xmlns="http://www.w3.org/1999/xhtml"><body/></html>')).toBeNull();
  });
});
