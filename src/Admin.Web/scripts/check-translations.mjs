// The missing-translation check (AI assistant design §5.5), run by `npm run lint` after `ng extract-i18n` has
// written what the templates and `$localize` calls say now. Usage:
//
//   node scripts/check-translations.mjs <freshly extracted messages.en-US.json> [locale directory]
//
// It fails when the committed source file is not what extraction produced (a string was added, changed or removed
// without `npm run i18n:extract`), and when a shipped translation lacks a message, holds one no source has any more,
// leaves one empty, or drops or invents a placeholder, which `$localize` would refuse at run time. So a new string
// cannot ship half-translated.
import { readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';

const LOCALE_DIR = process.argv[3] ?? 'src/locale';
const SOURCE_FILE = 'messages.en-US.json';
const PLACEHOLDER = /\{\$\w+\}|\{[A-Z_][A-Z0-9_]*\}/g;

const read = (path) => JSON.parse(readFileSync(path, 'utf8'));
// The set, not the count: a plural repeats its placeholder in every case, and Russian has more cases than English.
const placeholders = (text) => [...new Set([...text.matchAll(PLACEHOLDER)].map((m) => m[0]))].sort().join(' ');

const fresh = read(process.argv[2]).translations;
const committed = read(join(LOCALE_DIR, SOURCE_FILE)).translations;
const problems = [];

for (const [id, text] of Object.entries(fresh)) {
  if (committed[id] !== text) problems.push(`${SOURCE_FILE} lacks ${id} "${text}"`);
}
for (const id of Object.keys(committed)) {
  if (!(id in fresh)) problems.push(`${SOURCE_FILE} holds ${id}, which no template or $localize has any more`);
}
if (problems.length > 0) problems.push('Run `npm run i18n:extract` and translate what it adds.');

const translations = readdirSync(LOCALE_DIR).filter((f) => /^messages\..+\.json$/.test(f) && f !== SOURCE_FILE);
for (const file of translations) {
  const { locale, translations: target } = read(join(LOCALE_DIR, file));
  if (file !== `messages.${locale}.json`) problems.push(`${file} says its locale is ${locale}`);
  for (const [id, text] of Object.entries(fresh)) {
    const translated = target[id];
    if (translated === undefined) problems.push(`${file} lacks ${id} "${text}"`);
    else if (translated.trim() === '') problems.push(`${file} leaves ${id} "${text}" empty`);
    else if (placeholders(translated) !== placeholders(text)) {
      problems.push(`${file} ${id} has placeholders [${placeholders(translated)}], the source [${placeholders(text)}]`);
    }
  }
  for (const id of Object.keys(target)) {
    if (!(id in fresh)) problems.push(`${file} holds ${id}, which no template or $localize has any more`);
  }
}

if (problems.length > 0) {
  console.error(problems.join('\n'));
  process.exit(1);
}
console.log(`Translations complete: ${Object.keys(fresh).length} messages in ${translations.join(', ')}.`);
