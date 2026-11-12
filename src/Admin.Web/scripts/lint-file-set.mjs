// The lint file-set check, run by `npm run lint`: the files ESLint lints for the `lintFilePatterns` in angular.json
// must be the files on disk those patterns name. `overrides` in package.json forces a `minimatch` newer than the one
// eslint, @eslint/config-array and @typescript-eslint/typescript-estree declare, and a matcher that reads a pattern
// differently would narrow or widen what `ng lint` reads with every other check green (#706). Usage:
//
//   node scripts/lint-file-set.mjs [package directory]
//
// The expected set comes from walking the disk and reading the patterns' two shapes by hand, not from a glob library,
// so it does not share the matcher under test. An entry file with no is-this-the-main-module guard, as
// check-overrides.mjs is.
import { readdirSync, readFileSync } from 'node:fs';
import { join, relative, resolve, sep } from 'node:path';
import { ESLint } from 'eslint';
import { expectedFiles, patternsFrom, setDifference } from './lint-file-set.lib.mjs';

const dir = process.argv[2] ?? '.';
const angular = JSON.parse(readFileSync(join(dir, 'angular.json'), 'utf8'));
const patterns = patternsFrom(angular);

const onDisk = [];
const walk = (current) => {
  for (const entry of readdirSync(current, { withFileTypes: true })) {
    if (entry.name === 'node_modules' || entry.name.startsWith('.')) continue;
    const path = join(current, entry.name);
    if (entry.isDirectory()) walk(path);
    else onDisk.push(relative(dir, path).split(sep).join('/'));
  }
};
walk(dir);

const expected = expectedFiles(patterns, onDisk);
const eslint = new ESLint({ cwd: resolve(dir) });
const linted = (await eslint.lintFiles(patterns)).map((r) => relative(resolve(dir), r.filePath).split(sep).join('/'));

const missing = setDifference(expected, linted);
const extra = setDifference(linted, expected);
if (expected.length === 0 || missing.length > 0 || extra.length > 0) {
  console.error(`ESLint lints a different file set than lintFilePatterns names (${patterns.join(', ')}).`);
  if (expected.length === 0) console.error('The patterns name no file on disk.');
  for (const file of missing) console.error(`  not linted: ${file}`);
  for (const file of extra) console.error(`  linted, not named: ${file}`);
  process.exit(1);
}
console.log(`ESLint lints the ${expected.length} files lintFilePatterns names.`);
