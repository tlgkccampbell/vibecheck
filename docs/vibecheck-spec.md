# Architecture map specification (v0 draft)

This document defines the architecture map, the facts the checker establishes from the
code and the map, and the changeset the viewer draws. The target is C# solutions on
.NET 10 or later.

The map is a tool for understanding the conceptual change in a system, not an archive
of the diff and not an enforcement mechanism. It names the components a reviewer
thinks in, says which types belong to each, and stays put from one changeset to the
next. Everything else, in particular every edge, is read from the code. The viewer
draws what the code does at base and at head and highlights what changed. The code
diff remains the record of what changed textually.

The unit of partition is the type. The unit of change is the file. A type belongs to
exactly one leaf of the map, and a file belongs to every leaf whose types it declares.
Where a declaration belongs is a judgment the map records. What changed is a question
git answers per file, and the viewer shows that answer under the leaves it touches.

An earlier revision of this document declared edges in the map and checked the code
against them. That machinery, with its policies and proposals, was most of the
complexity and none of the understanding, and it was removed. Section 8 records it.

## 1. Files

| File | Purpose | Reviewed as, when committed |
|---|---|---|
| `vibecheck.json` | The map: nodes, bindings, views, exclusions. | A structural change to how the system is described. |
| `vibecheck.layout.json` | Stored node positions and sizes per view. | Presentation only. |

Layout is stored separately so that moving a box on the diagram never appears as a
structural change, and so that a structural change never has to carry coordinates.
A node missing from the layout file is placed by a deterministic rule and drawn
with a "new" treatment until a position is stored for it.

Neither file has to be committed. A team that reviews the map with the code keeps
both at the repository root, where the tool looks by default. A reviewer can keep
them anywhere else instead and name the repository with `--root`, and nothing in the
repository changes. Each run reads the map once and applies it to every revision it
analyzes, so a map kept outside the repository describes both ends of a changeset.

### 1.1 Format

Both files are JSON and are validated against a JSON Schema that ships with the
tool. JSON is chosen because the map is written by tools and language models far
more often than by hand, and for those authors an unambiguous grammar with exact
validation errors matters more than compactness.

`vibecheck fmt` rewrites both files in canonical form, and `check` warns on a file
that is not canonical. Canonical form is line-oriented so that every diff hunk is
one self-contained fact:

- Each node, external and group entry occupies exactly one line.
- The exclude list occupies one line, in the order written.
- Nodes, externals, views and groups are objects keyed by identifier, sorted by
  identifier.
- Keys within a record appear in the order the schema lists them.
- Two-space indentation, UTF-8, LF line endings, no trailing whitespace.

There are no comments. Anything worth saying goes in a `purpose` or `note` field,
where the viewer shows it.

Appending a record to a list also touches the previous line, because of JSON's
comma rule. This is cosmetic. The viewer presents map changes structurally, as
records added and removed, never as text.

## 2. Map schema

```json
{
  "version": 0,
  "solution": "src/Example.sln",
  "exclude": ["System.**"],
  "tests": ["Example.Tests::**"],
  "patterns": [
    { "id": "consumer", "name": "Consumer", "purpose": "Handles one message from the bus.", "keys": ["implements:MassTransit.IConsumer<>"] }
  ],
  "nodes": {
    "auth": { "name": "Auth", "purpose": "Identity, sessions and authorization decisions.", "category": "domain", "bind": ["Example.Auth.**"] },
    "billing": { "name": "Billing", "purpose": "Owns invoices, payments and the ledger.", "category": "domain" },
    "billing.api": { "name": "API", "category": "entry", "bind": ["Example.Billing.Api.**"] },
    "billing.core": { "name": "Core", "bind": ["Example.Billing.Core.**"] },
    "billing.ledger": { "name": "Ledger", "bind": ["Example.Billing.Ledger.**", "Example.Billing.Core.Postings.*"] },
    "shared": { "name": "Shared", "purpose": "Primitives with no business meaning.", "category": "foundation" },
    "shared.kernel": { "name": "Kernel", "bind": ["Example.Shared::Example.Shared.**"] },
    "shared.money": { "name": "Money", "bind": ["Example.Shared.Money"] }
  },
  "views": {
    "by-concern": { "name": "By concern", "level": "root", "groups": {
      "edges": { "name": "Edges", "purpose": "Where requests arrive and identities are checked.", "leaves": ["auth", "billing.api"] },
      "payments": { "name": "Payments", "purpose": "Everything that moves money.", "leaves": ["billing.core", "billing.ledger", "shared.money"] },
      "primitives": { "name": "Primitives", "leaves": ["shared.kernel"] }
    } }
  },
  "externals": {
    "stripe": { "name": "Stripe SDK", "bind": ["Stripe", "Stripe.*"] }
  }
}
```

### 2.1 Nodes

The node list is flat. An identifier is a sequence of segments matching
`[a-z0-9-]+` joined by dots, and a node's parent is the identifier with its last
segment removed. A node with one segment is top-level. The hierarchy is therefore
visible in a single line of a diff, and adding, moving or removing a node is a
one-record change.

A node has a display name, an optional one-line purpose, an optional category, an
optional note, and either `bind` or nothing. A node with `bind` is a leaf. A node without `bind` is a
container and must have at least one child.

Only leaves bind code. A container's extent is the union of its children's extents,
so containment holds by construction and needs no rule.

A category says broadly what role a node plays, so that a viewer can tell nodes
apart at a glance by a color and an icon. A node without a category takes the
category of its nearest ancestor that has one, so a container's category usually
covers its children and a leaf sets one only where it differs. The list is fixed,
so that every map uses the same names and a viewer can draw each one the same way
in every map:

| Category | Covers |
|---|---|
| `entry` | Where control enters the system: command lines, hosts, servers, and the endpoints of protocols. |
| `feature` | User-facing features built on the core, such as commands, editor features and reports. |
| `domain` | The core model: the concepts, representations and data structures the rest of the system works on. |
| `analysis` | Working out facts about the model: inference, checking and rules. |
| `transform` | Processing that turns one representation into another: pipelines, compilation, conversion, generated output. |
| `integration` | The outside world: storage, file systems, configuration files, networks, external services and protocols' wire types. |
| `foundation` | Shared infrastructure with no domain meaning: utilities, diagnostics, primitives. |
| `tooling` | Build-time and developer tooling, such as source generators and test support. |

The map's tree is the map's own. Node identifiers are not namespaces, a leaf may
gather types from several namespaces and several projects, and a container may
hold leaves drawn from anywhere. The skeleton mirrors namespaces only as a starting
point, and curation is expected to cut across them.

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
- A pattern with no wildcard names one type exactly, which is how a curated map
  lists the members of a concept that no namespace follows.
- A pattern may be qualified by an assembly name and `::`, as in
  `Example.Shared::Example.Shared.**`, and then matches only types declared in that
  assembly. An unqualified pattern matches in every assembly of the solution.

When several leaves' patterns match a type, the most specific pattern wins, where
specificity is the number of literal characters in the pattern, counting an assembly
qualifier. `Example.Billing.Core.Postings.*` therefore takes the postings types away
from `Example.Billing.Core.**` without any exclusion syntax, and an exact name takes
a type away from any wildcard. Two patterns of equal specificity that both match a
type leave it ambiguous, which section 2.5 resolves with a warning.

### 2.3 The `[Vibecheck]` attribute

A type may name its leaf directly:

```csharp
[Vibecheck("billing.core.payments")]
public sealed partial class CapturePayment { … }
```

The attribute is recognized by its full metadata name, `Vibecheck.VibecheckAttribute`,
wherever it is declared, so a repository declares it itself and takes no dependency
on the tooling:

```csharp
namespace Vibecheck;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface | AttributeTargets.Enum | AttributeTargets.Delegate)]
internal sealed class VibecheckAttribute(string leaf) : Attribute
{
    public string Leaf { get; } = leaf;
}
```

The attribute assigns membership; it does not create leaves. An attribute on any
part of a partial type applies to the type, so the generated half of a type follows
its hand-written half with no further rule. An attribute on a nested type is
ignored with a warning, since nested types follow their container. An attribute
naming something that is not a leaf is ignored with a warning, and the type binds
by pattern as if it carried none.

Patterns carry the rule and attributes carry the exceptions. The facts report how
many types each leaf binds by attribute, so that a namespace whose patterns no
longer describe it is visible as a number.

### 2.4 Binding precedence

A type the map excludes, or a test type of section 2.7.1, binds to nothing. Otherwise it binds to the leaf named by
its `[Vibecheck]` attribute; otherwise to the leaf whose pattern matches it most
specifically; otherwise to the automatic leaf `unsorted`. Explicit beats pattern,
and pattern beats the fallback.

### 2.5 The `unsorted` leaf

A type that no leaf binds is not an error. It belongs to a top-level leaf named
`unsorted`, which the map never declares and the checker always provides. The
viewer draws it with a warning treatment, the facts count what it holds, and
`check` warns with the count. A map in which `unsorted` is empty describes every
type. A map in which it is not still draws, which is what lets a map be adopted
before it is finished and lets a changeset that adds a type in a new namespace be
viewed before anyone has decided where the type belongs.

The same holds for a type two leaves claim equally. The leaf whose identifier
sorts first takes it, and `check` warns naming both.

### 2.6 Externals

An external is a node bound by assembly name patterns rather than type patterns,
with `*` matching any characters. Externals are top-level nodes, so a reference
from inside a component to an external is drawn as a port on the component's frame.
References to metadata symbols resolve to the external whose pattern matches the
containing assembly. When several patterns match, the pattern with the longest
literal part wins, so that `System.Text.Json` is bound by an external declaring that
name rather than by one declaring `System.*`. A referenced assembly that no external
binds is reported in the facts and drawn as an unbound port.

### 2.7 Exclusions

The `exclude` list holds type patterns, in the syntax of section 2.2, naming the
types that are not part of the system the map describes. An excluded type is not
partitioned, no leaf holds it, and a reference to it or from it creates no edge. A
file that declares nothing but excluded types is not a file of the map, so its
changes appear in no changeset. Exclusion is decided before binding, so a type that
an exclusion matches is excluded whatever `bind` patterns or `[Vibecheck]` attribute
say. An attribute on an excluded type earns a warning.

The use that motivates it is code the repository compiles but does not own, which
lands in namespaces it does not own: the regex source generator emits its matchers
into `System.Text.RegularExpressions.Generated`, and a polyfill declares
`System.Runtime.CompilerServices.IsExternalInit`. `System.**` removes all of it.
Test projects are not excluded but listed as test types, as section 2.7.1 says.

What the map excludes is still in the code diff, and nothing in the map claims to
be exhaustive. The facts record how many types and files were excluded so that the
number can be checked against expectations.

#### 2.7.1 Test types

The `tests` list holds type patterns in the same syntax, naming the test types. A
**test type** is a type that a `tests` pattern matches and no `exclude` pattern
does, so a type both lists match is excluded. Test projects consume every component
and add no structure of their own, and in a solution of twelve hundred types they
can account for most of the edges at the root, so a test type binds to nothing, no
leaf holds it, and a reference from it or to it creates no edge, as for an excluded
type. Unlike an excluded type, a test type stays in the analysis as evidence about
the system: the facts list it with its test methods and the references it makes
into the system, and place it by the system type it tests, as
[tests.md](tests.md) sections 3 and 4 describe. `Example.Tests::**` lists a whole
project by its assembly. An `[Vibecheck]` attribute on a test type earns a warning.
The canonical form writes the list only when it holds a pattern.

### 2.8 Views

The tree is one way to group the leaves, and it is the way the viewer navigates:
every type has one home, zooming in and out is reversible, and counts under a node
are unambiguous. It is not the only grouping a reviewer wants. A concept such as
the 65816 processor state in a compiler can have its home under semantics and still
be one thing when seen from the top, spanning leaves under semantics, layout and
flow analysis. A view says so.

A view names a level, which is `root` or a container, and partitions the leaves
under that level into groups. Each group has a name, an optional purpose, and the
list of leaves it holds. Every leaf under the level appears in exactly one group,
and no leaf from elsewhere appears at all; either is a well-formedness failure.
Within a group, leaves keep their own identity, so the viewer can open a group and
show its leaves as it would show a container's children.

A view changes nothing about bindings or facts. Every edge the facts record is
between two leaves, so the viewer aggregates the same edges under the tree or under
any view of a level, and the same reference counts and cycles are computed for a
view's groups as for a container's children. Views are therefore cheap to propose,
cheap to evaluate, and cheap to discard.

### 2.9 Patterns

The optional `patterns` list names, declares and ignores the patterns that a review
finds among the types, such as consumers that implement `IConsumer<>`. An entry with
an `id` and `keys` names the pattern that discovery finds with those keys, or
declares it when discovery does not find it. An entry with `ignore` drops the group of
one key from discovery, such as a marker interface that every message carries. A key
is `inherits:`, `implements:` or `attribute:` and the open name of a type, or
`suffix:` and the text a type's name ends with. [patterns.md](patterns.md) section 4
describes keys, discovery and the list. A pattern binds nothing and draws nothing, so
the list changes no binding and no edge. The canonical form writes the list only when
it holds an entry, one entry per line in the order written.

## 3. Observed dependencies

The checker opens the solution with Roslyn and, for every top-level type declared in
the solution's source, collects the symbols its declarations reference. Each
reference is resolved to the top-level type that declares the referenced symbol.

An observed dependency is the tuple:

```
(from-type, to-type, kind, symbol, file, line)
```

where `kind` is one of `inherits`, `implements`, `constructs`, `calls`, `attribute`
or `references`. The kind, file and line are kept as evidence so that the viewer
can show why an edge exists and where.

Resolution details that matter in C#:

- A reference in a type's base list or in its type parameters' constraints is a
  reference of that type.
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
- A type the map excludes, as section 2.7 says, is removed before anything else
  is considered. A reference to it or from it creates no edge and is not reported.
- `using` directives are not dependencies. A namespace import creates no edge.
- References within one type are not dependencies. References between two types in
  the same file are, and stay inside the leaf they share when they share one.
- Anonymous types are not types of the map, and a reference to one creates no edge.
  A tuple resolves to the framework's tuple type, which the core-library rule below
  drops.
- A fully qualified name declared in more than one assembly, such as a generator's
  helper emitted into every project, is prefixed with the assembly and `::` so that
  each type keeps its own identity. Patterns match the plain name together with the
  assembly.
- Symbols with no source in the solution resolve to an external by assembly name,
  and each keeps its kind and the top-level type that declares it, in the open form
  that [patterns.md](patterns.md) section 3.1 describes, such as
  `MassTransit.IConsumer<>`. References to the core library, meaning the assembly that defines `System.Object`
  in the compilation, are never recorded, since every type would carry them. Other
  framework assemblies are recorded and can be bound like any external.
- The left side of a member access that names a type or namespace is not recorded,
  since the member reference it qualifies already carries the dependency.

The analysis also records the **traits** of each top-level type: the class it
inherits, the interfaces it implements directly, the attributes applied to it, and
the parameter types of its primary constructor or else of its constructor with the
most parameters, each in open form, from source and from referenced assemblies alike.
An interface or a parameter type of the core library is not a trait, while a base
class or an attribute of the core library is. [patterns.md](patterns.md) section 3.1
says why.

A dependency between two types is a dependency between their leaves. The facts
record dependencies per reference, and record the count per pair of leaves. Every
drawing of an edge at any level, in the tree or in a view, is an aggregation of
leaf pairs, and the viewer performs it.

### 3.1 Declarations and sites

The analysis also records what is declared and where each declaration is used, so
that a changeset can tell the hunks that change a declaration from the hunks that
follow from one, as [attention.md](attention.md) section 4 describes. A
**declaration** is a type declared in source, nested or not, or a member that one
declares, as one syntax declares it:

```
(symbol, type, container, kind, accessibility, exposed, file, first line, last line, body line, shape, body, summary)
```

The symbol is the name a dependency's symbol uses, the containing type and the
member's name without parameters, so that overloads share it. The type is the
top-level type that holds the declaration, and the container is the type that holds
it directly. The kind is `type`, `method`, `constructor`, `property`, `field`,
`event`, `indexer` or `operator`, and a declaration is exposed when it and every type
around it are public or protected. The shape is a hash of the declaration's tokens
outside its body and its name, and the body is a hash of the body's tokens, so that
two declarations that differ only by name have the same shape. A type's shape leaves
out its members, which are declarations of their own. The summary is the first
sentence of the documentation comment.

A **site** is a reference to a declaration that the dependencies do not record: a
reference from a type to its own members, and a reference that names a constructor,
from an object creation or a constructor initializer:

```
(symbol, from-type, to-type, file, line)
```

Every use of a declaration is a site or a dependency, and readers take the union.

## 4. Checking

`vibecheck check` reads the map, analyzes the solution, and produces the facts
document. It fails only when the map itself cannot be used. Everything else is a
warning, because a map that is imperfect still draws, and a reviewer waiting on a
changeset is better served by a picture with an `unsorted` box than by no picture.

**Failures.** The file is valid against the schema. Every node's parent exists.
Every container has a child. No identifier is both a node and an external, and no
node is named `unsorted`. Every pattern in a `bind`, `exclude` or `tests` list is
well-formed, and so is every entry of the `patterns` list, which has either an
identifier and keys or an ignored key, and whose identifiers are unique. Every view names `root` or a container, and its groups partition the
leaves under it.

**Warnings.** The file is not in canonical form. A type binds to `unsorted`. A type
is claimed equally by two leaves. An `[Vibecheck]` attribute names no leaf, sits on a
nested type, or sits on an excluded type or a test type. An `exclude` pattern matches
only types of test projects, by the rule of section 5.1, and could move to `tests`.
A referenced assembly is bound by no
external. No type carries every key of an entry of the `patterns` list, or an entry
ignores a suffix key, which discovery never uses. The layout file mentions an identifier the map does not define. The
analysis itself reported something, such as a project that did not compile.

`check --strict` turns warnings into failures, for a repository that wants its map
complete before a change merges.

`check` also reports how the test types were placed, as "55 test types: 40 by name,
12 by references, 3 infrastructure", so that the rules of [tests.md](tests.md)
section 4 can be judged against real repositories. It reports each pattern it found
with its number of instances, its number of norms, the mean number of norms its own
instances lack, and its keys, as "pattern implements IConsumer<>: 14 instances, 4
norms, 0.3 departures each (implements:MassTransit.IConsumer<>)", so that the
thresholds of [patterns.md](patterns.md) section 5.2 can be judged against real
repositories.

### 4.1 Metrics

The checker computes, for every container and for the root, the edges among its
children with their reference counts and the cycles among them, and for every node
the number of types it holds, the number of references that stay inside it, and the
number that cross its boundary. The same is computed for every view's groups.

These numbers are not rules. They are the graph's answer to a proposed grouping,
and section 5 says how they are used.

## 5. Bootstrapping

A deterministic process can produce a correct map, meaning one in which `unsorted`
is empty, but not a good one, meaning one whose nodes are the components a reviewer
thinks in. Bootstrapping separates the two. A skeleton is derived from namespaces.
A language model then proposes the conceptual map from the types themselves, and
the graph verifies the proposal.

### 5.1 Skeleton

`vibecheck init` emits a map derived entirely from structure that people have already
chosen: one top-level node per project, and beneath it one node per namespace
segment beyond the project's root namespace, nested as the namespaces nest. The
result leaves `unsorted` empty by construction.

Before any node is derived, the skeleton writes `System.**` into `exclude`, and
`Assembly::**` into `tests` for every project whose types reference a test framework,
meaning an assembly named `xunit*`, `nunit.framework`,
`Microsoft.VisualStudio.TestPlatform.TestFramework` or `TUnit.Core`. Excluded types
and test types take no part in the derivation. Curation brings a project back by
deleting its line.

Every referenced assembly is bound, so that no port is unbound: framework
assemblies, meaning `System.*`, `Microsoft.Win32.*`, `netstandard` and `mscorlib`,
under one external named `framework`, and one external per other assembly,
identified by its normalized name and bound to it exactly.

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

### 5.2 Units

`vibecheck units` writes the input for the proposing step: a listing of the solution's
types, folded into units, with what is known about each. A unit is a type together
with the types that belong to it on structural grounds alone:

- A helper joins its only user. A type referenced by exactly one other type, and
  itself referencing at most two, is folded into that user, repeatedly, so that a
  type's private collaborators travel with it. The bound on its own references
  keeps a consumer from folding into its caller, which would collapse a pipeline
  into its driver.
- A subclass joins its base when they share a namespace. A base whose subclasses
  live elsewhere is an extension point, and each subclass belongs to its own
  concept.

For each unit the listing gives the head type's name and namespace, the first
sentence of its documentation comment, the other members, and the units it
references most and is referenced by most, with counts. On a solution of a thousand
types this is a few thousand lines, which a language model reads in one pass.

### 5.3 Proposing and verifying

The map is written by whoever can read the listing and name the concepts. In
practice that is a language model, and the names and purposes it writes are the
ones the viewer shows. The proposal is a map in the format of section 2: a tree of
containers and leaves, each leaf binding its units by namespace pattern where a
namespace follows the concept and by exact type name where it does not, with a
purpose line on every node and a category, from the list in section 2.1, on every
top-level node and on any node whose role differs from its parent's.

`vibecheck check` then answers with the metrics of section 4.1. The graph cannot say
whether a grouping is conceptually right, and it does not try. It says three things
that a wrong grouping reliably shows:

- A cycle among a level's children, where a type was placed by its name in a
  component that the code puts on the other side of a boundary. In the first trial
  of this process, every top-level cycle was such a misplacement.
- A leaf with almost no references inside it and many across its boundary, which
  is either a facade that everything calls, and is fine, or a split finer than the
  code supports.
- A boundary that severs a heavy edge, visible as the largest counts among a
  level's edges.

`vibecheck why <from> <to>` lists the pairs of types behind one edge, with counts, so
that the author sees which placement caused it. The author revises and checks
again. Three rounds were enough on a solution of a thousand types.

Automated clustering of the dependency graph is one more input to this step, and
not a substitute for it. Dense regions of a dependency graph are hubs more often
than concepts, and a clustering has no notion of names.

### 5.4 Views

The same loop proposes views. A view is a regrouping of the leaves under one level,
and `check` reports the reference counts of each group and the cycles among groups exactly
as it does for the tree, so that a proposed concern-based grouping of the top level
is judged by the same numbers as the tree is.

### 5.5 New types

A changeset that adds a type in a namespace the map already binds places it by
pattern. One that adds a type nowhere the map reaches places it in `unsorted`, and
the viewer shows it there with the warning treatment. Deciding where it belongs is
a map edit in the same change or a later one, and nothing waits on it.

## 6. Facts

`vibecheck facts` runs the checker against one commit and emits a JSON document. The
viewer consumes two of these and never reads the code or the map directly.

```jsonc
{
  "commit": "…",
  "map": { /* vibecheck.json, canonical */ },
  "excluded": { "types": 2, "files": 0 },
  "types": [
    { "name": "Example.Billing.Core.PaymentService", "assembly": "Example.Billing", "leaf": "billing.core", "by": "pattern",
      "files": ["src/Billing/Core/PaymentService.cs", "src/Billing/Core/PaymentService.Refunds.cs"],
      "traits": { "interfaces": [ { "type": "Example.Billing.IPaymentService" } ], "attributes": [], "injects": ["Stripe.ChargeService"] } },
    { "name": "Example.Billing.Integrations.StripeJsonContext", "assembly": "Example.Billing", "leaf": "billing.core", "by": "attribute",
      "files": ["src/Billing/Integrations/StripeClient.cs", "src/Billing/Generated/…/StripeJsonContext.g.cs"],
      "generator": "System.Text.Json.SourceGeneration.JsonSourceGenerator" },
    { "name": "Example.Experiments.Probe", "assembly": "Example.Billing", "leaf": "unsorted", "by": "fallback", "files": ["src/Billing/Probe.cs"] }
  ],
  "files": [
    { "path": "src/Billing/Core/PaymentService.cs", "hash": "…", "leaves": ["billing.core"] }
  ],
  "dependencies": [
    { "from": "Example.Billing.Core.Payment", "to": "Example.Auth.Session",
      "kind": "calls", "symbol": "Example.Auth.Session.Require", "file": "src/Billing/Core/Payment.cs", "line": 42 }
  ],
  "leafEdges": [ { "from": "billing.core", "to": "auth", "count": 17 } ],
  "externalRefs": [ { "from": "Example.Billing.Core.Charge", "assembly": "Stripe", "type": "Stripe.ChargeService", "kind": "calls",
    "symbol": "Stripe.ChargeService.Create", "file": "…", "line": 30, "count": 4, "external": "stripe" } ],
  "unresolvable": [ { "from": "…", "symbol": "…", "reason": "…", "file": "…", "line": 1 } ],
  "tests": {
    "types": [ { "name": "Example.Auth.Tests.SessionTests", "assembly": "Example.Auth.Tests", "files": ["tests/Auth/SessionTests.cs"],
      "subject": { "type": "Example.Auth.Session", "leaf": "auth", "by": "name" },
      "methods": [ { "symbol": "Example.Auth.Tests.SessionTests.Require_Succeeds", "file": "tests/Auth/SessionTests.cs", "first": 18, "last": 31,
        "skipped": false, "assertions": [ { "first": 24, "last": 24 } ] } ] } ],
    "exercises": [ { "from": "Example.Auth.Tests.SessionTests", "method": "Example.Auth.Tests.SessionTests.Require_Succeeds",
      "symbol": "Example.Auth.Session.Require", "file": "tests/Auth/SessionTests.cs", "line": 24 } ],
    "calls": [ { "from": "Example.Auth.Tests.SessionTests.Require_Fails_WhenExpired", "to": "Example.Auth.Tests.SessionBuilder.Expired",
      "file": "tests/Auth/SessionTests.cs", "line": 40 } ]
  },
  "nodes": { "billing": { "types": 40, "internal": 212, "crossing": 9, "attributed": 1 } },
  "levels": { "root": { "edges": [ { "from": "billing", "to": "auth", "count": 17 } ], "cycles": [] } },
  "views": { "by-concern": { "groups": { "payments": { "types": 31, "internal": 180, "crossing": 20, "attributed": 0 } }, "edges": [ { "from": "edges", "to": "payments", "count": 12 } ], "cycles": [] } },
  "patterns": [ { "id": "consumer", "name": "Consumer", "purpose": "Handles one message from the bus.", "by": "map",
    "keys": ["implements:MassTransit.IConsumer<>"], "instances": ["Example.Billing.Consumers.OrderPlacedConsumer", "…"],
    "norms": [ { "feature": "injects:Example.Billing.IUnitOfWork", "share": 13 } ], "meanDepartures": 0.3 } ],
  "violations": [ { "rule": "well-formedness", "message": "…" } ],
  "warnings": [ "3 types are unsorted" ]
}
```

Types are listed with every file that declares them, and files with every leaf
their types bind to, so that the viewer can descend from a leaf to its types to
their files to the diff. Excluded types and the files that declare nothing else are
absent, and `excluded` counts them. Test types are absent from `types`, from the
dependencies and from the declarations, and are not counted in `excluded`. The
`tests` member lists them instead, each with its files, its test methods and its
subject, with the references they make into the system as `exercises` and the
references among their own members as `calls`, as [tests.md](tests.md) section 3.2
describes. Each test method says whether it is skipped and gives the lines of its
assertion calls, and `tests.declarations` keeps the test types' declarations so that a
renamed test method is not taken for a removed one. A file that declares test types and no
system type is listed in `files` with no leaves, `test` set, and the leaf of its
test types' subject as `subject`. Dependencies are listed per reference, since
each one is evidence the viewer can show, and once more aggregated per pair of
leaves, since that is what every drawing starts from. External references are
aggregated per type and referenced type, with the first kind, symbol, file and line
and a count, and a reader that counts references per assembly sums them. Each type
carries its traits, when it has any, and `patterns` lists the patterns among the types,
with the keys, the instances and the norms of each, as [patterns.md](patterns.md)
sections 4 and 5 find them.
The facts also carry the declarations and sites of section 3.1 for the types the map
holds. The document is written without indentation. On a solution of a thousand
types with seventy thousand dependencies it is roughly twenty megabytes, before the
declarations and sites.

`vibecheck diff <base> <head>` combines two facts documents into a changeset document.
The changeset is the only input the viewer has, so it carries the map at head as well
as the differences:

- **Map**: the head map, from which the viewer takes the tree, the views and the
  externals.
- **Structural**: nodes, views and externals added, removed or modified between the
  two maps, and every type that binds to a different leaf at head than at base,
  since membership is structure.
- **Files**: every file either analysis lists, and every other file that git
  reports as changed, with its status (`added`, `removed`, `modified`, `renamed` or
  `unchanged`), its leaves at base and at head, and git's blob identifiers at each
  end when git reports it. A renamed file is one entry under its head path, with its
  base path as `previousPath`. A changed file that no type binds has no leaves at
  either end, and the viewer draws it under an **Other files** block. A test file
  has no leaves either, and carries `test` and its `subject` leaf from the facts. The
  viewer lists it in its subject leaf's Tests section, or under test infrastructure
  when it has no subject, and never in Other files.
- **Content**: for every node, the files under it that were added, removed or
  modified, as counts, rolled up through the hierarchy. A file counts under every
  leaf whose types it declares. A renamed file counts as modified under a node that
  holds one of its leaves at both ends, as removed under one that holds only its
  leaves at base, and as added under one that holds only its leaves at head. Changed
  test files count apart as `tests`, under every node that holds their subject leaf,
  and all of them at the top level.
- **Tests**: every test type with a changed file at either end, with its status, its
  subject, its changed files, and its test methods that the change added, removed or
  changed, where a method is changed when a hunk changes one of its lines. Each such
  method names the changed declarations it references, rewritten ones among them.
  The viewer's Tests switch lists them, as [tests.md](tests.md) section 7.3 describes.
- **Types**: every type at either end with the leaf it binds to at base and at head,
  the files that declare it, and whether it was added, removed, moved or unchanged.
  This is how the viewer descends from a leaf to its types to their files.
- **Edges**: for every pair of leaves with references at base or at head, both
  counts and whether the pair is new, gone, changed or unchanged. The viewer
  aggregates these to the level and view it is drawing. An edge that changed also
  carries its evidence: the type pairs behind it whose counts differ between base
  and head, largest change first. An unchanged edge carries no evidence, which keeps
  the document small.
- **Metrics**: the per-node metrics of section 4.1 at base and at head, and the
  per-level and per-view edges and cycles at head.
- **Hunks**: the textual diff of every changed file, including those that no type
  binds, with three lines of context and renames detected, keyed by the file's path,
  so that the viewer can descend from a node to its code without a second tool.
- **Trivial edits**: an edit to a C# file is trivial when the code on both sides
  parses to the same sequence of tokens, so that it touches only comments,
  documentation comments, whitespace, line breaks, `#region` lines or `#pragma`
  lines. Other directives, and code that an `#if` leaves out of the parse, count as
  code. Four more kinds of edit are trivial by the rules of
  [attention.md](attention.md) section 5: lines that differ from their pairs only by
  a renamed declaration's name, declarations moved unchanged between the files of
  one type or reordered within their file, and `using` directives alone. Each hunk
  and each file whose edits are all trivial is marked, with a `reason` that names the
  kind and what it mentions, such as the renamed declaration and its previous symbol
  with the decision that renamed it, or the file a declaration moved from. A file a
  generator wrote, which an `<auto-generated>` header or a `.Designer.cs` or `.g.cs`
  name says, carries the reason `generated` whether or not its edits are trivial. A
  trivial or generated file starts out reviewed, as
  [pull-requests.md](pull-requests.md) section 5 describes, and the viewer shows a
  node whose changed files are all reviewed in a muted style rather than as modified.
  Moving code to other lines can still reach the program through caller-information
  attributes and stack traces, and the marks accept that.
- **Decisions**: the hunks that change a declaration, and for each the hunks that
  follow from it, as [attention.md](attention.md) section 4 describes. Matching the
  declarations of section 3.1 by symbol tells which were added, removed, reshaped,
  rewritten, renamed or moved. A hunk whose changed lines touch one that was added,
  removed, reshaped, renamed or moved is a decision, with its declarations, their
  site counts at each end, and whether any is public. A hunk whose every changed line
  is blank, a comment, or a site of a declaration a decision changed, at that line's
  own end, is a consequence: its `follows` lists the decisions, the one with the most
  sites in it first, and each decision lists its consequences with the number of
  lines each explains. A decision's `marks` are the files whose every hunk is trivial
  or explained by that decision alone. A hunk in a test file follows from a decision
  by the same rule, read against the exercises of the test types, and a decision's
  site counts leave the tests out. Each decision lists as `tests` the test methods
  that reference its declarations, directly or through a helper, with the way, the
  helper, and the method's status in the change, as [tests.md](tests.md) section 5.1
  describes.
- **Test signals**: each hunk of a test file carries as `signals` the facts it shows
  about a test method: removed, skipped, an assertion removed, or an assertion
  changed, worded "expected value changed" when every changed assertion line differs
  from its pair only in literals. Each signal names its method and the changed
  declarations the method references, and touches changed code when there are any,
  as [tests.md](tests.md) section 6 describes. Each changed test method in the
  changeset's tests lists its signals.
- **Path**: the order in which to read the changeset, as [attention.md](attention.md)
  section 6 plans it: the surprises first, each with the signal that put it there,
  then the other changed leaves with the decisions, files and test files under them,
  and last what is set aside. The first tier ends with a step for each test type whose
  signals touch changed code and one step for every other test signal, and then a
  step for each new or changed instance of a pattern that departs from its norms. Each step names what it opens and the changed files it covers.
- **Patterns**: each pattern found at base that has an instance at head that the
  change added or changed, with its keys, its number of instances at base, its
  norms, those instances with their status, their departures from the norms, the
  norms they lack, their unusual traits, their companions and their nearest and
  typical examples, every other instance at head with its leaf, the source at head of
  each reported instance and example, and the rows that align each reported instance
  with each of the others member by member, as [patterns.md](patterns.md) sections 5.7,
  6 and 7.3 describe, and the three samples at base that Claude names it from, as
  section 8 there describes. A type is an instance at head when
  it carries every key of the pattern, and it is reported under the most specific of
  the patterns it is an instance of.
- **NewPatterns**: the patterns new in this change, which three or more added types
  share by a key that nothing at base carries, each with its keys and its instances
  with their leaves, as [patterns.md](patterns.md) section 4.5 describes.
- **Glossary**: the names the changeset introduces, as [attention.md](attention.md)
  section 8 lists them: added types and members, renamed declarations, nodes, views
  and externals added to the map, and assemblies referenced at head and not at base.
  Each term carries its kind, its block, the file and line of its declaration, its
  documentation summary when it has one, the number of sites that use it at head, the
  first use outside its declaring type, and the decision that introduced it. The list
  is grouped by block and ordered by use.

Nothing in the changeset document is interpreted. Narrative summaries, if any, are a
separate layer that cites entries in this document by identifier. On a solution of a
thousand types the changeset for a commit touching twenty files is under a megabyte,
most of it the type roster and the hunks.

## 7. Tooling

`vibecheck` is a .NET 10 console tool and a library. The library exposes the checker so
that a test in the solution can assert that `unsorted` is empty, or anything else
the facts say, as part of the normal test run.

| Command | Purpose |
|---|---|
| `vibecheck review [<range>]` | Serves the review workflow of [review-workflow.md](review-workflow.md) on the loopback interface and opens it in the browser, on the range given or on a picker. `vibecheck` with no command does the same. The picker lists the open pull requests of the repository that `gh` names; `--no-github` leaves GitHub out. |
| `vibecheck pr <number>` | Serves the review workflow and opens it on a pull request, given by number or by its address on GitHub, as [pull-requests.md](pull-requests.md) section 4 describes. It fetches the pull request's commits when they are missing, without writing any ref, and compares them as GitHub does. It takes the options of `review`. |
| `vibecheck init --solution <sln>` | Emits the deterministic skeleton as `vibecheck.json` beside the solution, or at the path `--map` names, and the facts document as `vibecheck.facts.json` beside the map. Refuses to overwrite an existing map. See section 5.1. |
| `vibecheck units` | Writes the unit listing of section 5.2 for the solution the map names, with the map's exclusions applied. |
| `vibecheck check` | Produces the facts for the working tree and prints the warnings and the metrics. Exit code 1 on a failure, or on a warning with `--strict`. `--facts <path>` also writes the facts document. |
| `vibecheck why <from> <to>` | Lists the type pairs behind the edge between two nodes, with counts, from a facts document given with `--facts` or from a fresh analysis. |
| `vibecheck fmt` | Rewrites both files in canonical form. `--check` reports without writing. |
| `vibecheck facts` | Emits the facts document for the working tree, recording the HEAD commit, suffixed `+dirty` when the tree has changes. |
| `vibecheck diff <base> <head>` | Combines two facts documents into the changeset document. With `--repo`, adds every file `git diff` reports as changed between the two commits, pairs renames, and attaches the hunks, with trivial edits marked. |
| `vibecheck changeset <base> [<head>]` | Produces the changeset document between two revisions in one step. Each revision is checked out into a temporary git worktree, built and analyzed, the two in parallel, and the current map is applied to both. The worktrees are removed afterward. One argument may be a range: `a..b`, or `a...b` to compare from the merge base as a pull request does. The head defaults to `HEAD`. `:worktree` names a snapshot of the uncommitted work, and on its own compares that with `HEAD`. `--no-build` skips the builds, which leaves out generated types. Analyses are cached per commit as `review` caches them; `--no-cache` turns that off. |
| `vibecheck guide` | Prints the guide for coding agents, `docs/keeping-the-map-in-sync.md`, which is embedded in the tool. `init` prints a note for the repository's `CLAUDE.md` or `AGENTS.md` that sends agents to it, and the review workflow offers to add the note to `CLAUDE.md` when it saves a map. |

Every command takes `--map <path>`; the default is `./vibecheck.json`, and the layout
file sits beside the map. The repository root, which the map's solution path and
every reported path are relative to, is `--root <dir>` when given and otherwise the
map's directory.

### 7.1 Turnaround

Opening a solution with Roslyn costs a full compilation, which is tens of seconds on
a large solution. `review` and `changeset` therefore cache the analysis of each
commit, which does not depend on the map, outside the repository and keyed by the
commit, the solution and the build of the tool, as
[review-workflow.md](review-workflow.md) section 5 describes. A range costs one
compilation per commit not seen before, and a change of map costs none. Both ends of
a range are analyzed in parallel, but each worktree starts without build output and
builds from scratch. `check`, `facts`, `why` and `units` do not use the cache yet;
`facts --out` and `why --facts` are the manual equivalent.

## 8. Out of scope for v0

- Enforcement. Declared edges, forbid and acyclic policies, proposals that edit the
  map, and the minimality findings on map edits were part of an earlier revision.
  They answered a question a diff viewer does not ask, and they were most of the
  specification. A governance layer can be built on the facts later without
  touching the map format.
- Member-level leaves. The analysis records members as declarations, as section 3.1
  says, and the viewer's code level descends to members within a type, but members
  are not map nodes.
- Path bindings as a secondary way to split a namespace. Type-name patterns and
  attributes cover the cases seen so far.
- Multiple solutions or repositories.
- Runtime, configuration or process-boundary dependencies.

## 9. Open questions

- Whether `units` should also emit the groupings a graph clustering finds, as one
  more input to the proposing step.
- How small a leaf should be. Views regroup leaves, so a leaf that is too large
  cannot be cut by a view. The first trial used leaves of ten to forty types.
