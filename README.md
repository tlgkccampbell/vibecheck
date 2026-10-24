# archviz

Tooling for reviewing large, generated pull requests at the architecture level. An
architecture map binds types to logical components by namespace pattern,
by exact name, or by an `[Archmap]` attribute. A checker reads every dependency from
the code and reports it per pair of components, and a viewer renders changesets as a
schematic that reviewers drill into. The map is proposed by a language model from a
listing of the code's types and verified against the dependency graph.

The design is in [docs/archmap-spec.md](docs/archmap-spec.md). The one-step review
workflow, with generated maps and narratives to come, is in
[docs/review-workflow.md](docs/review-workflow.md); its first phase is built.

## Layout

| Path | What it is |
|---|---|
| `src/Archmap.Core` | The library: map format, Roslyn analysis, checker and metrics, facts, changeset diff, skeleton and unit bootstrap. |
| `src/Archmap.Cli` | The `archmap` command-line tool, the review server, and the viewer in `Viewer/`. |
| `tests/Archmap.Tests` | Unit tests on synthetic analyses, plus an integration suite against the sample. |
| `samples/Orchard` | A small four-project solution with a curated `archmap.json` and one view. |
| `schema/` | JSON Schemas for `archmap.json` and `archmap.layout.json`. |
| `mock/` | A changeset of a real solution, wrapped as `changeset.js`, for opening the viewer without the server. See [Viewer](#viewer). |

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

`archmap` is a .NET tool. To install it from this working tree, or to replace an
installed copy with the current code, run:

```
pwsh scripts/reinstall-tool.ps1
```

The script packs the tool into `nupkg/` with a version of its own, the project's
`<Version>` with a `-dev.` suffix and a timestamp, so that NuGet never reuses an
older package cached under the same version. It then uninstalls any installed copy
and installs the new one. Stop any running `archmap` first, since it holds the
installed files open. `tool.nuget.config` names the local folder as the
only source for `archmap`, which works where a user-level NuGet configuration uses
package source mapping and so refuses `--add-source`. Without installing, run
`dotnet run --project src/Archmap.Cli --` from this repository.

## Reviewing a range

Run `archmap` anywhere inside a repository. It starts a local server and opens the
viewer in the browser, on a picker that lists the recent commits, the current branch
against the default branch, other branches, and open pull requests when the `gh`
command is installed. A range on the command line skips the picker:

```
archmap review main...feature
archmap review HEAD~3 HEAD
```

The review checks out both ends into temporary worktrees, builds and analyzes them in
parallel, and draws the changeset. The map is the `--map` file when given, then the
`archmap.json` nearest the current directory at the head revision, then the one at
the base revision, and otherwise the namespace skeleton of both ends. A map that
cannot be read is reported, and the skeleton is drawn instead.

Each commit's analysis, and each changeset, is cached under `%LOCALAPPDATA%\archmap`
on Windows and `~/.cache/archmap` elsewhere, or under `ARCHMAP_CACHE` when it is set.
A second review of the same range, or of a range that shares a commit with one seen
before, skips the compilation. `--no-cache` turns the cache off. The server listens
on `127.0.0.1` only and answers only a browser that opened the link it printed.

## Using the tool

```
archmap           [review options]
archmap review    [<base> [<head>] | <range>] [--map <archmap.json>] [--root <dir>] [--solution <sln>]
                  [--no-build] [--no-cache] [--no-open] [--port <n>] [--viewer <dir>]
archmap init      --solution <path.sln> [--root <dir>] [--map <archmap.json>] [--facts <out.json>]
archmap units     [--map <archmap.json>] [--root <dir>] [--out <units.txt>]
archmap check     [--map <archmap.json>] [--root <dir>] [--facts <out.json>] [--strict]
archmap why       <from> <to> [--map <archmap.json>] [--root <dir>] [--facts <facts.json>] [--limit <n>]
archmap fmt       [--map <archmap.json>] [--check]
archmap facts     [--map <archmap.json>] [--root <dir>] [--out <out.json>] [--commit <sha>]
archmap diff      <base-facts.json> <head-facts.json> [--out <out.json>] [--repo <dir> --base-rev <rev> --head-rev <rev>]
archmap changeset <base> [<head>] [--map <archmap.json>] [--root <dir>] [--out <out.json>] [--no-build] [--no-cache]
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

## Viewer

The viewer is `src/Archmap.Cli/Viewer/index.html` and `app.js`, embedded in the tool.
The server wraps the page in a document skeleton and serves each review's data as
`changeset.js` beside it, which sets `window.CHANGESET`. `start.html` is the picker and
the progress page. To work on the viewer, pass `--viewer src/Archmap.Cli/Viewer` so
that the server reads the files from disk and an edit shows on reload. To open the
viewer without the server, put a `changeset.js` such as `mock/changeset.js` beside
`index.html`; the page also publishes as an artifact with those files.

Every level is drawn from the changeset's leaf-pair edges aggregated to the blocks
of that level. ELK lays the blocks out top-down, so a dependency stack reads as a
stack, and merges parallel traces into trunks. Edges that leave the level end at
tabs on the frame: what uses the level along the top, what it uses along the bottom.
A view regroups the leaves under a level into other blocks, and a leaf opens its
types, the files that changed, their diffs, and the type pairs behind each of its
edges. Changed edges carry a label with the count and the delta; a change of fewer
than three references or under one percent is drawn as minor and left unlabeled.

To write a changeset document without the viewer, run `changeset` from the repository:

```
archmap changeset main...feature --out changeset.json
```

It analyzes the two ends as a review does, with the same cache, applies the map to
both, and writes the document. The builds matter because source generators run only
in a build; `--no-build` skips them and leaves the generated types out. `facts` and
`diff` remain for doing the same steps by hand.
