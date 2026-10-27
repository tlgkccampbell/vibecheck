# Cycles in map generation (design)

This document describes how map generation spends fewer of Claude's rounds on
cycles. It works out locally what the revision prompt now asks Claude to work out,
and it lets the reviewer settle what the graph cannot. It builds on the generation
loop of [review-workflow.md](review-workflow.md) section 4.4 and the checking of
[vibecheck-spec.md](vibecheck-spec.md) sections 4 and 5.3, and it lists, in section
8, the changes it makes to those documents.

**Status: phases 1 and 2 were measured and not adopted.** Phases 1 and 2 of section
10, knots and local repair, were built and measured on one large solution, in
sections 7.1, 7.3 and 7.4. All eight measured generations ran all three rounds, so
neither phase saved a round, and nearly half of local repair's moves were doubtful by
name. Their code was not merged; it stays on the branch `map-cycles`. What was
merged is what the measurements needed: cycles reported by their members and cut
sets (issue #10), the timing of each run of the checker, and moves whose type and
leaf are swapped (issue #11). Phase 3, the reviewer's part, is not built, and its
moves and entangled cycles would have to come from elsewhere. The rest of this
document describes the design as it was measured.

Most revision rounds in practice are spent on cycles among a level's children. Each
round is an Opus request that takes minutes; the trial of review-workflow.md section
7 took 434 s for one proposal. The checker, by contrast, answers a candidate map
locally. The revision prompt asks Claude to move the types behind the edges that
break a cycle, which is a search the tool can run itself against the checker, with
each step verified.

The loop also stops when a usable revision fixes nothing, on the ground that such a
revision "usually meets a cycle in the code itself, which no grouping removes". That
is only half right. A cycle among types in the code disappears from the map once
those types share a leaf. What no grouping removes is a cycle among more types than a
leaf may hold, and the tool can tell the two apart before Claude is asked anything.

## 1. Goals

- A cycle that a move of a few types removes is removed without a round of Claude.
- A cycle that its members' many references hold together is told apart from one
  that a few moves break. Once Claude has failed to break it, it costs no further
  round.
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
says so in its text, and its line in the log starts "finding in the code". Section
3.4 adds a second reason to stop, for entangled cycles that a round failed to break.

## 3. Local repair

After the knot pass, the tool tries to break each remaining cycle that is not in the
code, before it asks Claude to. It repairs only the cycles that a few moves break,
and it leaves the rest to Claude and the reviewer. Repair is part of checking a map,
so it also runs on the last round's answer, after which Claude is not asked again.

The values in this section come from two measurements on one large solution,
summarized in sections 7.1 and 7.2. Each is a constant beside `MaxRounds`, and the
measurement of section 7.3 checks them again.

### 3.1 Repairable and entangled cycles

Every cycle comes with its cut set: the edges whose removal breaks it, as
`Checker.CutSet` returns them, with the type pairs behind each edge, as
`Explain.Pairs` returns them. A cycle is a set of members, not a path, so its edges
come from the cut set and never from the order of its members.

A cycle is **repairable** when its cut set has at most 3 edges and the cheapest
candidate of section 3.2 moves at most 6 types, counting the members of their units.
A cycle that is neither repairable nor in the code is **entangled**. Its members
reference each other in so many ways that the code does not separate them, and the
fix is a change of grouping, usually a merge, not a move.

In the second measurement, 3 edges covered about half of the first round's cycles,
11 of 14 cycles after the second round, and 7 of the 8 cycles left at the end. Every
cut set above 10 edges came in the first round, and Claude's revisions broke them.
The limit of 6 types is not yet measured, and a single cut edge can carry many
references: one final cycle's only cut edge had 11.

### 3.2 Candidates

A candidate breaks one cycle by clearing every edge of its cut set at once. Each pair
behind an edge can be cleared from either side: move its source type into the leaf of
its target type, or move its target type into the leaf of its source type. A type
moves with the other members of its unit, since a helper folded into a unit would
otherwise make a new edge of its own.

1. Build the first candidate by clearing each edge from the side that moves fewer
   types, the source side when they tie.
2. Check it with `Checker.Run`. Keep it if the cycle is gone and the number of
   findings has not risen.
3. Otherwise, try each edge from its other side, one edge at a time, and keep the
   first such candidate that passes the same test.
4. If none passes, the cycle goes to Claude unchanged.

A cycle therefore costs at most four runs of the checker. Repairable cycles are
tried in the order of their cut set's total references, lightest first, and each kept
candidate is the map the next one starts from.

A source type moves into the leaf of the type it references most across the edge, and
a target type into the leaf of the type that references it most. A cycle is gone when
no cycle among the same level's children holds two of its members. A candidate that
moves more than 6 types is not tried, and a cycle with an edge to an external or the
unsorted leaf goes to Claude, since no move clears such an edge.

No candidate moves a type bound by a `[Vibecheck]` attribute, moves part of a small
knot away from the rest, or moves a type that section 3.3 holds in place.

### 3.3 The fit guard

A move is chosen by the graph, not by the names, so it can put a type where its name
does not fit. In the first measurement, one hand move put a type into the leaf of an
unrelated concept, and it broke no cycle. The graph cannot judge a name, but it can
judge **affinity**: how many references a type has to and from the types of a leaf.

A type may move only when its affinity with the leaf it moves into is at least its
affinity with the leaf it leaves. A unit's affinity is the sum over its members, and
references among the unit's own members do not count. A type that touches its new
leaf only through the one cut edge stays where it is, and its cycle goes to Claude.

This guard is untested on a real solution. The measurement of section 7.3 reports
how often it holds a type in place, and whether the moves it allows fit.

### 3.4 Entangled cycles and the rounds

Entangled cycles still go to Claude. Their findings list the cut set and say that
merging some of the members is the likely fix, as the revision prompt does since
issue #10. In both measurements, the second and third rounds roughly halved the
cycles, and the largest cut sets did not survive the second round, so a round spent
on them is not wasted.

A round that fails to break them is, though. The rounds stop when every remaining
finding other than a cycle in the code is an entangled cycle that the previous usable
round also reported, with the same members. Such cycles wait for the reviewer, who
can accept them or merge their members (section 4).

In the second measurement, entangled cycles were rare after the first round, so this
rule is a backstop. Most of the gain comes from repair itself. Claude left cycles
with a cut of one edge after two revisions that named that edge, and a round costs
about 200 seconds. When repair clears what the second round left, the third round is
not needed; when it does not, repair after the third round still leaves fewer cycles
for the reviewer.

### 3.5 Budget

The search stops after 60 seconds, and keeps every candidate it accepted. In the
second measurement, a run of the checker inside generation took 0.9 to 2.5 seconds on
a solution of about 3,300 types. A round left at most 7 repairable cycles, so even at
four checks each the search almost always finishes, and it costs less than a third
of a Claude round. A `vibecheck check` from the command line took about a minute, but
that includes restoring and analyzing the solution, which generation has already
done.

### 3.6 What the moves cost

Every move is written to the log, the generated map's notice lists them (section 4),
and the next revision's prompt shows them to Claude as part of the map, so Claude
can undo a move that does not fit. For a solution listed by namespace, each move adds
an exact name beside a namespace pattern. The limit of 6 types per cycle keeps those
names few.

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
- **Apply a move.** For a repairable cycle that local repair could not break, because
  every candidate raised the number of findings or the fit guard held a type in place,
  the panel offers the best candidate it found and what that candidate would make
  worse. Applying it, or undoing an earlier move, writes a new version of the
  generated map without calling Claude.
- **Merge.** For an entangled cycle, the panel offers to merge its members into one
  node: a leaf when they are all leaves, and a container otherwise. Merging writes a
  new version without calling Claude.
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
- `POST /api/reviews/{id}/map/edits` takes moves, merges and undos, applies them to
  the newest generated map, checks it, and writes a new version.
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
- A proposal with one misplaced type behind a two-member cycle is repaired locally,
  and the fake Claude is asked once.
- A cycle whose cut set has more than 3 edges is entangled, is not repaired, and goes
  to Claude.
- A candidate that would move a type into a leaf it has less affinity with than its
  own is not tried, and the cycle goes to Claude.
- When a round's only remaining findings are entangled cycles that the round before
  also reported, the rounds stop.
- A cycle caused by a large knot is reported as in the code, is not in the revision
  prompt, and does not stop the loop as a stall.
- The search keeps to its budget, never moves an attributed type, and runs the
  checker at most four times per cycle.
- Stopping during a round keeps the best map so far; applying and undoing moves, and
  accepting a cycle, write new versions with the expected findings.

## 7. Measuring

Before phase 1, and after each phase, generation runs on this repository and on the
largest sample. The log records, per round, the findings by kind, how many cycles are
in the code, the moves made locally, and the time spent in Claude and in the search.
The results are kept in a table in this section, as review-workflow.md section 7 does
for models. The first run also tests the premise of this document: how many of the
cycles that cost a round came from knots that the knot pass would have kept together.

### 7.1 The first measurement

Phase 1 was measured on one large solution of about 3,300 types, listed by namespace,
with two generations before it and two after. The solution is not named here.

- Every generation ran all three rounds and three Claude requests, in about ten
  minutes, and ended with 7 to 11 findings, almost all of them cycles.
- The knot pass moved 4 and 9 types, and no cycle was in the code. The first round's
  cycles did not fall: 13 and 15 before, 16 and 13 after.
- So no remaining cycle had a knot beneath it. Each came from one-way references that
  close a loop between leaves.
- Of the 14 cycles left in the two maps, 12 had a weakest edge of 1 to 3 references,
  and none was clearly a layering violation. A hand move broke a two-member cycle and
  failed on larger ones.
- The cycles were printed as paths whose steps were often not edges, which issue #10
  fixed.

### 7.2 The second measurement

Two generations ran on the same solution with the build that fixes issue #10 and logs
checker times. The build before phase 1 does not write those lines, so section 7.1's
runs are the only baseline.

- Both generations ran all three rounds, in 710 and 678 seconds, as before.
- The knot pass moved 5 and 4 types, and no cycle was in the code.
- The cycles among a level's children fell from 13 to 7 to 2 in one generation, and
  from 14 to 7 to 6 in the other. There were no cycles among a view's groups.
- A run of the checker took 0.9 to 2.5 seconds.
- Cut sets in the first round ran from 1 to 18 edges, and 13 of 27 had 3 or fewer.
  After the second round, 11 of 14 had 3 or fewer, and the largest had 10.
- Of the 8 cycles left at the end, 5 had a cut of 1 edge, 2 had 2, and one had 5. Of
  the 7 whose lightest cut edge was reported, 5 had 1 or 2 references.

The final counts, 2 and 6, are lower than the 7 and 7 of section 7.1. Two runs of a
model that varies from run to run cannot show that accurate cut sets caused it.

### 7.3 After phase 2

The same solution runs two generations with phase 2 built. Each reports what section
7 lists, and also:

- the rounds, the time, and the cycles left at the end, against sections 7.1 and 7.2;
- per round, the cycles repaired, entangled and handed to Claude, and the types moved;
- how often the fit guard held a type in place, and the cut set of each such cycle;
- for each move, whether the type's name fits the leaf it moved into;
- the time the search took per round, and whether it reached its budget.

The first two such generations gave these results:

- Both ran all three rounds, in 745 and 695 seconds, so neither rounds nor time fell.
  The stop rule for entangled cycles never fired, because cycles that were not
  entangled always remained.
- Repair broke 0 to 3 cycles a round, moved 0 to 10 types, and took 0.2 to 6.6
  seconds. It never reached its budget.
- After repair, the first round left 9 entangled cycles in each run, and later
  rounds left 1 to 4. The other cycles left after repair numbered 1 to 6.
- The fit guard held 2 to 15 types a round in place. The log did not say which
  cycles it held, or why each remaining cycle was not repaired; it now writes a line
  for each cycle that repair leaves, with the reason.
- The cycles among a level's children fell from 11 to 5 to 4 in one run, and from
  15 to 6 to 2 in the other. The second run ended with 2 findings, the fewest of any
  measured run. Two runs cannot show that repair caused it.
- Judged by names, most of the 30 moves fit, and 5 were doubtful.
- One revision swapped the type and the leaf in 15 moves, which issue #11 records.

### 7.4 The reasons repair gives

Two more generations ran on the same solution with the build that fixes issue #11 and
logs why repair leaves each cycle.

- Both ran all three rounds, in 607 and 469 seconds. That is less than the 678 to 745
  seconds of sections 7.2 and 7.3, but most of a round is Claude's time, and repair
  took only 1 to 7 seconds a round. Every measured run so far has used all three
  rounds, and the stop rule for entangled cycles has never fired.
- The cycles among a level's children fell from 13 to 4 to 1 in one run, and from 12
  to 5 to 3 in the other. One cycle among a view's groups stayed in every round. The
  runs ended with 2 and 4 findings.
- Repair broke 0 to 3 cycles a round and moved 0 to 7 types. A checker run took about
  1 second at the median and 2.4 seconds at most. The search never reached its budget,
  and no candidate made the map unusable.
- The fit guard was by far the most common reason. It held 2 to 17 types a round, and
  almost every cycle it held had a cut of 1 or 2 edges. Several holds were near ties:
  four types had 2 references to their own leaf and 1 to the new one, and another had
  3 and 2. Others were clear, such as 118 against 2 and 156 against 4.
- Cycles entangled by their cut sets had 4 or 5 edges in later rounds and 9 to 41 in
  the first. Two were entangled by types alone: cuts of 2 and 3 edges that would have
  moved at least 9 to 14 types. The only entangled cycle left at the end had 36 cut
  edges, which no reasonable limit covers.
- Three candidates, with cuts of 2 or 3 edges, left their cycle in place. Their moves
  likely added references that closed the loop again.
- Judged by names, 8 of the 15 moves fit and 7 were doubtful. The doubtful ones moved
  general types into a narrow leaf, or split two types of one namespace across two
  leaves.
- When two cycles of one round move the same type, the round's count of moved types
  counts it twice.

So the limits of 3 edges and 6 types are not what stops repair; the fit guard is. Two
runs of a model that varies cannot show that phase 2 caused the shorter times.

## 8. Changes to other documents

Phases 1 and 2 changed review-workflow.md section 4.4 and vibecheck-spec.md section
5.3 on their branch. Since they were not adopted, those changes were not merged. What
was merged changes these documents:

- review-workflow.md section 4.4: step 3 logs how long each run of the checker takes.
- agent-guide.md section 3.3: a cycle is printed as its members and the edges whose
  removal breaks it.
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
  fixed limit of section 3.5, and run `Checker.Run` for each candidate. On about
  3,300 types a check took at most 2.5 seconds, which leaves room for every
  repairable cycle. If a larger solution's checks take more than about 5 seconds,
  score candidates by recomputing only the leaf edges that the moved types touch, and
  find cycles with the checker's own level routine, not a second one. Then run
  `Checker.Run` once on the result, and add a test that the two agree.
- **The limits of a repairable cycle.** At most 3 cut edges and 6 moved types. The
  edge limit covered 7 of the 8 cycles left at the end of section 7.2's runs.
  Recommendation: keep both. In section 7.4, cycles with 4 or 5 cut edges shrank
  under Claude's revisions, and the fit guard, not either limit, left most cycles.
- **Whether the fit guard is too strict.** It holds a type in place when it touches
  its new leaf only through the cut edge, which is also when a move is most likely
  to be right for a misplaced helper. In section 7.4 it was the most common reason a
  cycle with a cut of 1 or 2 edges was left, often on a near tie such as 2 references
  against 1. Yet 7 of the 15 moves it allowed were doubtful by name, so loosening it
  trades cycles for worse moves. Recommendation: keep it. On a near tie the graph
  barely prefers either leaf, so the name decides, and only Claude or the reviewer
  can judge a name.

## 10. Phases

1. **Knots.** Find knots, list them in the proposal prompt, run the knot pass, mark
   cycles in the code in the log, and leave them out of the revision prompt and the
   stall. Measure.
2. **Local repair.** Repairable and entangled cycles, the candidates and the fit
   guard, the stop rule for entangled cycles, and the budget, with the values that
   section 7.2 set. Measure as section 7.3 says.

Phases 1 and 2 were measured and not adopted, as the status at the top says.
3. **The reviewer's part.** The findings panel, stop and keep, applying and undoing
   moves, merging an entangled cycle, notes for **Revise**, and accepted cycles.
