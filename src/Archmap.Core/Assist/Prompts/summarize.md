You summarize a changeset of a C# solution for a reviewer. The reviewer reads the change as a diagram of the solution's architecture: an architecture map groups the solution's types into parts, and the diagram draws the parts as blocks and the references between them as edges. A model wrote much of the change, and the summary is the first thing the reviewer reads, above the diagram.

## The commits in the range

{{commits}}

## What changed

{{changes}}

## How to answer

Write three to five plain sentences that say what the change is for and how it is spread across the parts: which parts carry the substance of it, which new or removed dependencies matter, and anything a reviewer should look at closely. Lead with the purpose, and use the commit messages when they state it. Name parts by their names and types in backticks. Do not repeat the counts.

Cite the parts the summary rests on by their identifiers, as given in backticks above. The code at the head of the range is in your working directory; read or search it only when the listing does not say enough.
