// The stale-override check's reading, which scripts/check-overrides.mjs runs over the package directory.
//
// An `overrides` entry forces a version that some dependant's declared range refuses. Once every dependant's range
// accepts it, the entry forces nothing, and it goes on pinning that version past every later release unnoticed:
// Dependabot raises a dependant but never drops an override. So this fails on an entry no dependant still needs, and
// says which ranges accept it, so the entry is dropped and the lockfile re-derived with it.
//
// The dependants' ranges come from package-lock.json, which records each package's declared range, not the override's
// result. It reads only a flat entry pinning an exact version, and the range syntax npm writes; a nested entry or any
// other form stops it, rather than letting it pass over what it cannot read.
const DEPENDENCY_FIELDS = ['dependencies', 'optionalDependencies', 'peerDependencies'];
const VERSION = /^(\d+)\.(\d+)\.(\d+)$/;
const PARTIAL = /^(\d+|[xX*])(?:\.(\d+|[xX*]))?(?:\.(\d+|[xX*]))?$/;

const parseVersion = (text) => {
  const match = VERSION.exec(text);
  if (!match) throw new Error(`cannot read version "${text}": the check reads only X.Y.Z`);
  return match.slice(1, 4).map(Number);
};

const compare = (a, b) => a[0] - b[0] || a[1] - b[1] || a[2] - b[2];
const between = (low, high) => [
  ['>=', low],
  ['<', high],
];

// A comparator is [operator, version]; a partial X or X.Y is widened the way npm widens it.
const comparators = (token, range) => {
  if (token === '' || token === '*' || /^[xX]$/.test(token)) return [];
  const op = /^(\^|~|>=|<=|>|<|=)?(.*)$/.exec(token);
  const [operator = '', rest] = [op[1], op[2].replace(/^v/, '')];
  const partial = PARTIAL.exec(rest);
  if (!partial) throw new Error(`cannot read range "${range}": "${token}" is not a version npm writes`);
  const parts = partial.slice(1, 4).map((p) => (p === undefined || /^[xX*]$/.test(p) ? undefined : Number(p)));
  const known = parts.findIndex((p) => p === undefined);
  const given = known === -1 ? 3 : known;
  if (parts.slice(given).some((p) => p !== undefined)) throw new Error(`cannot read range "${range}"`);
  const low = [parts[0] ?? 0, parts[1] ?? 0, parts[2] ?? 0];
  const bump = (index) => low.map((v, i) => (i < index ? v : i === index ? v + 1 : 0));

  if (given === 0) {
    if (operator === '<' || operator === '>') return [['<', [0, 0, 0]]];
    return [];
  }
  switch (operator) {
    case '':
    case '=':
      return given === 3 ? [['=', low]] : between(low, bump(given - 1));
    case '^': {
      const first = low[0] !== 0 || given === 1 ? 0 : low[1] !== 0 || given === 2 ? 1 : 2;
      return between(low, bump(first));
    }
    case '~':
      return between(low, bump(given === 1 ? 0 : 1));
    case '>=':
    case '<':
      return [[operator, low]];
    case '>':
      return given === 3 ? [['>', low]] : [['>=', bump(given - 1)]];
    case '<=':
      return given === 3 ? [['<=', low]] : [['<', bump(given - 1)]];
  }
  throw new Error(`cannot read range "${range}"`);
};

const holds = ([operator, bound], version) => {
  const order = compare(version, bound);
  return { '=': order === 0, '>=': order >= 0, '>': order > 0, '<=': order <= 0, '<': order < 0 }[operator];
};

/** Whether `version`, an exact X.Y.Z, satisfies `range` as npm reads it. Prerelease tags are refused, not guessed. */
export function satisfies(version, range) {
  const target = parseVersion(version);
  if (/-\s|\s-/.test(range)) throw new Error(`cannot read range "${range}": hyphen ranges are not read`);
  return range.split('||').some((set) => {
    const tokens = set
      .trim()
      .replace(/(\^|~|>=|<=|>|<|=)\s+/g, '$1')
      .split(/\s+/);
    return tokens.flatMap((token) => comparators(token, range)).every((c) => holds(c, target));
  });
}

/**
 * Every problem with `manifest.overrides`, read against `lock.packages`: an entry that is not a flat exact pin, and
 * one that every dependant's declared range already accepts, or that nothing depends on.
 */
export function staleOverrides(manifest, lock) {
  const problems = [];
  const packages = lock?.packages;
  if (lock?.lockfileVersion < 2 || typeof packages !== 'object') {
    return ['package-lock.json has no `packages` map (lockfileVersion 2 or later); the check has nothing to read'];
  }
  for (const [name, pin] of Object.entries(manifest.overrides ?? {})) {
    if (typeof pin !== 'string' || !VERSION.test(pin)) {
      problems.push(`overrides.${name} is ${JSON.stringify(pin)}, not an exact X.Y.Z; the check reads only that form`);
      continue;
    }
    const dependants = [];
    for (const [path, entry] of Object.entries(packages)) {
      if (path === '') continue;
      for (const field of DEPENDENCY_FIELDS) {
        const range = entry[field]?.[name];
        if (range !== undefined) dependants.push({ path: path.replace(/^.*node_modules\//, ''), range });
      }
    }
    if (dependants.length === 0) {
      problems.push(`overrides.${name} ${pin} is stale: no package in the lockfile depends on ${name}`);
      continue;
    }
    if (dependants.every(({ range }) => satisfies(pin, range))) {
      const ranges = dependants.map(({ path, range }) => `${path} ${range}`).join(', ');
      problems.push(`overrides.${name} ${pin} is stale: every dependant already accepts it (${ranges})`);
    }
  }
  if (problems.length > 0) {
    problems.push('Drop each stale entry from package.json, then re-derive the lockfile and commit both together.');
  }
  return problems;
}
