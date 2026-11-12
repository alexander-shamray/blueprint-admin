// The lint file-set check's own cases, run by `npm run lint:files` before the check itself.
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
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
  assert.throws(() => expectedFiles(['src/{a,x}/**/*.ts'], files), /neither/);
  assert.throws(() => expectedFiles(['src/?/**/*.ts'], files), /neither/);
});

test('a narrowed or widened lint set shows as a difference', () => {
  const expected = ['a', 'b'];
  assert.deepEqual(setDifference(expected, ['a']), ['b']);
  assert.deepEqual(setDifference(['a', 'b', 'c'], expected), ['c']);
});

test('a plain-path pattern no config matches is reported as not linted', () => {
  const dir = mkdtempSync(join(tmpdir(), 'lint-file-set-'));
  try {
    writeFileSync(join(dir, 'a.ts'), 'export const a = 1;\n');
    writeFileSync(join(dir, 'x.txt'), 'export const x = 1;\n');
    writeFileSync(join(dir, 'eslint.config.js'), "module.exports = [{ files: ['**/*.ts'], rules: {} }];\n");
    const lint = { architect: { lint: { options: { lintFilePatterns: ['a.ts', 'x.txt'] } } } };
    writeFileSync(join(dir, 'angular.json'), JSON.stringify({ projects: { web: lint } }));
    const script = fileURLToPath(new URL('./lint-file-set.mjs', import.meta.url));
    const run = spawnSync(process.execPath, [script, dir], { encoding: 'utf8' });
    assert.equal(run.status, 1);
    assert.match(run.stderr, /not linted: x\.txt/);
    assert.doesNotMatch(run.stderr, /not linted: a\.ts/);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});
