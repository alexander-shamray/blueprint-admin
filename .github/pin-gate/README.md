# The pin gate

**The claim: `Directory.Packages.props` is the only file that chooses a
package version, and NuGet restores from no feed a project file names.**
The packs the SDK downloads take their versions from the SDK `global.json`
names, and the properties and items by which a project chooses one instead
are refused below.
Central package management makes that the default and nothing more. A
project file evaluated after the pin file can set `VersionOverride`, turn
central management off for itself and take a `Version=`, add a feed, or
import a file that does any of these (#547). The denies on the pin file keep
the unattended commands out of it, but not out of a `.csproj`, which `/ship`
edits; this gate is what refuses the version that would otherwise reach
`main` past a review that did not read for it.

## What it refuses

In every tracked MSBuild file, with every name read without regard to case,
as MSBuild reads it. That is every `*proj`, `*proj.user`, `.props` and
`.targets` at any depth, and every other tracked file MSBuild could import:
an import reaches a file of any name, but only one whose first element is
`<Project>`. So every tracked file is parsed from its bytes, as XmlReader
reads them (the byte-order mark, the declared encoding, UTF-16 told from its
first bytes), up to its first element and no further; one in UTF-32 or an
encoding the parser does not know, which XmlReader may, or whose root it
cannot name without expanding an entity, is read and refused rather than
skipped. Project entries are held to literal `*proj` paths that resolve to a
tracked file in the repository, so nothing expands into a name after it was
read and nothing is fetched from outside. In each:

- a `Version` or `VersionOverride` on any item, as an attribute or as
  metadata, in an `ItemGroup` or an `ItemDefinitionGroup`: a `PackageReference`,
  and any other item NuGet restores by its own version, such as a
  `DotNetCliToolReference`;
- a `PackageVersion`, `GlobalPackageReference` or `PackageDownload` item,
  each of which carries a version of its own, and any `Known*` item, whose
  metadata names the version of a pack restore downloads;
- a property whose name starts `ManagePackageVersionsCentrally`,
  `CentralPackage`, `Restore`, `NuGet`, `DirectoryPackagesProps` or
  `ImportDirectoryPackagesProps`: central management, a feed, a restore input,
  NuGet's own settings, or a second pin file. A prefix rather than a list, so
  a property NuGet adds later is refused before anyone has read it, and read
  after any leading underscores, since MSBuild's own inputs to these names
  (`_DirectoryPackagesPropsBasePath`) redirect them as surely;
- a property that imports a file by path (`CustomBefore*`, `CustomAfter*`,
  `DirectoryBuild*`, `ImportDirectoryBuild*`, `ImportProjectExtension*`,
  `MSBuildProjectExtensionsPath`, `MSBuildExtensionsPath`,
  `MSBuildUserExtensionsPath`, `ForceImport*`, `ImportByWildcard*`,
  `ImportUserLocations*`, `LanguageTargets`, `BeforeMicrosoftNET*`,
  `AfterMicrosoftNET*`), and any property named `*Targets` or `*Props`, the
  way the common targets name what they import (`CodeAnalysisTargets`); and
  `RuntimeFrameworkVersion` or any `*PackageVersion`, which pick a pack's
  version;
- any other property whose literal value is a drive, rooted or UNC path, or
  climbs past the top of the repository: whatever imports it, the gate never
  reads it;
- a `PackageReference` whose `Include` or `Update` holds an item transform
  (`@(…)`, `%(…)`), which copies another item's metadata, `Version` with it,
  or a property (`$(…)`), which expands first and may hold one; and any
  property holding an item reference, the same transform one step removed;
- an `Import` or an `Sdk` element, and a `<Project Sdk>` outside the named
  list of SDKs the .NET SDK bundles: each brings in MSBuild the gate never
  reads, and an unbundled SDK, some of which share the `Microsoft.NET.Sdk`
  prefix, is itself a NuGet package with a version;
- a `ProjectReference` naming anything but a literal path to a tracked
  `*proj` file: a `$(…)`, `@(…)` or `%(…)` or a wildcard can expand into
  another name, or several, after the gate has read it, and a drive, rooted,
  UNC or climbing path reaches MSBuild outside the repository, a share over
  the network; and one carrying `Properties`, `AdditionalProperties` or a
  `Set*` metadata, which hands the referenced project global properties that
  outrank its own and the pin file's, as a response file's do;
- a `Target` or a `UsingTask`: code, which runs during restore as readily as
  during a build, and inside which a `CreateProperty`, an
  `<Output PropertyName>` or an `<MSBuild Properties>` sets what no rule here
  can see. No project carries one; one that needs it is a change to the gate;
- a file that does not parse or is not text, or carries a `DOCTYPE`: one the
  gate cannot read is not one it has passed.

The pin file is read by the same rules, save for the `PackageVersion` and
`GlobalPackageReference` items and the central-management properties it
exists to hold. It must also set `ManagePackageVersionsCentrally` to `true`
and `CentralPackageVersionOverrideEnabled` to `false`, each once, in a
property group directly under `<Project>` and under no `Condition`, so the
value read is the one MSBuild evaluates. The second makes
the SDK refuse a plain override too; it is not enough alone, because a
project file's own properties are evaluated after it and can set it back,
which the first list refuses.

Beside them, the restore inputs no project names: any tracked `.rsp` (an
`MSBuild.rsp` or `Directory.Build.rsp` hands MSBuild global properties,
which outrank the pin file), a `Directory.Packages.props` below the root
(NuGet takes the nearest above a project, so it would be the pin file for its
subtree), a `nuget.config` or a `global.json` anywhere but the root (each is
the nearest for its subtree too), a root `global.json` carrying
`msbuild-sdks`, a solution other than
`BlueprintAdmin.slnx`, and a project entry in that one naming anything but a
literal path to a tracked `*proj`.

## What the evaluation refuses

`evaluate.py` closes what a static read cannot: a property value built from
other properties (`$(SomeRoot)\..\x`), handed to a property the common
targets import by a name none of the rules above catches (#562). It has
MSBuild evaluate every tracked `*proj` (`-getProperty`, `-getItem` and
`-preprocess`), with no configuration and with `Release`, as the jobs build,
and before its job's build restores, so names and expansions stop
mattering. In each evaluation it refuses:

- an import outside the repository and the .NET install that ran it: every
  file `-preprocess` names, read from MSBuild's banners as XML, so no text
  inside a project can open a comment that hides one;
- a feed or a NuGet setting (`RestoreSources`, `RestoreConfigFile`,
  `RestoreRootConfigDirectory`) that evaluates to anything;
- a package folder or a local source (`RestorePackagesPath`, the fallback
  folders, `RestoreAdditionalProjectSources`) outside the .NET install,
  which names its own `NuGetFallbackFolder` and, where it exists, its
  `library-packs`; never the repository, since restore takes a package
  already extracted in a packages or fallback folder as it finds it, and a
  local source there would serve a `.nupkg` the repository chose;
- `ManagePackageVersionsCentrally` not `true`,
  `CentralPackageVersionOverrideEnabled` not `false`, or a
  `DirectoryPackagesPropsPath` other than the root pin file;
- a `PackageVersion` or `GlobalPackageReference` defined anywhere but the
  pin file or evaluating to a version other than its literal one there,
  and one of the pin file's missing;
- a `PackageReference` or `PackageDownload` that carries a `Version` or a
  `VersionOverride`, save the SDK's own implicit references, defined by a
  file in the .NET install, whose versions the `global.json` pin chooses.

It fails rather than judges when MSBuild does not evaluate a project, when
the SDK MSBuild ran from is not the install on `PATH`, and on a tree already
restored, whose restore output imports the packages' own props from the
NuGet cache, outside both bounds: run it on a clean checkout.

## What it does not judge

The context the build evaluates in, where it differs from this one (#566).
The evaluation varies the configuration and, by running on both of the
`host` job's legs, the operating system, and nothing else. Restore's global
properties (`MSBuildIsRestoring`, `ExcludeRestorePackageImports`), the
solution's (`SolutionDir`), the build step's environment, and whether
restore has written its output yet each differ when the build evaluates, so
a condition or a value keyed on one of them can import what this
evaluation never saw. Judging the build's own binary log instead is #566's
fix. Nor a file that evaluation reads without importing it, through a
property function, or what a target does: the rules above refuse every
target, and the review reads the rest.

The code the build compiles and the tests run: the residual
`docs/harness-boundaries.md` already accepts for `src/**`, which asks for a
review, not a parser. Nor the root `nuget.config`'s feeds, the SDK
`global.json` pins or the pin file's own versions: each is a class E change
and denied to every command that edits unattended. Nor a file a build
generates rather than tracks.

## How it runs

`ci.yml`, twice. In the `host` job before its build, on the Linux leg alone
since the tree is the same on both: a required check, so **it holds the
merge**, and a merge outside `/ship` waits on it too. It does not hold the
restore: the `host` job's Windows leg, `drift` and `smoke` restore in
parallel, as they already build and run the pull request's own code, which
is the residual above. In the `commits` job, on each commit before the head
and before that commit is built, because a later bisect restores each one on
a developer's machine.

The evaluation runs in the same two places, after the static read and
before the build, so before that job's build restores (the other jobs
restore beside it, as above); in the `host` job on both legs, since what
MSBuild evaluates can differ by operating system where the tree does not.

**The head's copy judges**, unlike the commit gate's base copy. The commit
gate judges a pull request's metadata, which the branch writes freely; this
one judges the tree, and a branch that weakens the gate does so in a
`.github/**` diff, which no unattended command may write and the review
reads. The `harness` job runs the suite beside it; `docs/testing.md` has the
live invocation.
