# The pin gate

**The claim: `Directory.Packages.props` is the only file that chooses a
package version, and NuGet restores from no feed a project file names.**
Central package management makes that the default and nothing more. A
project file evaluated after the pin file can set `VersionOverride`, turn
central management off for itself and take a `Version=`, add a feed, or
import a file that does any of these (#547). The denies on the pin file keep
the unattended commands out of it, but not out of a `.csproj`, which `/ship`
edits; this gate is what refuses the version that would otherwise reach
`main` past a review that did not read for it.

## What it refuses

In every tracked MSBuild file (`*proj`, `.props`, `.targets`, at any depth)
other than the pin file at the root:

- a `PackageReference` with `Version` or `VersionOverride`, as an attribute or
  as metadata, in an `ItemGroup` or an `ItemDefinitionGroup`;
- a `PackageVersion`, `GlobalPackageReference` or `PackageDownload` item,
  each of which carries a version of its own;
- a property whose name starts `ManagePackageVersionsCentrally`,
  `CentralPackage`, `Restore`, `NuGet`, `DirectoryPackagesProps` or
  `ImportDirectoryPackagesProps`: central management, a feed, a restore input,
  NuGet's own settings, or a second pin file. A prefix rather than a list, so
  a property NuGet adds later is refused before anyone has read it;
- an `Import` or an `Sdk` element, and a `<Project Sdk>` that is not one the
  .NET SDK bundles: each brings in MSBuild the gate never reads, and an
  unbundled SDK is itself a NuGet package with a version;
- a file that does not parse, or carries a `DOCTYPE`: one the gate cannot
  read is not one it has passed.

And in the pin file, `ManagePackageVersionsCentrally` anything but `true` or
`CentralPackageVersionOverrideEnabled` anything but `false`. The second makes
the SDK refuse a plain override too; it is not enough alone, because a
project file's own properties are evaluated after it and can set it back,
which the first list refuses.

## What it does not judge

Code a project file runs: a target, an inline task, an `Exec`. That is the
residual `docs/harness-boundaries.md` already accepts for `src/**`, and it
asks for a review, not a parser. Nor `nuget.config`, `global.json` or the pin
file's own versions: each is a class E change and denied to every command
that edits unattended.

## How it runs

`ci.yml`, twice, both times before the build that restores. In the `host`
job, on the Linux leg alone since the tree is the same on both, so a required
check holds it and a merge outside `/ship` waits on it too. In the `commits`
job, on each commit before the head, because that job restores each one, as
does anyone who later bisects onto it.

**The head's copy judges**, unlike the commit gate's base copy. The commit
gate judges a pull request's metadata, which the branch writes freely; this
one judges the tree, and a branch that weakens the gate does so in a
`.github/**` diff, which no unattended command may write and the review
reads. The `harness` job runs the suite beside it; `docs/testing.md` has the
live invocation.
