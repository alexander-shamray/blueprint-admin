// The pure half of lint-file-set.mjs, so lint-file-set.test.mjs can hold it without running ESLint.

// Every `lintFilePatterns` entry of every project's `lint` target in angular.json.
export const patternsFrom = (angular) => {
  const patterns = Object.values(angular.projects ?? {}).flatMap(
    (project) => project.architect?.lint?.options?.lintFilePatterns ?? [],
  );
  if (patterns.length === 0) throw new Error('angular.json names no lintFilePatterns');
  return patterns;
};

// A pattern is `<dir>/**/*.<ext>` or a plain path; anything else is refused rather than guessed at.
const matcher = (pattern) => {
  const tree = /^([^*?{}[\]!]+)\/\*\*\/\*\.([a-z]+)$/.exec(pattern);
  if (tree) return (file) => file.startsWith(`${tree[1]}/`) && file.endsWith(`.${tree[2]}`);
  if (!/[*?{}[\]!]/.test(pattern)) return (file) => file === pattern;
  throw new Error(`lintFilePatterns entry "${pattern}" is neither <dir>/**/*.<ext> nor a plain path`);
};

export const expectedFiles = (patterns, files) => {
  const matches = patterns.map(matcher);
  return files.filter((file) => matches.some((m) => m(file))).sort();
};

export const setDifference = (a, b) => {
  const other = new Set(b);
  return a.filter((x) => !other.has(x)).sort();
};
