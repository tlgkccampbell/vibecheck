# archviz

Tooling for reviewing large, generated pull requests at the architecture level. A
checked-in architecture map binds types to logical components by namespace pattern,
by exact name, or by an `[Archmap]` attribute. A checker reads every dependency from
the code and reports it per pair of components, and a viewer renders changesets as a
schematic that reviewers drill into. The map is proposed by a language model from a
listing of the code's types and verified against the dependency graph.

The design is in [docs/archmap-spec.md](docs/archmap-spec.md). The interaction mock is
in [mock/](mock/).

## Layout

| Path | What it is |
|---|---|
| `src/Archmap.Core` | The library: map format, Roslyn analysis, checker and metrics, facts, changeset diff, skeleton and unit bootstrap. |
| `src/Archmap.Cli` | The `archmap` command-line tool. |
| `tests/Archmap.Tests` | Unit tests on synthetic analyses, plus an integration suite against the sample. |
| `samples/Orchard` | A small four-project solution with a curated `archmap.json` and one view. |
| `schema/` | JSON Schemas for `archmap.json` and `archmap.layout.json`. |
| `mock/` | The viewer mock: a static page over a real changeset, laid out with ELK. See [Viewer mock](#viewer-mock). |

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
archmap units [--map <archmap.json>] [--out <units.txt>]
archmap check [--map <archmap.json>] [--facts <out.json>] [--strict]
archmap why   <from> <to> [--map <archmap.json>] [--facts <facts.json>] [--limit <n>]
archmap fmt   [--map <archmap.json>] [--check]
archmap facts [--map <archmap.json>] [--out <out.json>] [--commit <sha>]
archmap diff  <base-facts.json> <head-facts.json> [--out <out.json>] [--repo <dir> --base-rev <rev> --head-rev <rev>]
```

The map defaults to `./archmap.json` and the repository root is the map's directory.
To try it on the sample:

```
dotnet run --project src/Archmap.Cli -- check --map samples/Orchard/archmap.json
```

`check` fails only when the map itself cannot be used. Everything else is a warning:
a type no leaf binds lands in the automatic `unsorted` leaf, and the summary line
says how many. `check` also prints the cycles among each level's children and each
view's groups, which is what the proposing step reads. `why` lists the type pairs
behind one edge.

## Bootstrapping a map

1. `archmap init --solution <sln>` writes a skeleton map from namespaces, with
   `System.**` and every test project excluded.
2. `archmap units --out units.txt` writes the listing a language model reads: every
   type folded into units with its documentation summary and heaviest references.
3. The model writes the map: a tree of concepts with a purpose line each, leaves
   bound by namespace pattern where a namespace follows the concept and by exact
   type name where it does not.
4. `archmap check` answers with cycles, unsorted types and, in the facts, cohesion
   per node. `archmap why a b` explains an edge. Revise and repeat.

## Viewer mock

`mock/index.html` is a static page that draws one changeset document. It has no
build step: open the file, or publish the three files together. `changeset.js` wraps
the changeset `archmap diff` wrote for one commit of a real solution, so the page
loads from a file URL; the viewer proper will fetch the JSON.

Every level is drawn from the changeset's leaf-pair edges aggregated to the blocks
of that level. ELK lays the blocks out top-down, so a dependency stack reads as a
stack, and merges parallel traces into trunks. Edges that leave the level end at
tabs on the frame: what uses the level along the top, what it uses along the bottom.
A view regroups the leaves under a level into other blocks, and a leaf opens its
types, the files that changed, their diffs, and the type pairs behind each of its
edges. Changed edges carry a label with the count and the delta; a change of fewer
than three references or under one percent is drawn as minor and left unlabeled.

To regenerate the data for another range, produce facts at both ends and diff them:

```
archmap facts --out base.json          # at the base revision
archmap facts --out head.json          # at the head revision
archmap diff base.json head.json --out changeset.json --repo <repo> --base-rev <a> --head-rev <b>
```

Source generators run only when the solution has been built, so build each
revision before `facts`, or the generated types are missing from the picture.
