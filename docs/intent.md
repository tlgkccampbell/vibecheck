# Stated intent (design)

This document describes how the coding agent that writes a change states why it made
the change's main choices, in a fixed section of the pull request description, and how
a review of the pull request reads that section, ties each statement to the parts of
the changeset it names, and shows the reviewer where the change does something large
that no statement covers. It builds on the map and facts of
[vibecheck-spec.md](vibecheck-spec.md), the narratives of
[review-workflow.md](review-workflow.md), the description of
[pull-requests.md](pull-requests.md), the reading path of [attention.md](attention.md)
and the guide for coding agents, and it lists, in section 9, the changes it makes to
those documents.

All three phases of section 10 are built. The reading path has no step for a new
concept yet, so of the surprises of section 4.2 only new edges, which include edges
out of a foundation or domain block, new cycles and decisions are covered or flagged.

The design rests on one principle. An agent's statement of intent is a claim, not
evidence. It is the one account of why a change was made that comes from the author,
and the reviewer wants it, but a model can explain a choice after the fact as
fluently as before it. The review therefore never shows a statement as true. It shows
what the statement names, whether the change touches what it names, and which large
parts of the change no statement names at all. The last is the most useful thing the
feature produces, because an unexplained new dependency is exactly what a reviewer of
model-written code needs to find.

The agent writes its statements without running a review. Everything it needs is in
the guide that `vibecheck guide` prints and in `vibecheck.json`, and the one tool it
may run to check its text, `vibecheck intent`, reads the map and the git diff without
building the solution.

The word *decision* already means a hunk that changes a declaration
([attention.md](attention.md) section 4.1), and a hunk's `Reason` already means why it
starts out reviewed. This document therefore calls the author's statements
**intents**, and never uses either word for them.

## 1. Goals

- A coding agent that follows the guide states the intent of every large choice in a
  change, in a form that reads well on GitHub and that the review can parse, without
  running a review.
- A reviewer sees the author's intent for an edge, a block or a step beside Claude's
  narrative for it, marked as the author's.
- Each intent shows which of the things it names the change actually touches.
- Where the stated intent and the change disagree, the viewer flags it: an intent
  that names nothing the change touches, an anchor that names nothing at all, and a
  surprise on the reading path that no intent covers.
- Nothing in this design calls Claude. The parsing and the resolution are pure
  functions of the text and the changeset.

Non-goals for this design: any finding that an intent is false or a choice is wrong,
extracting intents from free text with Claude, requiring intents, and intents outside
pull requests. A change with no intent section reviews exactly as it does now, and a
review of a range or of uncommitted work has no intents.

## 2. The reviewer's path

1. The changeset opens on the map as it does now. The summary under the top bar gains
   a line such as "5 intents from the description · 2 surprises with no stated
   intent", which opens the **Intent** list in the details panel.
2. Selecting an edge or a block shows, above Claude's narrative, each intent that
   names it: the claim, its why, and what it was chosen instead of.
3. Each anchor of an intent is a link to what it names, with a mark that says whether
   the change touches it. An intent none of whose anchors the change touches carries
   a warning flag, "names nothing this change touches".
4. On the reading path, a first-tier step that an intent covers shows the intent's
   claim under its signal. One that no intent covers shows "no stated intent".
5. The Conversation tab shows the description as it does now, intent section included,
   since GitHub renders it.

## 3. The format

### 3.1 The section

Intents are written in Markdown, in the pull request description, in a section whose
heading is the word `Intent`, at any heading level. The section ends at the next heading of the same or a higher level,
or at the end of the text. Each top-level list item in the section is one intent.

```markdown
## Intent

- **Refunds post as negative ledger entries.** `billing.ledger`, `billing.api → billing.ledger`
  Why: the ledger stays append-only, so the monthly reconciliation needs no new case.
  Instead of: a `Refund` aggregate in a new `billing.refunds` leaf.
- **`RefundIssued` is published from the API, not the ledger.** `Orchard.Billing.Api.RefundsController`
  Why: the ledger must not reference messaging, which `notifications` owns.
```

An intent has these parts:

- The **claim**, the bold text that opens the item. It says what was chosen, in one
  sentence.
- The **anchors**, the inline code spans that follow the claim on its first line,
  separated by commas. Section 3.2 says what an anchor may name.
- The **why**, the text after `Why:` on a later line of the item.
- The **alternative**, the text after `Instead of:` on a later line. It is optional.

Any other text in the item is kept as a note and shown under the why. The parser is
tolerant. A claim without bold is the item's first sentence, `->` is read as `→`, and
the labels match without regard to case. An item with no claim is reported by
`vibecheck intent` and skipped by the review. An item with no anchors is kept, and the
review shows it only in the Intent list, since it names nothing to attach it to.

The section is visible Markdown rather than a hidden comment or a JSON block, because
the reviewer who reads the pull request on GitHub wants the same account, and an agent
writes prose more reliably than it writes a schema.

### 3.2 Anchors

An anchor is one of:

- A **node**, by its identifier in the map, such as `billing.ledger`. It names a leaf
  or a container. An external's identifier names the external.
- An **edge**, as two ends joined by `→`, such as `billing.api → billing.ledger`.
  Each end is a node or external identifier, or a type. An edge between two types names
  that type pair.
- A **file**, by its path from the repository's top, such as
  `src/Orchard.Billing/Api/RefundsController.cs`. A span that contains a `/` or ends in
  `.cs` is a file.
- A **type**, by its full name, such as `Orchard.Billing.Api.RefundsController`, or by
  its simple name when exactly one top-level type in the changeset has it.

A span that is none of these is not an anchor. It stays in the claim as code, so a
claim may name a member in backticks without the parser mistaking it for an anchor.

Nodes and edges between nodes resolve only against the map the review draws with.
When that map is not the repository's own, such as the generated map of
[review-workflow.md](review-workflow.md) section 4.4, node identifiers resolve to
nothing, and files and types still resolve. The guide therefore asks for a file or a
type on every intent that is about particular code, and for node identifiers only in
a repository with a `vibecheck.json`.

### 3.3 Where intents are read

A review reads intents only from the description of the pull request it reviews,
using the description's Markdown, `body`, which the pull request query fetches beside
`bodyHTML`. Commit messages are not searched. Reviewing pull requests is the tool's
main use, and one source means one place for the agent to write and for the reviewer
to look. A review of a range never looks for a pull request that matches it, since a
reviewer who wants the pull request reviews the pull request.

## 4. Resolution

### 4.1 Anchors against the changeset

`IntentResolver.Resolve` in `Vibecheck.Core` is a pure function of the parsed intents
and the changeset, as `ReadingPath.Plan` is. It resolves each anchor to what it names
and records whether the change touches it:

| Anchor | Touched when |
|---|---|
| Node | The node, or a leaf under it, has a changed file. |
| Edge between blocks | Some level or view draws a changed edge between those two blocks. |
| Edge between types | The type pair is in the evidence of a changed edge. |
| File | The file is changed. |
| Type | A file that declares the type is changed. |

An anchor is `touched`, `untouched` or `unknown`, the last when it names nothing in
the map or the code. An intent is **unbacked** when it has anchors and none of them is
touched. Unbacked intents and unknown anchors are facts on the intent, not errors.

### 4.2 Coverage

A **surprise** is a step in the first tier of the reading path
([attention.md](attention.md) section 6.1) of one of these kinds: a new edge, a new
cycle, a new edge out of a foundation or domain block, a decision, or a new concept.
A surprise is **covered** by an intent that has a touched anchor of these kinds:

| Surprise | Covered by |
|---|---|
| A new edge | The edge itself, an edge between blocks inside its two ends, or a type pair in its evidence. |
| A new cycle | An anchor that covers any edge of the cycle. |
| An edge out of a foundation or domain block | The same as a new edge. |
| A decision | The file that holds the decision's hunk, or a type it declares. |
| A new concept | One of its types or files. |

A node anchor covers no surprise. A node is too broad to say that the author meant a
particular edge, and an intent that names `billing` would otherwise cover every
surprise in billing. The steps for tests and patterns are not surprises in this
sense, since their signal is a departure from a norm rather than a choice.

Coverage does not reorder the path. A step carries the intents that cover it, and a
surprise that none covers carries the flag. Putting uncovered surprises first would
make the order depend on the description, which the author can edit while the
reviewer reads. It would also trust coverage, which shows only that an intent names a
surprise, not that its reason holds, and an author could push surprises down by
naming their files. The path therefore stays a function of the changeset alone, and
the flag is how an uncovered surprise stands out.

### 4.3 In the changeset

The changeset gains `Intents`, the list of resolved intents, each with its claim, why,
alternative, note and anchors, and each anchor with its text, kind, the
identifier of what it resolved to and its state. Path steps gain `Intents`, the
indices of the intents that cover them, and `Uncovered`, true for a surprise that none
covers.

The description changes without a commit, so the intents are not part of the cached
changeset. The server resolves them when the review loads, and again when a `pull`
event carries a description that differs, and sends an `intents` event with the list
and the coverage of the path. Resolution reads only the changeset, so it costs
nothing worth caching. A changeset published as an artifact carries the intents it
was published with.

## 5. In the viewer

### 5.1 The details panel

An edge or a block that some intent names shows an **Author's intent** section above
its narrative, listing each such intent. An intent shows its claim, its why, its
alternative and note when present, and its anchors as links that open what they name,
each marked touched, untouched or unknown. The section is labeled as the author's, in
the same way that narratives are labeled as written by a model, and it is never merged
into the narrative's text.

The **Intent** list, opened from the summary, shows every intent in order with the
same parts, and after them the surprises that no intent covers, each a link to its
step.

### 5.2 Discrepancies

The viewer flags three discrepancies between the stated intent and the change, each
with the warning mark the viewer already uses for the `unsorted` leaf and failed
checks:

- An unbacked intent, flagged on the intent wherever it is shown.
- An unknown anchor, flagged on the anchor, with what it was read as.
- An uncovered surprise, flagged on its path step and listed in the Intent list.

The summary's intent line counts the discrepancies, such as "5 intents · 1 names
nothing changed · 2 surprises with no stated intent". The flags are facts about what
the description names and what the change touches. They never say that an intent is
false, and a discrepancy that needs reading the code to see, such as an intent that
says the ledger no longer references messaging while it still does, is left for the
reviewer, who has the intent and Claude's narrative side by side.

### 5.3 The path

A covered step shows the claim of its first covering intent under its signal, with a
count when more cover it. An uncovered surprise shows "no stated intent" under its
signal. When the changeset has no intents at all, the path shows neither, since a
change with no intent section is not missing one.

### 5.4 Rendering

Intent text comes from the pull request description, which anyone who can open a pull
request writes. The viewer renders the claim, why,
alternative and note as text, with inline code spans as code and nothing else
interpreted, and never as HTML.

## 6. Claude's part

Claude takes no part. Narratives are not given the intents, and their prompts are
unchanged. A narrative is Claude's account of why a part changed, read from the code
and the commit messages, and the intent is the author's. Shown side by side, two
accounts written apart let the reviewer see where they differ. A narrative written
from the intent would mostly repeat it, and would hide exactly the difference the
reviewer is looking for. It would also make every edit to the description rewrite
the narratives it reaches.

Claude never writes, extracts or judges intents.

## 7. The coding agent's part

### 7.1 The guide

The guide for coding agents gains a section, after the steps for keeping the map in
sync, on stating intent. It says:

- What deserves an intent: a new or removed component; a new dependency between
  components; a new cycle, with why it is acceptable; a new package or project
  reference; a change to a public signature that other code calls; a departure from
  what the other instances of a pattern do; and any choice between real alternatives
  that a reviewer could ask about. Edits that follow from an intent need none of
  their own.
- What does not: restating the diff, listing files, and explaining an edit the code
  makes obvious. Three good intents are better than ten.
- The format of section 3, with an example, and the anchors to use: node identifiers
  and edges from `vibecheck.json` when the repository has one, and a file or type on
  every intent about particular code.
- Where to write it: in the pull request description. When the agent does not open
  the pull request itself, it gives the section in its report so that whoever opens
  the pull request can paste it.
- That a claim must say what was chosen, a why must give the reason and not repeat the
  claim, and an alternative must be one the agent actually weighed.
- That the agent may run `vibecheck intent` on its text before opening the pull
  request, and that it should not run a review.

The guide's one-sentence request for a summary of the map change, now in its section
3.5, becomes an intent anchored to the nodes that moved.

The guide covers more than the map, so its file is renamed from
`docs/keeping-the-map-in-sync.md` to `docs/agent-guide.md`, with the title
"vibecheck for coding agents". The embedded resource keeps the name `guide.md`.

### 7.2 The note

The note that `init` prints and the review workflow adds to `CLAUDE.md` or
`AGENTS.md` currently sends agents to the guide only before a change that moves
types. It becomes:

```markdown
## vibecheck

This repository's changes are reviewed with vibecheck, which draws each change on the
architecture map `vibecheck.json`. Before finishing a change, run `vibecheck guide` and
follow it. It says when the map needs updating, and how to state the intent of the
change in the pull request description.
```

`AgentNote.IsPresent` still looks for `vibecheck guide`, so a file with the old note
is not given the new one beside it. No repository carries the old note yet, so
nothing replaces it.

### 7.3 `vibecheck intent`

```
vibecheck intent [<file>] [--map <vibecheck.json>] [--root <dir>] [--base <rev>]
```

The command reads Markdown from the file, or from standard input when none is given,
such as a draft description an agent is about to pass to `gh pr create --body-file`.
It prints each intent it parses with its anchors, and for each anchor whether it
names a node of the map, a file at the working tree, or neither. With `--base`, it also
says which file anchors are changed since that revision, from `git diff --name-only`.

It does not build or analyze the solution, so it finishes in well under a second.
Type anchors and edges between types are therefore reported as unchecked, since
only the analysis knows the types. It exits with code 1 when the text has no intent
section or an item has no claim, and with 0 otherwise, reporting unknown anchors as
warnings, as `check` reports a stale map.

`IntentReader.Parse` is the one parser of the format, used by this command and by
the review.

## 8. Testing

No test calls Claude or GitHub. The parser and the resolver are pure functions, so
their tests sit in the sub-second unit suite. The tests state:

- The parser finds the section at any heading level, ends it at the next heading of
  its level or higher, reads the claim, anchors, why, alternative and note of each
  item, reads a claim without bold as the first sentence, reads `->` as `→`, keeps an
  item without anchors, and skips an item without a claim.
- A code span that is neither a node, an edge, a file nor a type stays in the text and
  is not an anchor.
- Each kind of anchor resolves to touched, untouched or unknown on a synthetic
  changeset, a node identifier under a generated map is unknown, and an intent with no
  touched anchor is unbacked.
- Each kind of surprise is covered by the anchors section 4.2 lists and not by a node
  anchor, and a changeset with no intents flags no step.
- A review of a pull request reads intents from the description's `body`, and a
  review of a range has none.
- Each discrepancy of section 5.2 is reported on a synthetic changeset, and a
  changeset with no intents reports none.
- `vibecheck intent` exits 1 without a section, reports an unknown node and an absent
  file, and with `--base` marks changed files, on a `TempRepo`.
- The note names the guide and the pull request description, and `AgentNoteTests`
  still find an old note present.

## 9. Changes to other documents

These land with the phase that makes them true:

- The guide is renamed to `docs/agent-guide.md` and gains the section of 7.1. Its
  section 3.5 and its checklist ask for intents. The `EmbeddedResource` in
  `Vibecheck.Cli.csproj`, the README and spec section 7 follow the rename.
- The note of section 7.2 replaces the current one in `AgentNote.Text`, and the README
  quotes it.
- Spec section 6 gains `Intents` and the path steps' `Intents` and `Uncovered`. Spec
  section 7 gains `vibecheck intent`.
- pull-requests.md section 6: the description is read as Markdown too, for its intent
  section. The pull request query fetches `body`.
- attention.md section 6.2: covered and uncovered surprises on the path.
- README: stated intent, for reviewers and for the agents that write changes.

## 10. Phases

Each phase ends with the tool usable, and with tests that state its new behavior.

1. **The format and the agent's side.** `IntentReader`, `vibecheck intent`, the
   guide's new section and rename, and the new note. Agents can start writing intents
   before the review reads them, and the command keeps their format honest.
2. **Intents in a review.** The `body` field, `IntentResolver`, the `intents` event,
   the details panel's section, the Intent list, and the flags for unbacked intents
   and unknown anchors.
3. **Coverage.** Coverage of surprises, the path's covered and uncovered steps, their
   flags, and the summary's counts.
