// The lint file-set check's own cases, run by `npm run lint:files` before the check itself.
import assert from 'node:assert/strict';
import { test } from 'node:test';
import { expectedFiles, patternsFrom, setDifference } from './lint-file-set.lib.mjs';

const files = ['src/a.ts', 'src/x/b.html', 'src/x/c.scss', 'e2e/d.ts', 'e2e/d.json', 'playwright.config.ts', 'other.ts'];

test('patterns read the way angular.json gives them', () => {
  const angular = { projects: { web: { architect: { lint: { options: { lintFilePatterns: ['src/**/*.ts'] } } } } } };
  assert.deepEqual(patternsFrom(angular), ['src/**/*.ts']);
  assert.throws(() => patternsFrom({ projects: {} }), /names no lintFilePatterns/);
});

test('a tree pattern and a plain path name their files and nothing else', () => {
  const patterns = ['src/**/*.ts', 'src/**/*.html', 'e2e/**/*.ts', 'playwright.config.ts'];
  assert.deepEqual(expectedFiles(patterns, files), [
    'e2e/d.ts',
    'playwright.config.ts',
    'src/a.ts',
    'src/x/b.html',
  ]);
});

test('a pattern shape the reader does not know is refused', () => {
  assert.throws(() => expectedFiles(['src/**/!(x).ts'], files), /neither/);
});

test('a narrowed or widened lint set shows as a difference', () => {
  const expected = ['a', 'b'];
  assert.deepEqual(setDifference(expected, ['a']), ['b']);
  assert.deepEqual(setDifference(['a', 'b', 'c'], expected), ['c']);
});
