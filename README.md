# archviz

Tooling for reviewing large, generated pull requests at the architecture level. A
checked-in architecture map binds code to logical components, a checker holds the map
and the code to each other, and a viewer renders changesets as a schematic that
reviewers drill into.

The design is in [docs/archmap-spec.md](docs/archmap-spec.md). The interaction mock is
in [mock/](mock/).

## Layout

| Path | What it is |
|---|---|
| `src/Archmap.Core` | The library: map format, Roslyn analysis, conformance rules, facts, changeset diff, skeleton bootstrap. |
| `src/Archmap.Cli` | The `archmap` command-line tool. |
| `tests/Archmap.Tests` | Unit tests on synthetic analyses, plus an integration suite against the sample. |
| `samples/Orchard` | A small four-project solution with a curated `archmap.json` that passes every rule. |
| `schema/` | JSON Schemas for `archmap.json` and `archmap.layout.json`. |
| `mock/` | The throwaway viewer mock with hand-written data. |

## Building and testing

Requires the .NET 10 SDK.

```
dotnet build
dotnet test
```

The unit tests run in well under a second. The integration suite builds the sample
solution and opens it with Roslyn once per run, which costs several seconds. For the
edit loop, run the unit tests alone:

```
dotnet test --filter "FullyQualifiedName!~Sample"
```

## Using the tool

Run from the repository root with `dotnet run --project src/Archmap.Cli --`, or
publish the CLI and put `archmap` on the path.

```
archmap init  --solution <path.sln> [--root <dir>] [--map <archmap.json>] [--facts <out.json>]
archmap check [--map <archmap.json>] [--propose <out.json>] [--facts <out.json>]
archmap fmt   [--map <archmap.json>] [--check]
archmap facts [--map <archmap.json>] [--out <out.json>] [--commit <sha>]
archmap diff  <base-facts.json> <head-facts.json> [--out <out.json>] [--repo <dir> --base-rev <rev> --head-rev <rev>]
```

The map defaults to `./archmap.json` and the repository root is the map's directory.
To try it on the sample:

```
dotnet run --project src/Archmap.Cli -- check --map samples/Orchard/archmap.json
```

`check` exits 1 on any violation and prints, for each one, the rule, the evidence,
and the map edit that would resolve it when one exists. `--propose` writes a copy of
the map with every such edit applied.
