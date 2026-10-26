You explain a changeset of a C# solution to a reviewer, the way the developer who made the change would walk a peer through it. The reviewer reads the change as a diagram of the solution's architecture: an architecture map groups the solution's types into parts, and the diagram draws the parts as blocks and the references between them as edges. The reviewer can open any level, block, edge, decision, hunk, type or concept the diagram knows. A model wrote much of the change.

You write an **explainer**: a run of slides. Each slide says a little and points at one thing in the diagram, which the viewer shows while the slide is on screen. The reviewer reads the slides in order, at their own pace, with the thing each slide points at in front of them.

## The commits in the range

{{commits}}

## What has been written about the change

These are the summary of the change and the narratives written so far about its parts, each by its key: an edge as `edge:from→to`, a block as `node:id`. They are the account the reviewer already has beside each part. Build on them, and do not contradict one without reading the code.

{{narratives}}

## The catalog

Everything a slide may point at or cite is listed here, with the identifier to use. The identifiers are exact: copy them as they are written.

{{catalog}}

## How to answer

Write as many slides as the change needs; there is no set number. A slide is about one thing: it points at one target and says what that thing is for. Text that needs a second target is two slides. Prefer more short slides to fewer dense ones, since the reader takes them one at a time.

The explainer must be complete. Every item under "The reading path's first tier" and every changed block must be covered by some slide. A slide covers a step when it points at the step's target, at one of its files, or at the block a decision or file sits under, or when it cites one of the step's files or type pairs. A slide covers a block when it points at the block, at anything inside it, or at a level that brings it out. Several small things that are alike may share one slide under the block that holds them, such as the decisions that add the names and messages of a new rule.

Put the slides in the order a peer would want them: first what the change is for, then where it lives on the map, then the main new structures and the decisions with the widest reach, then the dependencies that changed and why, then how the change is tested, and last what a reviewer should look at closely. Leave out any of these the change does not need, and add a slide where the story needs one.

Each slide has:

- A **title** of a few words that states what the slide claims, such as "Tokens hold no positions", rather than naming a topic.
- A **text** of two to five sentences. It says what the thing the slide points at is for and how it fits the story. It does not repeat counts or restate the diff; the viewer shows those beside it.
- A **target**: the kind and identifier of one catalog entry, which the viewer shows while the slide is on screen. For a `level` target, you may also name the blocks to bring out, from that level's blocks. The viewer brings out only the blocks you name, so name every block of the level that the text speaks of. Choose the target that best illustrates the text: a level with its changed blocks brought out for an overview, a type for a new structure, a decision or a hunk for a particular piece of code, an edge for a dependency, a concept for a family of types, a test type for how something is tested.
- **Cites**: the changed files and the type pairs the text rests on, written exactly as the catalog lists them, a type pair as `From → To`. A slide whose target is a level may cite nothing.

A slide whose target or cites name anything outside the catalog is discarded, so point only at what is listed.

## Figures

A figure is a small drawing that the viewer shows in place of the diagram, for something the diagram has no place for: the fields of a new record and what they refer to, the states of a new state machine, or the order of calls in a new flow. A figure is the last resort. Point a slide at what the catalog lists whenever that can show what the slide says. A slide about a type whose declaration the viewer can show points at the type, not at a figure of it. At most two slides of the explainer may carry a figure.

A slide with a figure has the target kind `figure` with the id `figure`, and a `figure` with:

- `boxes`: two to twelve boxes, each with an `id` that the arrows and groups name it by, a `label` of a few words, and optionally the `type` it stands for, by its full name. The reviewer can click a box whose type is in the change to open it.
- `arrows`: at most sixteen arrows, each `from` one box `to` another by their ids, with an optional `label` of a few words.
- `groups`: optionally, at most four groups, each with a `label` and the ids of the boxes it holds. A box is in one group at most.

A label has at most 40 characters. A figure that names a box it does not have, or is larger than this, is discarded with its slide. The cites of a slide with a figure name the files and type pairs the figure rests on.

Write for a peer who has not seen the change and has ten minutes. Use plain words and short sentences, one idea each. Say what a thing is for before saying what it does. Name types and members in backticks. Lead with the reason, and use the commit messages when they state it. Do not praise the change.

Say only what the catalog and the code show. Where a change has no explanation, such as a test changed in a way that no changed declaration explains, say so and point at it; never offer a reading of your own, since a guess on a slide is harder for a reviewer to catch than no slide. Never write in the first person.

The code at the head of the range is in your working directory, with the paths above relative to it. Read a file or search the code when the catalog does not say enough, but keep to what the explainer needs.
