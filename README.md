# archviz

Tooling for reviewing large, generated pull requests at the architecture level. An
architecture map binds types to logical components by namespace pattern,
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

## Installing

`archmap` is a .NET tool. Pack it and install it from the local package folder:

```
dotnet pack src/Archmap.Cli -c Release -o nupkg
dotnet tool install --global archmap --configfile tool.nuget.config
```

To pick up later changes, pack again with a higher `<Version>` in
`src/Archmap.Cli/Archmap.Cli.csproj` and run `dotnet tool update --global archmap
--configfile tool.nuget.config`. `tool.nuget.config` names the local folder as the
only source for `archmap`, which works where a user-level NuGet configuration uses
package source mapping and so refuses `--add-source`. Without installing, run
`dotnet run --project src/Archmap.Cli --` from this repository.

## Using the tool

```
archmap init      --solution <path.sln> [--root <dir>] [--map <archmap.json>] [--facts <out.json>]
archmap units     [--map <archmap.json>] [--root <dir>] [--out <units.txt>]
archmap check     [--map <archmap.json>] [--root <dir>] [--facts <out.json>] [--strict]
archmap why       <from> <to> [--map <archmap.json>] [--root <dir>] [--facts <facts.json>] [--limit <n>]
archmap fmt       [--map <archmap.json>] [--check]
archmap facts     [--map <archmap.json>] [--root <dir>] [--out <out.json>] [--commit <sha>]
archmap diff      <base-facts.json> <head-facts.json> [--out <out.json>] [--repo <dir> --base-rev <rev> --head-rev <rev>]
archmap changeset <base> [<head>] [--map <archmap.json>] [--root <dir>] [--out <out.json>] [--no-build]
```

The map defaults to `./archmap.json`, and the layout file sits beside it. The
repository root defaults to the map's directory.

Nothing has to be committed to the repository under review. To keep the map
elsewhere, write it there with `init --map` and pass `--root` to the other commands:

```
archmap init  --solution C:\src\app\App.sln --map C:\maps\app\archmap.json
archmap check --map C:\maps\app\archmap.json --root C:\src\app
```
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
4. `archmap check` answers with cycles, unsorted types and, in the facts, the
   references inside and across each node. `archmap why a b` explains an edge. Revise and repeat.

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

To produce the data for another range, run `changeset` from the repository:

```
archmap changeset main...feature --out changeset.json
```

It checks out both revisions into temporary git worktrees, builds and analyzes them
in parallel, applies the current map to both, and removes the worktrees afterward,
so the working tree is left alone and the map need not exist at either revision.
The builds matter because source generators run only in a build; `--no-build`
skips them and leaves the generated types out. `facts` and `diff` remain for doing
the same steps by hand.
