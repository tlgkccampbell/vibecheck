# vibecheck for coding agents

This document is written for language-model coding agents that change code in a C#
repository whose changes are reviewed with vibecheck. It says what the `vibecheck.json`
file is, when a code change also needs a map change, the steps to take before a change
is finished, and how to state the intent of a change in its pull request description.
`vibecheck guide` prints it, so the copy you read always matches the installed tool.

The full definition of the format is in the
[specification](https://github.com/tlgkccampbell/vibecheck/blob/master/docs/vibecheck-spec.md),
and the JSON Schema is
[vibecheck.schema.json](https://github.com/tlgkccampbell/vibecheck/blob/master/schema/vibecheck.schema.json).
Where this guide and the specification differ, the specification is right.

## 1. What the file is

`vibecheck.json` is the repository's **architecture map**. It names the components
that a reviewer thinks in, gives each a one-sentence purpose, and says which types
belong to each one. The `vibecheck` tool reads the code at both ends of a pull request,
applies the map to both, and draws the change as a diagram of those components: which
ones changed, and how the dependencies between them moved.

A reviewer reads your pull request through this map. When the map is stale, your
change is drawn wrongly: new types land in a warning box named `unsorted`, a type you
moved appears in the wrong component, and a component you added has no name or
purpose. Keeping the map in sync is part of making your change reviewable.

The map records **where types belong**, and nothing else about the code:

- It does not declare dependencies. Every edge in the diagram is read from the code.
  You never add an edge to the map, and the map never forbids one.
- It does not hold positions. Box positions live in `vibecheck.layout.json`, which is
  presentation only.
- It is not an enforcement mechanism. Apart from a few well-formedness rules, every
  problem is a warning, and an imperfect map still draws.

### 1.1 The parts of the map

```json
{
  "version": 0,
  "solution": "Orchard.slnx",
  "exclude": ["System.**"],
  "tests": ["Orchard.Tests::**"],
  "nodes": {
    "billing": { "name": "Billing", "purpose": "Owns invoices, payments and the ledger.", "category": "domain" },
    "billing.api": { "name": "API", "purpose": "Billing endpoints.", "category": "entry", "bind": ["Orchard.Billing.Api.**"] },
    "billing.ledger": { "name": "Ledger", "purpose": "Double-entry ledger, including the invoice record.", "bind": ["Orchard.Billing.Ledger.**"] }
  },
  "views": {
    "by-concern": { "name": "By concern", "level": "root", "groups": {
      "money": { "name": "Money", "purpose": "Everything that moves or records money.", "leaves": ["billing.ledger"] },
      "requests": { "name": "Requests", "purpose": "Where requests arrive and identities are checked.", "leaves": ["billing.api"] }
    } }
  },
  "externals": {
    "json": { "name": "System.Text.Json", "bind": ["System.Text.Json"] }
  }
}
```

| Member | What it holds |
|---|---|
| `solution` | The solution file, relative to the repository root. |
| `exclude` | Type patterns naming code the repository compiles but does not own, such as generated helpers in `System` namespaces. Excluded types are invisible to the map. |
| `tests` | Type patterns naming the test types. Test types are never drawn as components; the tool places each test by the type it tests. |
| `patterns` | Optional. Names, declares or ignores recurring roles among types, such as message consumers. It binds nothing. |
| `nodes` | The component tree. Each key is a dotted identifier, and a node's parent is its identifier without the last segment. |
| `views` | Alternative groupings of the leaves under one level, such as "by concern". |
| `externals` | Referenced assemblies, bound by assembly name pattern. |

A **leaf** is a node with `bind`, a list of type patterns. Only leaves hold code. A
**container** is a node without `bind`, and it must have at least one child.

### 1.2 How a type finds its leaf

The map partitions top-level types. Nested types always follow the type that contains
them, and a partial type is one type however many files declare it. A type binds by
the first of these rules that applies:

1. A type that an `exclude` pattern matches, or a test type, binds to nothing.
2. A type with a `[Vibecheck("leaf.id")]` attribute binds to that leaf.
3. A type binds to the leaf whose `bind` pattern matches it most specifically.
4. Any other type binds to the automatic `unsorted` leaf, which the viewer draws as a
   defect.

Patterns match the namespace and the top-level type name joined by a dot, without
generic arity, such as `Orchard.Billing.Core.PaymentService`:

- `*` matches any characters except a dot. `Orchard.Billing.Core.*` matches the types
  declared directly in that namespace.
- `**` matches any characters, dots included. `Orchard.Billing.Core.**` matches those
  types and every type in a namespace beneath it.
- A pattern without wildcards names one type exactly.
- `Assembly::Pattern` matches only types declared in that assembly.

Specificity is the number of characters that are not wildcards, counting an assembly
qualifier. An exact name therefore beats any wildcard, and
`Orchard.Billing.Core.Postings.*` takes the postings types away from
`Orchard.Billing.Core.**` without any exclusion syntax. Two patterns of equal
specificity that match the same type leave it ambiguous, which `check` reports.

## 2. When a code change needs a map change

Most changes need no map change at all. Edits inside existing types, new members, and
new types in a namespace that a `**` pattern already binds to the right component are
all placed by the existing map.

Use this table to decide. "Check" means run `vibecheck check` as section 3 describes.

| Your change | What to do in the map |
|---|---|
| Edit code inside existing types | Nothing. |
| Add a type in a namespace the map already binds | Usually nothing. If the new type belongs to a different component than its namespace's leaf, bind it there by exact name. |
| Add a type in a new namespace or a new project | Add a pattern to the leaf it belongs to, or add a new leaf (see below). Otherwise it lands in `unsorted`. |
| Rename or move a namespace | Update every pattern that names the old namespace. A stale pattern silently stops matching, and the types fall to `unsorted` or to a broader pattern. |
| Rename, move or delete a type that a pattern names exactly | Update or remove that exact pattern. |
| Delete the last types a leaf holds | Remove the leaf, its entries in view groups, and the parent container if it now has no children. `check` does not warn about an empty leaf, so you have to notice. |
| Introduce a new component or concept | Add a node with `name`, `purpose`, and a `category` when its role differs from its parent's. Add every new leaf to exactly one group of each view whose level contains it. |
| Remove or rename a node | Update its children's identifiers, every view group that lists it, and any `[Vibecheck("...")]` attribute that names it. |
| Add a test project | Add `Assembly::**` to `tests`, never to `exclude`. |
| Add a package or project reference to an assembly no external binds | Add an external for it, or add its name to an existing external's `bind`. |
| Add generated code that lands in a namespace the repository does not own | Add a pattern to `exclude`. `System.**` usually covers it already. |
| Add several types that share a role, such as handlers that implement one interface | Optional. Add a `patterns` entry with an `id`, `name`, `purpose` and `keys` if the role deserves a name. |
| Move a type between projects or folders without changing its namespace | Nothing for namespace patterns. Check assembly-qualified patterns (`Assembly::...`), which do follow the project. |

### 2.1 Placing a type

Put a type in the component that its **dependencies** say it belongs to, not only the
one its name suggests. A type placed by its name on the wrong side of a boundary
shows up as a dependency cycle between components, which `check` prints.

Prefer the cheapest binding that states the rule:

1. A namespace pattern, when a namespace follows the concept.
2. An exact type name in `bind`, when a type belongs somewhere its namespace does not
   say.
3. The `[Vibecheck("leaf.id")]` attribute, only when the repository already uses it or
   a pattern cannot express the exception. Patterns carry the rule and attributes
   carry the exceptions. The attribute is matched by its full name,
   `Vibecheck.VibecheckAttribute`, and the repository declares it itself.

### 2.2 Writing a new node

- An identifier is lowercase segments of letters, digits and hyphens joined by dots,
  such as `billing.ledger`. The identifier `unsorted` is reserved, and no node may
  share an identifier with an external.
- The `name` is short and is what the diagram shows on the box.
- The `purpose` is one plain sentence that says what the node is for, written for a
  reader who has never seen the code. The viewer shows it, so it is not a place for
  implementation detail or a changelog.
- A `category` is one of `entry`, `feature`, `domain`, `analysis`, `transform`,
  `integration`, `foundation` or `tooling`. Set it on top-level nodes and on any node
  whose role differs from its parent's; otherwise leave it out and let it inherit.
- Fit the existing grain. Leaves typically hold ten to forty types; a leaf of one or
  two types is noise unless it is a genuinely separate concept. The top level usually
  has four to nine nodes.
- The JSON has no comments. Anything worth saying goes in `purpose` or `note`.

### 2.3 What not to change

- **Do not rename or reorganize existing nodes to suit your change.** Node identifiers
  are meant to stay put from one change to the next, and the reviewer sees every map
  edit as a structural change. Reorganize the map only when that is the task.
- **Do not edit the map to hide a dependency.** If your change creates a cycle between
  components because the code really does depend that way, leave the map alone and
  let the reviewer see it. Move a type only when it was misplaced.
- **Do not hand-edit positions in `vibecheck.layout.json`.** The viewer writes it. A
  node missing from the layout is placed automatically. If `check` warns that the
  layout mentions an identifier you removed from the map, delete that one entry.
- **Do not create a map** in a repository that has none, unless asked. The tool works
  without one, from the namespaces.

## 3. Steps to take

### 3.1 Before you write code

Read `vibecheck.json` once. It is the shortest description of the system's
components that exists, and it tells you which component each new type should live
in. Choosing the namespace that the map already binds to the right component is
usually the whole of keeping the map in sync.

### 3.2 After the code compiles

Run the checker from the directory that holds the map:

```
vibecheck check
```

If the map is elsewhere, pass it and the repository root:

```
vibecheck check --map path/to/vibecheck.json --root path/to/repo
```

The check builds and analyzes the solution, which takes a few seconds on a small
solution and longer on a large one. Run it once per unit of work, after the code is in
shape, not after every edit. A typical clean result looks like this:

```
cycle at notifications: notifications.dispatch → notifications.templates → notifications.dispatch
ok: 36 types in 13 leaves, 2 excluded; 231 dependencies, 40 leaf edges, 0 warning(s)
tests: 3 test types: 1 by name, 1 by references, 1 infrastructure
```

If `vibecheck` is not installed, say so in your report, and say that the map was not
checked. Do not skip the map review in section 3.4.

### 3.3 Resolve what the check reports

**Failures** stop the tool from using the map, and the command exits with code 1.
Fix every one. They include an invalid identifier, a node whose parent does not
exist, a container with no children, a malformed pattern, and a view whose groups do
not list every leaf under its level exactly once. Adding a leaf without adding it to
the views is the most common way to cause a failure.

**Warnings** do not stop the tool, but each one that your change caused is yours to
fix:

| Warning | Fix |
|---|---|
| `N type(s) are unsorted: no leaf binds ...` | Bind the named types, as section 2.1 describes. |
| `X is claimed equally by a, b; a takes it` | Make one pattern more specific, or bind the type by exact name. |
| `assembly X is referenced N time(s) but no external binds it` | Add or widen an external. |
| `the map file is not in canonical form` | Run `vibecheck fmt`. |
| `[Vibecheck("x")] names no leaf of the map` | Correct the attribute, or add the leaf. |
| `layout mentions "x", which the map does not define` | Remove that entry from `vibecheck.layout.json`. |
| An `exclude` pattern names only test projects | Move the pattern to `tests`. |

Leave warnings that existed before your change alone, unless the task is to clean up
the map. Compare against a check of the base revision if you are not sure which are
new.

**Cycles** are printed for every level and view. A cycle is not an error, and many
systems have some. A cycle that your change introduced, though, deserves a look:

```
vibecheck why <from-node> <to-node>
```

`why` lists the type pairs behind one edge, with reference counts. If a single
misplaced type causes the cycle, move it to the right leaf. If the code genuinely
depends both ways, leave the map alone and mention the new cycle in your report.

### 3.4 Format and review the map diff

Run the formatter, which rewrites the map and the layout file in canonical form, with
one node per line and keys in a fixed order:

```
vibecheck fmt
```

`vibecheck fmt --check` reports without writing. Then read your diff of
`vibecheck.json` as the reviewer will see it, and confirm that:

- every added node has a name and a purpose a newcomer would understand;
- every new leaf appears in each view whose level contains it;
- no pattern still names a namespace or type that no longer exists;
- nothing changed that your code change does not explain.

### 3.5 Commit the map with the code

Commit the map change in the same commit or pull request as the code that needs it,
so that the review of that change draws correctly. State what moved in the map as an
intent, as section 4 describes, anchored to the nodes that moved:

```markdown
- **Refund types get their own leaf, `billing.refunds`, under the Money group.** `billing.refunds`
  Why: refunds have their own lifecycle and would double the size of the ledger leaf.
```

## 4. Stating intent

A reviewer of your pull request sees what changed, and Claude's account of why, read
from the code. The one account that comes from you is the **intent** section of the
pull request description, which says why you made the change's main choices. The
review reads it, ties each intent to the parts of the change it names, and shows the
reviewer every large part of the change that no intent covers. An unexplained new
dependency is exactly what such a reviewer looks for, so a choice you leave unstated
is flagged.

The review never takes an intent as true. It shows what the intent names and whether
the change touches it, beside Claude's account, so write what you actually chose and
why, not what reads well.

### 4.1 What deserves an intent

Write an intent for each of these:

- A new or removed component.
- A new dependency between components.
- A new cycle, with why it is acceptable.
- A new package or project reference.
- A change to a public signature that other code calls.
- A departure from what the other instances of a pattern do, such as a handler that
  skips the validation every other handler runs.
- Any other choice between real alternatives that a reviewer could ask about.

Edits that follow from an intent need none of their own. Do not restate the diff,
list files, or explain an edit that the code makes obvious. Three good intents are
better than ten.

### 4.2 The format

Write the intents in the pull request description, in a section whose heading is the
word `Intent`. Each top-level list item is one intent:

```markdown
## Intent

- **Refunds post as negative ledger entries.** `billing.ledger`, `billing.api → billing.ledger`
  Why: the ledger stays append-only, so the monthly reconciliation needs no new case.
  Instead of: a `Refund` aggregate in a new `billing.refunds` leaf.
- **`RefundIssued` is published from the API, not the ledger.** `Orchard.Billing.Api.RefundsController`
  Why: the ledger must not reference messaging, which `notifications` owns.
```

An intent has these parts:

- The **claim**, in bold, opens the item. It says in one sentence what was chosen.
- The **anchors** follow the claim on the same line, as inline code spans separated by
  commas. They name what the intent is about.
- The **why**, after `Why:` on a later line, gives the reason. It must not repeat the
  claim.
- The **alternative**, after `Instead of:` on a later line, is optional. Give one only
  when you actually weighed it.

An anchor is one of these:

| Anchor | Example |
|---|---|
| A node or external of the map, by identifier | `billing.ledger` |
| An edge, as two ends joined by `→` or `->`, each a node or a type | `billing.api → billing.ledger` |
| A file, by its path from the repository's top | `src/Orchard.Billing/Api/RefundsController.cs` |
| A type, by its full name, or by its simple name when that is unique | `Orchard.Billing.Api.RefundsController` |

Give every intent about particular code a file or a type anchor. Use node identifiers
and edges between nodes only in a repository with a `vibecheck.json`, since a review
without one draws a generated map whose identifiers yours will not match. A node
anchor alone never covers a particular edge or decision, because a node is too broad
to say which one you meant. A code span that is none of these, such as a member name
in the claim, stays text.

### 4.3 Where to write it

Put the section in the pull request description when you open the pull request. When
you do not open it yourself, give the section in your report, so that whoever opens
the pull request can paste it. Commit messages are not read for intents.

### 4.4 Check the format

Before opening the pull request, you may check your draft without building anything:

```
vibecheck intent description.md --base main
```

With no file, it reads standard input. It prints each intent with its anchors, says
whether each node anchor is in the map and each file exists, and with `--base` says
which files changed since that revision. It cannot check types, which only a review
knows. It exits with code 1 when the text has no `Intent` section or an item has no
claim, and reports anchors that name nothing as warnings. Fix the warnings. Do not run
a review to check your intents.

## 5. Checklist

- [ ] New types sit in a namespace the map binds to the right component, or the map
      binds them by pattern or exact name.
- [ ] Renamed or moved namespaces and types are updated in every pattern that named
      them.
- [ ] New leaves have a `name`, a `purpose`, a `category` where the role differs, and
      a place in every view that covers them.
- [ ] Removed leaves are gone from the nodes and from every view group.
- [ ] New test projects are in `tests`, and new referenced assemblies are bound by an
      external.
- [ ] `vibecheck check` reports no failures and no new warnings, and any new cycle is
      explained by `vibecheck why`.
- [ ] `vibecheck fmt` has been run.
- [ ] The map change is committed with the code change and stated as an intent.
- [ ] The pull request description has an `Intent` section with an intent for each
      large choice, each with a claim, anchors and a why, and `vibecheck intent`
      reports no failures and no warnings for it.
