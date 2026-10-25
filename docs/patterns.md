# Patterns in a review (design)

This document describes how a review recognizes the roles that many types in a system
play, such as ASP.NET controllers, Mediator handlers and MassTransit consumers, and how
it sets a new or changed instance of such a role beside the existing ones, so that a
reviewer can compare it with them. It builds on the map and facts of
[vibecheck-spec.md](vibecheck-spec.md), the decisions, reading path and glossary of
[attention.md](attention.md), and the test subjects of [tests.md](tests.md), and it
lists, in section 11, the changes it makes to those documents.

Phases 1 to 4 of section 13, traits and discovery, norms and departures, examples and
the compare view, and the Patterns switch with Claude's names, are built. Phase 5,
member norms, is not. The new concepts notice that section 7.6 mentions is not built
either, since attention.md section 7 is not; placement receives its hint without it.

The design rests on one principle. A pattern is evidence about the code, not part of
the architecture. Controllers sit in every feature, and a pattern cuts across the
leaves of the map the way tests do, so a pattern is never a node, a view or an edge.
It is a property of the types that play it, and it appears where those types appear.

Code a model writes is usually plausible on its own. What a reviewer cannot see from
the diff alone is whether it does what the fourteen other consumers in the system do:
whether it takes the unit of work they all take, carries the attribute they all carry,
or has the test they all have. The comparison is the point. Recognizing the pattern is
only what makes the comparison possible.

## 1. Goals

- A reviewer reading a new or changed type that plays a role many other types play is
  told the role, how many other types play it, and how this one departs from what they
  share, without leaving the type.
- A departure from what nearly every instance of a pattern does is in the first tier
  of the path, stated as a fact.
- The reviewer can set the new type beside the existing instance most like it, or the
  one most typical of the pattern, aligned member by member.
- Patterns are found without configuration. A map can name, add or ignore them.
- Patterns never add nodes, edges or concepts, and never lengthen a decision's header.
- Everything except a pattern's name and purpose is computed from the analysis the
  review already runs.

Non-goals for this design: any finding that a departure is wrong, patterns within a
method body, and patterns in test code.

## 2. The reviewer's path

1. The changeset opens on the map as it does now. Nothing about patterns is drawn on
   it until the reviewer asks.
2. In the leaf view, the line under a file's header that names the types the file
   declares marks a type that is an instance of a pattern with a chip, such as
   "consumer · 2 departures", or "consumer" when it departs from nothing.
3. Clicking the chip opens the pattern in the details panel: its name and purpose,
   the features its instances share with how many share each, a check or a cross for
   this instance against each, the two examples of section 6, and every instance by
   block.
4. **Compare** in the details panel turns the file's diffs into the compare view of
   section 7.3, with the instance on the left and an example on the right, aligned by
   member, until the reviewer goes back to the diff.
5. The path's first tier holds a step for each instance with departures, such as
   "`RefundIssuedConsumer` departs from the consumer pattern: no `IUnitOfWork`, no
   test", or "`CreateOrderHandler` departs from the handler pattern: no validator".
   The step opens the instance's file with the pattern in the details panel.
6. The Changes panel's **Patterns** switch lists the patterns the change touches, each
   with its new and changed instances and their departures, and after them the
   patterns that are new in this change. While it is on, hovering a pattern outlines
   the blocks that hold its instances.

## 3. Traits

### 3.1 What is recorded

A pattern is recognized by what a type declares about itself, which the analysis
partly records today and partly drops. A **trait** is one of these facts about a
top-level type:

- **Base.** The base type it inherits, when that is not `System.Object`.
- **Interface.** Each interface it implements directly.
- **Attribute.** Each attribute applied to the type itself.
- **Injects.** Each parameter type of its constructor with the most parameters, or of
  its primary constructor, which is how a type in a system with dependency injection
  says what it needs.

A generic type is recorded in its open form, as `MassTransit.IConsumer<>` or
`MediatR.IRequestHandler<,>`, so that every consumer shares the trait whatever message
it consumes. The type arguments are kept beside the open form, since they say which
message a consumer consumes and section 6 uses them.

Traits are recorded whether the referenced type is in the solution's source or in a
referenced assembly. This matters because most patterns are defined by a framework.
Today a reference to a symbol with no source becomes an external reference with no
kind, aggregated per type and assembly, so the fact that a type implements
`IConsumer<>` is not kept anywhere. The analysis reads the traits from each type's
symbol, which it already holds, so traits cost no new pass, and the dependency walker
gives each external reference the kind it already knows.

References to the core library are not recorded as dependencies, since every type
would carry them. Traits are an exception for base types and attributes, since a type
deriving from `Exception` or `Attribute` plays a role. An interface of the core
library, such as `IDisposable` or `IEquatable<>`, is not a trait, since too many types
carry one for it to name a role.

### 3.2 Uses

Section 5's norms also need to know what a type references, at the level of the
referenced type: that every consumer references `IUnitOfWork`, or that every
controller references `ILogger<>`. Dependencies within the solution record this
already. External references are aggregated per type and assembly with the first
symbol only, which loses it, so they become aggregated per type and **referenced
type**, with a count and the first symbol, file and line. A type's **uses** are the
types its dependencies and external references reach, in open form.

### 3.3 In the facts

Each type in the facts gains `traits`:

```jsonc
{ "name": "Orchard.Billing.Consumers.OrderPlacedConsumer", "leaf": "billing.consumers", "by": "pattern",
  "files": ["src/Billing/Consumers/OrderPlacedConsumer.cs"],
  "traits": {
    "interfaces": [ { "type": "MassTransit.IConsumer<>", "arguments": ["Orchard.Billing.Events.OrderPlaced"] } ],
    "attributes": [],
    "injects": ["Orchard.Billing.IUnitOfWork", "Microsoft.Extensions.Logging.ILogger<>"]
  } }
```

`externalRefs` changes from one entry per type and assembly to one entry per type and
referenced type. This is a change to the facts format, and every reader of external
references aggregates by assembly itself when it needs to.

A base class is written as an interface is, with its arguments when it is generic, and
is absent when the type inherits `Object`. A type with no traits at all has no
`traits`. Test types have no traits in the facts, since a test type is never an
instance of a pattern, as section 4.3 says.

### 3.4 Member attributes

Section 5.6's member norms need the attributes on a type's methods, such as
`[HttpGet]` or `[Authorize]` on a controller action. Each declaration of spec section
3.1 gains `attributes`, the open names of the attributes applied to it, from source or
from referenced assemblies. The walker already visits every attribute to record its
dependency, so this costs no new pass. The attributes of the core library are kept
here too, since `[Obsolete]` on one action of nine is worth seeing.

## 4. Patterns

### 4.1 Keys

A **key** is a trait that types can share, written as its kind and the open name of
the type:

- `inherits:Microsoft.AspNetCore.Mvc.ControllerBase`
- `implements:MassTransit.IConsumer<>`
- `attribute:Microsoft.AspNetCore.Mvc.ApiControllerAttribute`

A **pattern** is a set of keys and the types that carry all of them, its
**instances**. Injected types and uses are not keys, since every service injects a
logger, but they are features that section 5 compares.

A fourth kind, `suffix:Handler`, matches a type whose name ends with the text. It is
the weakest signal of the four, so it is never discovered and is valid only in a
pattern that the map declares, as section 4.4 describes. Discovery by suffix would
group types such as every `*Service` that share nothing but a word, and its norms
would be noise.

### 4.2 Discovery

Patterns are discovered at base, in the system as it was before the change, since the
change is judged against the code it joins. Discovery is a pure function of the facts:

1. Group the types the map holds by each key they carry.
2. Drop a group whose key the map ignores, as section 4.4 describes. This comes before
   the merge, so that a marker interface that every consumer also carries does not
   take the consumers with it.
3. Keep each group with at least **three** instances.
4. Merge groups with exactly the same instances into one pattern whose keys are all of
   theirs, so that a controller that inherits `ControllerBase` and carries
   `[ApiController]` is one pattern with two keys, not two patterns. The keys are
   ordered by kind, inherits before implements before attribute, and the first names
   the pattern.

A type that carries the keys of several patterns is an instance of each. When one
pattern's instances are all instances of another, as consumers deriving from a shared
`ConsumerBase` are all consumers, the type is reported under the more specific
pattern, the one with fewer instances, which follows the rule by which spec section
2.2 lets the most specific type pattern win. The other pattern still counts it.

A type is an instance at head when it carries every key of a pattern discovered at
base. A pattern that does not exist at base has no instances to compare with, and
section 4.5 describes how the review shows it.

### 4.3 What is never an instance

- A test type, which tests.md section 3.1 already keeps out of the map.
- A type whose every declaration a source generator produced, since its shape is the
  generator's and not the author's.
- An excluded type.
- A nested type, which follows its container as everywhere else in the map.

### 4.4 In the map

The map gains an optional `patterns` list beside `tests`:

```json
"patterns": [
  { "id": "consumer", "name": "Consumer", "purpose": "Handles one message from the bus.",
    "keys": ["implements:MassTransit.IConsumer<>"] },
  { "ignore": "implements:MediatR.INotification" }
]
```

An entry with `keys` names a pattern. When discovery finds a pattern whose instances
are exactly the types that carry the entry's keys, and whose keys include them, the
entry gives it its identifier, name and purpose, and the pattern keeps every key that
step 4 merged into it. When it does not, the entry
declares the pattern, which then needs no minimum number of instances. An entry with
`ignore` drops every discovered group whose keys include that key, such as a marker
interface that every message carries.

Discovery runs whether the map lists patterns or not, and a map with no `patterns`
list gets every pattern discovery finds. A key of the `suffix` kind is valid only in an
entry with `keys`, and `vibecheck check` warns for one in an `ignore` entry, where it
could match nothing that discovery found.

`vibecheck check` reports the patterns it found, each with its instance count, its
number of norms, and the mean number of norms its own instances depart from, as
"pattern consumer: 14 instances, 4 norms, 0.3 departures each
(implements:MassTransit.IConsumer<>)". The departures figure is the measure
of section 12: a pattern whose existing instances depart from more than about one
norm each would fill the path with departures, and says that the thresholds of
section 5.2 are too low for it. The check also warns for an entry whose keys no type carries.

### 4.5 New in this change

Three or more types at head that share a key no pattern has at base, and no type
carries at base, are a pattern **new in this change**. It is found by the rules of
section 4.2 applied to the types the change added. It has no norms, since three types
written together cannot establish what is usual among themselves, and no examples,
since every instance is new.

It is not a new concept in the sense of attention.md section 7. A concept is something
the map has no place for, and a pattern is not a place, so the map has nothing to
decide about it. The Patterns switch of section 7.5 lists it with its instances, and
it has no path step and no request to Claude. In the next review, whose base holds it,
it is a pattern like any other.

## 5. Norms and departures

### 5.1 Features

A **feature** of an instance is one of:

- A trait other than the pattern's own keys: another interface, base or attribute.
- An injected type, in open form.
- A use, in open form, unless the instance already carries or injects the type, so
  that one fact is not counted twice. A use of a type of the solution is named as the
  facts name the type, without its type parameters.
- **Tested**: the instance is the subject of a test type, by tests.md section 4.
- **Documented**: the type has a documentation summary.
- **Companion**: the instance has a companion in another pattern, by section 5.4.

### 5.2 Norms

A **norm** of a pattern is a feature that at least **80%** of its instances at base
share, in a pattern of at least **five** instances. Below five instances, a share is
too easily a coincidence, and the pattern gets examples but no norms. A norm records
how many instances share it, as "13 of 14".

### 5.3 Departures

A **departure** is a norm an instance does not have. It is reported only when the
change is responsible for it:

- For an instance added by the change, every norm it does not have.
- For an instance changed by the change, every norm it had at base and does not have
  at head.

A changed instance that departed from a norm before the change, and still does, is
not reported, since that is the system's state and not the change's doing. Departures
are worded as facts, with the count, as "13 of 14 consumers inject `IUnitOfWork`;
`RefundIssuedConsumer` does not". The tool never says that a departure is wrong, since
a consumer that writes nothing has no need of a unit of work.

### 5.4 Companions

Some patterns come in sets that are written together: a command, its handler and its
validator. They are linked by the type arguments that traits keep, not by their names.
`IRequestHandler<CreateOrder, OrderId>` and `AbstractValidator<CreateOrder>` share
`CreateOrder` exactly, and a name stem such as `CreateOrder` would be a guess that
the type argument makes unnecessary.

An instance of pattern A has a **companion** in pattern B when an instance of B has a
key type argument that is also a key type argument of the A instance. Pattern B is a
companion pattern of A when at least 80% of A's five or more instances have a
companion in B, which makes the companion a norm of A like any other feature. A new
handler for a command that has no validator then departs from the norm, worded as "29
of 31 handlers have a validator; `CreateOrderHandler` does not". The companion
pattern is named by its own name, so the wording follows the names of section 8.

Type arguments of the core library, such as `string` or `Guid`, never link instances,
since they would link every handler that returns an identifier. Only a type argument
that is a type of the system links instances, which keeps out the core library and
every other referenced assembly with it.

### 5.5 Unusual traits

A trait of an instance that no other instance of its pattern has is **unusual**: an
attribute, an interface or a base type that only this consumer carries. It is not a
departure, since an addition is not a lapse, and it has no path step. The details
panel lists it in a line of its own under the norms, and the compare view shows it on
its own row.

Only traits can be unusual. An injected type or a use that no other instance has is
the ordinary case, since every handler injects its own repository, and listing them
would bury the traits.

### 5.6 Member norms

The members of an instance that play the pattern's role are its **role members**:
its public methods that carry an attribute, or whose name occurs in at least two
instances of the pattern. A controller's actions are its role members by their
attributes. A method such as `Consume`, which the framework calls and every consumer
declares, is a role member by its name, and its norms are nearly always empty, which
is harmless.

A **member norm** is an attribute, by section 3.4, that at least 80% of the pattern's
role members carry, in a pattern with at least twenty role members across its
instances. A role member of a new or changed instance that lacks it departs from it,
by the rule of section 5.3 applied to the member: every member norm for an added
member, and only norms it had at base for a changed one. The departure names the
member, as "41 of 44 actions carry `[Authorize]`; `OrdersController.Export` does
not".

Member norms are presence only. A norm that holds as one attribute or another, such
as `[Authorize]` or `[AllowAnonymous]`, is not recognized: neither alone reaches 80%,
so neither is a norm, and the compare view shows the attributes side by side.

### 5.7 In the changeset

The changeset gains `Patterns`, one entry for each pattern with a new or changed
instance:

```jsonc
{ "id": "consumer", "name": "Consumer", "purpose": "…", "by": "claude",
  "keys": ["implements:MassTransit.IConsumer<>"], "count": 14,
  "norms": [ { "feature": "injects:Orchard.Billing.IUnitOfWork", "share": 13 },
             { "feature": "companion:validator", "share": 12 } ],
  "memberNorms": [ { "attribute": "Orchard.Shared.Kernel.RetryAttribute", "share": 19, "of": 22 } ],
  "instances": [
    { "type": "Orchard.Billing.Consumers.RefundIssuedConsumer", "status": "added",
      "departures": ["injects:Orchard.Billing.IUnitOfWork", "tested"],
      "memberDepartures": [ { "member": "Orchard.Billing.Consumers.RefundIssuedConsumer.Consume", "attribute": "Orchard.Shared.Kernel.RetryAttribute" } ],
      "unusual": ["attribute:Orchard.Shared.Kernel.ExperimentalAttribute"],
      "examples": { "nearest": "Orchard.Billing.Consumers.OrderPlacedConsumer", "typical": "Orchard.Billing.Consumers.InvoiceSentConsumer" } }
  ],
  "others": [ { "type": "…", "leaf": "…" } ],
  "sources": { "Orchard.Billing.Consumers.OrderPlacedConsumer": { "file": "…", "first": 8, "lines": ["…"] } } }
```

`others` lists every instance at head that the pattern does not report as added or
changed, with its leaf, so that the details panel and the map can show them. A changed
instance that a more specific pattern reports is among the others of the broader one.
An instance is changed when one of its files changed or it binds to another leaf. Each
norm carries the words that say an instance has it and lacks it, and each instance
carries `lacks`, every norm it lacks at head, so that the details panel can mark a norm
an instance lacked before the change apart from a departure, and `companions`, its
companion in each other pattern that has one. Each instance also carries
`compare`, the rows of section 7.3 that align it with each other type whose source the
pattern carries, by that type's name, and `sources` holds the text of the reported
instances as well as their examples, since the left column of the compare view needs
the instance's whole type and not only its hunks. Phases 1 to 3 carry everything
above except `memberNorms` and `memberDepartures`.

The changeset also gains `NewPatterns`, the patterns of section 4.5, each with its
keys, its name from its first key, and its instances with their leaves.

## 6. Examples

Each new or changed instance gets two examples from the pattern's other instances:

- The **nearest**: the instance with the most features in common with it, measured as
  the share of their combined features that both have. Ties go to an instance in the
  same leaf, then to the one whose key type arguments share the most leading namespace
  segments with the instance's, so a consumer of a billing event is compared with
  another consumer of a billing event, then to the first by name.
- The **typical**: the instance with the highest total of that measure against all
  the others, which is the instance most like the pattern as a whole. It is the same
  for every instance of the pattern.

When the two are the same type, the typical one is the one after it in the order, and
so it is when the typical is the instance itself. The examples are computed at head, so
that an example is code that exists after the change, but never from the change's own
new instances. A changed instance can be an example, as it was code before the change.

Each example's source at head is carried in the changeset, up to 300 lines, so that a
changeset published as an artifact can show it without the server. A longer type is
cut at its last whole member within the limit, and the compare view says so.

## 7. In the viewer

### 7.1 The leaf view

The leaf view lists each changed file once, with a line under its header that names
the types it declares. On that line, a type that is an instance of a pattern carries a
chip with the pattern's name and, when there are any, its number of departures. This
is the only place patterns appear in the leaf view. A decision's header gains nothing,
since attention.md section 4.4 keeps it to one line of bounded length, and since a
decision is a hunk, while a pattern is a property of a type.

### 7.2 The details panel

Clicking the chip opens the pattern's section in the details panel:

- The pattern's name, its purpose marked as Claude's when Claude wrote it, and its
  keys.
- The norms as a table, each with its share and a check or a cross for this instance,
  departures first. A companion norm names the companion, or the companion pattern
  when the instance has none.
- The member norms, when the pattern has any, with each departing member named under
  the norm it departs from.
- The instance's unusual traits of section 5.5, in one line headed "Only this
  instance", when it has any.
- The two examples, each opening the compare view.
- Every instance, grouped by block and collapsed to a count until opened, each opening
  its type in the leaf view.

### 7.3 The compare view

**Compare** swaps the file's diffs for a two-column view, until **Back to the diff**
undoes it. It needs the width of the code column, which is why it replaces the diff
there rather than opening in the panel.

- The left column is the instance at head, with its added lines marked in the gutter.
  A changed instance's base is in the diff the reviewer came from.
- The right column is the example at head, as plain source, since it is an example and
  not a change.
- Rows are aligned by member: the type's declaration and base list first, then the
  constructor, then members paired by name, then the remaining members paired by
  shape, which spec section 3.1 defines without the name, so that two controller
  actions with the same signature and attributes pair however they are named. A member
  with no counterpart has a row to itself, which follows the row of the member before
  it on its own side. The tool aligns the rows when it builds the changeset, so that
  the alignment is tested with the rest, and the viewer only draws them.
- A departure is marked on the row that shows it, such as the constructor row for an
  injected type that is missing, or an action's row for an attribute its member norm
  expects. An unusual trait, and a departure that no member shows, such as a missing
  test, is marked on the declaration's row.
- A picker above the right column switches between the nearest, the typical and any
  other instance whose source the changeset carries.

### 7.4 The path

An instance with departures is a step in the first tier, after the test steps,
listing its departures, its members' departures among them, and naming the instance's
files, so it is read when they are reviewed. Unusual traits put no instance on the
path. Steps are ordered by the number of departures, then by the size of the
pattern. They count toward the forty steps of attention.md section 6.1 and are
dropped before decision steps, while their counts stay on their leaves' steps. An
instance without departures is not a step of its own, and its leaf's step in the second
tier counts it, as "2 new consumers".

### 7.5 The Patterns switch

The Changes panel's title gains **Patterns** beside Path, Changes, Concepts and Tests.
It lists the patterns of the changeset, most departures first, each with its new and
changed instances, their departures and their leaves. Hovering a pattern outlines the
blocks that hold its instances, new and changed ones apart from the rest, since a
pattern is not a block and the map has no other way to show it: a block with a new or
changed instance has a solid outline, and a block with only other instances a dashed
one. While the switch is on, each block carries a badge, ◇ and a count, of the new and
changed instances it holds, those of patterns new in this change among them. A pattern
step on the path, a chip and the details panel name a pattern as the switch does.

After them, under **New in this change**, the switch lists the patterns of section
4.5, each named from its first key with its instances and their leaves. Hovering one
outlines its blocks the same way. It shows no norms and no departures, since it has
none, and says that it becomes a pattern with norms once the change is in the base of
a review.

### 7.6 Elsewhere

- In the new concepts notice, an `unsorted` type that is an instance of a pattern
  names the leaf of its nearest example, and placement receives that leaf as a hint.
  The hint names the pattern the type would be reported under, and its nearest
  instance among those a leaf holds, chosen as section 6 chooses examples.

## 8. Claude's part

Claude names each discovered pattern and writes its purpose in one sentence, given its
keys and the source of three instances. The three are the pattern's **samples**: its
most typical instances at base, by the order of section 6, carried in the changeset
as `samples`, with their source read at base and cut at 80 lines. The request is
keyed `pattern:<keys>` and made through the narrative pipeline right after the
summary, since the name shows on every chip. Its prompt depends on the base alone, so
the narrative cache, which keys a request by its prompt, makes it once per base
commit. An answer that cites a file other than the three, or none, or gives a name
longer than 40 characters, is discarded, and the pattern is named from its first key,
as "implements `IConsumer<>`", which is also the name while the answer is pending. A
map entry's name and purpose each win over Claude's, and a pattern whose entry gives
both is not asked about. A pattern new in this change is not named by Claude, since
its keys are all the review knows of it. The details panel marks what Claude wrote,
with the **regenerate** action of any narrative.

Claude does not judge departures or say whether an instance is a good one. A sentence
that a departure is harmless invites the reviewer to accept a difference they have not
read, which is the opposite of what departures are for, and the norms table, which
states each share, says what such a sentence would.

## 9. The API and the viewer

The facts gain `traits` on each type and aggregate `externalRefs` by referenced type,
as section 3.3 describes, and list the patterns found among their types as
`patterns`, each with its norms and the mean number of norms its own instances lack, so that the check and the changeset read them from one place, and their declarations gain `attributes`, as section 3.4
does. The map schema gains `patterns`. The changeset gains `Patterns` and
`NewPatterns`, the path gains pattern steps, and
a changeset published as an artifact carries all of it, since none of it needs the
server. Claude's names arrive as narrative events, which carry a `name` for a
pattern, so a published changeset names its patterns from their first keys.

No endpoint is added or changed.

## 10. Testing

Traits, discovery, norms, departures and examples are pure functions of analyses, so
they are tested on synthetic analyses and on small compilations built in memory from
source snippets, in the sub-second unit suite. The framework types that keys name are
declared in the snippets themselves, in assemblies named as the frameworks are, so the
suite references no framework it does not already use. The tests state:

- A type's base, interfaces, attributes and injected types are recorded in open form,
  from source and from referenced assemblies, with the type arguments kept, and an
  interface of the core library is not.
- External references are aggregated by referenced type, and every reader that counts
  them by assembly gives the counts it gave before.
- Three types sharing a key are a pattern and two are not, keys with the same
  instances are one pattern, an ignored key drops its group, and a declared pattern
  needs no minimum. A suffix key is never discovered, matches in a declared pattern,
  and earns a warning in an `ignore` entry.
- Three added types sharing a key that nothing at base carries are a pattern new in
  this change, with no norms, no examples and no path step.
- The check reports each pattern's instances, norms and mean departures of its own
  instances.
- A type in two nested patterns is reported under the more specific one and counted
  by both, and a test type, a generated type and a nested type are never instances.
- A feature shared by 80% of five or more instances is a norm, and a pattern of four
  has none.
- An added instance departs from every norm it lacks, a changed instance departs only
  from norms it had at base, and a departure from before the change is not reported.
- Two instances sharing a key type argument are companions, a type argument of the
  core library links nothing, and a companion pattern shared by 80% of five or more
  instances is a norm whose absence is a departure.
- A trait no other instance has is unusual and not a departure, and an injected type
  or a use no other instance has is neither.
- A method is a role member by an attribute or by a name in two instances, an
  attribute on 80% of twenty or more role members is a member norm, and an added or
  changed member departs from it by the rule for instances.
- The nearest example has the most features in common, ties go to the same leaf, the
  typical example is the same for every instance, and neither is ever a new instance.
- An instance with departures is a first-tier step after the test steps, and an
  instance without is counted on its leaf's step.
- The compare view pairs members by name, then by shape, and gives an unpaired member
  a row of its own.

The `Sample` suite checks traits and discovery on the sample solution once per run,
where the compilation is already paid for. The sample has five consumers of payment
events, four of which take the outbox, so that it has a pattern with norms.
Companions are tested on snippets, since a companion norm needs five handlers and the
sample gains nothing from them that the snippets do not show.

## 11. Changes to other documents

These land with the phase that makes them true:

- Spec section 2: the schema gains `patterns`. Section 3: external references keep
  their kind and the walker records traits. Section 3.1: declarations gain
  `attributes`. Section 4: the check reports the patterns it found with their norms
  and mean departures, and warns for a declared pattern that no type matches and for a
  suffix key in an `ignore` entry. Section 6: the facts gain `traits`, `externalRefs`
  is aggregated by referenced type, and the changeset gains `Patterns` and
  `NewPatterns`.
- attention.md section 6.1: the first tier holds pattern steps after the test steps.
  Section 7: a new type that is an instance of a pattern gets a placement hint.
  Section 8: a term names its pattern.
- review-workflow.md section 6.1: narratives include a pattern's name and purpose.
- README: patterns, departures, the compare view and the Patterns switch.

## 12. Open questions

- Whether 80% of five instances, 80% of twenty role members, and three instances for a
  pattern are the right thresholds. They are constants, not settings of the map, until
  real repositories say otherwise. The check's report of section 4.4 gathers the
  evidence: a pattern whose own instances depart from more than about one norm each
  says that its share should rise toward 90%, and a repository whose patterns have no
  norms says that it should fall.
- Whether a norm that holds as one attribute or another, such as `[Authorize]` or
  `[AllowAnonymous]` on an action, should be recognized, as section 5.6 leaves it out.
  It is the commonest member norm in web code, but it needs a rule for which
  alternatives form a norm, and the compare view shows the case meanwhile.

## 13. Phases

Each phase ends with the tool usable, and with tests that state its new behavior.

1. **Traits and discovery.** The walker records traits, external references keep
   their kind and are aggregated by referenced type, discovery runs at base, the map's
   `patterns` list, the check's report, and the leaf view's chip naming the pattern,
   with its instances listed in the details panel. Patterns are named from their first
   key. The check's report of patterns lands here, so that their sizes are known from
   the first real review.
2. **Norms and departures.** Features, companions, norms at base, departures for new
   and changed instances, unusual traits, the norms table in the details panel, the
   chip's departure count, the path's pattern steps, and the check's report of norms
   and mean departures, so that the data for section 12 gathers from here on.
3. **Examples and the compare view.** The nearest and typical examples, their source
   in the changeset, and the compare view with its member alignment and picker.
4. **The Patterns switch and Claude's names.** The switch with its badges, block
   outlines and patterns new in this change, Claude's names and purposes, and the
   placement's use of patterns.
5. **Member norms.** Declarations' attributes, role members, member norms and their
   departures, in the details panel, on the path and on the compare view's rows. It
   comes last because it needs the compare view to show what a member norm cannot,
   such as an action that carries one attribute where its peers carry another.
