// The stale-override check's own cases, run by `npm run overrides:check` before the check itself, so a check that
// stopped reading the lockfile fails here rather than passing over nothing.
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { test } from 'node:test';
import { satisfies, staleOverrides } from './overrides.mjs';

const lockWith = (ranges) => ({
  lockfileVersion: 3,
  packages: {
    '': { name: 'admin-web' },
    ...Object.fromEntries(
      Object.entries(ranges).map(([path, range]) => [`node_modules/${path}`, { dependencies: { minimatch: range } }]),
    ),
  },
});
const manifest = { overrides: { minimatch: '11.0.5' } };

test('an override some dependant still refuses is needed', () => {
  assert.deepEqual(staleOverrides(manifest, lockWith({ eslint: '^10.2.6', other: '^11.0.0' })), []);
});

test('an override every dependant accepts is stale, and the message names each range', () => {
  const problems = staleOverrides(manifest, lockWith({ eslint: '^11.0.0', '@eslint/config-array': '>=10 <12' }));
  assert.equal(problems.length, 2);
  assert.match(problems[0], /overrides\.minimatch 11\.0\.5 is stale/);
  assert.match(problems[0], /eslint \^11\.0\.0, @eslint\/config-array >=10 <12/);
});

test('an override nothing depends on is stale', () => {
  assert.match(staleOverrides(manifest, lockWith({}))[0], /no package in the lockfile depends on minimatch/);
});

test('a nested dependant and a peer range count', () => {
  const lock = lockWith({});
  lock.packages['node_modules/a/node_modules/b'] = { peerDependencies: { minimatch: '^10.0.0' } };
  assert.deepEqual(staleOverrides(manifest, lock), []);
});

test('an override that is not a flat exact pin is refused, not read', () => {
  const problems = staleOverrides({ overrides: { minimatch: '^11.0.5', glob: { minimatch: '11.0.5' } } }, lockWith({}));
  assert.match(problems[0], /overrides\.minimatch is "\^11\.0\.5", not an exact X\.Y\.Z/);
  assert.match(problems[1], /overrides\.glob is \{"minimatch":"11\.0\.5"\}/);
});

test('a lockfile with no packages map is refused', () => {
  assert.match(staleOverrides(manifest, { lockfileVersion: 1, dependencies: {} })[0], /no `packages` map/);
});

test('ranges read the way npm reads them', () => {
  const cases = [
    ['11.0.5', '^10.2.6', false],
    ['10.9.0', '^10.2.6', true],
    ['0.2.9', '^0.2.3', true],
    ['0.3.0', '^0.2.3', false],
    ['0.0.4', '^0.0.3', false],
    ['1.9.0', '^1', true],
    ['1.2.9', '~1.2.3', true],
    ['1.3.0', '~1.2.3', false],
    ['1.9.0', '~1', true],
    ['11.0.5', '10.x || 11.x', true],
    ['11.0.5', '>= 10.0.0 < 11.0.5', false],
    ['11.0.5', '>10', true],
    ['10.9.9', '>10', false],
    ['11.0.5', '<=11', true],
    ['11.0.5', '<=11.0.4', false],
    ['11.0.5', '*', true],
    ['11.0.5', '', true],
    ['11.0.5', '11.0.5', true],
    ['11.0.5', '=11.0.4', false],
    ['11.0.5', '11', true],
  ];
  for (const [version, range, expected] of cases) {
    assert.equal(satisfies(version, range), expected, `${version} against "${range}"`);
  }
});

test('a range it cannot read stops it rather than passing', () => {
  assert.throws(() => satisfies('11.0.5', '^11.0.0-rc.1'), /cannot read range/);
  assert.throws(() => satisfies('11.0.5', '10.0.0 - 11.0.0'), /hyphen ranges/);
  assert.throws(() => satisfies('11.0.5', 'latest'), /cannot read range/);
  assert.throws(() => satisfies('11.0.5-rc.1', '^11.0.0'), /cannot read version/);
});

// The subject is what the gate looks at: this repository's own manifest and lockfile. The check must find each
// override's dependants there, and must call the override stale once those same dependants accept anything.
test("this repository's overrides are read through their real dependants", () => {
  const read = (file) => JSON.parse(readFileSync(new URL(`../${file}`, import.meta.url), 'utf8'));
  const own = read('package.json');
  for (const name of Object.keys(own.overrides ?? {})) {
    const only = { overrides: { [name]: own.overrides[name] } };
    const lock = read('package-lock.json');
    assert.ok(
      !staleOverrides(only, lock).some((p) => p.includes('no package in the lockfile')),
      `${name} has no dependants`,
    );
    for (const entry of Object.values(lock.packages)) {
      for (const field of ['dependencies', 'optionalDependencies', 'peerDependencies']) {
        if (entry[field]?.[name] !== undefined) entry[field][name] = '*';
      }
    }
    assert.match(
      staleOverrides(only, lock)[0] ?? '',
      /every dependant already accepts it/,
      `${name} never reads stale`,
    );
  }
});

// The entry file itself, run as `npm run overrides:check` runs it, so a check that exits without reading fails here.
const runCheck = (dir) =>
  spawnSync(process.execPath, [fileURLToPath(new URL('./check-overrides.mjs', import.meta.url)), dir], {
    encoding: 'utf8',
  });

test('the check, run over a stale fixture, exits 1 naming the entry', () => {
  const dir = mkdtempSync(join(tmpdir(), 'check-overrides-'));
  try {
    writeFileSync(join(dir, 'package.json'), JSON.stringify(manifest));
    writeFileSync(join(dir, 'package-lock.json'), JSON.stringify(lockWith({ eslint: '^11.0.0' })));
    const run = runCheck(dir);
    assert.equal(run.status, 1);
    assert.match(run.stderr, /overrides\.minimatch 11\.0\.5 is stale: every dependant already accepts it \(eslint/);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("the check, run over this repository's package, names each override it still needs", () => {
  const own = JSON.parse(readFileSync(new URL('../package.json', import.meta.url), 'utf8'));
  const run = runCheck(fileURLToPath(new URL('..', import.meta.url)));
  assert.equal(run.status, 0, run.stderr);
  assert.equal(run.stdout.trim(), `Overrides still needed: ${Object.keys(own.overrides ?? {}).join(', ') || 'none'}.`);
});
