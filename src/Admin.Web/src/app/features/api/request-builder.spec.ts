import { buildUrl, parseHeaders, pretty, withFreshCommandId } from './request-builder';

describe('request-builder', () => {
  it('fills path placeholders encoded and appends only non-empty query values', () => {
    expect(
      buildUrl('http://localhost:5000/api/v1/orders/{id}/cancel', { id: 'a b' }, {}),
    ).toBe('http://localhost:5000/api/v1/orders/a%20b/cancel');
    expect(
      buildUrl('http://localhost:5000/api/v1/catalog/products/', {}, { cursor: '', limit: '5' }),
    ).toBe('http://localhost:5000/api/v1/catalog/products/?limit=5');
  });

  it('leaves an unfilled placeholder in place so the platform answers for it', () => {
    expect(buildUrl('http://h/{id}', {}, {})).toBe('http://h/{id}');
  });

  it('parses one Name: value header per line and reports the lines that are not headers', () => {
    expect(parseHeaders('Accept: application/json\n\n  X-Thing:  a:b  \nnonsense')).toEqual({
      headers: { Accept: 'application/json', 'X-Thing': 'a:b' },
      invalid: ['nonsense'],
    });
  });

  it('reports a header line whose name repeats an earlier one ignoring case', () => {
    expect(parseHeaders('Content-Type: text/plain\ncontent-type: application/json')).toEqual({
      headers: { 'Content-Type': 'text/plain' },
      invalid: ['content-type: application/json'],
    });
  });

  it('replaces commandId in a JSON object body and leaves anything else untouched', () => {
    expect(withFreshCommandId('{"commandId":"0","name":"x"}', 'new-id')).toBe('{\n  "commandId": "new-id",\n  "name": "x"\n}');
    expect(withFreshCommandId('{"name":"x"}', 'new-id')).toBe('{"name":"x"}');
    expect(withFreshCommandId('not json', 'new-id')).toBe('not json');
  });

  it('pretty-prints JSON and returns other text as-is', () => {
    expect(pretty('{"a":1}')).toBe('{\n  "a": 1\n}');
    expect(pretty('<html>')).toBe('<html>');
    expect(pretty('')).toBe('');
  });

  it('pretty-printing re-indents without rewriting a value: big numbers, escapes and repeated keys stay as sent', () => {
    expect(pretty('{"id":90071992547409931,"n":1.50,"s":"caf\\u00e9 \\"q\\" {,:}","a":1,"a":2,"e":[],"o":{ },"l":[true,null]}')).toBe(
      '{\n  "id": 90071992547409931,\n  "n": 1.50,\n  "s": "caf\\u00e9 \\"q\\" {,:}",\n  "a": 1,\n  "a": 2,\n  "e": [],\n  "o": {},\n  "l": [\n    true,\n    null\n  ]\n}',
    );
    expect(pretty('"just a string"')).toBe('"just a string"');
    expect(pretty('{"a":')).toBe('{"a":');
  });
});
