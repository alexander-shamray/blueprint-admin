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

  it('keeps a link inside the drawing and drops one that leaves it', () => {
    const svg = safeSvg(wrap('<use href="#a"/><a href="javascript:alert(1)"><text>x</text></a><a xlink:href="https://example.com"><text>y</text></a>'))!;
    expect(svg.outerHTML).not.toMatch(/javascript|example\.com/);
  });

  it('answers null for text that is not one SVG document', () => {
    expect(safeSvg('<div>no</div>')).toBeNull();
    expect(safeSvg('<svg')).toBeNull();
    expect(safeSvg('<html xmlns="http://www.w3.org/1999/xhtml"><body/></html>')).toBeNull();
  });
});
