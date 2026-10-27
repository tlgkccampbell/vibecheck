# Cycles in map generation (design)

This document describes how map generation spends fewer of Claude's rounds on
cycles. It works out locally what the revision prompt now asks Claude to work out,
and it lets the reviewer settle what the graph cannot. It builds on the generation
loop of [review-workflow.md](review-workflow.md) section 4.4 and the checking of
[vibecheck-spec.md](vibecheck-spec.md) sections 4 and 5.3, and it lists, in section
8, the changes it makes to those documents.

Phase 1 of section 10, knots, is built. Phases 2 and 3, local repair and the
reviewer's part, are not.

Most revision rounds in practice are spent on cycles among a level's children. Each
round is an Opus request that takes minutes; the trial of review-workflow.md section
7 took 434 s for one proposal. The checker, by contrast, answers a candidate map
locally. The revision prompt tells Claude that "moving the type that causes the
fewest of them usually breaks the cycle", which is a search the tool can run itself
against the checker, in seconds, with each step verified.

The loop also stops when a usable revision fixes nothing, on the ground that such a
revision "usually meets a cycle in the code itself, which no grouping removes". That
is only half right. A cycle among types in the code disappears from the map once
those types share a leaf. What no grouping removes is a cycle among more types than a
leaf may hold, and the tool can tell the two apart before Claude is asked anything.

## 1. Goals

- A cycle that a move of a few types removes is removed without a round of Claude.
- A proposal does not split types that reference each other in a loop, unless they
  are too many for one leaf.
- A cycle that is in the code itself is reported as such, is never sent back to
  Claude, and does not count toward the stall that ends the rounds.
- The reviewer can see what the checker still reports about a generated map, apply
  a suggested move, tell Claude what a concept is, accept a cycle as real, or stop
  generation and keep the best map so far.
- The number of rounds and the time of each generation are measured before and after,
  on this repository and on a large sample.

Non-goals for this design: any change to the map format, any change to the
checker's findings, and clustering of the dependency graph as a substitute for
Claude's proposal, which spec section 5.3 rules out.

## 2. Knots

A **knot** is a set of two or more types that reference each other in a loop: a
strongly connected component of the graph of type references among the included
types, with test types and excluded types left out. Every knot whose types sit in
more than one leaf shows up as a cycle at the level where those leaves' branches
meet, whatever the rest of the map says.

Knots are found once per generation, before the first proposal, by running
`Checker.Cycles`, the Tarjan search the checker already uses for levels and views,
over types instead of nodes. The references are the analysis's references among the
included types, which do not depend on the map. That search keeps its own stack
rather than recursing, since a chain of types can be far deeper than the call stack
allows.

A knot of at most `MapGenerator.LargeLeaf` types is **small**, and a larger one is
**large**. A small knot fits in one leaf; a large one does not, so its cycle is in
the code itself.

### 2.1 In the proposal

The proposal prompt lists each small knot by its types' full names and says that the
types of a knot belong in one leaf. It lists each large knot by its types' namespaces
and type count, and says that its types belong under one container, so that the cycle
they cause stays at one level. A knot whose types share one namespace is not listed,
since a namespace pattern keeps it whole. A solution listed by namespace lists only
its large knots, since its listing names few types and a small knot listed by name
would make the prompt grow with the number of types; the knot pass gathers its small
knots instead.

A knot within one unit is still listed. A helper that only one type uses folds into
that type's unit, so a knot often forms a single unit, and a proposal still splits a
unit when it binds by namespace.

### 2.2 The knot pass

After every proposal and every revision, before the map is checked, the knot pass
moves each small knot's stray types into the leaf that holds most of the knot. A tie
goes to the leaf that the knot references most, and then to the leaf whose identifier
sorts first. The moves are exact-name moves, applied by `MapEdits` as Claude's are. A
type bound by a `[Vibecheck]` attribute is never moved; a knot that holds one is moved
to that type's leaf instead.

### 2.3 Cycles in the code

A cycle among a level's children is **in the code** when it disappears once the
references inside large knots are left out of the leaf edges. Such a cycle is still a
finding, and the generated map still draws it, but it is not sent to Claude and does
not count when the loop compares a revision with the one before. The loop
stops once only cycles in the code remain, and among usable proposals it keeps the
one with the fewest other findings, then the fewest findings. A finding in the code
says so in its text, and its line in the log starts "finding in the code".

## 3. Local repair

After the knot pass, the tool tries to break each remaining cycle that is not in the
code, before it asks Claude to:

1. Take the cycle's cut set, the edges whose removal breaks it, as `Checker.CutSet`
   returns them, and the type pairs behind each edge, as `Explain.Pairs` returns them.
   A cycle is a set of members, not a path, so its edges come from the cut set and
   never from the order of its members.
2. On the edge of the cut set with the fewest references, each pair gives two
   candidates: move the source type to the target's leaf, or move the target type to
   the source's leaf. A type moves with the other members of its unit, since a helper
   folded into a unit would otherwise make a new edge of its own.
3. Check each candidate with `Checker.Run`. Keep the candidate that lowers the number
   of findings the most, preferring the one that moves fewer types.
4. Repeat until no candidate lowers the number of findings, or until the budget of
   section 3.1 is spent.

A candidate never moves a type bound by a `[Vibecheck]` attribute, and never moves
part of a small knot away from the rest.

Only what is left goes to Claude. The revision prompt says that the listed cycles are
the ones that moving a few types did not break, and asks for a change of grouping:
a new leaf, a split, or a merge.

### 3.1 Budget

Each candidate costs one run of the checker. The search stops after 40 moves or 30
seconds, whichever comes first, and keeps every move it made. Both limits are
constants beside `MaxRounds`, set from the measurements of section 7.

### 3.2 What the moves cost

A move is chosen by the graph, not by the names, so a type can land in a leaf that
its name does not fit. That is the misplacement spec section 5.3 describes in reverse,
and it is the cost of saving a round. Every move is written to the log, the generated
map's notice lists them (section 4), and the next revision's prompt shows them to
Claude as part of the map. For a solution listed by namespace, each move adds an
exact name beside a namespace pattern, which is why the budget caps the number of
moves.

## 4. The reviewer's part

The generated map's notice now says only how many findings remain. It becomes a
panel that lists them, and the reviewer acts on them there.

- **Stop and keep.** While the map is being generated, the notice offers to stop. The
  round in progress is cancelled and the best usable map so far is kept, as when a
  revision fails. Before any map is usable, stopping ends generation with nothing,
  and the skeleton stays.
- **The findings.** Each finding is listed with its kind and, for a cycle, its nodes,
  the type pairs behind each edge, and whether it is in the code. The moves of
  sections 2.2 and 3 are listed under a heading of their own, each with an undo.
- **Apply a move.** For a cycle that local repair could not break without raising the
  number of findings elsewhere, the panel offers the best move it found and what that
  move would make worse. Applying it, or undoing an earlier move, writes a new version
  of the generated map without calling Claude.
- **Tell Claude.** A text box beside each finding, and one for the whole map, takes a
  note such as "`PaymentGateway` belongs in Billing; the cycle with Orders is real".
  **Revise** sends one more round with the notes in the prompt, above the findings,
  and writes a new version.
- **Accept as real.** A cycle the reviewer accepts is recorded with that version of
  the generated map, is not counted in the notice, and is left out of the findings of
  any later round of **Revise**. The viewer still draws it and still marks it, since
  the cycle is real.

Each new version is offered by the notice as generated maps are now, and switching to
it reruns the review without compilation, as review-workflow.md section 4.4 says.

## 5. The API and the viewer

- The map job's events carry the findings of the best map so far as structured items:
  kind, text, nodes, type pairs, whether the cycle is in the code, and whether it is
  accepted. They also carry the moves made locally.
- `POST /api/reviews/{id}/map/stop` cancels a generating job and keeps the best map.
- `POST /api/reviews/{id}/map/edits` takes moves and undos, applies them to the newest
  generated map, checks it, and writes a new version.
- `POST /api/reviews/{id}/map/revise` takes the notes and the accepted cycles and runs
  one round of Claude on the newest generated map.

Accepted cycles are stored in the cache beside the generated map version they belong
to, and a new version carries them forward while the same cycle exists in it.

## 6. Testing

`MapGeneratorTests` already runs generation against a fake Claude. New tests state:

- A knot's types are found from a small analysis, and a large knot is told apart from
  a small one.
- A proposal that splits a small knot comes out of the knot pass with the knot in the
  leaf that held most of it, and the fake Claude is asked once.
- A proposal with one misplaced type behind a cycle is repaired locally, and the fake
  Claude is asked once.
- A cycle caused by a large knot is reported as in the code, is not in the revision
  prompt, and does not stop the loop as a stall.
- The search keeps to its budget and never moves an attributed type.
- Stopping during a round keeps the best map so far; applying and undoing moves, and
  accepting a cycle, write new versions with the expected findings.

## 7. Measuring

Before phase 1, and after each phase, generation runs on this repository and on the
largest sample. The log records, per round, the findings by kind, how many cycles are
in the code, the moves made locally, and the time spent in Claude and in the search.
The results are kept in a table in this section, as review-workflow.md section 7 does
for models. The first run also tests the premise of this document: how many of the
cycles that cost a round came from knots that the knot pass would have kept together.

## 8. Changes to other documents

- review-workflow.md section 4.4: step 3 finds knots and runs the knot pass and local
  repair before it checks; step 4 sends only the findings left, leaves out cycles in
  the code, and no longer says that a stall usually means a cycle no grouping removes.
  The notice's paragraph points to section 4 of this document.
- vibecheck-spec.md section 5.3: the loop tries moves against the checker before the
  author revises, and a cycle among more types than a leaf holds is named as the one
  kind that no grouping removes.
- README.md lists this document with the others.

## 9. Open questions

Each question below ends with a recommendation, which stands until it is decided.

- **Whether an accepted cycle belongs in the map format**, so that `vibecheck check`
  and maps saved to the repository know about it. Keep it in the cache. Acceptance
  only matters while a map is being generated or revised, since a map in the
  repository is used as it is and never regenerated. Spec section 4 treats a cycle as
  a metric rather than a warning, so a saved map has nothing to silence. Revisit
  this if a cycle ever becomes a warning or fails `check --strict`.
- **Whether candidates should also include moving a whole leaf to another
  container**, which breaks some cycles that no type move does. Leave whole-leaf
  moves out of the search. Moving a leaf changes its identifier and the view groups
  that list it, and whether a leaf belongs under a container is a question about
  concepts. That is the change of grouping the revised prompt of section 3 already
  asks Claude for. Revisit this if the measurements of section 7 show cycles among
  containers that type moves leave behind.
- **Whether the search's budget should scale with the number of types.** Keep the
  fixed limits of section 3.1, since a time limit already shrinks the number of
  candidates as each check grows slower. In phase 2, time one `Checker.Run` on the
  largest sample first. If it takes more than about half a second, the search would
  try too few candidates. In that case, score candidates by recomputing only the leaf
  edges that the moved types touch, and find cycles with the checker's own level
  routine, not a second one. Then run `Checker.Run` once on the result, and add a
  test that the two agree.

## 10. Phases

1. **Knots.** Find knots, list them in the proposal prompt, run the knot pass, mark
   cycles in the code in the log, and leave them out of the revision prompt and the
   stall. Measure.
2. **Local repair.** The search of section 3, its budget, and the revised prompt.
   Measure.
3. **The reviewer's part.** The findings panel, stop and keep, applying and undoing
   moves, notes for **Revise**, and accepted cycles.
