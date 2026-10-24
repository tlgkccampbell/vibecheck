# vibecheck

Tooling for reviewing large, generated pull requests at the architecture level. An
architecture map binds types to logical components by namespace pattern,
by exact name, or by an `[Vibecheck]` attribute. A checker reads every dependency from
the code and reports it per pair of components, and a viewer renders changesets as a
schematic that reviewers drill into. The map is proposed by a language model from a
listing of the code's types and verified against the dependency graph.

The design is in [docs/vibecheck-spec.md](docs/vibecheck-spec.md). The one-step review
workflow, with generated maps and narratives, is in
[docs/review-workflow.md](docs/review-workflow.md); its first four phases are built.
Pull request reviews and reviewed files are in [docs/pull-requests.md](docs/pull-requests.md);
all five of its phases are built: reviewed files and the full diff, opening pull requests,
reading the discussion, writing, and checks in detail.
Directing the reviewer's attention, with decisions and their consequences, semantic
triviality, a reading path, new concepts and a glossary, is designed in
[docs/attention.md](docs/attention.md); none of it is built yet.

## Layout

| Path | What it is |
|---|---|
| `src/Vibecheck.Core` | The library: map format, Roslyn analysis, checker and metrics, facts, changeset diff, skeleton and unit bootstrap. |
| `src/Vibecheck.Cli` | The `vibecheck` command-line tool, the review server, and the viewer in `Viewer/`. |
| `tests/Vibecheck.Tests` | Unit tests on synthetic analyses, plus an integration suite against the sample. |
| `samples/Orchard` | A small four-project solution with a curated `vibecheck.json` and one view. |
| `schema/` | JSON Schemas for `vibecheck.json` and `vibecheck.layout.json`. |
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

`vibecheck` is a .NET tool. To install it from this working tree, or to replace an
installed copy with the current code, run:

```
pwsh scripts/reinstall-tool.ps1
```

The script packs the tool into `nupkg/` with a version of its own, the project's
`<Version>` with a `-dev.` suffix and a timestamp, so that NuGet never reuses an
older package cached under the same version. It then uninstalls any installed copy
and installs the new one. Stop any running `vibecheck` first, since it holds the
installed files open. `tool.nuget.config` names the local folder as the
only source for `vibecheck`, which works where a user-level NuGet configuration uses
package source mapping and so refuses `--add-source`. Without installing, run
`dotnet run --project src/Vibecheck.Cli --` from this repository.

## Reviewing a range

Run `vibecheck` anywhere inside a repository. It starts a local server and opens the
viewer in the browser, on a picker that lists the recent commits, the current branch
against the default branch, other branches, and open pull requests when the `gh`
command is installed. A range on the command line skips the picker:

```
vibecheck review main...feature
vibecheck review HEAD~3 HEAD
vibecheck review :worktree
```

`:worktree` names the uncommitted work, tracked and untracked files alike, and on its
own reviews it against `HEAD`; `main...:worktree` reviews everything since the branch
left `main`, committed or not. The tool snapshots the work as a commit object without
touching the index, the files or any ref.

The review checks out both ends into temporary worktrees, builds and analyzes them in
parallel, and draws the changeset. The map is the `--map` file when given, then the
`vibecheck.json` nearest the current directory at the head revision, then the one at
the base revision, then the concept map Claude generated for the solution in an
earlier review, and otherwise the namespace skeleton of both ends. A map that cannot
be read is reported, and the skeleton is drawn instead.

A review drawn with the skeleton asks Claude Code to generate a concept map in the
background, revising it against the dependency graph for up to three rounds, and
the viewer offers to switch to it when it is ready. The generated map is kept and
reused for later reviews, and a review that finds types it does not place asks
Claude to place them, which makes a new version. "Save to repository" writes the
map as `vibecheck.json` beside the solution. The calls run on your own Claude account,
with none of your settings or MCP servers, and these two get no tools. Map generation
uses Claude Opus 5.5 and placement uses Claude Sonnet 5.5, whatever your Claude Code
default is.

Once the changeset is drawn, Claude explains it: a summary above the drawing, and two
or three sentences on why each changed edge and part changed, in a tooltip on the edge
and in the details panel. Each explanation cites the type pairs or files it rests on,
and one that cites anything outside the changeset is discarded. Claude reads the code
at head, in a worktree of its own, with read-only tools. Narratives use Claude Sonnet
5.5 and are cached, so a second look at a range costs nothing.

`--model` names one model for every call, and `--no-ai` turns the calls off. The
terminal logs each call's model, time and cost.

Each commit's analysis, and each changeset, is cached under `%LOCALAPPDATA%\vibecheck`
on Windows and `~/.cache/vibecheck` elsewhere, or under `VIBECHECK_CACHE` when it is set.
A second review of the same range, or of a range that shares a commit with one seen
before, skips the compilation. `--no-cache` turns the cache off. The server listens
on `127.0.0.1` only and answers only a browser that opened the link it printed.

## Reviewing a pull request

When the `gh` command is installed and signed in, the picker lists the repository's
open pull requests, those that request your review first, with their author, draft
state, checks and review decision. `vibecheck pr 42`, or the pull request's address,
opens one straight away. The server reads GitHub with the token `gh` gives, which
stays in its memory and never reaches the page.

Opening a pull request whose commits are not present fetches them with an empty
refmap, so only objects and `FETCH_HEAD` are written and no branch or remote-tracking
ref moves. The review compares the head with its merge base with the base branch's tip
on GitHub, as GitHub does, rather than with a local branch that may be stale, and pairs
renamed files as GitHub pairs them.

A bar under the top bar shows the pull request's state, checks, review decision and
merge state. While its page is open the server reads the pull request every 30
seconds and sends it to the page when it changes; **Refresh** reads it at once, and the
bar says when GitHub's rate limit has made it stale. When the author pushes, a notice
offers a review of the new head, or of the commits since your last review. A review of
a range whose head is the head of exactly one open pull request is attached to it too.

The bar's **Conversation** tab shows the description, comments, reviews and pushes in
order, with each review's threads. In the **Map** tab, review threads appear under the
diff lines they belong to, and outdated threads in a list at the top of their file.
Blocks with unresolved threads carry a count. Your pending review is read back, and its
comments are marked pending. Bodies are GitHub's HTML, sanitized again in the page,
which runs under a Content-Security-Policy.

Click a line number in a diff to comment on that line, and Shift-click another on the same
side to comment on the lines between. **Add single comment** posts at once, and **Start a
review** gathers comments in a pending review, as on GitHub. Threads take replies and can be
resolved, comment boxes preview through GitHub's Markdown renderer, and **Suggest change**
fills in a suggestion block. **Review** in the bar submits the pending review as a comment,
an approval or a request for changes, or discards it.

The Conversation tab opens with the head commit's checks, failing first, with required checks
marked; a failed GitHub Actions job shows the end of its log on request. Failures and warnings
that checks report on lines appear in the diffs and are counted on the map's blocks. Nothing is posted except by one of
these actions, and the terminal logs each write with GitHub's answer. A closed or merged pull
request is read-only.
`--no-github` leaves GitHub out.

## Using the tool

```
vibecheck           [review options]
vibecheck review    [<base> [<head>] | <range>] [--map <vibecheck.json>] [--root <dir>] [--solution <sln>]
                  [--no-build] [--no-cache] [--no-ai] [--no-github] [--model <model>] [--no-open] [--port <n>] [--viewer <dir>]
vibecheck pr        <number> | <url> [review options]
vibecheck init      --solution <path.sln> [--root <dir>] [--map <vibecheck.json>] [--facts <out.json>]
vibecheck units     [--map <vibecheck.json>] [--root <dir>] [--out <units.txt>]
vibecheck check     [--map <vibecheck.json>] [--root <dir>] [--facts <out.json>] [--strict]
vibecheck why       <from> <to> [--map <vibecheck.json>] [--root <dir>] [--facts <facts.json>] [--limit <n>]
vibecheck fmt       [--map <vibecheck.json>] [--check]
vibecheck facts     [--map <vibecheck.json>] [--root <dir>] [--out <out.json>] [--commit <sha>]
vibecheck diff      <base-facts.json> <head-facts.json> [--out <out.json>] [--repo <dir> --base-rev <rev> --head-rev <rev>]
vibecheck changeset <base> [<head>] [--map <vibecheck.json>] [--root <dir>] [--out <out.json>] [--no-build] [--no-cache]
```

The map defaults to `./vibecheck.json`, and the layout file sits beside it. The
repository root defaults to the map's directory.

Nothing has to be committed to the repository under review. To keep the map
elsewhere, write it there with `init --map` and pass `--root` to the other commands:

```
vibecheck init  --solution C:\src\app\App.sln --map C:\maps\app\vibecheck.json
vibecheck check --map C:\maps\app\vibecheck.json --root C:\src\app
```
To try it on the sample:

```
dotnet run --project src/Vibecheck.Cli -- check --map samples/Orchard/vibecheck.json
```

`check` fails only when the map itself cannot be used. Everything else is a warning:
a type no leaf binds lands in the automatic `unsorted` leaf, and the summary line
says how many. `check` also prints the cycles among each level's children and each
view's groups, which is what the proposing step reads. `why` lists the type pairs
behind one edge.

## Bootstrapping a map

1. `vibecheck init --solution <sln>` writes a skeleton map from namespaces, with
   `System.**` and every test project excluded.
2. `vibecheck units --out units.txt` writes the listing a language model reads: every
   type folded into units with its documentation summary and heaviest references.
3. The model writes the map: a tree of concepts with a purpose line each, leaves
   bound by namespace pattern where a namespace follows the concept and by exact
   type name where it does not.
4. `vibecheck check` answers with cycles, unsorted types and, in the facts, the
   references inside and across each node. `vibecheck why a b` explains an edge. Revise and repeat.

## Viewer

The viewer is `src/Vibecheck.Cli/Viewer/index.html` and `app.js`, embedded in the tool.
The server wraps the page in a document skeleton and serves each review's data as
`changeset.js` beside it, which sets `window.CHANGESET`. `start.html` is the picker and
the progress page. To work on the viewer, pass `--viewer src/Vibecheck.Cli/Viewer` so
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

Every diff detects renames, so a moved file shows its edits rather than every line
deleted and added again. Changed files that no type binds, such as project files,
documents and workflows, open from an **Other files** block on the top level.

Each changed file has a reviewed checkbox in the leaf view, and a block offers to mark
all of its files at once. A reviewed file is set aside: its chip and its types are
muted, its diffs are collapsed, it sorts last, and a block whose changed files are all
reviewed is drawn muted. The top bar shows how many files are reviewed. A file whose
edits touch only comments, whitespace or layout, and a file renamed without changes,
start reviewed, and unmarking one sticks. A mark belongs to the file's change: it is
kept in `reviewed.json` in the repository's cache folder, keyed by the path and git's
blobs at both ends, so a later range that leaves the file alone keeps the mark and one
that changes it clears it. **Mute reviewed files** turns the muting off but keeps the
marks.

To write a changeset document without the viewer, run `changeset` from the repository:

```
vibecheck changeset main...feature --out changeset.json
```

It analyzes the two ends as a review does, with the same cache, applies the map to
both, and writes the document. The builds matter because source generators run only
in a build; `--no-build` skips them and leaves the generated types out. `facts` and
`diff` remain for doing the same steps by hand.
