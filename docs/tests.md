# Tests in a review (design)

This document describes how a review takes in the changes a change makes to its tests
without drawing them as part of the system. It builds on the map and facts of
[vibecheck-spec.md](vibecheck-spec.md), the reviewed marks and Other files block of
[pull-requests.md](pull-requests.md), and the decisions, reading path and glossary of
[attention.md](attention.md), and it lists, in section 11, the changes it makes to
those documents.

The design rests on one principle. Tests are evidence about the architecture, not part
of it. A test project references every component it tests and adds no structure of
its own, which is why the skeleton excludes it today, and drawing it would put its
edges in front of the decisions the map exists to show. So a test is never a node or
an edge. In the default view it appears only as a property of the code it exercises,
and on its own only when the change does something to it that a reviewer of generated
code needs to see, such as removing an assertion from a test of code the change also
rewrote.

Today the tests are excluded from the analysis entirely, and git adds their changed
files back as files that no type binds, so they land in the Other files block beside
project files and documents. They are in the view as a list, and the tool knows
nothing about them. This design reverses both halves: the tool analyzes them, and the
view moves them out of the way.

## 1. Goals

- A reviewer reading a decision sees which tests reference it and what the change did
  to each, without leaving the decision.
- A change that removes or skips a test, or removes or changes an assertion, is in the
  first tier of the path, stated as a fact. A test of changed code has a step of its
  own, and the rest share one step.
- Test files never add nodes, edges, terms or concepts, and they never swell the
  reviewer's main progress count.
- A reviewer who wants to read the tests on purpose has one place that lists them by
  the block they test.
- Everything here is computed from the analysis the review already runs. It needs no
  Claude and no test run.

Non-goals for this design: running tests, measuring coverage, any finding about
whether a test is good, and frameworks other than the four the skeleton recognizes.
Whether the tests pass is the checks' business, as pull-requests.md section 9 shows
it.

## 2. The reviewer's path

1. The changeset opens on the map as it does now, with no test blocks and no test
   edges. The Other files block no longer holds test files.
2. A decision's header gains a line on its tests, such as "referenced by 6 tests
   directly and 31 through helpers: 1 added, 2 changed", or "no test references it"
   for a public decision. The line names up to two changed test methods and counts
   the rest. Its consequence
   count names the tests it reaches apart from the rest: "14 consequences in 9 files,
   and 4 in tests".
3. The path's first tier holds a step for each test type with a test signal of
   section 6 that touches changed code, such as "assertion removed from
   `Require_Succeeds`, which references `Session.Require`, rewritten". The step opens
   the test's hunk. Every other signal is gathered into one closing step of the first
   tier, such as "4 tests changed in ways no changed declaration they reference
   explains".
4. In the leaf view, a leaf's changed test files are listed in a collapsed **Tests**
   section after its own files.
5. The bar counts test files apart: "24 of 30 files reviewed · 3 of 9 test files".
6. A **Tests** switch in the Changes panel's title, beside the path and the changes,
   lists every changed test by the block it tests, with its signals and the changed
   declarations it references. A change that touches only tests opens there.

## 3. Test types

### 3.1 In the map

The map gains a `tests` list beside `exclude`, holding type patterns in the syntax of
spec section 2.2:

```json
{
  "version": 0,
  "solution": "src/Example.sln",
  "exclude": ["System.**"],
  "tests": ["Example.Tests::**"],
  "nodes": { }
}
```

A **test type** is a type that a `tests` pattern matches and no `exclude` pattern
does. Like an excluded type, a test type binds to nothing, no leaf holds it, and a
reference from it or to it creates no edge, so the map, its levels, its views and its
checks are what they are today. Unlike an excluded type, a test type stays in the
analysis: its declarations, its references to the system and the files that declare
it are kept, as section 3.2 describes. An exclusion is decided first, so a type both
lists match is excluded. A `[Vibecheck]` attribute on a test type earns a warning, as
it does on an excluded type.

The skeleton writes `Assembly::**` into `tests`, not `exclude`, for every project
whose types reference a test framework, using the rule spec section 5.1 states today.
A map that still names such a project under `exclude` keeps working, and `vibecheck
check` warns once for each such pattern that it can move to `tests`.

### 3.2 In the facts

The analysis already sees the test types, since exclusion is applied to its result
and not to the Roslyn pass, so keeping them costs nothing new. The facts gain a
`tests` member and drop the test types from `excluded`:

```jsonc
"tests": {
  "types": [
    { "name": "Example.Auth.Tests.SessionTests", "assembly": "Example.Auth.Tests",
      "files": ["tests/Auth/SessionTests.cs"],
      "subject": { "type": "Example.Auth.Session", "leaf": "auth", "by": "name" },
      "methods": [
        { "symbol": "Example.Auth.Tests.SessionTests.Require_Succeeds", "file": "tests/Auth/SessionTests.cs",
          "first": 18, "last": 31, "skipped": false, "assertions": 3 }
      ] }
  ],
  "exercises": [
    { "from": "Example.Auth.Tests.SessionTests", "method": "Example.Auth.Tests.SessionTests.Require_Succeeds",
      "symbol": "Example.Auth.Session.Require", "file": "tests/Auth/SessionTests.cs", "line": 24 }
  ],
  "calls": [
    { "from": "Example.Auth.Tests.SessionTests.Require_Fails_WhenExpired",
      "to": "Example.Auth.Tests.SessionBuilder.Expired", "file": "tests/Auth/SessionTests.cs", "line": 40 }
  ]
}
```

An **exercise** is a reference from a test type to a declaration in the system,
recorded with the same evidence as a dependency and with the member of the test type
whose lines hold it, when one does. A **call** is a reference from a member of a test
type to a member of a test type, the same one or another, which is what section 5.1
follows through helpers. Exercises and calls are kept out of `dependencies`, so no
reader of the facts can turn them into edges by accident.

A **test method** is a method of a test type that carries an attribute declared in a
test framework assembly: `Fact` or `Theory` in xUnit, `Test`, `TestCase` or
`TestCaseSource` in NUnit, `TestMethod` or `DataTestMethod` in MSTest, and `Test` in
TUnit. A test method is **skipped** when it carries `Skip` on its xUnit attribute,
`Ignore` in NUnit or MSTest, or `Skip` in TUnit. Its **assertions** are the calls in
its lines into an assertion library: the assertion types of the four frameworks,
FluentAssertions and Shouldly. The assembly lists for frameworks and assertion
libraries are one list in `Vibecheck.Core`, the one the skeleton uses today, widened
to name the assertion libraries.

The files of test types are listed in `files` with no leaves and with the test type's
subject leaf as `subject`, so that the changeset can tell a test file from a file no
type binds.

## 4. Subjects

A test type's **subject** is the system type it tests, which places it in the view.
It is decided by the first of these rules that gives an answer:

1. **By name.** The test type's name, with a suffix `Tests`, `Test`, `Should`,
   `Spec` or `Specs` removed, is the name of one system type. When several system
   types have that name, the one whose namespace shares the longest tail with the test
   type's wins, and a tie gives no answer.
2. **By references.** The system type that the test type's exercises reference most,
   when that type holds at least half of them.

A test type that has no test methods, or that neither rule places, is **test
infrastructure**: a fixture, a builder or a helper. It has no subject and appears in
the view under its own heading, last.

The subject's leaf is where the test type appears. The `by` field records the rule
that placed it, and the viewer shows it on hover, so that a placement by references,
which is harder to predict, says so. The subject decides placement only. Which
decisions a test speaks to is decided by its exercises, as section 5 describes, so a
test of `Session` that also calls `TokenStore` is listed under decisions in both.

The suffix rule and the references rule are the only rules until real reviews show
they place too little. To show that, `vibecheck check` reports how the test types were
placed, as "40 by name, 12 by references, 3 infrastructure", and section 12 keeps the
question of further conventions.

## 5. On decisions

### 5.1 The tests of a decision

A test method references a declaration **directly** when one of its own exercises
names it, and **through a helper** when a member of test infrastructure that it calls,
directly or through other such members by the facts' `calls`, has an exercise that
names it. Helpers are followed within test infrastructure and not into the system,
since following calls through the system would be a static coverage measure, which
this design does not attempt. Many tests build what they test through a helper, such
as `SessionBuilder.Expired()`, and counting only direct references would make "no
test references it" wrong for them.

A test method references a decision when it references any of the decision's
declarations at head, or at base for a removed declaration, by either way. Each
decision in the changeset gains `Tests`, the test methods that reference it, each with
the way it does, naming the helper for one through a helper, and its status in the
change: `added`, `removed`, `changed` when a hunk touches its lines, or `unchanged`.

The decision's header in the leaf view counts the direct and helper references apart,
since a shared fixture that builds the whole application would otherwise make every
decision "referenced by 400 tests". It counts the methods by status and names up to
two changed ones, since a test method's name, such as `Require_Throws_WhenExpired`, is
often the sentence a reviewer needs. It counts the rest as "and 28 more", since a
change to many tests would otherwise push the code down the page. Each count opens a list of the methods in the
details panel, and hovering a method referenced through a helper names the helper. A
public decision, in the sense of attention.md section 4.1, that no test method
references at either end says "no test references it". That line states what the
references show. It does not say that the declaration is untested, since a test can
reach it through system code that the reference does not name, and the hover says
so.

A rewritten declaration is not a decision, but a test signal of section 6 can name
it, so the declaration diff's rewritten entries carry their test methods the same way.

### 5.2 Consequences in tests

With test types in the analysis, a hunk in a test file can follow from a decision by
the rule of attention.md section 4.2, as a call site updated for a reshaped
signature does. Such a hunk is a consequence like any other, collapsed and listed
under its decision, and a test file whose hunks are all rename consequences starts out
reviewed by attention.md section 5.1. The decision's header counts consequences in
tests apart from the rest, and **Mark consequences reviewed** marks test files by the
same rule as other files.

## 6. Test signals

### 6.1 The kinds

A **test signal** is a fact about what a change did to a test method, computed from
the declaration diff, the hunks and the test methods at both ends:

- **Removed.** A test method at base is not at head, and no renamed or moved
  declaration accounts for it.
- **Skipped.** A test method that was not skipped at base is skipped at head.
- **Assertion removed.** A test method at both ends has fewer assertions at head than
  at base.
- **Assertion changed.** A changed line in a test method's hunk holds an assertion
  call, at either end, and the method is at both ends. When the line pairs with one
  on the other side, as attention.md section 5.1 pairs lines, and the pair differs
  only in the literal arguments of the assertion, the signal is worded "expected
  value changed", since that is the sharpest case.

Each signal names its test method and the hunk that shows it. A signal is a fact and
is worded as one: "assertion removed from `Require_Succeeds`". The tool never says
that a test was weakened, since an assertion can move into a helper and an expected
value can be wrong at base. For the same reason, an assertion that changed is not
ranked as stronger or weaker: `Equal` becoming `NotNull` is an assertion changed,
and the reviewer reads the line. Ranking assertions would be a judgment of the test,
which this design does not make.

### 6.2 What makes a signal a surprise

A signal **touches changed code** when its test method references, at either end and
by either way of section 5.1, a declaration that the declaration diff lists as
anything but unchanged. This is the case a reviewer of generated code most needs to
see, where the implementation and the test of it changed together, and each test type
with such a signal is a step of its own in the first tier of the path.

Every other signal is still a surprise. A test whose expected value changed while
nothing it references changed often means that behavior moved below what its
references reach, as when a test of `Invoice.Total()` changes its expectation because
`TaxCalculator.Rate()` was rewritten. The references reach one level, and this design
does not follow calls through the system to find the rest. Removed and skipped tests
are rare in any change and always worth a look. So these signals are gathered into
one closing step of the first tier, which bounds what they add to the path at one
step however many there are.

### 6.3 In the changeset

Each hunk in a test file gains `Signals`, the kinds of section 6.1 with the test
method each names and, for a signal that touches changed code, the declarations it
touches. A removed test method's signal is on the hunk that removes it.

## 7. In the viewer

### 7.1 The default view

Test files leave the Other files block, which holds only the files that no type
binds and that are not test files. The map draws no test node and no test edge.

In the leaf view, a leaf with changed test files whose subject is in it lists them in
a **Tests** section after its own files, collapsed to one line that counts them and
their signals. Opened, it shows each file as the leaf view shows any file, with its
reviewed checkbox. Test infrastructure has no leaf, so it is reached from the Tests
switch alone.

Decisions show their tests as section 5 describes, and a test file's header shows its
signals as chips.

### 7.2 The path

A test type with a signal that touches changed code is one step in the first tier,
listing its signals, after the decision steps. The steps are ordered by the reach of
the decisions their signals touch, most first, so that a skipped test of the widest
decision comes first among them. A test type's step names its changed files, so it is
read when they are reviewed.

The other signals of section 6.2 are one step after those, the last of the first
tier, worded "4 tests changed in ways no changed declaration they reference explains"
and listing each test method with its signals. It names the changed files of their
test types and is read when they are reviewed. A changeset with no such signal has no
such step.

In the second tier, a leaf's step lists its changed test files after its own files,
and test infrastructure comes last, beside the files that no type binds. The third
tier counts test files that are consequences, trivial or reviewed with the rest. Test
steps count toward the forty steps of attention.md section 6.1 and are dropped with
file steps, while their signals stay on their leaves' steps.

### 7.3 The Tests switch

The Changes panel's title gains a third switch, **Tests**. It lists the changed test
types by the block their subjects are in, ordered as the blocks are in the path, and
test infrastructure last. Each test type lists its methods that were added, removed or
changed, each with its signals and the changed declarations it references, and each
declaration opens its decision. While the switch is on, each block on the map carries
a badge with the number of changed test files whose subjects it holds, beside its
decision badge.

When every changed file is a test file or a file that no type binds, the changeset
has no decision and no changed leaf to show, and the panel opens on the Tests switch,
with the badges drawn.

### 7.4 Progress

Test files take reviewed marks as other files do, and the reviewed store and the
pull request's viewed state treat them alike. The bar counts them apart, as "24 of 30
files reviewed · 3 of 9 test files", so that a change with a large volume of tests
does not hide how much of the system's change is read. A step of the path counts its
test files toward its being read.

### 7.5 What tests leave alone

Test types never hold a term of the Concepts panel, are never new concepts, are never placed, and are
never in a narrative's inputs, since none of these is drawn from anything but the
system's types. A test type is never `unsorted`.

## 8. Claude's part

None. Every feature here is computed from the analysis and the hunks.

Claude does not write a sentence on what a decision's changed tests assert. A summary
of the tests invites the reviewer to trust tests they have not read, which is the
opposite of what the signals are for, and the names of the changed test methods, which
the decision's line already shows, say most of what such a sentence would.

## 9. The API and the viewer

The facts gain `tests` as section 3.2 describes, and their files gain `subject`. The
changeset's files gain `subject`, its decisions and rewritten declarations gain
`Tests`, its hunks gain `Signals`, its path gains test steps, and its content gains a
count of changed test files under each node, rolled up by subject leaf. A changeset
published as an artifact carries all of it, since none of it needs the server.

No endpoint is added or changed. Marks on test files use
`POST /api/reviews/{id}/reviewed` as marks on other files do.

## 10. Testing

Subjects, exercises, test methods, signals and the path's test steps are pure
functions of analyses and hunks, so they are tested on synthetic analyses and on small
compilations built in memory from source snippets, in the sub-second unit suite. The
test-framework attributes are declared in the snippets themselves, in assemblies named
as the frameworks are, so the suite references no test framework it does not already
use. The tests state:

- A type both lists match is excluded, a test type binds to nothing and creates no
  edge, and the skeleton writes a test project into `tests`.
- A test type is placed by name first, by references second, and is test
  infrastructure when it has no test methods or neither rule places it, and a name
  shared by two system types is decided by namespace or not at all. The check reports
  how many were placed by each rule.
- A decision lists the test methods that reference it with their statuses, a public
  decision with none says so, and a test hunk that follows from a decision is counted
  apart among its consequences.
- A reference through a chain of helpers in test infrastructure is found and names
  the helper, a cycle of helper calls ends, a reference from a helper into the system
  is followed no further, and the header counts direct and helper references apart.
- Each signal kind is found, a renamed test method is not removed, an assertion line
  that differs only in literals is worded as an expected value changed, and a signal
  touches changed code exactly when its method references, by either way, a
  declaration the declaration diff lists as changed.
- A test signal that touches changed code is a first-tier step after the decisions,
  ordered by reach, every other signal is in the one closing step of the first tier,
  and a changeset with no other signal has no closing step.
- Test files are not in the Other files block, count apart in the bar, and a
  changeset of tests alone opens on the Tests switch.

The `Sample` suite checks the test types, subjects and exercises on the sample
solution once per run, where the compilation is already paid for.

## 11. Changes to other documents

These land with the phase that makes them true:

- Spec section 2: the schema gains `tests`. Section 2.4: a test type binds to
  nothing. Section 2.7 no longer gives test projects as a use of `exclude`, and a new
  section beside it defines test types by section 3.1 here. Section 5.1: the skeleton
  writes test projects into `tests`. Section 6: the facts gain `tests` and files'
  `subject`, `excluded` no longer counts test types, and the changeset gains the
  members of section 9 here.
- Spec section 4: the check reports how test types were placed, and warns for each
  `exclude` pattern it can move to `tests`.
- attention.md section 4.4: a decision's header shows its tests and counts
  consequences in tests apart. Section 6.1: the first tier holds test steps after the
  decisions and the closing step of the other signals, and the second tier lists test
  files under their leaves.
- pull-requests.md section 4.4: the Other files block holds no test files. Section
  5.1: the bar counts test files apart.
- README: tests on decisions, test signals and the Tests switch.

## 12. Open questions

- Whether to read conventions beyond the suffix rule for subjects, such as a test
  folder mirroring the source folder. The check's report of section 4 decides it: a
  further rule is added when real repositories place many test types by references
  that a convention would have placed better.
- Whether a `catch` added around the call a test makes, or an assertion replaced by
  one that expects an exception, should be a signal. Each is a syntax rule of its own,
  and both are rare enough to wait for a review that misses one.
- Whether the closing step of section 6.2 grows too large on real changes. If it
  does, following calls through the system from a test's exercises would move some of
  its signals into steps of their own, at the cost of a member-level call graph.

## 13. Phases

Each phase ends with the tool usable, and with tests that state its new behavior.

1. **Test types.** The `tests` list, the skeleton and the check's warning, the facts'
   `tests` member, subjects, test files out of the Other files block and into their
   leaves' Tests sections, the bar's separate count, and the Tests switch listing test
   types by block. It moves the tests out of the way first, which every review
   benefits from.
   The check's report of how test types were placed lands here, so that the data for
   section 12 gathers from the first real review.
2. **Tests on decisions.** Exercises matched to decisions, the facts' `calls` and
   references through helpers, the decision's header line with its two counts and the
   names of the changed methods, consequences in tests counted apart, and the switch
   listing each method's changed declarations.
3. **Test signals.** The four kinds, with an assertion changed worded as an expected
   value changed when only literals differ, the hunks' `Signals`, the chips on file
   headers, the path's test steps, and its closing step for the other signals.
