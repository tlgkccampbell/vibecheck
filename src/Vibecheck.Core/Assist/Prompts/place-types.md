You maintain the architecture map of a C# solution. A reviewer uses the map to read changesets at the level of concepts. A change added types that no leaf of the map binds, so they land in an automatic leaf named `unsorted`, which the reviewer sees as a defect. Place each of them.

A leaf is a node of the map that binds types. Each leaf below is given with its identifier, its name, its purpose and a few of the types it already holds.

{{leaves}}

These are the types to place, by full name:

{{types}}

A type that is an instance of a pattern, a role that several types play, such as a message consumer or a controller, names the pattern and the instance most like it, with the leaf that holds that instance. Types that play one role often sit with their peers, so that leaf is a strong hint, but it is not a rule: a type belongs where the concept it serves belongs.

The listing below shows the units these types belong to, for context. A unit is a type together with the helpers that only it uses, so a type usually belongs with the rest of its unit. Under each `=== Namespace ===` header, a unit starts with its head type's name, and the type's full name is the header's namespace, a dot, and that name. The line starting with `+` names the unit's other types. The lines starting with `->` and `<-` name the units it references most and is referenced by most, with counts; most of those are already placed, and they say where the unit belongs.

{{listing}}

Answer with one placement for each type to place: its full name, exactly as listed above, and the identifier of the leaf it belongs in. Place a type in an existing leaf whenever one fits. When none does, add a new leaf: give it an identifier whose parent is a container or nothing at all for the top level, a short name and a one-sentence purpose, and then place types in it like any other leaf. A view must list every leaf under its level, so for each view whose level holds the new leaf, also name the group of that view the leaf joins. An identifier is lowercase letters, digits and hyphens, in segments joined by dots, and its parent is the identifier without its last segment.
