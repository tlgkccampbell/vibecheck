# Directing attention (design)

This document describes five features that tell a reviewer where to look in a large
changeset and what they can set aside: cause and consequence clustering, semantic
triviality, the reading path, new concepts as a review event, and the glossary. It
builds on the changeset of [vibecheck-spec.md](vibecheck-spec.md), the narratives of
[review-workflow.md](review-workflow.md) and the reviewed marks of
[pull-requests.md](pull-requests.md), and it lists, in section 11, the changes it makes
to those documents.

The features share one principle. A change a model wrote is a few decisions and a
large volume of consequences, and the reviewer's scarce resource is attention. The
tool works out from the code where the decisions are, what follows from them, and
what cannot matter, and it says so in the drawing. Claude is used to name, to propose
and to explain, never to judge the code, and everything it says is cited and checked
against the changeset before it is shown, as narratives are. Nothing starts out
reviewed unless the tool can prove that reading it would tell the reviewer nothing.

## 1. Goals

- A reviewer reads each decision once and sees its consequences as a count with a
  sample, wherever in the map they land, rather than as forty hunks to scroll past.
- Edits that cannot change what the program does start out reviewed, with the reason
  stated, and the reviewer's unmark still wins.
- The viewer offers an order in which to read, computed from the facts, so that the
  surprises come first and the volume comes last.
- A concept the change invents is a decision for the reviewer, not something the tool
  settles on its own.
- The reviewer learns the names a change introduces before meeting them in the diff.
- Each feature works without Claude. Where Claude adds a sentence, the feature shows
  the facts until the sentence arrives, and shows the facts alone when it never does.

Non-goals for this design: any finding about whether the code is correct, any score
or verdict on the change, and any new call to Claude that reads code to form an
opinion of it.

## 2. The reviewer's path

1. The changeset opens on the map as it does now. The summary above the drawing gains
   a **Path** control, and a panel beside the Changes panel lists the reading path in
   three tiers: read first, then the rest, and set aside.
2. A **New concepts** notice appears when the change adds types the map does not
   describe. Each entry names the types, shows what they reference and what references
   them, and carries Claude's proposal for a home with the reviewer's choice to accept
   it, place the types elsewhere, or leave them unsorted for now.
3. The **Glossary** lists the names the change introduces, grouped by block, with
   where each is declared and how often it is used. Hovering an introduced name in any
   diff table shows its entry.
4. In the leaf view, a hunk that changes a declaration is a **decision**, and carries a
   count of the hunks that follow from it. Those hunks are **consequences**: each is
   collapsed, says which decision it follows from, and expands on demand. Selecting a
   decision lists its consequences by block in the details panel, which is the
   decision's reach across the map.
5. A file whose edits are a pure rename, a move or a change of `using` directives
   starts out reviewed, with the reason on its header, as trivial files do today.
6. Following the path, the forward arrow opens each step where it lives: a block at its level,
   an edge selected, a decision scrolled to. A step is done when its files are
   reviewed, and the bar shows the progress along the path beside the file count.

## 3. Declarations and sites

Every feature below needs to know which declarations changed and where each one is
referenced. The analysis records types and the references between them, and nothing
about members. It gains two lists, computed in the same Roslyn pass and cached with
the rest, so that a review that hits the cache pays nothing new.

**Declarations.** Every member declared by a top-level type in source, and the type
itself:

```
(symbol, type, kind, accessibility, file, first line, last line, shape, body, summary)
```

The symbol is the name `RawDependency.Symbol` already uses, which is the containing
type and the member's name without parameters. The overloads of one name are
therefore one declaration, and their shapes are combined. The kind is `type`,
`method`, `constructor`, `property`, `field`, `event`, `indexer` or `operator`. The
shape is a hash of the declaration's tokens up to its body, with the name itself left
out, so that two declarations that differ only by name have the same shape. The body
is a hash of the body's tokens, or nothing for a declaration without one. A type's
shape covers its modifiers, type parameters and base list, and its members are
declarations of their own. The summary is the first sentence of the documentation
comment, as `TypeInfo.Summary` is for a type.

**Sites.** Every reference from a type to a member or type declared in the solution,
including references within the declaring type, which the dependencies leave out:

```
(symbol, from-type, file, line)
```

Cross-type sites are already in the dependencies, so this list records only the
within-type ones, and readers take the union. On a solution of a thousand types the
within-type sites are expected to be a few times the dependencies, which is noted in
section 12.

Matching the two ends' declarations by symbol gives the **declaration diff**: a
declaration is `added`, `removed`, `reshaped` when its shape differs, `rewritten`
when only its body differs, `renamed` when a removed and an added declaration in the
same type have the same shape and the same body, and `moved` when they have the same
symbol tail, shape and body in different types or files. A type renamed is a type
whose members all pair this way with those of a type that appeared. The diff is
computed by `ChangesetBuilder` and nothing in it is interpreted.

## 4. Cause and consequence clustering

### 4.1 Decisions

A **decision** is a hunk that holds a changed declaration: one that is added,
removed, reshaped or renamed. A rewritten declaration is an edit to read as it is now,
since nothing else follows from it. Each decision carries the declarations it holds,
with their kinds, accessibilities and the number of sites that reference each at
either end.

A decision is `public` when any of its declarations is public or protected in a
public type, since callers outside the solution may exist that the changeset cannot
show. The leaf view says so on the decision's header.

### 4.2 Consequences

A **consequence** is a hunk whose every changed line is explained by a decision. A
line is explained when it is trivial in the sense of section 5, or when it carries a
site, at the end the line belongs to, of a declaration that a decision changed: a
removed line at base referencing a declaration that is gone or reshaped, or an added
line at head referencing one that is new or reshaped. A hunk that holds a changed
declaration of its own is a decision, never a consequence, and a hunk with a changed
line that nothing explains is neither.

A consequence follows from every decision whose declarations it references, and is
listed under each. The decision it is shown closest to, and the one its header names,
is the one with the most sites in the hunk. A hunk that references only the renamed
side of a rename is a rename consequence, which section 5.1 treats more strongly.

The rule works per line, so a changed line that both references a reshaped
declaration and makes an unrelated change on the same line is still explained. The
reviewer can see this: a consequence is collapsed, not hidden, and the sample a
decision shows is its consequence lines, where an unrelated change is still readable.
Section 12 keeps the question of a token-level rule.

### 4.3 In the changeset

The changeset gains a `Decisions` list. Each decision has an identifier, its file and
hunk, its declarations as section 4.1 describes, and its consequences as pairs of
file and hunk with the number of explained lines in each. Each hunk gains `Follows`,
the identifiers of the decisions it follows from, in the order of section 4.2. The
viewer computes the rollup by block from the files' leaves, by the rule of
pull-requests.md section 5.3, so that the details panel can list a decision's
consequences under the blocks that hold them.

### 4.4 In the viewer

In the leaf view, a decision's hunk is drawn with a header: the kind of change, the
declaration, the public mark where it applies, and "n consequences in m files,
across k blocks", with its consequences in test files counted apart as ", and 4 in
tests". The header also says which test methods reference the decision, directly and
through helpers, and names the changed ones, or says "no test references it" for a
public decision that none does, as [tests.md](tests.md) section 5.1 describes; the
counts open the lists of the methods in the details panel. Consequences are collapsed to one line each, with "follows from"
and the decision's declaration as a link, and open on a click like a reviewed file's
diff. A file whose hunks are all consequences sorts after files with decisions and
before reviewed files, and its chip says `consequence`.

The Changes panel lists every decision in the changeset, so that the reviewer sees the
decisions as a whole before meeting any of them in a diff. The list is headed by an
account of the changed hunks: how many are decisions, how many follow from them, how
many are trivial, and how many are edits on their own, which is the volume the
reviewer must read as it is. The rows are ordered by reach, the most blocks first,
then the most files, then public before private, and each names its kind of change
and its declaration and counts its consequences and the blocks they land in. Hovering
a row brings out the block the decision is in and the blocks it reaches on the map, as
hovering a block brings out its neighborhood. Each block on the map carries a badge
with the number of decisions its files hold, so the map says where the causes are
before anything is selected.

Selecting a decision, from a row, from its header or from the path, lists its
consequences in the details panel grouped by block, each opening its hunk, and marks
its block as the source and the blocks that hold its consequences as the reach while
the decision stays selected, with an arrow from the source to each reached block that
counts the hunks it points at. Selecting from a row also moves the map to the level
that draws the source and the reach as separate blocks, unless the level on screen
already does. A decision in `shared.kernel` with consequences in six blocks is a
change whose reach the map was built to show.

A decision's header offers **Mark consequences reviewed**. It marks every file whose
changed hunks are all consequences of this decision, or trivial, and reports how many
it marked and how many it left because they also carry other edits. A reviewed mark
is a property of a file, as pull-requests.md section 5.2 defines it, and this design
keeps that. A file whose edits are all consequences does not start out reviewed,
since the decision it follows from has not been read.

## 5. Semantic triviality

An edit is trivial today when both sides parse to the same tokens. Four more kinds of
edit cannot change what the program does, or can do so only through names used as
data, and each can be proven from the declaration diff and the syntax trees. Each
kind has a reason that the file's header shows in place of "no code changed", and the
hunk marks stay as they are.

### 5.1 Renames

A rename consequence is a hunk whose every changed line pairs with a line on the other
side such that the base line's tokens, with the old name replaced by the new one,
are the head line's tokens. The pairing is positional within the hunk, after trivial
lines are dropped. This is a stronger check than section 4.2 makes, since each line is
proven identical apart from the name, so a file whose edits are all rename
consequences starts out reviewed, with the reason "renamed `Old` to `New`" linking to
the rename's decision. Reading the decision is enough.

The rename's decision itself is not trivial. A name can reach the program as data:
through `nameof`, reflection, serialization, and callers outside the solution. The
decision's header says so for a public declaration, and the reviewer reads the
decision once.

### 5.2 Moves

A declaration moved between files of one type, such as a partial type split in two,
or reordered within its file, with the same shape and body, is a move. A hunk whose
changed lines are all the removed or added lines of moved declarations is trivial,
with the reason "moved from `path`" or "members reordered". A declaration moved to
another type is a decision, since its sites change. A file renamed by git is already
handled by the rename detection of pull-requests.md section 4.5.

### 5.3 Using directives

A hunk whose changed lines are all `using` directives is trivial with the reason
"usings only", and a file whose other tokens are unchanged takes the same reason. A
directive is not a dependency, as spec section 3 says, and both ends compiled. The
one way such an edit reaches the program is by making a more specific extension
method applicable, which the marks accept as they accept line numbers.

### 5.4 Generated files

A changed file that a source generator produced is not in the diff, since generated
documents are not in the repository. A checked-in file with an `<auto-generated>`
header, or that a `.Designer.cs` or `.g.cs` convention names, starts out reviewed with
the reason "generated", linking to the other files of the types it declares, where the
hand-written half lives. A generated file edited by hand is hidden by this, and the
reason on its header is there so that the reviewer can unmark it.

### 5.5 In the changeset

`FileHunks.Trivial` and `Hunk.Trivial` stay booleans, and both gain `Reason`: a kind
from a fixed list, with the names or path it mentions, and, for a rename consequence,
the identifier of the rename's decision. A hunk that is trivial by the rename rule is
still a consequence of that decision, so the decision counts it. The reviewed store
records the kind it pre-marked a file with, as it records "no code changed" today,
and the viewer takes the names and paths from the hunks.

## 6. The reading path

### 6.1 Steps and tiers

The path is a list of steps, each a block, an edge, a decision or a file, in three
tiers. It is computed by `ReadingPath.Plan` in `Vibecheck.Core`, a pure function of
the changeset, as `NarrativePlanner.Plan` is, and carried in the changeset as `Path`.
The same changeset always gives the same path.

**Read first** holds the surprises, each with the signal that put it there:

- An edge that is new at head between two blocks of any level, largest first. The
  signal names the pairs behind it.
- A cycle at head among a level's children or a view's groups that was not there at
  base, as one step naming the edge that closed it.
- A new edge out of a block whose category is `foundation` or `domain` into a block of
  any other category. These categories are the ones the rest of the system rests on,
  and an edge out of them is the one direction that is a surprise whatever the system
  is. This is a signal, not a rule, and the map's categories decide it.
- A decision whose consequences span the most blocks, then the most files. A public
  decision outranks a private one with the same reach.
- A changed block with the highest fan-in at head, meaning the sum of references into
  it from other blocks of its level, since decisions in it reach the most.
- A new concept of section 7, whether or not the reviewer has decided on it.

**Then** holds every other changed leaf, largest change first within each level, and
under each the decisions and the files with edits of their own that the first tier
did not take, and after them the changed test files whose subject the leaf holds, as
[tests.md](tests.md) section 7.2 describes. A leaf whose changed files are all set
aside is not a step, since the third tier accounts for it. A changed test file that
no leaf's step lists, such as one of test infrastructure, comes last on its own, and
a changed file that no type binds comes after it.

**Set aside** holds the consequences, the trivial and generated files and the files
already reviewed, as counts with the blocks they are in. A file that is both trivial
and a consequence counts as trivial. The tier exists so that the reviewer sees that
the volume has been accounted for, not so that they read it.

Steps are deduplicated: a block that a surprise names is not listed again in the
second tier, an edge that closed a new cycle is the cycle's step and not an edge step
of its own, and a decision's consequences are not steps. The path has at most forty
steps in its first two tiers. A changeset with more drops file steps first, then
decision steps, whose counts stay on their leaves' steps, and then leaf steps, which
one closing step names and counts.

### 6.2 In the viewer

The Changes panel opens on the path when the changeset has one, and a switch in its
title shows the changes instead. The path lists the tiers with each step's signal in
plain words, such as "Billing core now references Stripe: 12 references in 3 pairs"
or "signature of `Session.Require` changed, public: 14 consequences in 9 files across
4 blocks". A step opens where it lives: an edge or a cycle at its level or view, with
the edge selected; a block in its level; a decision at its hunk, with its reach in
the details panel; a file in its leaf. The step opened last is the current step. It
is marked in the list, and under it the panel lists its files with their reviewed
checkboxes and offers **Mark read**, which marks them all, so that what finishes the
step is in front of the reader. Two arrow buttons move along the path. The forward
arrow opens the first step after the current one whose files are not all reviewed,
or the first such step from the top. The back arrow opens the step before the
current one, read or not, since a reader may move on without finishing a step and
want it back. The bar shows
"4 of 11 steps read" beside the file count. A step is read when every changed file it
names is reviewed, so the path and the marks are one state and the path never asks
for a second kind of mark. The files an edge step names are the changed files of the
types on its referencing side.

A step for an edge or a node shows that edge's or node's narrative once it has
arrived, so the path gets Claude's explanations with no request of its own. When the
map switches, the path is recomputed with the changeset, and the open step is kept
where its leaf still exists.

### 6.3 Claude's part

The order within the first tier is deterministic and is enough. A later phase may
ask Claude, given the summary and the first tier, to order the tier and to write one
sentence on what connects the steps, citing step identifiers; an answer that cites
anything else is discarded and the deterministic order stands. It is requested as the
summary is, cached by the request, and marked as Claude's. It goes last because it
trades the stability of the path between sessions for an ordering judgment, and
section 12 keeps the question of whether that trade is worth it.

## 7. New concepts

### 7.1 What counts

A change introduces a concept when it adds something the map has no place for:

- Types at head that bind to `unsorted`, under any map.
- A new leaf that placement proposes, under a generated map. Today
  review-workflow.md section 4.5 creates the leaf and makes a new map version
  without asking.
- A node, view or external that the change adds to a committed map, which the
  structural diff already reports.

Types that placement puts into an existing leaf are membership, not concepts. They
are applied as they are today and listed in the same notice as placed, each with its
leaf and an **Undo** that returns it to `unsorted`.

### 7.2 The event

Each concept is one entry in a `concepts` event, sent when the review is ready and
again when placement answers. An entry lists its types with their files and
namespaces, the units they belong to, the blocks they reference most and are
referenced by most at head, taken from the changeset's edges to and from `unsorted`,
and, when Claude has answered, the proposal: a leaf identifier, a name, a purpose, a
parent, and a group in each view whose level holds it. For a concept that the change
added to a committed map, the entry shows the node as the change wrote it and asks
nothing, since the author already decided.

`TypePlacer.PlaceAsync` is split into a proposal, which asks Claude and returns its
answer checked against the map, and an application, which makes the new map version.
Placements into existing leaves are applied at once. New leaves wait.

### 7.3 The reviewer's choices

- **Accept** applies the proposal: the leaf with Claude's name and purpose, or the
  reviewer's edit of them, and the types bound to it by exact name. Under a generated
  map this is a new version, offered as versions are today. Under a committed map the
  review reruns with the amended map in memory, and a **Write to vibecheck.json**
  action rewrites the working tree's map in canonical form, so that the binding can be
  committed or suggested to the author. The file is the one the review found, and the
  action says which it is before it writes.
- **Place elsewhere** picks an existing leaf from the tree, and applies the binding
  the same way.
- **Leave unsorted** keeps the types in `unsorted`, drawn with the warning treatment,
  and records the choice for the session so that the notice does not return for the
  same types.

A concept stays in the first tier of the path until the reviewer chooses, and the
choice is logged with the Claude calls, since it changes the map.

## 8. The glossary

### 8.1 Terms

The vocabulary a change introduces is the set of names it adds, which the
declaration diff knows exactly:

- Added types, with their leaf at head.
- Added members of any accessibility, public first.
- Renamed declarations, as "`Old` is now `New`".
- Nodes, views and externals the change adds to the map.
- Assemblies referenced at head that were not referenced at base, from the external
  references.

Each term carries its kind, its block, the file and line of its declaration, its
documentation summary when it has one, the number of sites that use it at head, the
file and line of the first use outside its declaring type, and the decision of
section 4 that introduced it, when one did. A term with no documentation comment says
so, which is itself worth a reviewer's attention in code a model wrote.

The glossary is carried in the changeset as `Glossary`, grouped by block and ordered
by use count, so that the names a reviewer will meet most come first.

### 8.2 In the viewer

The Glossary opens from the summary and from a block's details panel, where it shows
the block's terms alone. Each term opens its declaration in the leaf view. In every
diff table, an introduced name is marked with a dotted underline, and hovering it
shows the entry. The match is by name within the changed lines, which is enough for
a hover.

### 8.3 Claude's part

A term with no documentation summary gets a one-sentence definition from Claude,
requested through the narrative pipeline as a narrative kind of its own, keyed
`term:<symbol>`, in batches by block after the levels and views. The request gives
the declaration's lines and the lines of up to four sites, and asks for one sentence
that says what the thing is, citing the declaring file. A definition that cites any
other file, or that is empty, is discarded, and the entry says "no documentation" in
its place. The definition is marked as Claude's and has the same **regenerate**
action as any narrative. This is a description of what the code declares, with the
declaration beside it, and not a judgment of it.

## 9. The API and the viewer

The changeset gains `Decisions`, `Path` and `Glossary`, and its hunks gain `Follows`
and `Reason`, as the sections above describe. A changeset published as an artifact
carries all three, so the viewer opened from a file shows the path and the glossary
without the server, as it shows everything but narratives today.

The event stream gains a `concepts` event. The narrative events carry term
narratives under their keys with no change of shape.

| Endpoint | Purpose |
|---|---|
| `POST /api/reviews/{id}/concepts/{n}` | Takes `action`, one of `accept`, `place` and `leave`, with the leaf identifier for `place` and the edited name and purpose for `accept`. Answers with the map version, or reruns the review under a committed map. |
| `POST /api/reviews/{id}/map/write` | Rewrites the working tree's map with the accepted bindings. Refuses when the review's map is not a file in the working tree. |
| `POST /api/reviews/{id}/reviewed` | Unchanged. The decision's mark action and the path's progress use it. |

The write routes follow the guards of pull-requests.md section 11.

## 10. Testing

No test calls Claude, and the declaration diff, the clustering, the triviality kinds,
the path and the glossary are pure functions of analyses and hunks, so they are
tested on synthetic analyses and on small compilations built in memory from source
snippets, which keeps them in the sub-second unit suite. The tests state:

- The declaration diff tells added, removed, reshaped, rewritten, renamed and moved
  apart, pairs overloads under one symbol, and pairs a renamed type with its members.
- A hunk whose changed lines all reference a reshaped declaration is its consequence,
  one with an unexplained line is not, and one that holds a changed declaration is a
  decision. A consequence of two decisions is listed under both and shown under the
  one with more sites.
- Marking a decision's consequences marks the files whose hunks are all consequences
  or trivial and leaves the others, and reports both counts.
- A rename consequence is proven line by line, and a rename of a public declaration
  is a decision the file does not start reviewed for. Moves, `using` changes and
  generated files take their reasons, and the reviewer's unmark wins over each.
- The path puts a new edge, a new cycle, an edge out of a foundation block, and the
  widest decision in the first tier in that order, deduplicates blocks across tiers,
  and marks a step read when its files are reviewed.
- Placement into an existing leaf applies at once, a new leaf waits, each choice
  produces the map it should, and writing the map refuses outside the working tree.
- The glossary lists each added or renamed name once with its uses and first use, and
  a term definition that cites another file is discarded.

The `Sample` suite checks the declaration lists and sites on the sample solution, so
that the Roslyn pass is covered once per run where it already costs the compilation.

## 11. Changes to other documents

These land with the phase that makes them true:

- Spec section 3 gains the declarations and sites of section 3 here, as a second
  thing the analysis observes. Section 6 gains `Decisions`, `Path`, `Glossary`,
  `Follows` and `Reason`, and the paragraph on trivial edits lists the kinds of
  section 5. Section 8 no longer lists member-level information as out of scope,
  though members are still not map nodes.
- review-workflow.md section 4.5: placement into existing leaves is applied at once,
  and a new leaf waits for the reviewer, as section 7 here describes. Section 6.1 gains
  term narratives.
- pull-requests.md section 5.1: a file becomes reviewed by the tool for the reasons of
  section 5 here as well, and the decision's mark action joins the ways a reviewer
  marks files.
- README: the reading path, decisions and consequences, the glossary, and new concepts.

## 12. Open questions

- The within-type sites may make the analysis of a large solution several times
  bigger. If they do, the alternative is a second, narrower Roslyn pass over the
  changed files alone when the changeset is built, which costs a compilation on a
  cache hit and is why the analysis was chosen first.
- Symbols name overloads together. Clustering by the specific overload would need
  parameter types in `RawDependency.Symbol`, which the narratives' evidence and
  `vibecheck why` print, so it is a contract change to be made when a real review shows
  the coarser rule misleading.
- The consequence rule works per line. A token-level rule, which would prove that a
  consequence line differs from its base line only in the reference, is what makes
  rename consequences safe to pre-mark, and it may be worth extending to reshaped
  declarations whose change is an added argument.
- Whether a file whose edits are all consequences of one decision should start out
  reviewed once that decision's file is marked, which would make the mark follow the
  reading rather than require the action.
- Whether the first tier of the path should be computed per view as well as per level,
  since a view's groups are the concepts a reviewer may think in.
- Whether Claude's ordering of the first tier, section 6.3, earns its place.

## 13. Phases

Each phase ends with the tool usable, and with tests that state its new behavior.

1. **Declarations and clustering.** The declarations and sites in the analysis, the
   declaration diff, decisions and consequences in the changeset, the leaf view's
   headers and collapsed consequences, the details panel's list by block, and the
   mark action. It needs no Claude and helps every review, so it comes first.
2. **Semantic triviality.** Renames, moves, `using` changes and generated files, with
   their reasons on file headers and in the reviewed store.
3. **The reading path.** `ReadingPath.Plan`, the `Path` member, the panel, the arrows
   along it and the progress in the bar.
4. **The glossary.** The `Glossary` member, the panel, the hover in diffs, and term
   narratives through the narrative pipeline.
5. **New concepts.** The split of placement into proposal and application, the
   `concepts` event, the reviewer's three choices, and writing the map.
6. **Claude's ordering of the first tier**, if section 12 answers yes.
