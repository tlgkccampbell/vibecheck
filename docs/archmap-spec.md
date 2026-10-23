# Architecture map specification (v0 draft)

This document defines the architecture map, the conformance rules that bind it to the
code, and the facts the checker produces for the changeset viewer. The target is C#
solutions on .NET 10 or later.

The unit of partition is the type. The unit of change is the file. A type belongs to
exactly one leaf of the map, and a file belongs to every leaf whose types it declares.
Where a declaration belongs is a question the language answers per type, through its
namespace, and the map binds that answer. What changed is a question git answers per
file, and the viewer shows that answer under the leaves it touches.

The map is a specification, not documentation. Everything the viewer draws is a fact
the checker has established, and every dependency in the code must correspond to
something the map permits. The two cannot drift silently in either direction.

> Status: this revision moves the unit of partition from files to types. The
> implementation in this repository still partitions by file and will be brought to
> this revision next.

## 1. Files

| File | Purpose | Reviewed as |
|---|---|---|
| `archmap.json` | The semantic map: nodes, bindings, edges, policies. | A structural change to the system. |
| `archmap.layout.json` | Stored node positions and sizes per view. | Presentation only. Never affects conformance. |

Layout is stored separately so that moving a box on the diagram never appears as a
structural change, and so that a structural change never has to carry coordinates.
A node missing from the layout file is placed by a deterministic rule and drawn
with a "new" treatment until a position is stored for it.

### 1.1 Format

Both files are JSON and are validated against a JSON Schema that ships with the
tool. JSON is chosen because the map is written by tools and language models far
more often than by hand, and for those authors an unambiguous grammar with exact
validation errors matters more than compactness.

`archmap fmt` rewrites both files in canonical form, and `check` fails on a file
that is not canonical. Canonical form is line-oriented so that every diff hunk is
one self-contained fact:

- Each node, external, edge and policy entry occupies exactly one line.
- Nodes and externals are objects keyed by identifier, sorted by identifier.
- Edges are sorted by `from`, then `to`.
- Keys within a record appear in the order the schema lists them.
- Two-space indentation, UTF-8, LF line endings, no trailing whitespace.

There are no comments. Anything worth saying goes in a checked field such as
`purpose` or `note`, because a comment is the one part of the file the checker
cannot hold to account.

Appending a record to a list also touches the previous line, because of JSON's
comma rule. This is cosmetic. The viewer presents map changes structurally, as
records added and removed, never as text.

## 2. Map schema

```json
{
  "version": 0,
  "solution": "src/Example.sln",
  "nodes": {
    "auth": { "name": "Auth", "purpose": "Identity, sessions and authorization decisions." },
    "auth.core": { "name": "Core", "bind": ["Example.Auth.**"] },
    "billing": { "name": "Billing", "purpose": "Owns invoices, payments and the ledger.", "edges": "declared" },
    "billing.api": { "name": "API", "bind": ["Example.Billing.Api.**"] },
    "billing.core": { "name": "Core", "bind": ["Example.Billing.Core.**"] },
    "billing.ledger": { "name": "Ledger", "bind": ["Example.Billing.Ledger.**", "Example.Billing.Core.Postings.*"] },
    "shared": { "name": "Shared", "purpose": "Primitives with no business meaning." },
    "shared.kernel": { "name": "Kernel", "bind": ["Example.Shared::Example.Shared.**"] }
  },
  "externals": {
    "stripe": { "name": "Stripe SDK", "bind": ["Stripe", "Stripe.*"] }
  },
  "edges": [
    { "from": "auth", "to": "shared" },
    { "from": "billing", "to": "auth", "label": "Authorizes payment operations" },
    { "from": "billing", "to": "shared" },
    { "from": "billing.api", "to": "billing.core" },
    { "from": "billing.core", "to": "billing.ledger" },
    { "from": "billing", "to": "stripe", "label": "Charges cards", "payload": "ChargeRequest" }
  ],
  "policies": {
    "root": { "edges": "declared", "externals": "observed" },
    "forbid": [ { "from": "shared", "to": "*" } ],
    "acyclic": ["root", "billing"]
  }
}
```

### 2.1 Nodes

The node list is flat. An identifier is a sequence of segments matching
`[a-z0-9-]+` joined by dots, and a node's parent is the identifier with its last
segment removed. A node with one segment is top-level. The hierarchy is therefore
visible in a single line of a diff, and adding, moving or removing a node is a
one-record change.

A node has a display name, an optional one-line purpose, and either `bind` or
nothing. A node with `bind` is a leaf. A node without `bind` is a container and
must have at least one child.

Only leaves bind code. A container's extent is the union of its children's extents,
so containment holds by construction and needs no rule.

The map's tree is the map's own. Node identifiers are not namespaces, a leaf may
gather types from several namespaces and several projects, and a container may
hold leaves drawn from anywhere. The skeleton mirrors namespaces only as a starting
point.

### 2.2 Type patterns

A leaf binds types by patterns over their fully qualified names. The name a pattern
is matched against is the namespace and the top-level type name joined by a dot,
without generic arity: `Example.Billing.Core.PaymentService`. Nested types are not
matched; they follow the top-level type that contains them.

- `*` matches any sequence of characters other than a dot, so `Example.Billing.Core.*`
  matches the types declared directly in that namespace.
- `**` matches any sequence of characters including dots, so `Example.Billing.Core.**`
  matches those types and every type in a namespace beneath it.
- Wildcards may appear anywhere, so `Example.Flow.Register*` matches by type name
  within a namespace.
- A pattern may be qualified by an assembly name and `::`, as in
  `Example.Shared::Example.Shared.**`, and then matches only types declared in that
  assembly. An unqualified pattern matches in every assembly of the solution.

When several leaves' patterns match a type, the most specific pattern wins, where
specificity is the number of literal characters in the pattern, counting an assembly
qualifier. `Example.Billing.Core.Postings.*` therefore takes the postings types away
from `Example.Billing.Core.**` without any exclusion syntax. Two patterns of equal
specificity that both match a type leave it ambiguous, which rule 1 reports.

### 2.3 The `[Archmap]` attribute

A type may name its leaf directly:

```csharp
[Archmap("billing.core.payments")]
public sealed partial class CapturePayment { … }
```

The attribute is recognized by its full metadata name, `Archmap.ArchmapAttribute`,
wherever it is declared, so a repository declares it itself and takes no dependency
on the tooling:

```csharp
namespace Archmap;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface | AttributeTargets.Enum | AttributeTargets.Delegate)]
internal sealed class ArchmapAttribute(string leaf) : Attribute
{
    public string Leaf { get; } = leaf;
}
```

The attribute assigns membership; it does not create leaves. The name it carries
must be a leaf of the map, or rule 1 fails for that type with no mechanical
proposal, since choosing a component is a judgment. An attribute on any part of a
partial type applies to the type, so the generated half of a type follows its
hand-written half with no further rule. An attribute on a nested type is a failure
under rule 1, since nested types follow their container.

The convention is that patterns carry the rule and attributes carry the exceptions:
the type that lives in the wrong namespace for historical reasons, the flat
namespace that curation split by meaning, the component that straddles namespaces.
The facts report how many types each leaf binds by attribute, so that a namespace
whose patterns no longer describe it is visible as a number.

### 2.4 Binding precedence

A type binds to the leaf named by its `[Archmap]` attribute; otherwise to the leaf
whose pattern matches it most specifically; otherwise it is unmapped. Explicit beats
pattern. A type bound by attribute to one leaf while a pattern of another leaf
matches it is not a conflict; the attribute is the override that patterns exist to
be overridden by.

### 2.5 Edge policy

Each container carries an `edges` policy that governs edges among its children. The
`root` policy applies among top-level nodes. The default is `observed`.

- `declared`: every observed dependency between two children must be covered by a
  declared edge between those children, and every declared edge must be realized.
  Rules 3 and 4 apply.
- `observed`: edges among the children are taken from the code and drawn as found.
  Rules 3 and 4 do not apply at this level. This is the adoption setting for parts
  of the system that have not been curated yet.

The policy is per level, so a team can hold the component level to `declared` while
leaving the module level inside a component `observed`, and tighten later.

### 2.6 Edges

An edge has `from`, `to`, an optional `label`, an optional `payload`, and an
optional `asserted` flag. Both ends are node or external identifiers that are
siblings. An edge between nodes that are not siblings is a format error.

Every edge is a dependency, and there is no edge kind. The dependency is verified.
The label is asserted and is drawn as a caption. If `payload` names a type, rule 4
requires that type to be referenced across the edge, and the viewer draws the edge
as a data flow labeled with the type. Every visual distinction between edges is
therefore backed by a rule. Flow direction is the dependency direction. A flow that
runs against the dependency, such as a return value or a callback, is a matter for
the label in v0.

An edge with `"asserted": true` says the checker cannot see it. Asserted edges are
exempt from rule 4 and are drawn dashed. They exist for reflection, dependency
injection, message buses and anything that crosses a process boundary.

### 2.7 Externals

An external is a node bound by assembly name patterns rather than type patterns,
with `*` matching any characters. Externals are top-level nodes, so an edge to an
external is declared at the root level from a top-level node, and inside that node
the edge appears as a port. References to metadata symbols resolve to the external
whose pattern matches the containing assembly. When several patterns match, the
pattern with the longest literal part wins, so that `System.Text.Json` is bound by an
external declaring that name rather than by one declaring `System.*`. The root
`externals` policy is `observed` by default, which means references to unbound
assemblies are reported but not failures. Setting it to `declared` makes an unbound
external assembly a failure.

### 2.8 Rendering of policy levels

At a `declared` level the picture is a contract. Every edge was written down, and a
new edge in a changeset arrives with a map diff that someone decided on. At an
`observed` level the picture is whatever the code does, and nobody has approved it.
The viewer must keep a reviewer from extending the trust earned by the first to the
second.

The distinction is drawn on the container, since the policy belongs to the
container. An observed container has a visibly different frame and a persistent
badge, and the edges among its children render in a muted style. The top of the
view shows the count of declared and observed levels so that the maturity of the
map is readable at a glance.

In a changeset, a new edge at an observed level is the most prominent item in that
level's change list and is flagged as undeclared. A declared level makes such an
edge impossible without a map diff. An observed level only makes it visible, so the
viewer compensates with prominence.

The viewer offers a one-click promotion of an observed container, which emits the
map edit that sets its policy to `declared` and declares its currently observed
edges. This is `check --propose` applied to one container.

## 3. Observed dependencies

The checker opens the solution with Roslyn and, for every top-level type declared in
the solution's source, collects the symbols its declarations reference. Each
reference is resolved to the top-level type that declares the referenced symbol.

An observed dependency is the tuple:

```
(from-type, to-type, kind, symbol, file, line)
```

where `kind` is one of `inherits`, `implements`, `constructs`, `calls`, `attribute`
or `references`. Rules in v0 treat all kinds alike. The kind, file and line are
kept as evidence so that the viewer can show why an edge exists and where.

Resolution details that matter in C#:

- A referenced member resolves to its containing type, and a nested type to the
  top-level type that contains it. A partial type is one type however many files
  declare it, and references to any of its parts resolve to it.
- Source is every document of every project in the solution, including documents a
  source generator produced. Generated types are types in the assembly like any
  other and are partitioned like any other. The facts record which generator
  produced a type's declaration so that the viewer can say so.
- A declaration in build output, meaning a file under `obj/` or `bin/` that a
  project compiles, or in a file outside the repository, is not source. A
  reference to a symbol declared only there is reported as `unresolvable` with the
  location, and creates no edge.
- `using` directives are not dependencies. A namespace import creates no edge.
- References within one type are not dependencies. References between two types in
  the same file are, and lift to the leaf they share when they share one.
- Symbols with no source in the solution resolve to an external by assembly name.
  References to the core library, meaning the assembly that defines `System.Object`
  in the compilation, are never recorded, since every type would carry them. Other
  framework assemblies are recorded and can be bound like any external.
- The left side of a member access that names a type or namespace is not recorded,
  since the member reference it qualifies already carries the dependency.

Observed leaf-to-leaf dependencies are lifted to the hierarchy by lowest common
ancestor. For a dependency from type `x` to type `y` with leaves `X` and `Y` and
lowest common ancestor `N`, the lifted edge is between the children of `N` that
contain `X` and `Y`. Every observed dependency lifts to exactly one level, which is
where rules 3 and 4 check it and where the viewer draws it.

## 4. Conformance rules

The checker fails on any violation. Each rule names what it checks so that a
failure message can cite it.

1. **Partition.** Every top-level type declared in the solution's source binds to
   exactly one leaf, by attribute or by pattern as section 2.4 says. A type that
   binds to none is `unmapped`. A type that two patterns of equal specificity match
   is `ambiguous`. A type whose attribute names something that is not a leaf, or an
   attribute on a nested type, is `misattributed`. All three are failures.
2. **Well-formedness.** The file is canonical and valid against the schema. Every
   node's parent exists. Every container has a child. Edges join siblings. Every
   identifier an edge or policy mentions exists. Patterns are well-formed.
3. **Permission.** For every observed dependency whose lifted level has policy
   `declared`, a declared edge exists between the lifted endpoints. The failure
   message cites the file, line and symbol that created the undeclared edge.
4. **Realization.** For every declared edge at a level with policy `declared`, at
   least one observed dependency lifts to it. An edge with a `payload` type must be
   realized by a dependency whose symbol is that type or a member of it. Asserted
   edges are exempt. A declared edge with no evidence is dead and is a failure.
5. **Policy.** `forbid` entries match no observed lifted edge. Every node listed in
   `acyclic` has no cycle among the lifted edges of its children. These apply at
   `observed` levels too, since they constrain the code, not the map.

Rules 3 and 4 together are the two directions of drift. Rule 3 catches code the map
does not describe. Rule 4 catches map the code does not justify.

There is no cohesion rule, because a type is one thing however many files declare
it. The earlier file-based revision needed one; types do not.

### 4.1 Proposed edits

For most violations the checker knows the map edit that would resolve it, and it
says so in the failure message. `archmap check --propose <path>` writes a copy of
the map with every such edit applied, so that an agent or a curator reviews a diff
rather than authoring records.

| Violation | Proposed edit |
|---|---|
| Permission | Add the missing edge, with no label. |
| Realization | Remove the dead edge. |
| Partition, unmapped | Add a leaf bound to the type's namespace, beside the existing leaf whose patterns share the longest namespace prefix with it, or at the top level when no leaf shares more than every leaf does. |
| Partition, ambiguous | None. The message lists the competing leaves and their patterns. |
| Partition, misattributed | None. The message names the attribute and what it names. |
| Policy | None. A policy violation is a problem in the code or a decision about the policy, and the tool proposes neither. |

A type that is unmapped, ambiguous or misattributed binds to no leaf, so the
references it makes realize nothing. A partition failure can therefore be
accompanied by realization failures that disappear once the type is bound.

Proposals only ever make the map describe the code. They never relax a policy, never
add a label or a payload, and never assert an edge. Those are judgments.

### 4.2 Changeset minimality

This is a property of a changeset rather than of one commit, and it is checked by
`archmap diff` rather than `archmap check`. Given a base and head that both pass
`check`:

- Every edge added to the map is realized at head and was not realized at base.
- Every edge removed from the map was realized at base and is not realized at head.
- Every leaf added to the map binds at least one type at head.
- Every leaf removed from the map bound no type at head.

A map edit that none of these justify is reported as gratuitous. The viewer shows it
as a structural change with no evidence, which is the right prompt for a reviewer.

## 5. Bootstrapping

A deterministic process can produce a correct map, meaning one that passes every
rule, but not a good one, meaning one whose nodes are the logical components a
reviewer thinks in. Bootstrapping separates the two.

### 5.1 Skeleton

`archmap init` emits a map derived entirely from structure that people have already
chosen: one top-level node per project, and beneath it one node per namespace
segment beyond the project's root namespace, nested as the namespaces nest. Every
level is `observed`, no edges are declared, and the result passes `check` by
construction.

Every referenced assembly is bound, so that the root externals policy can be set to
`declared` at once: framework assemblies, meaning `System.*`, `Microsoft.Win32.*`,
`netstandard` and `mscorlib`, under one external named `framework`, and one external
per other assembly, identified by its normalized name and bound to it exactly.

Identifiers and bindings are derived by these deterministic rules. Identifiers are
stable once committed regardless of how they were derived, and curation renames
freely, so a clean derivation is preferred to a clever one.

1. A project is a top-level node. Its identifier is the project name with any
   prefix common to every project in the solution removed, lowercased, with dots
   and every other excluded character replaced by a hyphen. `Example.Billing`
   becomes `billing` when every project in the solution starts with `Example.`.
2. A project's root namespace is the longest namespace prefix shared by every
   top-level type it declares. Below the project, identifiers mirror the namespace
   segments beyond the root namespace, normalized by the same rule.
3. Single-child chains are not collapsed. `Features.Payments` becomes
   `billing.features.payments` even when `Features` has one child namespace, so
   that the derivation of one identifier never depends on which sibling namespaces
   exist.
4. A namespace that declares types directly and also has child namespaces with
   types becomes a container with an additional leaf named `self`, bound to the
   namespace's `*` pattern. A namespace with no child namespaces becomes a leaf
   bound to its `**` pattern. A project whose types all share one namespace becomes
   a leaf bound to that namespace's `**` pattern.
5. Types in the global namespace, such as the class the compiler synthesizes for
   top-level statements, go in a leaf named `global` bound to `*` qualified by the
   project's assembly.
6. Every pattern the skeleton writes is qualified by the project's assembly when
   the same namespace also declares types in another project of the solution, and
   unqualified otherwise.

Two sibling names that normalize to the same identifier receive a numeric suffix.
The display name is the original project name or namespace segment.

Alongside the map, `init` emits the facts document for the current tree with a
`metrics` section: for each node, the count of dependencies that stay inside it and
the count that cross its boundary, and the cycles among its children. These are
hints for curation, not rules.

### 5.2 Curation

Curation turns the skeleton into a map worth reviewing against. It decides that
three namespaces are one component, that a namespace called `Helpers` belongs to
the ledger, what each purpose line says, which edges deserve a flow label, and which
levels are `declared`. These are judgments, and they are made by a language model, a
person, or both.

What makes this safe is that the judgment is confined to the tree, its names and
its bindings. Everything else is derived from the code, and a proposal is run
through `check`. A grouping that is incoherent with the code is rejected with the
exact reason, and the author iterates. To keep proposals small and reviewable,
curation regroups namespaces and projects by pattern, and reaches for type-name
patterns or `[Archmap]` attributes only where a namespace does not follow the
components.

A flat namespace is the case curation exists for. The skeleton gives it one leaf, the
facts carry every type-to-type reference inside that leaf, and a curator proposes a
split as type patterns or attributes that the checker then verifies. A type added to
such a namespace later either matches the curated patterns or fails rule 1 with a
proposal, which is the reviewed decision the design is built to surface.

The curation loop is slow, non-deterministic and reviewed, and that is acceptable
because it runs once and then occasionally. The stability requirement applies to the
committed map, not to the process that first produced it.

The `metrics` section lets two candidate maps be compared on something other than
taste: fewer boundary-crossing dependencies, fewer cycles among siblings, fewer
types bound by attribute, and more levels at `declared` are each better.

## 6. Facts

`archmap facts` runs the checker against one commit and emits a JSON document. The
viewer consumes two of these and never reads the code or the map directly. This is
the contract between the checker and the viewer.

```jsonc
{
  "commit": "…",
  "map": { /* archmap.json, canonical */ },
  "types": [
    { "name": "Example.Billing.Core.PaymentService", "assembly": "Example.Billing", "leaf": "billing.core", "by": "pattern",
      "files": ["src/Billing/Core/PaymentService.cs", "src/Billing/Core/PaymentService.Refunds.cs"] },
    { "name": "Example.Billing.Integrations.StripeJsonContext", "assembly": "Example.Billing", "leaf": "billing.core", "by": "attribute",
      "files": ["src/Billing/Integrations/StripeClient.cs", "src/Billing/Generated/…/StripeJsonContext.g.cs"],
      "generator": "System.Text.Json.SourceGeneration.JsonSourceGenerator" }
  ],
  "files": [
    { "path": "src/Billing/Core/PaymentService.cs", "hash": "…", "leaves": ["billing.core"] }
  ],
  "dependencies": [
    { "from": "Example.Billing.Core.Payment", "to": "Example.Auth.Session",
      "kind": "calls", "symbol": "Example.Auth.Session.Require", "file": "src/Billing/Core/Payment.cs", "line": 42,
      "level": "root", "edge": ["billing", "auth"] }
  ],
  "externalRefs": [ { "from": "Example.Billing.Core.Charge", "assembly": "Stripe", "symbol": "Stripe.ChargeService.Create", "file": "…", "line": 30, "count": 4, "external": "stripe", "topLevel": "billing" } ],
  "unresolvable": [ { "from": "…", "symbol": "…", "reason": "…", "file": "…", "line": 1 } ],
  "metrics": { "billing": { "internal": 212, "crossing": 9, "cycles": [], "attributed": 1 } },
  "violations": [ { "rule": "permission", "message": "…", "evidence": { /* dependency */ }, "proposal": { /* edit */ } } ]
}
```

Types are listed with every file that declares them, and files with every leaf
their types bind to, so that the viewer can descend from a leaf to its types to
their files to the diff. Dependencies are listed per reference, since each one is
evidence the viewer can show. External references are aggregated per type and
assembly, with the first symbol, file and line and a count, since every type
references the framework and listing each use would dominate the document. The
document is written without indentation. On a solution of twelve hundred files
with eighty thousand dependencies it is roughly twenty megabytes.

`archmap diff <base> <head>` combines two facts documents into a changeset document:

- **Structural**: nodes and edges added or removed between the maps, each with the
  dependencies at head or base that justify it, and any gratuitous edits. A type
  that moved between leaves, by attribute or by pattern, is listed here too, since
  membership is structure.
- **Content**: for every node, the files under it that were added, removed or
  modified, as paths and as counts, rolled up through the hierarchy. A file counts
  under every leaf whose types it declares.
- **Edges**: for every lifted edge, whether it is new, gone, or unchanged, and the
  change in its evidence count.
- **Hunks**: the textual diff per file, attached to the file's leaves, so that the
  viewer can descend from a node to its code without a second tool.

Nothing in the changeset document is interpreted. Narrative summaries, if any, are a
separate layer that cites entries in this document by identifier.

## 7. Tooling

`archmap` is a .NET 10 console tool and a library. The library exposes the checker so
that an xUnit test in the solution can assert conformance as part of the normal test
run.

| Command | Purpose |
|---|---|
| `archmap init --solution <sln>` | Emits the deterministic skeleton as `archmap.json` beside the solution, and the facts document with metrics as `archmap.facts.json`. Refuses to overwrite an existing map. See section 5. |
| `archmap check` | Runs rules 1 through 5 against the working tree. Exit code 1 on any violation. `--propose <path>` writes the map with proposed edits applied. `--facts <path>` also writes the facts document. |
| `archmap fmt` | Rewrites both files in canonical form. `--check` reports without writing. |
| `archmap facts` | Emits the facts document for the working tree, recording the HEAD commit, suffixed `+dirty` when the tree has changes. |
| `archmap diff <base> <head>` | Combines two facts documents into the changeset document, including minimality findings. With `--repo`, attaches hunks from `git diff` between the two commits. |

Every command takes `--map <path>`; the default is `./archmap.json`, and the
repository root is the map's directory.

### 7.1 Turnaround

Opening a solution with Roslyn costs a full compilation, which is tens of seconds on
a large solution. The intended shape is for `check` and `facts` to cache the facts
document keyed by commit so that `diff` on a reviewed range costs one compilation per
unseen commit. v0 does not cache; `facts --out` is the manual equivalent. Incremental
analysis is out of scope.

## 8. Out of scope for v0

- Member-level leaves. The viewer's code level descends to members within a type,
  but members are not map nodes.
- Path bindings as a secondary way to split a namespace. Type-name patterns and
  attributes cover the cases seen so far; a path binding would be added only when a
  real repository needs it.
- Gateway policies, such as "only `billing.api` may reference outside `billing`."
- Multiple solutions or repositories.
- Runtime, configuration or process-boundary dependencies beyond asserted edges.
- Narrative summaries.

## 9. Open questions

- Whether a type whose `[Archmap]` attribute names a different leaf than the pattern
  that would otherwise bind it deserves a warning, or whether the per-leaf count of
  attributed types in the metrics is enough.
