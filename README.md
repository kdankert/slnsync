# slnsync

Keeps an aggregate `.sln` / `.slnx` solution in sync with source solutions. The projects from each source solution are
mirrored into their own solution folder in the aggregate, so you can work on a single service or on the whole repository
in one IDE window.

In watch mode, changes flow both ways: editing a source solution updates its folder in the aggregate, and editing the
aggregate updates all source solutions.

## Requirements

- .NET 10 runtime (the .NET 10 SDK is needed to build or pack the tool)
- Solutions in either `.sln` or `.slnx` format (read and written via
  [Microsoft.VisualStudio.SolutionPersistence](https://www.nuget.org/packages/Microsoft.VisualStudio.SolutionPersistence))

## Install

`slnsync` is a .NET tool. To build and install it from this checkout:

```bash
dotnet pack -c Release
dotnet tool install --global --add-source ./nupkg slnsync
```

After making changes, rebuild and update the installed tool:

```bash
dotnet pack -c Release
dotnet tool update --global --add-source ./nupkg slnsync
```

When the package is available on NuGet, install it with:

```bash
dotnet tool install --global slnsync
```

Use `dotnet tool uninstall --global slnsync` to remove it.

The default package version is `0.0.0` for local builds. CI can override it at pack time with, for example,
`dotnet pack -c Release -p:Version=1.2.3`.

## Config

The configuration is an XML file and is the required positional argument. Its extension must be `.xml`.

```xml
<configuration>
  <SolutionPath>all.slnx</SolutionPath>
  <ProjectPaths>
    <Path>backend/Backend.slnx</Path>
    <Folder>Backend Services</Folder>
  </ProjectPaths>
  <ProjectPaths>
    <Path>frontend/Frontend.sln</Path>
    <Folder>Frontend</Folder>
  </ProjectPaths>
</configuration>
```

| Element         | Meaning                                                                                 |
|-----------------|-----------------------------------------------------------------------------------------|
| `SolutionPath`  | The aggregate solution. It is created if it does not exist.                             |
| `ProjectPaths`  | Repeat once per source solution. Requires a `Path` and a `Folder`.                      |
| `Path`          | The source solution to read from or write to.                                           |
| `Folder`        | The solution folder in the aggregate that holds this source's projects.                 |

Relative paths are resolved against the directory containing the config file; absolute paths are used as-is. The
config must contain at least one `ProjectPaths` entry, and every entry must have both a `Path` and a `Folder`.

Use a distinct `Folder` name for each source solution: the folder is treated as owned by that source, so two sources
sharing a folder would overwrite each other's projects.

## Usage

```bash
slnsync slnsync.xml          # sync once
slnsync slnsync.xml --watch  # sync once, then re-sync on changes
```

The config file is the only positional argument; `--watch` is optional.

### One-shot sync

On start, each source solution that exists is read and its projects are copied into the matching folder of the
aggregate. Missing source solutions are skipped. The aggregate is created if needed.

For each source folder, `slnsync`:

- removes the projects currently in that folder and adds the source solution's projects in their place;
- rewrites project paths relative to the aggregate solution;
- skips projects already present elsewhere in the aggregate (matched by full path);
- leaves projects in other folders and other solution content untouched.

### Watch mode

Watch mode performs an initial sync and then monitors the solution files for last-write changes:

- a change to a source solution re-syncs only that source's folder into the aggregate;
- a change to the aggregate solution rewrites **all** configured source solutions from their folders;
- writes made by `slnsync` itself are recognized and do not trigger another sync.

No timestamps or state files are used. Solution files are re-read on every sync, and change detection uses SHA-256
fingerprints kept in memory. Errors are printed to the console without stopping the watcher. Press Ctrl+C to stop.

## CI

`build.yml` restores, builds, and packs on every push and pull request. `publish.yml` runs only when a GitHub release
is published; it packs using the release tag as the version (minus a leading `v`) and pushes the package to NuGet via
trusted publishing. The job needs `id-token: write` and a `NUGET_USER` secret containing the nuget.org profile name,
and the NuGet trusted publishing policy must allow `publish.yml`.

## Notes

- `slnsync` only edits solution project lists. It does not move, copy, or create project files on disk.
- Projects added manually to a managed folder in the aggregate are removed on the next sync from that source.
- Project type information is preserved when projects are copied in either direction.

## License

MIT — see [LICENSE](LICENSE).
