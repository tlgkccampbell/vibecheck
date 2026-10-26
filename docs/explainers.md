# Explainers (design)

This document describes explainers: a linear narrative that walks a reviewer through a
changeset the way its author would walk a peer through it, as a run of **slides** that
each say a little and point at one thing in the viewer. The viewer shows a slide's text
over the main panel and moves the panel to what the slide points at: a level with the
changed blocks brought out, an edge, a block, a decision, a hunk, a type, an instance
of a pattern beside its example, or a concept. The reviewer moves back and forth at
their own pace. An explainer is written by Claude, only when the reviewer asks for it,
and is cached. It builds on the narratives of [review-workflow.md](review-workflow.md),
the reading path and concepts of [attention.md](attention.md), the compare view of
[patterns.md](patterns.md) and the intents of [intent.md](intent.md), and it lists, in
section 9, the changes it makes to those documents.

All three phases of section 11 are built.

The design rests on two principles. First, an explainer is a story, and the viewer is
its illustrations. Every slide points at something the changeset already holds, by an
identifier the viewer already knows how to open, so that the story never says anything
the reviewer cannot see for themselves. Claude chooses the order and the words; the
viewer chooses nothing and shows what it is told. A slide that points at nothing the
changeset holds is sent back, and one that still does is dropped. A figure drawn for a
slide is the exception, kept for what the viewer has no place for, and section 3.3 keeps
it small.

Second, the explainer is a second account, not the first. The reading path of
[attention.md](attention.md) section 6 is deterministic, ordered by surprise and tied to
the reviewed marks, and it stays the account the review rests on. An explainer trades
that stability for a storyteller's order, purpose first and consequence after, which is
how a peer learns a change fastest. The two are kept apart so that the reviewer can take
the tour and then finish the path, and so that a change of words in the explainer never
moves a step or a mark.

The word *narrative* already means the two or three sentences on why one edge or node
changed, and *step* means a place on the path. This document calls the whole story an
**explainer** and its parts **slides**, and the thing a slide points at its **target**,
as a step's target names what the step opens.

## 1. Goals

- A reviewer who has never seen the change can ask for an explainer and, in a few
  minutes of reading, know what the change is for, where it lives on the map, what its
  main new structures and decisions are, how it is tested, and what to look at closely.
- Each slide shows its text beside the thing it describes, in the viewer's own drawing
  of it, and never beside a picture of it drawn apart.
- The reviewer moves forward and back freely, leaves the story to look around, and
  comes back to the slide they were on.
- An explainer is written only on request, costs one request to Claude in the common
  case, and is cached so that a second look costs nothing.
- A slide's target is checked against the changeset as a narrative's citations are, and
  the explainer is marked as written by a model wherever it is shown.

Non-goals for this design: an explainer that replaces the summary, the narratives or the
path; one written without being asked for; speech or timing of any kind; and editing the
explainer in the viewer. A changeset reviewed without Claude has no explainer, as it
has no narratives.

## 2. The reviewer's path

1. The changeset opens as it does now. The left panel's title is a menu of its faces,
   and **Explainer** sits second in it, after Path and before Changes, Intent,
   Concepts, Patterns and Tests. The menu replaced a row of tabs, which seven faces
   made wider than the panel, so that the last tabs were cut off. Its face says in one
   paragraph what an explainer is and offers **Write it**, with the cost in plain
   words: one request to Claude, about a minute.
2. While Claude writes, the face shows progress, and the reviewer goes on with the map.
3. When the explainer arrives, the face lists its slides by number and title, and the
   first slide opens: its text appears in a card over the main panel, and the panel
   shows the slide's target, as a path step opens where it lives.
4. The card's arrows, the face's arrows and the keyboard move among the slides. Each
   slide moves the panel to its target, with the viewer's usual transitions.
5. The reviewer clicks a block, opens a diff, or reads a narrative in the details
   panel. The card stays, with the slide it was on. **Show again** returns the panel
   to the slide's target, and the arrows move on from the slide, not from where the
   reviewer wandered.
6. A slide whose target is also a step of the path offers **Open on the path**, which
   switches the face to the path at that step, where the reviewer can mark its files.
7. Closing the card ends the tour but keeps the explainer. The face lists its slides
   until the review is closed, and **Regenerate** writes it again.

## 3. Slides

### 3.1 What a slide is

A slide has:

- A **title** of a few words that states what the slide claims, such as "Refunds post
  as negative entries" or "Tokens hold no positions", rather than naming a topic. A
  claim tells the reader what to look for in the target; a topic does not.
- A **text** of two to five sentences. It says what the slide's target is for and how
  it fits the story, and does not repeat the counts, the diff or the narrative the
  details panel already shows for the target.
- A **target**, what the viewer shows while the slide is current, of one of the kinds
  of section 3.2.
- **Cites**, the changed files and type pairs the text rests on, checked as a
  narrative's citations are. A slide whose target is a level or a view may cite
  nothing, since an overview rests on the whole.

An explainer has as many slides as the change needs, and no set number. A slide is
about one thing: it points at one target and says what that thing is for, and text that
needs a second target is two slides. A large change gets a long explainer of short
slides rather than a short one of dense slides, since a reader takes a slide at a time,
and a slide that says one thing is easier to take than one that says three.

An explainer is **complete** when every step of the first tier of the reading path, and
every changed block, is covered: the target of some slide, or named by one as section
4.3 defines it. A slide may gather several small things that are alike, such as the
decisions that add the names and messages of a new rule, under the block that holds
them, so completeness bounds what the explainer must mention and not how many slides it
takes. The first tier is already cut to the surprises, so the bound grows with the
change and no further.

The prompt asks for the shape a peer would use: what the change is for, where it lives
on the map, the main new structures and the decisions with the widest reach, the
dependencies that changed and why, how the change is tested, and what to look at
closely. Claude chooses which of these the change needs and in what order.

### 3.2 Targets

A target names one thing by an identifier the changeset holds, and the viewer opens it
as it opens the path's steps. The kinds are:

| Kind | Names | The viewer shows |
|---|---|---|
| `level` | A level, or a view, and optionally the blocks to bring out | The level or view, with the named blocks outlined and the rest dimmed, as hovering a pattern outlines its blocks. |
| `edge` | A changed edge at a level or a view, as `from→to` | The level or view with the edge selected, so that its narrative is in the details panel. |
| `block` | A changed block, or a view's group | The block in context, selected, with its details. |
| `decision` | A decision | Its hunk in the leaf view, with its reach in the details panel and its arrows on the map, as selecting a decision does. |
| `hunk` | A changed file and the index of one of its hunks | The file in the leaf view, scrolled to the hunk. |
| `type` | A type added or moved in the change | The leaf view at the type's first changed file, as a pattern step opens its instance. |
| `pattern` | An instance of a pattern reported in the change | The compare view, with the instance beside its nearest example. |
| `concept` | A concept of the glossary | The Concepts face at that concept, with its types listed. |
| `tests` | A test type with changed methods | Its first changed hunk, with the Tests face open. |
| `figure` | A figure of section 3.3 | The figure, drawn in the stage. |

Every kind but `figure` names something the viewer already draws, and the row says which
existing action opens it, so that the explainer adds no new way of showing anything. The
set is deliberately the set of places the reading path and the panels already reach,
which keeps the check of section 4.3 a lookup.

### 3.3 Figures

A figure is for what the viewer has no place for: the fields of a new record and what
they refer to, the states of a new state machine, the order of calls in a new flow. A
figure is a small declarative drawing, not an image: a list of boxes with a label and an
optional type, a list of arrows between them with an optional label, and optional
groups. The viewer draws it with ELK in the stage, in the stage's own style, so that a
figure looks like the map rather than like a picture pasted over it, and so that it is
legible in both themes. A box whose type names a type of the changeset is a link that
opens the type.

The prompt says that a figure is the last resort, that at most two slides of an explainer
may carry one, and that a slide about a type whose declaration the viewer can show
points at the type instead. Figures are phase 3 of section 11, so that the first two
phases find out how often Claude reaches for one when it is not offered.

As built, a slide with a figure has the target kind `figure`, and its answer carries a
`figure` of `boxes`, each with an `id`, a `label` and an optional `type`, `arrows`, each
`from` one box `to` another with an optional `label`, and optional `groups`, each with a
`label` and the ids of its `boxes`. The check moves the figure onto the resolved target,
as `Figure` in `Vibecheck.Assist`, and rejects it when it is missing, has fewer than two
boxes or more than twelve, more than sixteen arrows or more than four groups, a label
longer than forty characters, a box without an id or a label, an id given twice, an arrow
or a group that names a box the figure does not have, an arrow from a box to itself, or a
box in two groups. A box's type is kept whether or not the changeset has it, and the
viewer draws a type it does not have as text rather than as a link. The limit of two is
the check's as well as the prompt's: the third figure of an answer is rejected with that
reason, the retry may not add a figure past those that passed the first time, and a
figure that fails twice is dropped like any slide. A figure covers what its cites name
and nothing else, since it names nothing in the catalog.

The viewer lays a figure out with ELK's layered algorithm, each group a node that holds
its boxes, both left to right and top to bottom, and keeps the direction that draws it
larger in the room above the card, at no more than the map's own scale. Boxes are drawn
as blocks, with the label in the header band and the type's short name under it, and
arrows and their labels as the map's edges. The frame is titled with the slide's title
and says that Claude drew it and that it is not part of the map. While a figure is shown,
the trail keeps the location the stage returns to, the breadcrumb ends in **Figure**, the
view switch is hidden, and the details panel describes the figure and lists its types.
Any mount of a location ends the figure: another slide, closing the card, a crumb, a box
that opens its type, or any other navigation. A redraw, as when a mark or an option
changes, leaves it in place. The done event and the server log count the slides that
carry a figure, so that a real run reports how often Claude reaches for one.

## 4. Writing an explainer

### 4.1 The catalog

`ExplainerPlanner.Plan` in `Vibecheck.Assist` is a pure function of the changeset. It
builds the **catalog**: the identifiers Claude may target, each with the words the
viewer uses for it. The catalog is built from what the review has already chosen as
worth reading, rather than from the whole changeset:

- The levels and views that draw a changed edge or hold a changed block, each with its
  changed blocks and the changed edges `NarrativePlanner` lists for it.
- Every step of the path's first two tiers, as its target: the edges, cycles, blocks,
  decisions, test types, pattern instances and files. The first tier is listed first and
  as such, with each step's signal, since the explainer must cover it.
- For each changed file on the path, its hunks with edits of their own, each by index
  with its header line and its decision's header when it is one.
- The types the change added or moved, with the file that declares each.
- The concepts new in the change.

A changeset larger than the path's forty steps is already cut to them, so the catalog
has a bound the path set. A cycle is listed as the edge that closed it, since the viewer
opens a cycle at that edge. A file the changeset sets aside as trivial or generated is
not listed at all, and neither is a file that only the analysis lists, such as a source
generator's output under `obj`, which git never reported and which has no hunks. The
path leaves the first in its third tier and never names the second, and a model that
sees a generator's output among the changed files tells the reviewer to check whether it
was meant to be committed, which the trial of section 6 showed.

### 4.2 The request

An explainer is one request, in the form of section 7 of
[review-workflow.md](review-workflow.md), with the head worktree as the working
directory and the read-only tools Read, Grep and Glob, so that Claude can read what the
catalog names. The prompt holds:

- The commit messages of the range, as a narrative request does, since in a change a
  model wrote they often state the purpose.
- The summary and every narrative written so far, each by its key. They are Claude's
  own earlier account of each part, and the explainer is told to use them and not to
  contradict them without reading the code.
- The catalog, grouped as section 4.1 lists it, with each entry's identifier, its name,
  its signal on the path when it has one, and for a decision its header and reach.
- For each decision on the path, its hunk, up to the lines a node narrative gets.
- The shape of section 3.1, the kinds of section 3.2, the rule that a slide is about
  one thing and that the explainer covers every first-tier step and changed block, and
  the rule that each slide's target and cites must come from the catalog.
- The rule that a slide says only what the catalog and the code show. Where a change
  has no explanation, such as a test changed in a way that no changed declaration
  explains, the slide says so and points at it, and never offers a reading of its own.
  A guess on a slide is harder for a reviewer to catch than no slide, because the
  slide's confidence is the explainer's.

Intents are not given. [intent.md](intent.md) section 6 keeps Claude's account and the
author's apart so that the reviewer can see where they differ, and an explainer written
from the intents would read the author's claims back as the story. The card shows the
intents that cover a slide's target beside the text instead, marked as the author's, as
a path step does.

The answer follows `explainer.schema.json`: a list of slides, each with its title, text,
target kind and identifier, the blocks to bring out when the target is a level, its
cites, and its figure when it has one. Prompt and schema are embedded files beside the
narrative prompts, versioned together.

### 4.3 Checking

`ExplainerWriter.Check` resolves each slide against the catalog. A slide is rejected when
its target names nothing in the catalog, when a block it brings out is not under its
level, or when a cite names a file not changed or a pair not in some changed edge's
evidence. A figure slide is also rejected by the rules of section 3.3. As with narratives, the rejected slides are requested once more together, with
the slides that passed as context and the reason each was rejected, and a slide that
fails twice is dropped. The explainer keeps its order with the gaps closed, and the face
says how many slides were dropped and why. An answer with no slide that passes counts as
failed, and the face offers to try again.

The check also measures completeness. A first-tier step is covered by a slide that
targets it, that targets or cites a file or type pair the step names, or that targets
the block a decision or file step sits under. A changed block is covered by a slide
that targets it or anything inside it. Coverage is a fact about the explainer, not a
rejection, since a story may leave a thing out on purpose. The one retry request
therefore carries both the rejected slides with their reasons and the uncovered steps
and blocks, and asks for a slide on each uncovered one or a sentence on an existing
slide that names it. What is still uncovered after the retry is listed in the face, as
section 5.1 describes, so the reviewer sees what the tour skipped.

The text is not checked. A type named in backticks is rendered as a link when the
changeset has it and as code when it does not, as narratives are rendered, and the
reviewer has the target in front of them to read the text against.

### 4.4 The cache and the events

An explainer is cached as narratives are, under the review cache keyed by the whole
request, which holds the prompt template and so changes when the prompt does. The
request holds no path of this machine. A second **Write it** on the same changeset reads
the cache and costs nothing; **Regenerate** bypasses it and stores the new explainer in
its place.

The server gains `POST /api/reviews/{id}/explainer`, with `regenerate` in its body,
which answers Accepted and writes in the background. Progress and the result go to the
page as `explainer` events carrying the run of the result they belong to, as narrative
events do, with `state` of `waiting`, `writing`, `done` or `failed`, and on `done` the
slides, the number of them that carry a figure, and the number dropped. The prompt holds the narratives, so a request made while
they are still being written waits for them: the server sends `waiting`, with the
narratives written so far and their total, and `writing` once it asks Claude. The events are replayed to a page that opens later, so a reload keeps
the explainer. A request for a review whose Claude is not available answers as the
narratives endpoint does. A changeset published as an artifact carries no explainer, as
it carries no narratives.

When a review gets a new result, as when the map switches from the skeleton to the
generated concept map, the explainer's blocks and edges name the old map, so the page
drops it and the face offers to write one for the new result. The old one stays in the
cache under its key.

## 5. In the viewer

### 5.1 The Explainer face

The face appears in the title's menu whenever the review has Claude, since unlike the other
faces it has something to offer before it has data. Before an explainer exists it shows
its paragraph and **Write it**. While one is being written it shows the spinner and the
message the server sends, and the reviewer can leave the face. While it waits for the
narratives it says so, with how many are written, such as "Waiting for Claude to finish
the narratives (12 of 30 written); the explainer is written from them.", and the count
follows the narratives as they arrive. Once one exists it lists
the slides, numbered, each with its title and a word for its target's kind, with the
current slide marked as the current step is. Its pinned head holds the arrows and
"3 of 9", as the path's head does. Clicking a slide opens it. Under the slides, **Not in the explainer** lists the
first-tier steps and changed blocks that no slide covers, each a link to its step or
block, and is absent when the explainer is complete. **Regenerate** sits at the bottom,
and a line under the list says how many slides were dropped when any were.

### 5.2 The card

The current slide's text shows in a card over the lower part of the main panel, whether
the panel shows the stage or the leaf view. The card holds the slide's number and title,
its text, its cites as links, the intents that cover its target when the changeset has
intents, the arrows, **Show again**, **Open on the path** when a step shares the target,
and a close. It is marked "written by Claude" as the summary is. Its width is the text's
measure, about eighty characters, centered, and its height is at most a third of the
panel, with the text scrolling inside it past that.

The card covers part of the panel, so the viewer fits a slide's target to the room above
it: a level fits to the stage less the card's height, a selected block or edge is brought
into that room, and a hunk is scrolled so that its header sits above the card. The map's
pan and zoom stay live under the card.

### 5.3 Moving among the slides

A slide opens its target as `openStep` opens a step's: the same trail, mount, arrival and
selection, and for a `level` target with blocks to bring out, the outline and dimming
that hovering a pattern uses, kept while the slide is current. A `type`, `pattern` or
`concept` target opens as the corresponding link does. A target the current changeset no
longer has, which can only happen after a map switch, is reported on the card and the
panel stays where it is.

While the card is shown, the right and left arrow keys move among the slides, Home and
End go to the first and last, and Escape closes the card. The stage's own use of the
arrow keys, which selects siblings, and of Escape, which goes up a level, yields while
the card is shown, as the help tip already takes Escape first. The arrows move from
the current slide, not from where the reviewer has wandered, so that looking around
never loses the place.

Clicking elsewhere never changes the slide. The card stays with its slide, so that a
reviewer who opens a diff from a slide about a decision still has the slide's words
beside it. **Show again** opens the target again.

### 5.4 What the explainer does not touch

Opening a slide marks nothing reviewed and changes no step of the path. The reviewed
marks and the path stay one state, as [attention.md](attention.md) section 6.2 has it,
and the explainer is a reader over that state. **Open on the path** is the one bridge:
it opens the step that shares the slide's target, where the step's files and
**Mark read** are. A slide whose target has a step of its own shows that step's read
state as a word on the slide's row in the face, so that a reviewer taking the tour a
second time sees what is finished. The check of section 4.3 names that step on the
slide's resolved target, as `step`: the first step of the path's first two tiers whose
target is the same catalog entry, such as the cycle whose closing edge the slide
targets.

## 6. Claude's part

Claude writes the whole explainer: its order, its titles and its text. Section 4.2
gives it the parts the review has already chosen as worth reading and its own
narratives of them, and section 4.3 holds every slide to the catalog. It is given no
rule for the order beyond the shape of section 3.1, since the order is the judgment the
feature exists for.

The model is a new member of `ClaudeModels`, `Explainer`, which `--model` replaces as it
replaces the others. The default is `claude-opus-5-5`, chosen by a trial before the
feature was built. Each model wrote the explainer of a pull request of 63 files and 10
commits that added a lexer, its token types and the generator's emission of their kinds,
from a hand-built prompt of about 20,000 tokens with the catalog of section 4.1, twice:
once with a count of five to twelve slides, and once with no count and the completeness
rule of section 3.1.

| Model | Slides | Words | First tier covered | Rejected | Time | Cost |
|---|---|---|---|---|---|---|
| `claude-opus-5-5`, counted | 12 | 893 | 3 of 7 | 0 | 44 s | $0.46 |
| `claude-sonnet-5-5`, counted | 11 | 599 | 3 of 7 | 1 cite | 16 s | $0.19 |
| `claude-opus-5-5`, complete | 31 | 1674 | 7 of 7 | 0 | 82 s | $0.64 |
| `claude-sonnet-5-5`, complete | 23 | 923 | 7 of 7 | 0 | 30 s | $0.19 |

Both were accurate on what the catalog showed, and both reached completeness once the
rule asked for it. Opus wrote the clearer explainer each time: its titles state claims,
its text gives the reason before the mechanism, and its slides on a reshaped signature
say what was so before and what is so now, where Sonnet's say what the code does. Opus
also went past the evidence twice, with a reading of a test change it could not explain
and a warning about generated files the trial's catalog had listed as changed, which the
rules of sections 4.1 and 4.2 now stop. Sonnet never did, but two of its slides said
little and read as written to satisfy coverage. The explainer is read once and slowly,
so the clearer text is worth three times the cost and the longer wait, and Sonnet stays
the choice for a reviewer who wants it faster. The request is abandoned after five
minutes, as a narrative request is.

## 7. Testing

No test calls Claude. The planner and the check are pure functions, so their tests sit
in the sub-second unit suite. They state:

- The catalog of a synthetic changeset lists each level and view with changed edges,
  each step of the path's first two tiers as a target, the hunks of the path's files
  with their indices, the added and moved types, and the new concepts, and lists a
  cycle as its closing edge.
- The check accepts one slide of each kind of section 3.2 that names a catalog entry,
  rejects a slide whose target is not in the catalog, whose brought-out block is not
  under its level or whose cite is outside the changeset, and keeps the order with gaps
  closed.
- Coverage: a first-tier step is covered by each of the ways section 4.3 lists and by
  nothing else, a changed block by a slide targeting anything inside it, and a complete
  explainer reports nothing uncovered.
- The writer requests the rejected slides and the uncovered steps once more, with their
  reasons and the slides that passed, drops the slides that fail twice, and reports the
  count and what stays uncovered.
- The request's key holds no path of this machine, a second write with the same key
  reads the cache, and a regeneration replaces what is stored.
- The check accepts a figure and keeps a box type the changeset does not have, rejects a
  figure whose arrow names a missing box and the other malformed or oversized figures of
  section 3.3, and rejects a third figure on the first answer and on the retry.
- The server accepts the request, sends `writing` and `done` events with the run, and
  replays them to a later watcher, driven by the canned `IClaude`; the `done` event and
  the log count the slides with a figure.

The viewer has no test suite, as the other designs note; a manual pass on the sample
and on `mock/changeset.js` with a canned explainer covers the card and the keys.

## 8. Cost

The trial of section 6 puts an explainer at under a dollar and under a minute and a
half with Opus on a change of 63 files, which is about half of what the narratives of
such a range cost with Sonnet in [review-workflow.md](review-workflow.md) section 7.
The cost grows with the first tier and the decisions on the path, which the path caps,
rather than with the change.

Once built, the explainer was measured on pull request 3 of this repository, which added
stated intents: 28 changed files, 23 changed edges, and 58 narratives in its prompt. Its
catalog held 124 entries and 7 first-tier steps.

| Request | Slides | Words | Covered | Dropped | Figures | Time | Cost |
|---|---|---|---|---|---|---|---|
| Explainer, `claude-opus-5-5` | 26 | 1456 | all | 0 | 1 | 83 s | $0.65 |
| Narratives, `claude-sonnet-5-5`, 7 of 10 requests not cached | — | — | — | 0 | — | 20 s longest | $0.41 |

The first answer passed its check and covered everything, so no retry was needed. With
the narratives in its prompt the explainer still cost under a dollar and took under a
minute and a half. Claude drew one figure, of the kinds of anchor and the path steps each
kind covers.

## 9. Changes to other documents

These land with the phase that makes them true:

- README, Viewer section: the Explainer face, the card and the keys, and the cost.
- review-workflow.md section 6: the explainer as a second layer over the narratives,
  with its own request and cache; section 7: the `Explainer` model and its
  measurement.
- attention.md section 6.2: **Open on the path** from a slide.
- vibecheck-spec.md section 6: the explainer is not a member of the changeset
  document, as narratives are not, and the note there says so.
- The agent guide is unchanged. An explainer is written for the reviewer from the
  change and asks nothing of the agent that wrote it.

## 10. Open questions

- Whether the explainer should be given the intents after all, attributed to the
  author in the text, since a peer presenting their own change would state them. The
  design keeps them out for the reason of section 4.2 and shows them beside the slide
  instead; a real review with both will say whether the card reads well that way.
- Whether to let the reviewer ask for one slide again with a reason, as a narrative can
  be regenerated on its own, or to send a question from the card and get a slide back.
  Both make the explainer a conversation, which may be what the feature wants to be,
  and both wait for the first to be used.
- Whether an explainer should be exportable as a Markdown walkthrough with links into
  the pull request, for the reviewer to post or keep. It is cheap once the slides exist,
  and it is an outward action, so it waits to be asked for.
- Whether the arrow keys should move among slides while the card is shown, as section
  5.3 has it, or leave the map's sibling selection alone and use Page Up and Page Down.
  Presentation convention says the arrows, and the card is the thing in front.
- Whether the figure form of section 3.3 is enough, or whether a sequence of calls wants
  a form of its own.

## 11. Phases

Each phase ends with the tool usable, and with tests that state its new behavior.

1. **One request, the face and the card.** `ExplainerPlanner`, `ExplainerWriter` with
   its check, retry and cache, the `Explainer` model measured again on the sample, the endpoint
   and events, the Explainer face with **Write it**, the list and **Regenerate**, the
   card, and the `level`, `edge`, `block`, `decision`, `hunk` and `type` targets with
   the arrows and the keys. This is the feature; the rest refines it.
2. **The other targets and the bridge.** The `pattern`, `concept` and `tests` targets,
   blocks brought out on a level, **Open on the path** and the read state on a slide's
   row, the intents beside a slide, and the drop and offer on a new result.
3. **Figures.** The figure form, its drawing in the stage, and the prompt's leave to use
   one, with a count of how often it does.
