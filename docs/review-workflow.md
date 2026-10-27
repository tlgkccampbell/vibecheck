# Review workflow (design)

This document describes how a reviewer goes from a range of commits to an
understanding of what changed, in one step. It builds on the map, facts and
changeset of [vibecheck-spec.md](vibecheck-spec.md), and it lists, in section 10, the
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

1. The reviewer runs `vibecheck` anywhere inside a repository. The tool starts a local
   server and opens the viewer in the default browser.
2. With no range on the command line, the first page is a picker. It offers the
   current branch against the default branch, the uncommitted work against `HEAD`,
   recent commits on the current branch, other local branches, and, when the `gh`
   command is available and signed in, the repository's open pull requests, including
   those whose commits are not present locally, which opening one fetches
   ([pull-requests.md](pull-requests.md) section 4.2). A free-form field takes any range `vibecheck changeset` accepts, and `:worktree` for
   the uncommitted work (section 4.6).
3. Choosing a range starts a review. The page shows each stage's progress as it runs
   and draws the changeset as soon as it exists.
4. Narratives fill in while the reviewer reads. Hovering an edge shows its narrative
   once it has arrived, and the reference counts until then.
5. When the map was generated, a notice offers to switch from the namespace skeleton
   to the concept map once that is ready, and later offers to save the map into the
   repository.

`vibecheck review <range>` skips the picker and opens straight into that review, and
`vibecheck pr <number>` into a pull request.

## 3. Architecture

The `vibecheck` tool hosts an ASP.NET Core server on the loopback interface. The
server runs the pipeline of section 4 as background jobs, keeps their results in
the cache of section 5, and serves the viewer and a small JSON API. The viewer is
the page that was in `mock/`, moved into the tool. It still reads
`window.CHANGESET`, which the server sets from a `changeset.js` it serves beside
each review's page, so the same page also opens from a file and publishes as an
artifact. Narratives reach the page through the review's event stream, and a page
opened from a file shows none. The endpoints that read and review pull requests, and
the routes and events of reviewed files, are in [pull-requests.md](pull-requests.md)
section 10.

```
browser ── viewer (static files, embedded in the tool)
   │  GET /api/ranges, POST /api/reviews, GET /api/reviews/{id}/events (SSE), …
   ▼
vibecheck server ── pipeline jobs ── git (worktrees, diffs)
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
| `POST /api/reviews/{id}/narratives` | Writes every narrative again, or, given a `key`, one of them. |
| `POST /api/settings` | Stores the reviewer's theme choice, `auto`, `light` or `dark`, in `settings.json` under the cache root, where every later session reads it. The browser cannot keep it, since each session has a port of its own. |

The event stream opens with a `review` event that names the range, and carries
`stage` events (a stage reported progress), a `ready` event when the changeset is
ready, or a `failed` event with the reason. A `map` event reports the generation of a
better map, a `narratives` event the progress of the narratives (`writing`, `done` or
`failed`), and a `narrative` event carries each narrative that passes its check. A
review is rerun when it switches maps, so narrative events carry the run they explain,
and a page ignores those of another run. The pages never poll.

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
parallel, as `vibecheck changeset` does now. The result of this stage is the
`AnalysisResult`, which does not depend on the map, and it is cached per commit.
Caching the analysis rather than the facts means that a change of map, such as the
switch from skeleton to concept map, costs a check of each end and no compilation.

Narratives read the code at head (section 6.2), so a review checks out its head into
a worktree of its own when its narratives start, since the analysis usually comes from
the cache and has no worktree to keep. The worktree lasts as long as the review and is
removed when the tool exits.

### 4.3 Choose the map

The map comes from the first of these that exists:

1. The `--map` option.
2. `vibecheck.json` at the head revision, or in the working tree when the head is the
   uncommitted work.
3. `vibecheck.json` at the base revision.
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

1. Build the unit listing from both ends' analyses together, with the skeleton's
   exclusions, so that types that exist only at base are bound as well. A listing
   longer than 120,000 characters, about 550 types, is replaced by a listing by
   namespace: each namespace with its type count, its largest units' head types, a
   few summaries, and its heaviest references to and from other namespaces. Claude
   then binds by namespace pattern. Without this, both the prompt and the map that
   Claude writes grow with the number of types, and a large solution's proposal
   ran past fifteen minutes.
2. Ask Claude to propose a map from the listing (section 7). The answer lists nodes
   and views in a fixed shape; the tool adds the skeleton's solution, exclusions and
   externals, and the map reader validates the result.
3. Check the proposal against the analysis. Collect the format errors of a proposal
   that cannot be used, and otherwise the unsorted types, the types two leaves claim
   equally, the cycles at each level with the type pairs behind each edge as
   `vibecheck why` reports them, the cycles among a view's groups, and leaves of more
   than sixty types. A map listed by namespace is not held to the sixty-type limit,
   since splitting a namespace needs the type-level detail that its listing leaves
   out. Before the check, the knot pass of [map-cycles.md](map-cycles.md) section 2
   moves the stray types of each set of types that reference each other in a loop
   into one leaf, and the proposal prompt lists those sets. A cycle that comes from
   such a set too large for one leaf is marked as in the code. Each finding of every
   round, each type the knot pass moves, how long each run of the checker takes, and
   each reason a proposal cannot be used, are written to the log.
4. If anything was found, send the findings other than cycles in the code back with
   the best map so far and ask for edits to it: nodes to remove, nodes to add or
   change, view groups to add, and types to move to a leaf by exact name. The request lists only the units, or for a
   large solution the namespaces, of the types the findings name, and the answer is
   as long as its edits rather than as long as the map. Until some proposal is usable
   there is no map to edit, so Claude proposes a whole map again, told why the last
   one was rejected. Stop when nothing but cycles in the code is found, after three
   rounds, or when a usable revision does not reduce the number of other findings. A
   revision that fails, or takes longer than fifteen minutes, also ends the rounds.
5. Keep the usable proposal with the fewest findings other than cycles in the code,
   then the fewest findings, preferring the later of two that tie.

The prompts are in `src/Vibecheck.Core/Assist/Prompts`. The result is stored as the
solution's generated map, version 1, and reused by every later review of that
solution, which keeps the picture stable from one review to the next. Each version
is kept. The viewer's notice offers to switch to it, since switching changes the
layout under the reader; the switch reruns the review, which costs no compilation.
A "Save to repository" action writes the map as `vibecheck.json` beside the solution,
where reviews of the uncommitted work find it at once and reviews of later commits
find it once it is committed. The action never overwrites an existing file.

### 4.5 Place new types

A review whose generated map leaves types unsorted at either end asks Claude to
place them. The request lists the unsorted types by full name, the map's leaves with
their purposes and a few of their types, the map's views and groups, and, as
context, the units the unsorted types belong to with their heaviest references. A
type is placed by name rather than by unit, because a helper folds into the unit of
the type that uses it, and that unit is usually placed already. Claude answers with
a leaf for each type, adding a leaf under a container where none fits. A new leaf
joins one group of every view whose level holds it: the group Claude names, or else
the group holding most of its siblings. The result is checked and stored as exact
type bindings in a new version of the generated map, which the viewer offers like
the first. A map the repository owns is never changed this way.

### 4.6 Uncommitted work

The uncommitted work becomes a commit object without touching the reviewer's index,
working tree or refs. The tool writes the working tree, tracked and untracked files
alike and with `.gitignore` honored, through a temporary index file
(`GIT_INDEX_FILE`) into a tree, and makes a commit of that tree with `HEAD` as its
parent. The real index is copied first, so that git rehashes only the files that
changed. No ref points at the commit, so git collects it eventually. Its author,
committer and dates are fixed, so its hash depends only on the tree and the parent:
the same uncommitted work always gives the same commit, and the caches of section 5
need no special key for it. When nothing is uncommitted, the snapshot is `HEAD`
itself.

A range names the uncommitted work as `:worktree`. Git does not allow a colon in a
ref name, so no branch or tag can have that name. `:worktree` on its own means
`HEAD..:worktree`, and it can stand on either side of any range, such as
`main...:worktree`. The viewer shows a snapshot as "working tree" in the range.

### 4.7 Build the changeset

`ChangesetBuilder.Build` combines the two ends' facts and the hunks between them, as
now. The changeset event goes out at this point, and the viewer draws.

### 4.8 Explain

Narratives are generated as section 6 describes, in the background, and stream to
the viewer as each one passes its check.

## 5. The cache

The cache lives under `%LOCALAPPDATA%\vibecheck\` on Windows and
`$XDG_CACHE_HOME/vibecheck/` elsewhere, in one folder per repository. A repository is
identified by the hash of its first root commit and the path of its solution, so
that clones and worktrees of one repository share a folder.

| Entry | Key | Invalidated by |
|---|---|---|
| Analysis | Commit, solution, build flag, tool build | Never; dropped least recently used. |
| Generated map | Repository | A new version from section 4.5, or the reviewer's "regenerate". |
| Changeset | Base, head, map hash | Never; it is cheap to rebuild from the analyses. |
| Narratives | The request: its prompt, which holds the inputs and the template, its schema and its model | The reviewer's "regenerate". |

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
  different edges with different narratives. An edge is known by its two blocks, so
  it is explained once, at the level whose children they are, or in the view whose
  groups they are, and shows its narrative wherever it is drawn. Edges from the top
  level's components to externals are explained there.
- Each changed node, at the level whose child it is, shown in the details panel when
  the node is selected or open. A view's groups get no narrative of their own, since a
  group is a regrouping of leaves the levels already explain.
- Each term of the glossary that has no documentation summary, as one sentence that
  says what the solution means by it, keyed `term:<words>` and requested in batches
  after the levels and views. See [attention.md](attention.md) section 8.4.
- The name and purpose of each pattern the changeset reports, keyed `pattern:<keys>`
  and requested right after the summary, from the source of three of its instances at
  base. See [patterns.md](patterns.md) section 8.

Edges to the tabs on a level's frame have no narrative of their own. The edge they
stand for has one at the enclosing level, and the viewer shows that one. An edge
between two leaves of one group that are not siblings in the tree has no narrative.

`NarrativePlanner` in `src/Vibecheck.Core/Assist` repeats the viewer's aggregation to
decide what each level and view explains.

### 6.2 Inputs

Narratives are requested in batches of one level or one view, so that the narratives
a reader sees side by side were written together. A level or view with more than 24
changed edges and nodes is split into batches of 24. A batch contains:

- The commit messages in the range, which in a change a model wrote often state the
  intent outright. A range of more than 60 commits lists its latest 60.
- For each edge, the evidence pairs with their counts at base and head, the largest
  changes first, up to eight; and for each pair, up to four added and four removed
  reference sites, with kind, symbol, file and line, taken from the two ends'
  analyses. A site counts as added or removed when the other end has no site of the
  same kind and symbol in the same file, since edits move lines.
- For each node, its changed files, up to thirty, with up to 160 lines of their
  hunks, trivial hunks left out.
- The names and purposes of the nodes involved, from the map.

The summary is a request of its own, with the commit messages, the changed files under
each top-level node, and the changed edges of the level the viewer opens on.

Claude runs with the head worktree as its working directory and the read-only tools
Read, Grep and Glob, so that it can look past the limits when an edge needs it.

Batches go in this order: the summary, then the level the viewer opens on, then the
other levels and views breadth first, larger changes first within each. At most
four requests run at once.

### 6.3 Checking

Each narrative states the evidence it rests on: the type pairs for an edge, the files
for a node, the declaring file for a term, and the nodes for the summary. A narrative
that cites nothing, names a pair not in the edge's evidence, a file not under the node,
a file other than the one that declares the term or a node not in the map, or that
the answer leaves out, is discarded. The discarded narratives of a batch are requested
once more together, apart from the rest, with the reason each was discarded. If the
second answer fails too, the edge keeps its counts and shows no narrative. The check
is cheap and catches the most damaging failure, an explanation of a dependency that is
not in the changeset.

### 6.4 Presentation

Narratives are marked as written by a model. The summary sits under the top bar,
showing its first two lines until the reader expands it, with the parts it cites as
links and the progress of the other narratives. An edge's narrative appears in a
tooltip on hover, and in the details panel when the edge is selected, with its cited
pairs. A node's appears in the details panel with its cited files. The summary has a
"regenerate all" action, and each narrative a "regenerate" action that writes it again
on its own and stores it in place of the old one.

### 6.5 The explainer

The explainer of [explainers.md](explainers.md) is a second layer over the narratives:
a run of slides that walks the reviewer through the whole change, each slide pointing
at one thing the viewer already shows. It is written only when the reviewer asks for
it, in one request of its own, after the result's narratives, which its prompt holds
by key. `ExplainerPlanner` builds the catalog of what a slide may point at from the
reading path, and `ExplainerWriter` checks each slide against it, asks once more for
the slides that fail and the first-tier steps and changed blocks no slide covers, and
drops what fails twice. The explainer is cached apart from the narratives, keyed by its
whole request as they are, and the server sends it as `explainer` events.

## 7. Calling Claude

The tool calls the Claude Code command line in print mode. Each request runs
`claude -p` with the prompt on standard input and these options:

- `--output-format stream-json`, `--verbose` and `--json-schema <schema>`, so that
  the call's events arrive as they happen and the final one carries an answer of a
  known shape. A request that reports its activity adds `--include-partial-messages`.
- `--no-session-persistence`, so that reviews do not fill the reviewer's session
  history.
- `--restricted` and `--strict-mcp-config`, so that the call loads none of the
  reviewer's settings, hooks or MCP servers and its file tools stay inside its
  working directory. It still runs on the signed-in account.
- `--tools` naming only what the request needs, with the same list in
  `--allowedTools` so that nothing asks for permission: no tools for map proposals
  and placements, which carry everything in the prompt, and Read, Grep and Glob for
  narratives and the explainer.
- `--model`, always, so that the tool's choice and not the reviewer's Claude Code
  settings decides the model. The defaults below are pinned to exact identifiers,
  and `vibecheck review --model <model>` replaces them for every request.

The defaults were chosen by measurement. Each model generated the map of this
repository (81 types) from the same range, and then placed the ten types of a new
namespace into its own map:

| Model | Generation | Rounds | Findings left | Placement |
|---|---|---|---|---|
| `claude-opus-5-5` | 83 s, $0.30 | 2 | 0 | 8 s, $0.047 |
| `claude-sonnet-5-5` | 78 s, $0.22 | 2 | 0 | 4 s, $0.030 |
| `claude-haiku-5-5` | 434 s, $0.06 | 3 | 2 | 17 s, $0.003 |

Opus wrote the clearest map, with concepts that cut across namespaces where the
code does; Sonnet's followed namespaces more closely, and Haiku's merged unrelated
parts. Opus and Sonnet placed the new types identically. A map is generated once per
solution and every later review draws with it, so map generation uses
`claude-opus-5-5`; placement uses `claude-sonnet-5-5`.

Narratives were measured on the same range, drawn with the generated map: 35
narratives (the summary, 17 edges and 17 nodes) in five requests, four at a time.

| Model | Cost | Longest request | Dropped |
|---|---|---|---|
| `claude-opus-5-5` | $1.21 | 49 s | 0 |
| `claude-sonnet-5-5` | $0.32 | 22 s | 0 |
| `claude-haiku-5-5` | $0.04 | 81 s, a retry | 1 |

All three were accurate on this range. Opus wrote the most, often past the two or
three sentences asked for; Sonnet kept closest to the brief. Haiku was the cheapest by
far but needed a second request for narratives that failed their check, which made it
the slowest. The reviewer waits on narratives, so they use `claude-sonnet-5-5`.

The explainer uses `claude-opus-5-5`, which wrote the clearer explainer in the trial
of [explainers.md](explainers.md) section 6. An explainer is read once and slowly, so
the clearer text is worth the higher cost and the longer wait. On pull request 3 of this
repository, with its 58 narratives in the prompt, it wrote 26 slides in 83 s for $0.65,
against $0.41 for the seven narrative requests that were not already cached;
[explainers.md](explainers.md) section 8 has the details.

Each narrative request, and the explainer's request, is abandoned after five minutes and counts as failed, so that
one hung call cannot keep the narratives from finishing. Each map proposal and revision is abandoned after fifteen
minutes, since a large solution's map takes long to write.

Map generation reports its activity every ten seconds in the viewer's notice and the
log: how long the request has run, and about how many tokens Claude has spent
thinking and writing the answer. Claude Code streams the size of the thinking but
not its text. The answer's size is estimated at four characters a token.

The calls go through one interface, `IClaude`, with the real implementation and a
canned one that tests use. Prompts and schemas are files in the tool, versioned
together. A generated map is kept until it is regenerated, whatever the prompt
version, since its stability is the point; narratives key their cache by the
whole request, which includes the prompt template.

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

1. **Server, picker and caches.** (Done.) The `review` command and the bare `vibecheck`
   command, the server and its events, the viewer moved into the tool, the analysis
   and changeset caches. A repository with a map needs no other command. Without a
   map, the review uses the skeleton.
2. **Uncommitted work.** The synthetic head of section 4.6 and its picker entry.
   (Done.)
3. **Generated maps.** `IClaude`, map generation, placement of new types, the switch
   notice and the save action. (Done.)
4. **Narratives.** Inputs, batching, checking, streaming, and the tooltip, panel and
   summary in the viewer. (Done.)
5. **Claude Code integration.** An `/vibecheck` skill that opens a review of the
   current session's changes.

No test calls Claude. The canned `IClaude` drives the pipeline tests, and a manual
smoke script exercises the real command line against the sample.

## 10. Changes to the specification

These land in [vibecheck-spec.md](vibecheck-spec.md) with the phase that makes them true:

- Section 1 gains the cache and the generated map as places a map can come from.
- Section 6 gains the `narratives` member of the changeset document. (Not done in
  phase 4: narratives stream as events beside the changeset, so a changeset published
  as an artifact carries none.)
- Section 7 gains `vibecheck review` and the bare `vibecheck` command. The other
  commands stay as building blocks for scripts. (Done in phase 1.)
- Section 7.1 describes the analysis cache, which replaces the manual
  `facts --out` workaround. (Done in phase 1.)
- Section 8 no longer lists narrative summaries as out of scope. (Done in phase 4.)
- The viewer moves from `mock/` into the tool. `mock/` keeps a sample `changeset.js` for opening the viewer without the server. (Done in phase 1.)

## 11. Open questions

- Whether the switch from skeleton to generated map should keep the reader's place,
  by opening the concept-map level that holds the leaf they were looking at.
- How large a range stays useful. A range of a hundred commits has a long commit
  message list and many changed edges, and the batch limits may need to scale with it.
- Whether pull requests whose commits are not present locally should be fetched, which
  writes to the reviewer's repository. Settled by [pull-requests.md](pull-requests.md)
  section 4.2: opening a pull request fetches its commits without writing any ref.
