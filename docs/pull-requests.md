# Pull request reviews (design)

This document describes how a reviewer does a whole GitHub pull request review from
vibecheck: reading the conversation and the review threads, writing comments and
replies, following the checks, and approving or requesting changes. It also describes
reviewed files, a mark that the reviewer sets on a file to set it aside, which takes
over the muting of trivial edits. It builds on [review-workflow.md](review-workflow.md)
and lists, in section 13, the changes it makes to that document and to
[vibecheck-spec.md](vibecheck-spec.md).

## 1. Goals

- A reviewer who opens a pull request in vibecheck never needs to open it on GitHub
  to finish the review.
- The drawing never waits on GitHub. The pull request's data arrives afterward and
  joins what is already on screen, as narratives do.
- What the reviewer sees is what GitHub shows: the same diff, the same lines to
  comment on, and the same threads in the same places.
- Discussion is placed on the map, so that the reviewer sees where in the
  architecture the open questions are.
- Nothing is posted to GitHub except by an explicit action of the reviewer. Claude
  neither posts nor drafts what the reviewer posts.
- The reviewer can set a file aside as reviewed and see at a glance what remains.

Non-goals for this design: merging, which the reviewer does on GitHub; drafting
comments or review summaries with Claude; closing or editing the pull request, requesting
reviewers, labels and assignees, reactions, editing or deleting comments, rerunning
checks, and hosts other than GitHub.com and GitHub Enterprise Server.

## 2. The reviewer's path

1. The picker lists the repository's open pull requests, with those that request the
   reviewer's review first. Each shows its author, whether it is a draft, its checks
   and its review decision. A pull request whose commits are not present locally is
   listed too, and opening it fetches them (section 4.2).
2. Opening a pull request starts a review of its range, as any range does. A bar
   under the top bar shows the pull request's number, title, author, state, checks
   and review decision as soon as GitHub answers.
3. The viewer has two tabs. **Map** is the drawing as it is now, and files are read
   where they are now, in the leaf view of the node that holds them. Changed files
   that no type binds open from an **Other files** block (section 4.4).
   **Conversation** shows the description, the comments, the reviews and the pushes,
   in order, and the checks.
4. Review threads appear on the diff lines they belong to, in the leaf view. Blocks on
   the map that hold unresolved threads carry a count.
5. The reviewer comments on lines, replies to threads, resolves them, and marks files
   reviewed as they go. Inline comments gather in a pending review, as on GitHub.
6. The **Review** button in the bar submits the pending review as a comment, an
   approval or a request for changes, with a summary.
7. When the author pushes, a notice offers to review the new head, or only the
   commits since the reviewer's last review.

`vibecheck pr <number>` and `vibecheck pr <url>` skip the picker and open straight
into a pull request.

## 3. Architecture

The server talks to GitHub. The page talks only to the server, as now, so that the
reviewer's GitHub token never reaches the browser.

```
browser ── viewer (Map, Conversation)
   │  GET /api/reviews/{id}/events (SSE: pull, reviewed, …), POST /api/reviews/{id}/pull/…
   ▼
vibecheck server ── IGitHub ── GraphQL and REST over HTTPS (token from gh)
                 ── git (fetch of the pull request's commits)
                 ── review store (reviewed files, in the cache folder)
```

### 3.1 The GitHub client

Every call goes through one interface, `IGitHub`, with typed methods, a real
implementation and a canned one that tests use, as `IClaude` does. The real one sends
HTTPS requests with `HttpClient`. It reads through GraphQL, which returns a pull
request's threads, reviews, timeline and checks in one query, and it writes through
GraphQL and REST, whichever has the operation.

Running `gh api` for each call would also work, but each call would start a process,
which costs a few hundred milliseconds and makes polling expensive. The tool runs
`gh` only to learn the token and the repository.

### 3.2 Authentication and the repository

The token comes from `gh auth token --hostname <host>`, which also honors `GH_TOKEN`
and `GITHUB_TOKEN`. The server holds it in memory for the session. It is never
written to the cache, the log or a page. The repository comes from
`gh repo view --json nameWithOwner,url`, which follows the default that
`gh repo set-default` chooses in a fork. The host comes from the repository's URL, so
GitHub Enterprise Server works without settings.

If `gh` is missing or not signed in, the picker lists no pull requests and a notice
says why. Reviews of ranges are unaffected. `--no-github` chooses the same on purpose.

### 3.3 Freshness

GitHub cannot send webhooks to a loopback server, so the server polls. While a page
holds a review's event stream open, the server runs the pull request query every 30
seconds, and at once after each write. It sends a `pull` event with the whole
snapshot when the snapshot differs from the last one sent. The pages still never poll.
A query costs one or two points of GraphQL's 5,000 an hour, so a session stays far
inside the limit. When GitHub reports a secondary rate limit, the server waits as the
response asks and the bar says that the data is stale. A refresh button runs the
query at once.

Pull request data is not cached on disk. Its whole value is being current, and the
first drawing does not wait on it.

## 4. Opening a pull request

### 4.1 Identifying it

A review is attached to a pull request when it is started from the picker's entry or
from `vibecheck pr`, or when its head is the head commit of exactly one open pull
request. `POST /api/reviews` gains a `pull` member that names the number.

### 4.2 Its commits

When the head commit is not present locally, the server fetches it with
`git fetch --refmap= <remote> refs/pull/<n>/head <base ref>`. The empty refmap keeps
git from updating any ref. Only objects and `FETCH_HEAD` are written, so the
reviewer's branches and remote-tracking refs stay as they were. This settles the
open question in review-workflow.md section 11. Opening a pull request from the
picker is the reviewer's consent to the fetch, and the progress page shows it as a
stage.

### 4.3 Its range

GitHub compares the head with the merge base of the head and the base branch's tip
on GitHub. The review uses the same commits: the head is `headRefOid`, and the base is
the merge base of `baseRefOid` and the head. The picker currently uses the local
`origin/<base>`, which is stale whenever the reviewer has not fetched lately. A stale
base gives a diff that differs from GitHub's, and comments placed from it land on the
wrong lines or are refused.

### 4.4 Its diff

The lines that a comment can be placed on are the lines of GitHub's diff: three lines
of context, with renames detected. Every review collects its hunks with the same
rules, so in a pull request the lines on screen and the lines that can carry a
comment are the same set. Renames are detected in reviews of plain ranges too, so
that a diff reads the same however the review was opened (section 4.5).

The changeset holds hunks only for files that the analysis knows. A review needs
every changed file, since a pull request's project files, documents and workflows
need review and can carry comments too. The changeset therefore also collects the
hunks of each changed file that no type binds. The viewer draws those files as an
**Other files** block on the top level, beside `unsorted`. Inside it is one leaf for
each top-level folder that holds such files, and one for files at the repository
root, each opening in the leaf view like any leaf. When every such file is in one of
those places, the block is that leaf itself. These blocks have no types and no edges.
A test file is not one of these files: it is listed in the Tests section of the leaf
its subject binds to, or under test infrastructure, as [tests.md](tests.md) section
7.1 describes.
This applies to every review, not only to pull requests, since a range has such files
as well. The split is a first guess, to be revised as the tool is used on real
reviews.

The tool has no flat list of every changed file, such as GitHub's Files tab. Each
file is read in the leaf view of the node that holds it, which is the reading order
the map exists to give.

The changeset's paths are relative to the directory that the review runs in, and
GitHub's are relative to the repository root. The server translates between the two
in one place, and the page sees only the changeset's paths.

### 4.5 Renamed files

`GitHunks` currently runs `git diff --no-renames`, and the changeset's file list is
built by matching the two ends' analyses by path. A file moved from `A.cs` to `B.cs`
is therefore listed as `A.cs` removed and `B.cs` added, and its diff shows every line
deleted and every line added, even when one line changed. Code that a model wrote
moves files often, so this hides real edits in a large share of reviews.

Every review now detects renames:

- `GitHunks` runs `git diff --find-renames`, with git's default similarity of 50
  percent, and reports the pairs it finds.
- `ChangesetBuilder` joins each pair's removed and added entries into one
  `FileChange` with the status `renamed`, the head path as `Path`, and the base path
  as the new `PreviousPath`. Its leaves at base come from the base path and its leaves
  at head from the head path. The file's hunks are keyed by its head path, as every
  hunk is.
- A rename whose contents did not change has no hunks. It starts out reviewed, like a
  trivial file, with the reason "renamed without changes" (section 5.1). A rename
  that changes the file's leaf still shows on the map, because its types move.
- In the leaf view, a renamed file shows its head path with "renamed from" and its
  base path. Where the base path is listed on its own, such as in a leaf that the file
  has left, it links to the renamed file's diff.

A renamed file counts in `Content` by the leaves it has at each end. Under a node that
holds a leaf at both ends, it counts as modified. Under a node that holds only its
base leaves, it counts as removed, and under one that holds only its head leaves, as
added. A file that moves between components therefore shows as leaving one and
arriving in the other, which is the change the map is there to show.

Git pairs files by similarity, so it can pair two files that were not a rename. In a
pull request, GitHub's pairing decides, since comments are placed by it. The server
compares git's pairs with the `previous_filename` of GitHub's file list. Where they
differ, it diffs the pair that GitHub names directly, as `git diff <base>:<old>
<head>:<new>`, and splits any pair that GitHub does not report. In a plain range,
git's pairing stands.

### 4.6 New pushes

When a `pull` event names a head that differs from the review's, a notice says how
many commits were pushed. It offers two reviews. One is of the new head, which costs
one analysis, since the base end is usually cached. The other is of the commits since
the reviewer's last submitted review, from `viewerLatestReview.commit` to the new
head, when that commit is still an ancestor of the head. The old review stays open
until the reviewer leaves it. Reviewed marks carry over to files that the push did not
change (section 5.2).

## 5. Reviewed files

### 5.1 The concept

A changed file is **reviewed** or not. A reviewed file is set aside wherever it
appears. Its chip and its types are muted, its diff tables are collapsed, it sorts to
the end of each list, and a block whose changed files are all reviewed is drawn muted.
This is exactly what the viewer does today for a file whose edits are all trivial,
and the two become one concept.

A file becomes reviewed in one of two ways:

- **By the tool.** A file whose edits are all trivial, as spec section 6 defines
  them, starts out reviewed, with the reason "no code changed", or, for the kinds of
  [attention.md](attention.md) section 5, "renamed `Old` to `New`" linking to the
  rename's decision, "moved from `path`", "members reordered" or "usings only". So
  does a renamed file whose contents did not change, with the reason "renamed without
  changes", and a file a generator wrote, with the reason "generated" linking to the
  hand-written files of its types.
- **By the reviewer.** A checkbox on each file's header in the leaf view marks or
  unmarks it. A leaf or block in the details panel offers to mark all
  of its changed files. A decision's header offers to mark the files that follow
  from it alone, as [attention.md](attention.md) section 4.4 describes.

The reviewer's choice wins. A trivial file that the reviewer unmarks stays unmarked.

Trivial hunks inside a file that is not trivial are still greyed, since triviality
remains a property of each hunk. It is only the file-level flag that becomes the
first source of the reviewed mark.

A reviewed file's edges keep their status. An edge is a dependency, read at the level
of the map, and setting a file aside does not change what the code depends on.

The bar shows the progress, such as "14 of 40 files reviewed", in every review, not
only in a pull request. Test files count apart, as "14 of 40 files reviewed · 3 of 9
test files", so that a change with a large volume of tests does not hide how much of
the system's change is read. The toggle "Mute trivial edits" becomes "Mute reviewed
files". Turned off, it draws every file as its real status but keeps the marks.

### 5.2 What a mark is attached to

A mark belongs to a file's change, not to its path alone. It is keyed by the
repository, the path, and the file's blob identifiers at base and head. The path is
the head path, or the base path for a removed file. A renamed file has one mark,
under its head path, with the blob of its base path at base. A push that
changes the file clears the mark, since there is something new to review. A push that
leaves the file alone keeps it. Two reviews of different ranges that give one file the
same change share its mark.

Marks are stored in the cache folder of the repository, in a small file of their
own, and never in the repository. Browser storage would not do, because each session
listens on a new port. A new port is a new origin, so the page would find its marks
gone at every start.

Marks do not go to GitHub. GitHub has a "Viewed" flag of its own, set through
`markFileAsViewed`, and the tool neither sets nor reads it. Keeping marks local is a
decision for now, to be revisited once the tool has been used for real reviews.

### 5.3 Where the rollup is computed

The changeset's `Content` counts trivial files under each node, and the viewer
compares that count with the modified count. The reviewed mark changes while the page
is open, so the rollup moves to the viewer. It works from each file's leaves at base
and head, which `FileChange` already carries, with the rule that `Content` uses: a
file counts under every node that contains a leaf its types bind. `NodeContent.Trivial`
is removed, and the hunk's `trivial` flag stays.

The current rule mutes a node only when all of its changed files are modified, none
added or removed. A reviewer can review an added file, so the new rule mutes a node
whenever it has changed files and all of them are reviewed. A renamed file counts
under nodes as section 4.5 says.

## 6. The conversation

The Conversation tab shows, oldest first:

- The description, with its author and time.
- Comments on the pull request.
- Reviews, with their state (approved, changes requested, commented or dismissed),
  their summary, and links to their inline threads.
- Pushes, force-pushes and changes of base.
- Changes of state: marked ready, converted to draft, closed, reopened, merged.

Bodies are rendered from GitHub's `bodyHTML`, so that Markdown, mentions, issue
links, task lists and images look as they do on GitHub, without a Markdown renderer
in the viewer. Section 11 covers how the page treats that HTML. The pull request
query also fetches the description's Markdown, `body`, which the review reads for its
`Intent` section, as [intent.md](intent.md) section 3.3 describes.

The checks sit at the top of the tab (section 9). A box at the bottom adds a comment
to the conversation.

## 7. Review threads

### 7.1 Reading them

The query reads every review thread with its path, its side and lines, whether it is
resolved and whether it is outdated, and its comments with their authors and their
`diffHunk`. A thread that is not outdated appears under the line it belongs to, in
the leaf view's diff tables. A resolved thread is collapsed to one line.

The server places each thread, in `ThreadPlacer`, and sends the places with the
snapshot. A thread's line is a line of the pull request's current diff, so it is in the
review's diff at head when the review's head is the pull request's head, and at base
when the review's base is the pull request's base as well. A thread on the head side
also keeps the line it was written on in its original commit, so it keeps its place in
a review of that commit after later pushes. GitHub can give an outdated thread a line,
and the placer ignores it.

An outdated thread's lines are no longer in the diff. It appears in a list at the top
of its file, with the hunk that it was written against, and in the conversation under
the review it started in. So do a thread on the whole file and a thread whose line the
review's diff does not show, such as a thread on the base side in a review of the
commits since the reviewer's last review. A thread on a file the review's diff does not
hold appears only in the conversation.

### 7.2 On the map

A block on the map carries a count of the unresolved threads on files under it, with
the rule of section 5.3. Selecting the block lists those threads in the details panel,
each linking to its line. Threads on files that no type binds are counted on the
**Other files** block. The Changes panel on the left gains a filter that
keeps only files with unresolved threads.

## 8. Writing

### 8.1 Comments and the pending review

Selecting a line, or a range of lines on one side, in any diff table opens a comment
box. It has two buttons, as on GitHub:

- **Add single comment** posts the comment at once, through REST
  `POST /pulls/{n}/comments`.
- **Start a review**, or **Add review comment** once one is started, adds the comment
  to the reviewer's pending review, through GraphQL `addPullRequestReviewThread`. The
  pending review is created when the first comment needs one.

A pending review lives on GitHub, where only its author can see it. It survives a
restart of the tool, and the server reads it back when the review opens. Its comments
appear in place, marked pending, and the bar shows how many there are.

Every comment is anchored to the head commit of the review, with the path, line and
side that GitHub's API takes. A review whose head is no longer the pull request's
head can still comment. GitHub places the comment on that commit and marks it
outdated against the new one, as it does for comments from its own web page.

A reply to a thread joins the pending review when there is one and posts at once
otherwise, through `addPullRequestReviewThreadReply`. That is GitHub's behavior too.
Each thread also has **Resolve** and **Unresolve**.

### 8.2 Composing

Comment boxes take Markdown. **Preview** renders it through GitHub's
`POST /markdown`, in the repository's context, so that it matches what will be
posted. **Suggest change** fills the box with a `suggestion` block holding the
selected lines' text at head, for the reviewer to edit. Unsent text survives a reload
of the page through `sessionStorage`, and nothing else keeps it.

### 8.3 Submitting

The **Review** button opens a box with a summary and three choices: **Comment**,
**Approve** and **Request changes**. It submits the pending review with
`submitPullRequestReview`. With no pending review, it creates and submits one in one
call. GitHub refuses Comment and Request changes without a summary or comments, and
the box says so before it sends. **Discard review** deletes the pending review and its
comments, after a confirmation.

GitHub refuses an approval or a request for changes from the pull request's author.
The box disables both for an author, using `viewerDidAuthor`, and says why. A closed
or merged pull request is shown read-only. Any other refusal is shown with GitHub's
message.

### 8.4 Logging

The terminal logs each write: what was posted, where, and GitHub's answer. That
matches the log of each Claude call, and it gives the reviewer a record of what the
tool did as them.

## 9. Checks

The bar shows the rollup of the head commit's checks as one badge: passing, failing,
pending or none. The Conversation tab lists each check run and commit status with its
name, its workflow, whether it is required, its state and its duration, failing first.
Each links to its page on GitHub. The query reads them from the head commit's
`statusCheckRollup`, with `isRequired` for the pull request. The bar also shows
`reviewDecision` and `mergeStateStatus`, such as "blocked: 1 approval required", so the
reviewer can see what stands between the pull request and merging.

A later phase brings failures closer:

- Check annotations carry a path and lines. They appear in the diff tables, like
  threads, and are counted on the map, so a failing build points at the blocks it
  concerns. Annotations belong to the head commit, so they land on lines only in a
  review of the pull request's head, and are listed at the top of their file
  otherwise. Only failures and warnings are counted. Workflows also annotate paths
  that are not files, such as `.github`, and those annotations stay in the check's
  row.
- A failed job shows the last lines of its log, read through REST
  `GET /actions/jobs/{id}/logs`, in the check's row. The page asks for it when the
  reviewer opens it, rather than with every poll, and the server keeps a finished
  job's log in memory. GitHub deletes logs after the repository's retention period,
  90 days by default, and the row then says so.

## 10. The API

The server gains these endpoints. Each acts on the review's pull request and answers
404 for a review without one.

| Endpoint | Purpose |
|---|---|
| `GET /api/ranges` | Lists pull requests as now, also those not present locally, with number, author, draft, checks and whether the reviewer's review is requested. |
| `POST /api/reviews` | Takes `pull`, the number, in place of or beside `range`. |
| `GET /api/reviews/{id}/pull` | Returns the current snapshot of the pull request. |
| `POST /api/reviews/{id}/pull/refresh` | Runs the query at once. |
| `POST /api/reviews/{id}/pull/comments` | Posts a comment. Without a path, it goes to the conversation. With a path and lines, it is a single comment or joins the pending review, as `review` says. |
| `POST /api/reviews/{id}/pull/threads/{thread}/replies` | Replies to a thread. |
| `POST /api/reviews/{id}/pull/threads/{thread}/resolved` | Resolves or unresolves a thread. |
| `POST /api/reviews/{id}/pull/review` | Submits the pending review with an event and a summary. |
| `DELETE /api/reviews/{id}/pull/review` | Discards the pending review. |
| `POST /api/markdown` | Renders Markdown for a preview. |
| `POST /api/reviews/{id}/reviewed` | Marks or unmarks paths as reviewed. Available in every review. |

The event stream gains a `pull` event, with the snapshot, and a `reviewed` event, with
the marks, so that two pages open on one review agree. `changeset.js` carries the
review's marks and pull request number in `CHANGESET_META`, so that the first drawing
already shows them.

## 11. Security

The server can now write to GitHub as the reviewer, so a page that drives it can do
the same. The guards of review-workflow.md section 8 apply to every new route: the
loopback `Host`, the token cookie, which is `HttpOnly` and `SameSite=Strict`, and JSON
request bodies, which a cross-site form cannot send. The JSON rule extends from POST
to every method that writes, DELETE included.

Comment bodies are text that other people wrote, shown in a page that can call the
server. GitHub sanitizes `bodyHTML`, and the page also passes it through an allowlist
sanitizer before inserting it, so that a gap in either does not become script in the
page. The viewer's pages gain a `Content-Security-Policy` that allows only the
viewer's own scripts and the pinned libraries it loads. Links in bodies open in a new
tab with `rel="noopener"`.

The token stays in the server's memory, as section 3.2 says.

## 12. Testing

No test calls GitHub. The canned `IGitHub` answers from JSON files recorded from
GitHub's GraphQL and REST responses, and the tests state these behaviors:

- A line or range in a hunk maps to the path, line and side GitHub takes, and back,
  including renamed files and paths in a subdirectory.
- Threads land on their lines, outdated threads land in their file's list, and the
  counts roll up to nodes by the rule of section 5.3.
- The range of a pull request uses `baseRefOid`, not a local ref.
- A moved file becomes one `renamed` entry with its edits as hunks, against a
  temporary repository. It counts as modified, removed or added under each node by
  the leaves it has at each end. A rename without changes starts reviewed.
- In a pull request, a pair that GitHub names and git does not, or the reverse, is
  diffed as GitHub pairs it.
- A reviewed mark survives a push that leaves its file alone and clears on one that
  changes it. Trivial files start reviewed, and the reviewer's unmark wins.
- Comments, replies, submissions and discards send the requests GitHub expects, and
  an author's approval is refused before it is sent.
- The write routes refuse a request without the token or without a JSON body.
- The fetch of section 4.2 writes no ref, against a temporary repository.

These are unit tests, run in the edit loop with the rest. A manual smoke script
exercises the real client against a scratch pull request. The reviewed rollup runs in
the viewer, so it is written as a pure function of the files and marks, which keeps it
easy to check by hand and to test once the viewer has tests.

## 13. Changes to other documents

These land with the phase that makes them true:

- Spec section 6: `FileChange` gains the status `renamed` and a `PreviousPath`, and
  `Content` counts a renamed file by the rule of section 4.5. `NodeContent.Trivial` is
  removed, and the paragraph on trivial edits says that a trivial file starts out
  reviewed. The changeset's hunks cover every changed file, including those that no
  type binds, and are collected with rename detection.
- Spec section 7: `vibecheck pr` and `--no-github`.
- review-workflow.md section 2: the picker lists pull requests whose commits are not
  local. Section 3 points to this document's endpoints. The question in section 11 on
  fetching is settled by section 4.2.
- README: reviewed files, and reviewing a pull request.

## 14. Phases

Each phase ends with the tool usable, and with tests that state its new behavior.

1. **Reviewed files and the full diff.** Rename detection and the `renamed` status,
   the **Other files** block with its hunks, and the reviewed mark: its store, its key,
   the rollup in the viewer, the checkboxes and the progress, with trivial files and
   unchanged renames pre-marked. It needs no GitHub and helps every review, so it
   comes first. Renames come before marks, since a mark's key depends on them.
2. **Opening pull requests.** The client, authentication, the picker's list, the fetch,
   the base from GitHub, the reconciliation of rename pairs with GitHub's,
   `vibecheck pr`, the bar with state and checks rollup, polling, and the new-push
   notice. Read-only.
3. **Reading the discussion.** The Conversation tab, threads in the diffs,
   outdated threads, the counts on the map, and the pending review read back.
4. **Writing.** Comments, replies, the pending review, resolving, preview,
   suggestions, submitting with approval or a request for changes, and discarding.
5. **Checks in detail.** The list of checks with required ones marked, annotations in
   the diffs and on the map, and the tails of failed logs.

## 15. Open questions

None at present. The split of **Other files** (section 4.4) and the local-only
reviewed mark (section 5.2) are first choices to revisit after real use.
