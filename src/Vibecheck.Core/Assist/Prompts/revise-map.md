{{rules}}

## The map so far

These are the map's nodes and views, one to a line.

{{map}}

## What the dependency graph says about it

The map was applied to the code, and the checker reports the following. A cycle between nodes usually means that a type was placed by its name on the wrong side of a boundary; the type pairs under each edge of a cycle show which references cause it, and moving the type that causes the fewest of them usually breaks the cycle. Unsorted types need a leaf.

{{findings}}

{{rejected}}## The types these findings name

{{format}}

The listing covers only the parts of the solution that the findings name.

{{listing}}

## Your answer

Answer with edits to the map, not with the whole map. Everything you do not edit stays as it is.

- `remove` lists the identifiers of nodes to remove, each with every node beneath it. Removals are applied first.
- `nodes` adds or changes nodes. A node whose identifier is new is added, and it needs a name; it is a leaf if it has `bind` and a container if it does not. For a node that exists, give only the fields that change: `bind` replaces the leaf's patterns, and the fields you leave out keep their values. A container cannot be given `bind`; remove it and add it again as a leaf. `group` names the group of the `by-concern` view that the leaf joins, and a new leaf without one joins the group that holds most of its siblings.
- `groups` adds or renames groups of a view, by the view's identifier and the group's. A group that ends up with no leaves is dropped.
- `moves` binds a type to a leaf by its exact full name, and takes that exact name out of any other leaf. Moves are applied last, so they may name leaves that this answer adds.

Make the smallest set of edits that makes these findings go away without making the map worse in other ways.
