import asset from '../../../public/assistant-catalogue.json';
import { catalogue } from './assistant-service';

/**
 * The catalogue the panel sends is also a file the build copies into the host's `wwwroot`, which the MCP route reads as
 * its one resource (AI assistant design §12). `catalogue()` owns it; the file is its output in the source language, and
 * this spec fails a file that has fallen behind, with the text that replaces it.
 */
describe('assistant-catalogue.json', () => {
  it('is what catalogue() returns, so the MCP route and the panel explain the same things', () => {
    const wanted = JSON.stringify(catalogue(), null, 2) + '\n';

    expect(
      JSON.stringify(asset, null, 2) + '\n',
      `src/Admin.Web/public/assistant-catalogue.json is stale; replace it with:\n${wanted}`,
    ).toBe(wanted);
  });
});
