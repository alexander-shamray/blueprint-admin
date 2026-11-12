// The stale-override check, run by `npm run lint`; scripts/overrides.mjs says what it reads and why. Usage:
//
//   node scripts/check-overrides.mjs [package directory]
//
// An entry file with no is-this-the-main-module guard, as check-translations.mjs is: a guard comparing argv with
// import.meta.url reads false in a checkout reached through a junction or symlink, and the check would exit 0 unread.
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { staleOverrides } from './overrides.mjs';

const dir = process.argv[2] ?? '.';
const read = (file) => JSON.parse(readFileSync(join(dir, file), 'utf8'));
const manifest = read('package.json');
let problems;
try {
  problems = staleOverrides(manifest, read('package-lock.json'));
} catch (error) {
  problems = [error.message];
}
if (problems.length > 0) {
  console.error(problems.join('\n'));
  process.exit(1);
}
console.log(`Overrides still needed: ${Object.keys(manifest.overrides ?? {}).join(', ') || 'none'}.`);
