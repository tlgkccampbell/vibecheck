# Review workflow (design)

This document describes how a reviewer goes from a range of commits to an
understanding of what changed, in one step. It builds on the map, facts and
changeset of [archmap-spec.md](archmap-spec.md), and it lists, in section 10, the
changes it makes to that specification.

The tool is meant for reviewing large changes that a language model wrote. Its
starting assumption is that the reviewer has a Claude account and Claude Code
installed, so the tool can call Claude whenever a step needs judgment: proposing a
map when the repository has none, and explaining in a few sentences why each part
of the changeset changed.

## 1. Goals

- A reviewer points the tool at a span of history and sees the changeset drawn, with
  no other commands to run and no files to move.
- The first drawing never waits on Claude. Everything that needs a model arrives
  afterward and improves what is already on screen.
- Looking at the same range again, or at a range that differs by a few commits, is
  close to instant.
- A repository that commits a map gets a stable, reproducible picture. A repository
  without one gets a generated map and accepts some nondeterminism, held to one draw
  per repository rather than one per review.
- Every narrative is grounded in the dependency evidence and is checked against it
  before it is shown.

Non-goals for this design: hosting the viewer for other people, reviewing several
repositories at once, and any form of enforcement.

## 2. The reviewer's path

1. The reviewer runs `archmap` anywhere inside a repository. The tool starts a local
   server and opens the viewer in the default browser.
2. With no range on the command line, the first page is a picker. It offers the
   current branch against the default branch, the uncommitted work against `HEAD`,
   recent commits on the current branch, other local branches, and, when the `gh`
   command is available, open pull requests whose commits are present locally. A
   free-form field takes any range `archmap changeset` accepts.
3. Choosing a range starts a review. The page shows each stage's progress as it runs
   and draws the changeset as soon as it exists.
4. Narratives fill in while the reviewer reads. Hovering an edge shows its narrative
   once it has arrived, and the reference counts until then.
5. When the map was generated, a notice offers to switch from the namespace skeleton
   to the concept map once that is ready, and later offers to save the map into the
   repository.

`archmap review <range>` skips the picker and opens straight into that review.

## 3. Architecture

The `archmap` tool hosts an ASP.NET Core server on the loopback interface. The
server runs the pipeline of section 4 as background jobs, keeps their results in
the cache of section 5, and serves the viewer and a small JSON API. The viewer is
the page that was in `mock/`, moved into the tool. It still reads
`window.CHANGESET`, which the server sets from a `changeset.js` it serves beside
each review's page, so the same page also opens from a file and publishes as an
artifact. Phase 4 adds an event stream to the viewer for narratives.

```
browser ── viewer (static files, embedded in the tool)
   │  GET /api/ranges, POST /api/reviews, GET /api/reviews/{id}/events (SSE), …
   ▼
archmap server ── pipeline jobs ── git (worktrees, diffs)
                       │          ── MSBuild + Roslyn (analysis)
                       │          ── claude -p (map proposals, narratives)
                       ▼
                    cache (per repository, outside the repository)
```

The API has these endpoints:

| Endpoint | Purpose |
|---|---|
| `GET /api/ranges` | Lists the ranges the picker offers, with commit counts and titles. |
| `POST /api/reviews` | Starts or reopens a review of a base and a head. Returns the review's identifier. |
| `GET /api/reviews/{id}/events` | Streams the review's progress and results as server-sent events. |
| `GET /api/reviews/{id}/changeset` | Returns the current changeset document. |
| `POST /api/reviews/{id}/map` | Switches the review to another map, or saves the generated map into the repository. Phase 3. |
| `POST /api/reviews/{id}/narratives` | Regenerates the narratives, or one of them. Phase 4. |

The event stream opens with a `review` event that names the range, and carries
`stage` events (a stage reported progress), a `ready` event when the changeset is
ready, or a `failed` event with the reason. Later phases add a `map` event when a
better map is available, and a `narrative` event for each narrative that passes its
check. The pages never poll.

The tool exits when the reviewer closes the terminal or presses Ctrl+C. Jobs that
are still running are cancelled, and their worktrees are removed.

For working on the viewer itself, `--viewer <dir>` serves the viewer's files from
disk instead of from the embedded copy, so that an edit shows on reload.

## 4. The pipeline

A review is identified by its base, its head and its map. Each stage below reads
the cache first and does its work only on a miss.

### 4.1 Resolve

The range resolves to two commits by the rules `RevisionChangeset.ResolveRange`
already implements. A head that is the uncommitted work resolves to a synthetic
commit, described in section 4.6.

### 4.2 Analyze

Each end is checked out into a temporary worktree, built and analyzed, the two in
parallel, as `archmap changeset` does now. The result of this stage is the
`AnalysisResult`, which does not depend on the map, and it is cached per commit.
Caching the analysis rather than the facts means that a change of map, such as the
switch from skeleton to concept map, costs a check of each end and no compilation.

The head worktree is kept for the life of the review, because narratives read the
code at head (section 6.2). It is removed when the review closes, and recreated
from the commit if a later narrative needs it.

### 4.3 Choose the map

The map comes from the first of these that exists:

1. The `--map` option.
2. `archmap.json` at the head revision, or in the working tree when the head is the
   uncommitted work.
3. `archmap.json` at the base revision.
4. The generated map for this repository, from the cache.
5. The skeleton of spec section 5.1, derived from the head analysis.

A map from the repository that cannot be read, such as one in an older format, is
reported as a warning, and the skeleton is drawn instead.

A map from the repository or from `--map` is used as it is. Unbound types land in
`unsorted`, and the viewer shows them there, as spec section 5.5 describes. When
the map is the skeleton, the review also starts map generation (section 4.4), and
the skeleton serves only until the generated map is ready.

### 4.4 Generate a map

Map generation runs the bootstrapping loop of spec section 5.3 without a person in
it:

1. Build the unit listing from the head analysis, with the skeleton's exclusions.
2. Ask Claude to propose a map from the listing (section 7). The response is a map
   document, validated by the map reader.
3. Check the proposal against the head analysis. Collect the cycles at each level,
   the unsorted types, and the type pairs behind each cycle edge, as `archmap why`
   reports them.
4. If anything was found, send the findings back with the proposal and ask for a
   revision. Stop after three rounds or when nothing is found.
5. Place the types that exist only at base (section 4.5), so that removed types do
   not fall into `unsorted`.

The result is stored as the repository's generated map and reused by every later
review of that repository, which keeps the picture stable from one review to the
next. The viewer's notice offers to switch to it, since switching changes the layout
under the reader. A "Save map to repository" action writes it, with its layout file,
to the repository root, where it becomes source 2 of section 4.3 from then on.

### 4.5 Place new types

A review whose generated map leaves types unsorted at either end asks Claude to
place them. The request lists the unsorted units, the map's leaves with their
purposes, and each unit's heaviest references by leaf. Claude answers with a leaf
for each unit, or a new leaf under an existing container with a name and purpose.
The answer is checked as in section 4.4 and merged into the generated map as exact
type bindings, which is a new version of that map. A map the repository owns is
never changed this way.

### 4.6 Uncommitted work

The uncommitted work becomes a commit object without touching the reviewer's index,
working tree or refs. The tool writes the working tree, tracked and untracked files
alike and with `.gitignore` honored, through a temporary index file
(`GIT_INDEX_FILE`) into a tree, and makes a commit of that tree with `HEAD` as its
parent. No ref points at the commit, so git collects it eventually. Its hash depends
only on the tree and the parent, apart from the timestamp, so the cache keys it by
its tree hash and parent instead.

### 4.7 Build the changeset

`ChangesetBuilder.Build` combines the two ends' facts and the hunks between them, as
now. The changeset event goes out at this point, and the viewer draws.

### 4.8 Explain

Narratives are generated as section 6 describes, in the background, and stream to
the viewer as each one passes its check.

## 5. The cache

The cache lives under `%LOCALAPPDATA%\archmap\` on Windows and
`$XDG_CACHE_HOME/archmap/` elsewhere, in one folder per repository. A repository is
identified by the hash of its first root commit and the path of its solution, so
that clones and worktrees of one repository share a folder.

| Entry | Key | Invalidated by |
|---|---|---|
| Analysis | Commit (or tree and parent), build flag, tool version | Never; dropped least recently used. |
| Generated map | Repository | A new version from section 4.5, or the reviewer's "regenerate". |
| Changeset | Base, head, map hash | Never; it is cheap to rebuild from the analyses. |
| Narratives | Changeset key, level or view, edge or node, prompt version, model | The reviewer's "regenerate". |

Each generated map version is kept, so that an older review can be reopened with the
map it was drawn with. An analysis of a large solution is tens of megabytes, so the
cache holds a configurable total size, two gigabytes by default, and evicts analyses
first.

## 6. Narratives

A narrative is two or three sentences that say why one part of the changeset
changed, not only what changed. Counts and diffs already say what.

### 6.1 What is explained

- The changeset as a whole, as a summary shown above the drawing.
- Each changed edge that some level or view of the viewer draws. The viewer
  aggregates leaf-pair edges to the blocks of the level it draws, so an edge between
  two top-level components and an edge between two leaves inside one of them are
  different edges with different narratives.
- Each changed node, shown in the details panel.

Edges to the tabs on a level's frame have no narrative of their own. The edge they
stand for has one at the enclosing level.

### 6.2 Inputs

Narratives are requested in batches of one level or one view, so that the narratives
a reader sees side by side were written together. A batch contains:

- The commit messages in the range, which in a change a model wrote often state the
  intent outright.
- For each edge, the evidence pairs with their counts at base and head, the largest
  changes first, up to a limit; and for each pair, the changed reference sites, with
  symbol, file and line, which the builder takes from the two ends' facts.
- For each node, its changed files with their hunks, trivial hunks left out, up to a
  limit.
- The names and purposes of the nodes involved, from the map.

Claude runs with the head worktree as its working directory and the read-only tools
Read, Grep and Glob, so that it can look past the limits when an edge needs it.

Batches go in this order: the summary, then the level the viewer opens on, then the
other levels and views breadth first, larger changes first within each. At most
four requests run at once.

### 6.3 Checking

Each narrative states the evidence it rests on: the type pairs for an edge, and the
files for a node. A narrative that names a pair not in the edge's evidence, or a file
not under the node, is discarded and requested once more on its own. If the second
answer fails too, the edge keeps its counts and shows no narrative. The check is
cheap and catches the most damaging failure, an explanation of a dependency that is
not in the changeset.

### 6.4 Presentation

Narratives are marked as written by a model. An edge's narrative appears in a
tooltip on hover and in the details panel when the edge is selected, with its cited
pairs. The summary has a "regenerate" action, and so does each narrative.

## 7. Calling Claude

The tool calls the Claude Code command line in print mode. Each request runs
`claude -p` with the prompt on standard input and these options:

- `--output-format json` and `--json-schema <schema>`, so that the answer is one
  document of a known shape.
- `--no-session-persistence`, so that reviews do not fill the reviewer's session
  history.
- `--allowedTools` limited to Read, Grep and Glob, with every tool that writes or
  runs commands disallowed.
- `--model`, when the reviewer has configured one for the task. The defaults are
  the Claude Code default for map proposals and a faster model for narratives.

The calls go through one interface, `IClaude`, with the real implementation and a
canned one that tests use. Prompts and schemas are files in the tool, versioned
together, and the prompt version is part of every cache key that depends on it.

If `claude` is not on the path or not signed in, the review proceeds without the
steps that need it. The skeleton serves as the map, the viewer shows counts, and a
notice says why. `--no-ai` chooses the same on purpose.

## 8. Security

The server binds to `127.0.0.1` on a free port. Because it starts processes that
read the repository, it accepts a request only when the request carries the
session's random token, which the opened URL carries, and when its `Host` header
names the loopback address. That stops another page in the same browser, and DNS
rebinding, from driving it.

## 9. Phases

Each phase ends with the tool usable, and with tests that state its new behavior.

1. **Server, picker and caches.** The `review` command and the bare `archmap`
   command, the server and its events, the viewer moved into the tool, the analysis
   and changeset caches. A repository with a map needs no other command. Without a
   map, the review uses the skeleton.
2. **Uncommitted work.** The synthetic head of section 4.6 and its picker entry.
3. **Generated maps.** `IClaude`, map generation, placement of new types, the switch
   notice and the save action.
4. **Narratives.** Inputs, batching, checking, streaming, and the tooltip, panel and
   summary in the viewer.
5. **Claude Code integration.** An `/archmap` skill that opens a review of the
   current session's changes.

No test calls Claude. The canned `IClaude` drives the pipeline tests, and a manual
smoke script exercises the real command line against the sample.

## 10. Changes to the specification

These land in [archmap-spec.md](archmap-spec.md) with the phase that makes them true:

- Section 1 gains the cache and the generated map as places a map can come from.
- Section 6 gains the `narratives` member of the changeset document.
- Section 7 gains `archmap review` and the bare `archmap` command. The other
  commands stay as building blocks for scripts. (Done in phase 1.)
- Section 7.1 describes the analysis cache, which replaces the manual
  `facts --out` workaround. (Done in phase 1.)
- Section 8 no longer lists narrative summaries as out of scope.
- The viewer moves from `mock/` into the tool. `mock/` keeps a sample `changeset.js` for opening the viewer without the server. (Done in phase 1.)

## 11. Open questions

- Whether the switch from skeleton to generated map should keep the reader's place,
  by opening the concept-map level that holds the leaf they were looking at.
- Whether `--bare` mode suits these calls. It would keep the reviewer's own hooks and
  instructions out of them, but it may not use the signed-in account.
- How large a range stays useful. A range of a hundred commits has a long commit
  message list and many changed edges, and the batch limits may need to scale with it.
- Whether pull requests whose commits are not present locally should be fetched, which
  writes to the reviewer's repository.
