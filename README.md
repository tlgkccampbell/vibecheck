# vibecheck

Tooling for reviewing large, generated pull requests at the architecture level. An
architecture map binds types to logical components by namespace pattern,
by exact name, or by an `[Vibecheck]` attribute. A checker reads every dependency from
the code and reports it per pair of components, and a viewer renders changesets as a
schematic that reviewers drill into. The map is proposed by a language model from a
listing of the code's types and verified against the dependency graph.

The design is in [docs/vibecheck-spec.md](docs/vibecheck-spec.md). Coding agents that change
code in a repository reviewed with vibecheck follow
[docs/agent-guide.md](docs/agent-guide.md), which `vibecheck guide`
prints; see [Telling coding agents about the map](#telling-coding-agents-about-the-map). The one-step review
workflow, with generated maps and narratives, is in
[docs/review-workflow.md](docs/review-workflow.md); its first four phases are built.
Pull request reviews and reviewed files are in [docs/pull-requests.md](docs/pull-requests.md);
all five of its phases are built: reviewed files and the full diff, opening pull requests,
reading the discussion, writing, and checks in detail.
Directing the reviewer's attention, with decisions and their consequences, semantic
triviality, a reading path, new concepts and a Concepts panel, is designed in
[docs/attention.md](docs/attention.md); its first four phases, decisions and their
consequences, semantic triviality, the reading path and the Concepts panel, are built.
Tests in a review are designed in [docs/tests.md](docs/tests.md); all three of its
phases, test types, tests on decisions and test signals, are built.
Patterns, the roles many types play, such as controllers, handlers and consumers, and the
comparison of a new instance with the existing ones, are designed in
[docs/patterns.md](docs/patterns.md); its first four phases are built: traits, patterns found at base, the map's
`patterns` list, norms and departures, the check's report of patterns, a chip on a changed type that names its
pattern and counts its departures, the pattern's norms in the details panel, a step on the reading path for
each instance that departs, a compare view that sets an instance beside its nearest or typical example, member
by member, a Patterns switch that lists the patterns, outlines their blocks and lists the patterns new in a change,
Claude's names and purposes for patterns, and patterns as hints for placing unsorted types.
Stated intent, in which the coding agent that writes a change says why it made the change's main choices in an
`Intent` section of the pull request description, is designed in [docs/intent.md](docs/intent.md); all three
of its phases are built: the format, the guide's section on it, `vibecheck intent`, which checks a draft,
intents in a review, shown beside Claude's narratives with flags for those that name nothing the change touches,
and the surprises on the reading path that no intent covers.
Explainers, a linear narrative that walks a reviewer through a change as its author would, slide by slide over
the viewer's own drawing of each part, are designed in [docs/explainers.md](docs/explainers.md); all three of
its phases are built: the request, the face and the card over every kind of target, the bridge to the reading
path, and figures for what the map has no place for.

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

Click a line number in a diff to comment on that line. Drag across line numbers, or
Shift-click another on the same side, to comment on a range of lines. **Add single comment** posts at once, and **Start a
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
vibecheck guide
vibecheck intent    [<file>] [--map <vibecheck.json>] [--root <dir>] [--base <rev>]
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
   `System.**` excluded and every test project listed under `tests`.
2. `vibecheck units --out units.txt` writes the listing a language model reads: every
   type folded into units with its documentation summary and heaviest references.
3. The model writes the map: a tree of concepts with a purpose line each, leaves
   bound by namespace pattern where a namespace follows the concept and by exact
   type name where it does not.
4. `vibecheck check` answers with cycles, unsorted types and, in the facts, the
   references inside and across each node. `vibecheck why a b` explains an edge. Revise and repeat.

## Telling coding agents about the map

A map stays useful only while changes keep it in sync, and in a repository where coding
agents write the changes, the agents have to know that. `vibecheck guide` prints the
guide for them, [docs/agent-guide.md](docs/agent-guide.md),
which is embedded in the tool so that it always matches the installed version. It also
asks agents to state the intent of each change's main choices in an `Intent` section of
the pull request description, as [docs/intent.md](docs/intent.md) describes, and
`vibecheck intent` checks a draft of that section in well under a second, without
building the solution. A short note in the repository's `CLAUDE.md` or `AGENTS.md`
sends agents to the guide:

```markdown
## vibecheck

This repository's changes are reviewed with vibecheck, which draws each change on the
architecture map `vibecheck.json`. Before finishing a change, run `vibecheck guide` and
follow it. It says when the map needs updating, and how to state the intent of the
change in the pull request description.
```

`vibecheck init` prints this note, with the map's path, after writing a map inside the
repository. In the viewer, **Save to repository** offers to add the note when neither
`CLAUDE.md` nor `AGENTS.md` at the repository's top mentions `vibecheck guide`. The note
goes in `AGENTS.md` when `CLAUDE.md` imports it with an `@AGENTS.md` line or is missing
while `AGENTS.md` exists, and in `CLAUDE.md` otherwise. The viewer shows the note and the
file it goes in before writing anything. It appends the note, or creates the file, and
changes nothing else; commit it with the map.

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
edits touch only comments, whitespace or layout, a file renamed without changes, and a
file a generator wrote start reviewed, with the reason on the file's header, and
unmarking one sticks. So does a file whose lines differ only by a renamed
declaration's name, with a link to the rename's decision, one whose declarations moved
unchanged between the files of a type or were reordered within it, and one whose
edits are `using` directives alone. A mark belongs to the file's change: it is
kept in `reviewed.json` in the repository's cache folder, keyed by the path and git's
blobs at both ends, so a later range that leaves the file alone keeps the mark and one
that changes it clears it. **Mute reviewed files** turns the muting off but keeps the
marks.

A hunk that adds, removes, renames, moves or changes the signature of a declaration is a
**decision**, and carries a header that says what changed, marks it public when callers
outside the solution may exist, and counts its consequences. A hunk whose every changed
line is blank, a comment, or a use of a declaration a decision changed is a
**consequence**: it is collapsed to one line that names its decision, and a file of
nothing but consequences says `consequence` on its chip and sorts after the files with
decisions. The Changes panel lists every decision in the changeset, widest reach first,
under an account of how many hunks are decisions, follow from them, are trivial, or are
edits on their own, and each block on the map carries a badge with the number of decisions
its files hold. Hovering a decision in the list brings out the blocks it is in and
reaches, and selecting one, from the list or from its header, lists its consequences by
block in the details panel, outlines its block and the blocks it reaches on the map, and
draws an arrow from the one to each of the others with the number of hunks it points at.
**Mark consequences reviewed** marks the files whose edits that decision alone explains,
and says how many it left.

The Changes panel opens on the **reading path**: the surprises first, each with the
signal that put it there, such as a new dependency between blocks, a new cycle, a
decision with the widest reach or the changed block most referenced from its level;
then the other changed blocks with their decisions and files; and last what is set
aside as counts. A step opens where it lives and becomes the current step, which lists
its files with their reviewed checkboxes and offers **Mark read**. A step is read once
every file it names is reviewed. The forward arrow opens the next unread step, and
the back arrow returns to the step before. The bar counts the steps read beside the
files.

**Concepts**, a third face of the same panel, lists the concepts the change touches:
runs of words, such as "Command Handler", that the names of several unrelated types
share. A word that one type holds, or one type and the types named after it, is not a
concept, and neither is a word such as "Helper" that names nothing on its own. Concepts
new in the change come first, and each lists the types the change added or changed
under it, opening in the leaf view, with the other types on request. A concept that a
type's whole name spells, such as "Consumer" for `IConsumer`, shows that type's
documentation; when Claude is available it defines the others in one sentence, checked
to cite the files of the types it was shown and marked as Claude's. A block's details
panel lists the concepts its own changed types hold. In every diff, a concept within the
name of a type in a changed line carries a dotted underline, hovering it shows its
entry, and clicking it opens Concepts.

Tests are never drawn on the map. The map's `tests` list names the test types, which
bind to no block and create no edges, and each is placed by its **subject**, the type
it tests: by its name less a suffix such as `Tests`, or else by the type its references
reach most. A test type with no test methods, or that neither rule places, is test
infrastructure. A block's changed test files are listed in a collapsed **Tests** section
of its leaf view, after its own files, and the bar counts them apart, as "24 of 30
files reviewed · 3 of 9 test files". The **Tests** face of the Changes panel lists the
changed test types by block, with the test methods each change added, removed or
changed, and test infrastructure last; while it is open, each block carries a badge
with the number of its changed test files. A change of tests alone opens there.
`vibecheck check` reports how the test types were placed, and warns about an `exclude`
pattern that names only test projects, which belongs in `tests`.

A decision's header says which tests reference it, as "referenced by 6 tests directly
and 31 through helpers: 1 added, 2 changed", and names up to two changed test methods, or
says "no test references it" for a public decision that none does. A test reaches a
declaration through a helper when a member of a test type that it calls, such as a
builder, references it; calls are followed through test code and never into the
system. Each count opens the list of the methods in the details panel, where hovering
one names its helper. A hunk in a test file can follow from a decision, as an updated
call site does, and the decision counts its consequences in tests apart. In the Tests
face, each changed test method names the changed declarations it references, each
opening its decision.

A change to a test also gives **test signals**, stated as facts and never as a
judgment of the test: a test method removed or skipped, an assertion removed, or an
assertion changed, worded "expected value changed" when only its literals differ. A
test file's header shows its signals as chips. A test type whose signals touch code
the change also changed, such as an assertion removed from a test of a rewritten
method, has a step of its own in the path's first tier after the decisions, and the
other signals share one closing step.

When the review has Claude, the Changes panel gains an **Explainer** face. An explainer
is a story of the change, written by Claude on request as a run of short slides, each
pointing at one level, view, edge, block, decision, hunk, type, pattern instance, concept
or test type, or, as a last resort and on two slides at most, at a figure. **Write it** costs one
request to Claude, which takes about a minute and costs under a dollar with Opus; the
explainer is cached, so asking again costs nothing, and **Regenerate** writes it anew.
While it is written, the face shows progress, first how many of the narratives it is
written from are done, and the map stays usable. The face then
lists the slides by number, title and target kind, with the current one marked and
arrows with "3 of 9" in its head. Under them, **Not in the explainer** links the
first-tier steps and changed blocks no slide covers, and a line says how many slides
failed their check and were dropped. The current slide shows in a card over the lower
part of the map or the leaf view, marked as written by Claude. The card shows the
slide's title, its text and the files and type pairs it rests on, and the panel moves
to the slide's target, fitted into the room above the card. A level or view slide
outlines the blocks it brings out and dims the rest, as hovering a pattern does, for as
long as the slide is current. A pattern slide opens the compare view beside the
instance's nearest example, a concept slide the Concepts face at the concept, and a
tests slide the test type's first changed hunk with the Tests face. A figure slide draws
a small drawing of boxes, labeled arrows and groups in the stage in place of the map, in
the map's own style, marked as drawn by Claude; the breadcrumb ends in **Figure**, a box
whose type the change has opens the type, and moving to another slide, closing the card
or any navigation returns the stage to the map. When the pull
request states intents, the card shows the ones that cover the slide's target, marked
as the author's, as a path step does. A slide whose target is also a step of the path
offers **Open on the path**, which opens that step where its files can be marked, and
its row in the face says whether that step is read. While the card is open,
the right and left arrow keys move among the slides, Home and End go to the first and
last, and Escape closes the card; the map's own use of those keys waits until it is
closed. Looking around never changes the slide, and **Show again** returns to its
target. Opening a slide marks nothing reviewed. When the review gets a new result, as
when it switches to the generated concept map, the page drops the explainer, which
named the old map's blocks, and the face offers to write one for the new map. `mock/changeset.js` carries a canned
explainer, as `window.CHANGESET_EXPLAINER`, so that the face and the card open without
the server.

The theme switch in the top bar follows the system, or always uses light or dark. The
`vibecheck` command keeps the choice in `settings.json` under its cache folder, so a
later session on another port opens in the same theme; a page opened from a file
keeps it in the browser.

To write a changeset document without the viewer, run `changeset` from the repository:

```
vibecheck changeset main...feature --out changeset.json
```

It analyzes the two ends as a review does, with the same cache, applies the map to
both, and writes the document. The builds matter because source generators run only
in a build; `--no-build` skips them and leaves the generated types out. `facts` and
`diff` remain for doing the same steps by hand.
