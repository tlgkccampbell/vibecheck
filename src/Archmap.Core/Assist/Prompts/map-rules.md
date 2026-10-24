You are writing the architecture map of a C# solution. A reviewer uses the map to read large changesets, many of them written by language models, at the level of concepts: which components changed, and how the dependencies between them moved. The names and purposes you write are what the reviewer sees, so write them for a reader who has never seen this code.

## What a map is

A map is a tree of nodes. Every node has an identifier, a short display name and a one-sentence purpose.

- An identifier is one or more segments of lowercase letters, digits and hyphens, joined by dots, such as `billing` or `billing.ledger`. A node's parent is its identifier without the last segment, and every parent must itself be a node. A node with one segment is top-level.
- A leaf is a node with `bind`, a list of type patterns. Only leaves bind code. A container is a node without `bind`, and it must have at least one child.
- Every type in the listing must be bound by exactly one leaf. A type no leaf binds lands in an automatic leaf named `unsorted`, which the reviewer sees as a defect.

## Type patterns

A pattern matches the fully qualified name of a top-level type: its namespace and its name joined by a dot, such as `Example.Billing.Core.PaymentService`. Nested types follow the type that contains them.

- `*` matches any characters except a dot. `Example.Billing.Core.*` matches the types declared directly in that namespace.
- `**` matches any characters, dots included. `Example.Billing.Core.**` matches those types and every type in a namespace beneath it.
- A pattern without wildcards names one type exactly. Use exact names for the members of a concept that no namespace follows.
- A pattern may be qualified by an assembly, as in `Example.Shared::Example.Shared.**`, to match only that assembly's types.
- When several patterns match a type, the most specific one wins, where specificity is the number of characters that are not wildcards. An exact name therefore takes a type away from any wildcard.

## What a good map looks like

- The top level has four to nine nodes, each a component the reviewer would name when describing the system.
- The tree is your own. It does not have to follow projects or namespaces. A leaf may gather types from several namespaces and projects, and a container may hold leaves from anywhere. Bind by namespace pattern where a namespace follows the concept, and by exact type name where it does not.
- Leaves hold ten to forty types where the code allows it. Views regroup leaves, so a leaf that is too large cannot be cut by a view, and a leaf of one or two types is noise.
- Place each type where its dependencies put it. A type placed by its name in a component that the code puts on the other side of a boundary shows up as a dependency cycle between components.
- Each purpose is one sentence that says what the node is for, in plain words.
- Give every top-level node a category, and give a deeper node a category only when its role differs from its parent's. The categories are:
  - `entry`: where control enters the system, such as command lines, hosts, servers and endpoints.
  - `feature`: user-facing features built on the core, such as commands, editor features and reports.
  - `domain`: the core model, meaning the concepts, representations and data structures the rest of the system works on.
  - `analysis`: working out facts about the model, such as inference, checking and rules.
  - `transform`: processing that turns one representation into another, such as pipelines, compilation and generated output.
  - `integration`: the outside world, such as storage, file systems, configuration, networks and external services.
  - `foundation`: shared infrastructure with no domain meaning, such as utilities, diagnostics and primitives.
  - `tooling`: build-time and developer tooling, such as source generators and test support.

## Views

A view regroups the leaves under one level into named groups, so that the reviewer can see the system by concern rather than by ownership. Propose one view of the top level, with the identifier `by-concern` and the name "By concern", whose groups say what the leaves are for. Its `level` is `root`. Every leaf of the map must appear in exactly one of its groups, and each group lists leaf identifiers, not containers. Leave the views empty if the system is too small for a second grouping to say anything.

## Fixed parts

These identifiers are already taken by external assemblies, so no node may use them: {{externals}}.

These patterns are excluded from the map and appear nowhere in the listing: {{exclude}}.

## The listing

The listing below groups the solution's types into units. A unit is a type together with the helpers that only it uses. Under each `=== Namespace ===` header, a unit starts with its head type's name, and the type's full name is the header's namespace, a dot, and that name. The number in brackets is how many types the unit holds, and the line starting with `+` names the others, which are in the same namespace unless the unit says it spans several. The quoted line is the head type's documentation summary. The lines starting with `->` and `<-` name the units it references most and is referenced by most, with counts.

Bind every type: a unit's types belong together, so a namespace pattern or the exact names of all its members places the whole unit.
