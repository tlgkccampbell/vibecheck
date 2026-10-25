// Viewer over one changeset. Every level is drawn from the leaf-pair edges in
// the changeset, aggregated to the children of the level. ELK places the blocks, and the
// blocks and the curved edges then settle together. Edges that leave the level end at
// pins, which stand among the blocks as hexagonal tags. A view regroups the leaves under
// a level; a leaf opens its types, files and diffs.

(function () {
  "use strict";

  const CS = window.CHANGESET;
  const META = window.CHANGESET_META || {};
  const MAP = CS.map;
  const SVGNS = "http://www.w3.org/2000/svg";
  const reduceMotion = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
  const ROOT = "root", UNSORTED = "unsorted";
  const BLOCK = { w: 190, h: 112 }, PIN_H = 26;
  // The parts of a pin's hexagonal tag: the icon chip's size, and the padding inside each pointed end.
  const PIN_CHIP = 16, PIN_PAD = 5;
  // A pin's width leaves room for its icon chip and its label between the pointed ends of its hexagonal tag.
  const pinWidth = (name) => Math.min(name.length, 22) * 6.9 + PIN_H + 2 * PIN_PAD + PIN_CHIP + 6;
  const elk = new ELK();

  // ---------- Model ----------

  const NODES = MAP.nodes, EXTERNALS = MAP.externals, VIEWS = MAP.views;
  const isExternal = (id) => !!EXTERNALS[id];

  // The Other files block holds the changed files that no type binds, such as project files, documents and workflows.
  // It has one leaf for each top-level folder that holds such files and one for the files at the top, or is itself the
  // leaf when every such file is in one of those places. Its identifiers hold a "~" and a "/", which no map
  // identifier can, so they never collide with the map's. See docs/pull-requests.md section 4.4.
  const OTHER = "~other";
  const OTHER_FILES = CS.files.filter((f) => f.status !== "unchanged" && !f.test && !f.leavesBase.length && !f.leavesHead.length);
  const otherGroupOf = (path) => OTHER + "/" + (path.includes("/") ? path.slice(0, path.indexOf("/")) : "");
  const OTHER_GROUPS = [...new Set(OTHER_FILES.map((f) => otherGroupOf(f.path)))].sort();
  const OTHER_LEAVES = OTHER_GROUPS.length > 1 ? OTHER_GROUPS : OTHER_FILES.length ? [OTHER] : [];
  const isOther = (id) => id === OTHER || id.startsWith(OTHER + "/");
  // Returns the Other files leaf that holds a path.
  const otherLeafOf = (path) => OTHER_LEAVES.length === 1 ? OTHER : otherGroupOf(path);
  const otherFilesOf = (id) => OTHER_FILES.filter((f) => id === OTHER || otherGroupOf(f.path) === id);

  // A test file declares test types and no type of the system, and the map never draws it. A changed test file is
  // listed in the Tests section of the leaf its subject binds to, or in the test infrastructure leaf when it has no
  // subject. That leaf is never drawn either, and the Tests switch reaches it. Test files count apart in the progress.
  // See docs/tests.md section 7.
  const TESTS = "~tests";
  const TEST_FILES = CS.files.filter((f) => f.test && f.status !== "unchanged");
  const TEST_TYPES = CS.tests || [];
  // A test signal is a fact about what a change did to a test method, never a judgment of the test. Each hunk of a test
  // file carries the signals it shows. See docs/tests.md section 6.
  const SIGNAL_TEXT = { removed: "removed", skipped: "skipped", "assertion-removed": "assertion removed", "assertion-changed": "assertion changed", "expected-changed": "expected value changed" };
  // Returns the signals of a file's hunks.
  const signalsOf = (path) => (HUNKS.get(path) || []).flatMap((h) => h.signals || []);
  // Returns a signal as a chip that names its kind, with its method and the changed declarations it references on hover.
  const signalChip = (s, withMethod) => el("span", { class: "chip chip-signal" + (s.touches.length ? " touches" : ""),
    title: s.method + (s.touches.length ? "\nReferences " + s.touches.map((t) => `${memberName(t.symbol, t.kind)}, ${t.change}`).join("; ") : "\nReferences no changed declaration") },
    SIGNAL_TEXT[s.kind] || s.kind, withMethod ? ` · ${memberName(s.method, "method").split(".").pop()}` : "");
  // Returns a value indicating whether a method's symbol belongs to a type, whose name may carry an assembly prefix.
  const isMethodOf = (method, type) => method.startsWith(type.replace(/^.*::/, "") + ".");
  // Returns the first hunk among some files, in their order, that carries a signal the test accepts, as its path and index, or null.
  function signalHunk(paths, accept) {
    for (const path of paths) {
      const index = (HUNKS.get(path) || []).findIndex((h) => (h.signals || []).some(accept));
      if (index >= 0) return { path, index };
    }
    return null;
  }
  // Opens a changed test type at its first hunk with a signal, or else at the top of its first changed file.
  function openTestType(t) {
    const at = signalHunk(t.files, (x) => isMethodOf(x.method, t.name));
    return at ? openHunk(at.path, at.index) : openFile(t.files[0]);
  }
  // Returns the changed test files whose subject binds to a leaf, or those without a subject for the test infrastructure leaf.
  const testFilesOf = (leaf) => TEST_FILES.filter((f) => leaf === TESTS ? !f.subject : f.subject === leaf);

  const isLeaf = (id) => id === UNSORTED || id === TESTS || OTHER_LEAVES.includes(id) || !!(NODES[id] && NODES[id].bind);
  const parentOf = (id) => id === ROOT ? null : id.startsWith(OTHER + "/") ? OTHER : id.lastIndexOf(".") < 0 ? ROOT : id.slice(0, id.lastIndexOf("."));
  const isUnder = (id, anc) => anc === ROOT || id === anc || id.startsWith(anc + (anc === OTHER ? "/" : "."));
  const shortName = (type) => type.replace(/^.*::/, "").split(".").pop();

  const hasUnsorted = CS.types.some((t) => t.leafHead === UNSORTED || t.leafBase === UNSORTED);
  const CHILDREN = {};
  for (const id of Object.keys(NODES)) (CHILDREN[parentOf(id)] = CHILDREN[parentOf(id)] || []).push(id);
  if (hasUnsorted) (CHILDREN[ROOT] = CHILDREN[ROOT] || []).push(UNSORTED);
  if (OTHER_FILES.length) (CHILDREN[ROOT] = CHILDREN[ROOT] || []).push(OTHER);
  if (OTHER_GROUPS.length > 1) CHILDREN[OTHER] = OTHER_GROUPS.slice();
  for (const k of Object.keys(CHILDREN)) CHILDREN[k].sort();
  const childrenOf = (id) => CHILDREN[id] || [];
  const ALL_LEAVES = Object.keys(NODES).filter(isLeaf).concat(hasUnsorted ? [UNSORTED] : [], OTHER_LEAVES);
  const leavesUnder = (id) => isLeaf(id) ? [id] : ALL_LEAVES.filter((l) => l !== UNSORTED && isUnder(l, id)).concat(id === ROOT && hasUnsorted ? [UNSORTED] : []);

  // The categories of section 2.1 of the specification. Each is drawn in one of five color families and with an icon
  // on a 16-unit grid. Five families are as many as stay distinct from one another, and the change colors stay on
  // edges, borders and badges, so the two never share a mark. The icon tells apart categories in the same family.
  const CATEGORY = {
    entry: { label: "Entry point", family: "blue", icon: "M3 4.5 L6.5 8 L3 11.5 M8.5 11.5 H13" },
    feature: { label: "Feature", family: "blue", icon: "M8 2.2 L9.4 6.6 L13.8 8 L9.4 9.4 L8 13.8 L6.6 9.4 L2.2 8 L6.6 6.6 Z" },
    domain: { label: "Domain model", family: "teal", icon: "M8 2 L13.5 5 V11 L8 14 L2.5 11 V5 Z M2.5 5 L8 8 L13.5 5 M8 8 V14" },
    analysis: { label: "Analysis", family: "teal", icon: "M10.2 10.2 L14 14 M11 6.8 A4.2 4.2 0 1 1 2.6 6.8 A4.2 4.2 0 1 1 11 6.8 Z" },
    transform: { label: "Transform", family: "magenta", icon: "M2 5 H12 M9.5 2.5 L12 5 L9.5 7.5 M14 11 H4 M6.5 8.5 L4 11 L6.5 13.5" },
    integration: { label: "Integration", family: "orange", icon: "M5.5 1.8 V5 M10.5 1.8 V5 M3.5 5 H12.5 V7.5 A4.5 4.5 0 0 1 3.5 7.5 Z M8 12 V14.5" },
    foundation: { label: "Foundation", family: "slate", icon: "M8 2 L14 5 L8 8 L2 5 Z M2 8 L8 11 L14 8 M2 11 L8 14 L14 11" },
    tooling: { label: "Tooling", family: "slate", icon: "M13.6 4.6 A3.6 3.6 0 0 1 9 8.8 L4.2 13.6 A1.5 1.5 0 0 1 2.1 11.5 L6.9 6.7 A3.6 3.6 0 0 1 11.1 2.1 L9.2 4 L9.6 6.1 L11.7 6.5 Z" },
  };

  // Returns the category of a node, group or pin target: a node's own or its nearest ancestor's, the most common
  // among a group's leaves, or null for an external, the unsorted leaf, or a node with no category on its path.
  function categoryOf(id) {
    if (id.startsWith("g:")) {
      const counts = new Map();
      for (const l of groupLeaves(id)) { const c = categoryOf(l); if (c) counts.set(c, (counts.get(c) || 0) + 1); }
      return [...counts.entries()].sort((a, b) => b[1] - a[1])[0]?.[0] || null;
    }
    for (let x = id; x && x !== ROOT; x = parentOf(x)) if (NODES[x] && NODES[x].category) return NODES[x].category;
    return null;
  }
  const familyClass = (id) => { const c = categoryOf(id); return c ? " fam-" + CATEGORY[c].family : ""; };

  // Returns an icon chip for a category: a rounded square in the family color with the icon drawn over it.
  function categoryChip(category, x, y, size) {
    const g = svg("g", { class: "chip fam-" + CATEGORY[category].family });
    g.append(svg("rect", { x, y, width: size, height: size, rx: size / 5, class: "chip-bg" }));
    const k = (size - 2) / 16;
    g.append(svg("path", { d: CATEGORY[category].icon, class: "chip-icon", transform: `translate(${x + 1} ${y + 1}) scale(${k})`, "stroke-width": (1.6 / k).toFixed(2) }));
    g.append(svg("title", {}, CATEGORY[category].label));
    return g;
  }

  function nameOf(id) {
    if (id === ROOT) return META.solution || MAP.solution;
    if (id === UNSORTED) return "Unsorted";
    if (id === OTHER) return "Other files";
    if (id === TESTS) return "Test infrastructure";
    if (isOther(id)) return id.length > OTHER.length + 1 ? id.slice(OTHER.length + 1) + "/" : "Top-level files";
    if (NODES[id]) return NODES[id].name;
    if (EXTERNALS[id]) return EXTERNALS[id].name;
    if (id.startsWith("g:")) { const [v, g] = id.slice(2).split("/"); return VIEWS[v].groups[g].name; }
    return id;
  }
  function purposeOf(id) {
    if (id === ROOT) return META.title ? `Changeset ${META.range || ""}: ${META.title}` : "";
    if (id === UNSORTED) return "Types that no leaf binds. The map should be extended to place them.";
    if (id === OTHER) return "Changed files that no type binds, such as project files, documents and workflows.";
    if (id === TESTS) return "Changed test types that test no single type of the system, such as fixtures, builders and helpers.";
    if (isOther(id)) return id.length > OTHER.length + 1 ? `Changed files under ${id.slice(OTHER.length + 1)}/ that no type binds.` : "Changed files at the top that no type binds.";
    if (NODES[id]) return NODES[id].purpose || "";
    if (EXTERNALS[id]) return EXTERNALS[id].purpose || "External assembly.";
    if (id.startsWith("g:")) { const [v, g] = id.slice(2).split("/"); return VIEWS[v].groups[g].purpose || ""; }
    return "";
  }

  // Indexes over the changeset.
  const FILES = new Map(CS.files.map((f) => [f.path, f]));
  // The renamed files by their paths at base, which the types at base still list.
  const RENAMED_FROM = new Map(CS.files.filter((f) => f.previousPath).map((f) => [f.previousPath, f]));
  // Returns the file at a path, which may be a renamed file's path at base, or null.
  const fileOf = (path) => FILES.get(path) || RENAMED_FROM.get(path) || null;
  const HUNKS = new Map(CS.hunks.map((h) => [h.path, h.hunks]));
  const TYPES_BY_LEAF = new Map();
  for (const t of CS.types) {
    const leaf = t.leafHead || t.leafBase;
    if (!TYPES_BY_LEAF.has(leaf)) TYPES_BY_LEAF.set(leaf, []);
    TYPES_BY_LEAF.get(leaf).push(t);
  }
  const typesOf = (leaf) => TYPES_BY_LEAF.get(leaf) || [];
  // The patterns the change touches, by identifier: each pattern found at base with the instances the change added or
  // changed, and every other instance at head. See docs/patterns.md.
  const PATTERNS = new Map((CS.patterns || []).map((p) => [p.id, p]));
  // The patterns that report each added or changed instance, with the instance, by the instance's type name.
  const PATTERNS_OF = new Map();
  for (const p of PATTERNS.values()) {
    for (const i of p.instances) {
      if (!PATTERNS_OF.has(i.type)) PATTERNS_OF.set(i.type, []);
      PATTERNS_OF.get(i.type).push({ pattern: p, instance: i });
    }
  }

  // ---------- Reviewed files ----------
  // A changed file is reviewed when the reviewer has set it aside, or when the tool has and the reviewer has not
  // unmarked it. The server keeps the marks. Each maps a path to the reason the file is reviewed: "reviewer", "renamed"
  // for a file renamed without changes, or the kind of the file's reason in the changeset's hunks: "trivial" for a file
  // whose edits touch no code, "rename", "moved", "reordered", "usings" or "generated", as docs/attention.md section 5
  // defines them. A reviewed file is set aside wherever it appears while reviewed files are muted. See
  // docs/pull-requests.md section 5.
  const REVIEWED = new Map(Object.entries(META.reviewed || {}));
  const CHANGED_FILES = CS.files.filter((f) => f.status !== "unchanged");
  const FILE_HUNKS = new Map(CS.hunks.map((h) => [h.path, h]));

  // Returns the words for the reason an edit is trivial, or a file is set aside, as docs/attention.md section 5 names
  // them: the kind, with the names or path it mentions. The hunk, when given, says which way a move went.
  function reasonText(reason, hunk) {
    if (!reason) return null;
    switch (reason.kind) {
      case "trivial": return "no code changed";
      case "renamed": return "renamed without changes";
      case "rename": return `renamed ${memberName(reason.previous || "", "member")} to ${memberName(reason.symbol || "", "member")}`;
      case "moved": return `${movedFrom(hunk) ? "moved from" : "moved to"} ${reason.path}`;
      case "reordered": return "members reordered";
      case "usings": return "usings only";
      case "generated": return "generated";
      default: return null;
    }
  }
  // Returns a value indicating whether a hunk, or any hunk of a file given by its hunks, adds lines, in which case
  // its moved declarations came from the other file; otherwise they went there.
  const movedFrom = (hunk) => !hunk || (Array.isArray(hunk) ? hunk : [hunk]).some((h) => h.lines.some((l) => l[0] === "+"));
  // Returns the note on a reviewed file's header for the reason it is reviewed: the words, with the rename's decision,
  // the other file of a move, or the hand-written files of a generated file's types as links. Null for the reviewer's
  // own marks.
  function reasonNote(path, why) {
    const file = FILE_HUNKS.get(path);
    const reason = file && file.reason && file.reason.kind === why ? file.reason : { kind: why };
    const text = reasonText(reason, file ? file.hunks : null);
    if (!text) return null;
    const note = el("span", { class: "file-note" }, text);
    if (reason.kind === "rename" && DECISIONS.has(reason.decision)) {
      const d = DECISIONS.get(reason.decision);
      note.append(" · ", el("button", { class: "link", onclick: () => openHunk(d.file, d.hunk), title: "Open the rename's decision" }, "decision"));
    } else if (reason.kind === "moved" && fileOf(reason.path)) {
      note.replaceChildren(movedFrom(file.hunks) ? "moved from " : "moved to ", el("button", { class: "link", onclick: () => openFile(reason.path), title: reason.path }, reason.path.split("/").pop()));
    } else if (reason.kind === "generated") {
      const others = [...new Set(CS.types.filter((t) => t.files.includes(path)).flatMap((t) => t.files).filter((p) => p !== path))];
      if (others.length) note.append(" · hand-written: ", ...others.flatMap((p, i) => [i ? ", " : "", el("button", { class: "link", onclick: () => openFile(p), title: p }, p.split("/").pop())]));
    }
    return note;
  }

  // ---------- Decisions and consequences ----------
  // A decision is a hunk that changes a declaration, and a consequence is a hunk whose every changed line follows from
  // decisions, as the changeset finds them. A consequence stays collapsed under a line that names its decision, and a
  // file whose hunks are all consequences sorts after the files that hold decisions. See docs/attention.md section 4.
  const DECISIONS = new Map((CS.decisions || []).map((d) => [d.id, d]));
  const hunkId = (path, index) => path + "#" + index;
  const CHANGE_TEXT = { added: "added", removed: "removed", reshaped: "signature changed", renamed: "renamed", moved: "moved" };

  // Returns a value indicating whether every hunk of a file is a consequence or trivial, and at least one is a consequence.
  function consequenceOnly(path) {
    const hunks = HUNKS.get(path) || [];
    return hunks.some((h) => h.follows) && hunks.every((h) => h.follows || h.trivial);
  }
  // Returns the name a declaration is shown by: a type's own name, or a member's type and name, without the namespace.
  function memberName(symbol, kind) {
    const parts = [];
    let depth = 0, start = 0;
    for (let i = 0; i < symbol.length; i++) {
      const ch = symbol[i];
      if (ch === "<") depth++;
      else if (ch === ">") depth--;
      else if (ch === "." && !depth) { parts.push(symbol.slice(start, i)); start = i + 1; }
    }
    parts.push(symbol.slice(start));
    return parts.slice(kind === "type" ? -1 : -2).join(".");
  }
  // Returns a declaration of a decision as text, naming its old name too when it was renamed or moved.
  const declText = (x) => x.previous ? `${memberName(x.previous, x.kind)} → ${memberName(x.symbol, x.kind)}` : memberName(x.symbol, x.kind);
  // Returns the declaration a decision is named by: the one with the most sites at either end.
  const leadOf = (d) => d.declarations.slice().sort((a, b) => b.sitesBase + b.sitesHead - a.sitesBase - a.sitesHead)[0];
  // Returns the files of the system that hold a decision's consequences, the leaves those files count under, and how
  // many of its consequences are in test files, which count apart. See docs/tests.md section 5.2.
  function reachOf(d) {
    const isTest = (p) => { const f = fileOf(p); return !!(f && f.test); };
    const files = [...new Set(d.consequences.map((c) => c.file).filter((p) => !isTest(p)))];
    const blocks = new Set(files.flatMap((p) => { const f = fileOf(p); return f ? countedLeaves(f) : []; }));
    return { files, blocks, tests: d.consequences.filter((c) => isTest(c.file)).length };
  }
  function reachText(d) {
    const r = reachOf(d), system = d.consequences.length - r.tests;
    if (!system) return `${plural(r.tests, "consequence", "consequences")} in tests`;
    return `${plural(system, "consequence", "consequences")} in ${plural(r.files.length, "file", "files")}, across ${plural(r.blocks.size, "block", "blocks")}`
      + (r.tests ? `, and ${r.tests} in tests` : "");
  }
  // Returns the leaves the decision's own file counts under, which are the blocks the decision is drawn from.
  function sourceLeavesOf(d) {
    const f = fileOf(d.file);
    return f ? countedLeaves(f) : [];
  }
  // The decisions in the order the Changes panel lists them: the widest reach first, by blocks, then files, then
  // public before private, then by the number of consequences. The order never changes, so it is computed once.
  const DECISION_LIST = [...DECISIONS.values()].map((d) => { const r = reachOf(d); return { d, rank: [r.blocks.size, r.files.length, d.public ? 1 : 0, d.consequences.length] }; })
    .sort((a, b) => { for (let i = 0; i < a.rank.length; i++) if (a.rank[i] !== b.rank[i]) return b.rank[i] - a.rank[i]; return 0; }).map((x) => x.d);
  // Returns the decisions whose own files count under any of the leaves.
  function decisionsUnder(leaves) {
    const set = new Set(leaves);
    return DECISION_LIST.filter((d) => sourceLeavesOf(d).some((l) => set.has(l)));
  }
  // Returns how the changed hunks divide: decisions, consequences, trivial hunks, and the hunks that are none of
  // these, which are the edits nothing in the changeset accounts for.
  function hunkTally() {
    const t = { total: 0, decisions: 0, consequences: 0, trivial: 0, other: 0 };
    for (const f of CHANGED_FILES) {
      (HUNKS.get(f.path) || []).forEach((h, i) => {
        t.total++;
        if (DECISIONS.has(hunkId(f.path, i))) t.decisions++;
        else if (h.follows && DECISIONS.has(h.follows[0])) t.consequences++;
        else if (h.trivial) t.trivial++;
        else t.other++;
      });
    }
    return t;
  }
  // Returns the level that draws a set of leaves as separate blocks: the deepest node above all of them, or the parent
  // of that node when it is itself a leaf, or the top level when nothing is common.
  function levelShowing(leaves) {
    const chains = [...new Set(leaves)].map((l) => [ROOT, ...ancestors(l)]);
    if (!chains.length) return ROOT;
    let depth = 0;
    while (chains.every((c) => c.length > depth + 1 && c[depth + 1] === chains[0][depth + 1])) depth++;
    const common = chains[0][depth];
    return isLeaf(common) ? parentOf(common) : common;
  }

  // Returns the leaves a changed file counts under, as the changeset's content counts it: its leaves at head, its
  // leaves at base when it was removed, and its leaves at both ends when it was renamed. A file that no type binds
  // counts under its Other files leaf, and a test file counts under none.
  function countedLeaves(f) {
    if (f.test) return [];
    if (!f.leavesBase.length && !f.leavesHead.length) return [otherLeafOf(f.path)];
    if (f.status === "removed") return f.leavesBase;
    return f.status === "renamed" ? [...new Set(f.leavesBase.concat(f.leavesHead))] : f.leavesHead;
  }

  // Returns the paths of the changed files that count under each node, by the rule of countedLeaves: a file counts
  // under each leaf it counts under, under every ancestor of those leaves, and under the top level. It depends on the
  // files alone, so the marks can be laid over it whenever they change.
  function filesByNode(files) {
    const out = new Map();
    for (const f of files) {
      const ids = new Set([ROOT]);
      for (const l of countedLeaves(f)) for (let x = l; x && x !== ROOT; x = parentOf(x)) ids.add(x);
      for (const id of ids) { if (!out.has(id)) out.set(id, []); out.get(id).push(f.path); }
    }
    return out;
  }
  const FILES_BY_NODE = filesByNode(CHANGED_FILES.filter((f) => !f.test));

  // Returns the paths of a node's or a group's changed files, how many there are, and how many of them are reviewed.
  function reviewedCount(key) {
    const paths = key.startsWith("g:") ? [...new Set(groupLeaves(key).flatMap((l) => FILES_BY_NODE.get(l) || []))] : FILES_BY_NODE.get(key) || [];
    return { paths, changed: paths.length, reviewed: paths.filter((p) => REVIEWED.has(p)).length };
  }

  // Returns a file's status, which is "reviewed" for a changed file that is reviewed while reviewed files are muted.
  const fileStatus = (path) => {
    const f = fileOf(path);
    const st = f ? f.status : "unchanged";
    return st !== "unchanged" && state.opts.mute && REVIEWED.has(f.path) ? "reviewed" : st;
  };
  // Returns a type's status: "reviewed" when every changed file that declares it is, its own when it was added,
  // removed or moved, and otherwise "modified" when any of its files changed.
  const typeStatus = (t) => {
    const changed = t.files.map(fileStatus).filter((f) => f !== "unchanged");
    if (changed.length && changed.every((f) => f === "reviewed")) return "reviewed";
    if (t.status !== "unchanged") return t.status;
    return changed.length ? "modified" : "unchanged";
  };
  const typeTouched = (t) => typeStatus(t) !== "unchanged";
  const CHANGED_EDGES = CS.edges.filter((e) => e.status !== "unchanged");
  const CONTENT = CS.content, METRICS = CS.metrics;

  // Returns the file counts of a node. The top level also counts the files that no type binds.
  function contentOf(id) {
    if (isOther(id)) return otherContent(id);
    const c = CONTENT[id] || { files: 0, added: 0, removed: 0, modified: 0, status: "unchanged" };
    if (id !== ROOT || !OTHER_FILES.length) return c;
    const o = otherContent(OTHER);
    return { files: c.files + o.files, added: c.added + o.added, removed: c.removed + o.removed, modified: c.modified + o.modified, status: c.status === "unchanged" ? "children" : c.status };
  }
  // Returns the file counts of the Other files block or one of its leaves, where a renamed file counts as modified.
  function otherContent(id) {
    const files = otherFilesOf(id), count = (st) => files.filter((f) => f.status === st).length;
    return { files: files.length, added: count("added"), removed: count("removed"), modified: count("modified") + count("renamed"), status: isLeaf(id) ? "modified" : "children" };
  }
  // Returns the status drawn for a node or a group. While reviewed files are muted, one whose changed files are all
  // reviewed is "reviewed". A group otherwise shows the changes inside its leaves.
  function statusOf(key) {
    const r = reviewedCount(key);
    if (state.opts.mute && r.changed > 0 && r.reviewed === r.changed) return "reviewed";
    if (key.startsWith("g:")) return groupLeaves(key).some((l) => statusOf(l) !== "unchanged") ? "children" : "unchanged";
    return contentOf(key).status;
  }
  function groupLeaves(key) { const [v, g] = key.slice(2).split("/"); return VIEWS[v].groups[g].leaves; }
  function metricsOf(key) {
    if (key.startsWith("g:")) {
      const [v, g] = key.slice(2).split("/");
      const head = (CS.views[v] && CS.views[v].groups[g]) || null;
      return { base: null, head };
    }
    return METRICS[key] || { base: null, head: null };
  }
  function changedEdgesUnder(leaves) {
    const set = new Set(leaves);
    return CHANGED_EDGES.filter((e) => set.has(e.from) || set.has(e.to)).length;
  }

  // ---------- Levels ----------
  // A location is { level, view?, group? } for a schematic or { leaf } for a leaf.

  function locKey(loc) {
    if (loc.leaf) return "leaf:" + loc.leaf;
    return loc.level + (loc.view ? "@" + loc.view + (loc.group ? "/" + loc.group : "") : "");
  }
  function parseLoc(key) {
    if (!key) return { level: ROOT };
    if (key.startsWith("leaf:")) { const id = key.slice(5); return isLeaf(id) ? { leaf: id } : { level: ROOT }; }
    const [level, rest] = key.split("@");
    if (!(level === ROOT || ((NODES[level] || level === OTHER) && !isLeaf(level)))) return { level: ROOT };
    if (!rest) return { level };
    const [view, group] = rest.split("/");
    if (!VIEWS[view] || VIEWS[view].level !== level) return { level };
    if (group && !VIEWS[view].groups[group]) return { level, view };
    return group ? { level, view, group } : { level, view };
  }
  function locName(loc) {
    if (loc.leaf) return nameOf(loc.leaf);
    if (loc.group) return VIEWS[loc.view].groups[loc.group].name;
    if (loc.view) return VIEWS[loc.view].name;
    return nameOf(loc.level);
  }
  function viewsAt(level) { return Object.keys(VIEWS).filter((v) => VIEWS[v].level === level); }

  // The children a location draws, each with the leaves it stands for. A view's groups partition the map's leaves, so
  // a view of the top level also draws the blocks that stand outside the map, unsorted and Other files, as the tree does.
  function drawnChildren(loc) {
    const tree = (c) => ({ key: c, id: c, kind: c === UNSORTED ? "unsorted" : "node", leaves: leavesUnder(c) });
    if (loc.group) return VIEWS[loc.view].groups[loc.group].leaves.map((l) => ({ key: l, id: l, kind: "node", leaves: [l] }));
    if (loc.view) {
      const v = VIEWS[loc.view];
      const groups = Object.keys(v.groups).map((g) => ({ key: `g:${loc.view}/${g}`, id: `g:${loc.view}/${g}`, kind: "group", leaves: v.groups[g].leaves }));
      return loc.level === ROOT ? groups.concat(childrenOf(ROOT).filter((c) => c === UNSORTED || c === OTHER).map(tree)) : groups;
    }
    return childrenOf(loc.level).map(tree);
  }

  // Lifts an endpoint outside the drawn children to the thing a pin is named after.
  function outsideOf(loc, x) {
    if (isExternal(x)) return { id: x, kind: "external" };
    if (loc.group) {
      const v = VIEWS[loc.view];
      for (const g of Object.keys(v.groups)) if (v.groups[g].leaves.includes(x)) return { id: `g:${loc.view}/${g}`, kind: "group" };
    }
    if (loc.level === ROOT) return { id: x, kind: x === UNSORTED ? "unsorted" : "node" };
    const sib = childrenOf(parentOf(loc.level)).find((c) => isUnder(x, c));
    return sib ? { id: sib, kind: sib === UNSORTED ? "unsorted" : "node" } : { id: x, kind: "node" };
  }

  function edgeStatus(base, head, members) {
    if (base === 0) return "added";
    if (head === 0) return "removed";
    return base !== head || members.some((m) => m.status !== "unchanged") ? "modified" : "unchanged";
  }

  // A modified edge is minor when its count moved by fewer than three references, or by less than one percent.
  const isMinor = (e) => e.status === "modified" && Math.abs(e.head - e.base) < Math.max(3, 0.01 * e.base);

  // Describes a list of edges as "n changed" with the minor ones counted separately.
  function changedText(edges) {
    const changed = edges.filter((e) => e.status !== "unchanged");
    const minor = changed.filter(isMinor).length;
    return `${changed.length - minor} changed` + (minor ? `, ${minor} minor` : "");
  }

  // Builds the drawn children, pins and aggregated edges of a location.
  function buildLevel(loc, opts) {
    const children = drawnChildren(loc);
    const owner = new Map();
    for (const c of children) for (const l of c.leaves) owner.set(l, c.key);
    const agg = new Map(), pins = new Map();
    const pinFor = (dir, o) => {
      const key = `pin:${dir}:${o.id}`;
      if (!pins.has(key)) pins.set(key, { key, dir, id: o.id, kind: o.kind, name: nameOf(o.id), changed: false, edges: 0 });
      return pins.get(key);
    };
    for (const e of CS.edges) {
      const a = owner.get(e.from), b = owner.get(e.to);
      let ka, kb;
      if (a && b) { if (a === b) continue; ka = a; kb = b; }
      else if (a && !b) {
        const o = outsideOf(loc, e.to);
        if (o.kind === "external" && !opts.externals) continue;
        ka = a; kb = pinFor("out", o).key;
      } else if (!a && b) {
        const o = outsideOf(loc, e.from);
        ka = pinFor("in", o).key; kb = b;
      } else continue;
      const k = ka + "→" + kb;
      if (!agg.has(k)) agg.set(k, { key: "edge:" + k, from: ka, to: kb, base: 0, head: 0, members: [] });
      const g = agg.get(k);
      g.base += e.base; g.head += e.head; g.members.push(e);
    }
    let edges = [...agg.values()];
    for (const g of edges) g.status = edgeStatus(g.base, g.head, g.members);
    edges = edges.filter((g) => g.status !== "unchanged" || (opts.threshold !== "changed" && g.head >= +opts.threshold));
    for (const g of edges) {
      for (const k of [g.from, g.to]) {
        if (!k.startsWith("pin:")) continue;
        const p = pins.get(k);
        p.edges++;
        if (g.status !== "unchanged") p.changed = true;
      }
    }
    const used = new Set(edges.flatMap((g) => [g.from, g.to]));
    const pinList = [...pins.values()].filter((p) => used.has(p.key));
    const cycles = findCycles(children.map((c) => c.key), edges.filter((g) => !g.from.startsWith("pin:") && !g.to.startsWith("pin:")));
    const inCycle = new Set(cycles.flat());
    return { loc, children, pins: pinList, edges, cycles, inCycle };
  }

  // Tarjan's strongly connected components; a component of two or more is a cycle.
  function findCycles(keys, edges) {
    const out = new Map(keys.map((k) => [k, []]));
    for (const e of edges) out.get(e.from).push(e.to);
    let index = 0;
    const idx = new Map(), low = new Map(), onStack = new Set(), stack = [], result = [];
    function strong(v) {
      idx.set(v, index); low.set(v, index); index++; stack.push(v); onStack.add(v);
      for (const w of out.get(v)) {
        if (!idx.has(w)) { strong(w); low.set(v, Math.min(low.get(v), low.get(w))); }
        else if (onStack.has(w)) low.set(v, Math.min(low.get(v), idx.get(w)));
      }
      if (low.get(v) === idx.get(v)) {
        const comp = [];
        let w;
        do { w = stack.pop(); onStack.delete(w); comp.push(w); } while (w !== v);
        if (comp.length > 1) result.push(comp.sort());
      }
    }
    for (const k of keys) if (!idx.has(k)) strong(k);
    return result;
  }

  // ---------- Layout ----------

  const layoutCache = new Map();
  function layoutKey(loc, opts) { return locKey(loc) + "|" + opts.threshold + "|" + (opts.externals ? 1 : 0); }

  // Returns the text drawn on an edge's label, or null when the edge is not labeled.
  function labelParts(e) {
    if (e.status === "unchanged" || isMinor(e)) return null;
    return e.status === "added" ? [{ t: "+" + e.head, cls: "lbl-delta" }, { t: "new", cls: "lbl-count" }]
      : e.status === "removed" ? [{ t: "−" + e.base, cls: "lbl-delta" }, { t: "gone", cls: "lbl-count" }]
      : [{ t: String(e.head), cls: "lbl-count" }, { t: fmtDelta(e.base, e.head) || "±0", cls: "lbl-delta" }];
  }
  const labelWidth = (parts) => parts.map((p) => p.t).join(" ").length * 6.6 + 12;

  // Lays out a level, caching the result. Previews and the zoom transition use the same layouts as the levels.
  async function layoutFor(level, opts) {
    const key = layoutKey(level.loc, opts);
    if (layoutCache.has(key)) return layoutCache.get(key);
    const out = await splineLayout(level);
    layoutCache.set(key, out);
    return out;
  }

  // Returns the point at a fraction of the way along a path of cubic Bézier segments, given as its first point
  // followed by two control points and an end point for each segment. The fraction is spread evenly over segments.
  function bezierAt(path, t) {
    const n = (path.length - 1) / 3, k = Math.min(n - 1, Math.floor(t * n));
    return cubicAt(path.slice(3 * k, 3 * k + 4), t * n - k);
  }

  // Returns the point at a fraction of the way along one cubic Bézier curve given by its four points.
  function cubicAt([p0, p1, p2, p3], t) {
    const u = 1 - t;
    return { x: u * u * u * p0.x + 3 * u * u * t * p1.x + 3 * u * t * t * p2.x + t * t * t * p3.x,
      y: u * u * u * p0.y + 3 * u * u * t * p1.y + 3 * u * t * t * p2.y + t * t * t * p3.y };
  }

  // Places the labels of a level's labeled edges along their cubic paths and returns their centers by edge id.
  // Edges with a higher priority are placed first, and each label goes at the first point along its path, from the
  // middle outward, where it overlaps no label already placed and none of the given obstacles.
  function placeLabels(level, paths, priority, obstacles = []) {
    const labels = new Map(), placed = [...obstacles];
    const overlaps = (r) => placed.some((q) => r.x < q.x + q.w + 4 && q.x < r.x + r.w + 4 && r.y < q.y + q.h + 2 && q.y < r.y + r.h + 2);
    const labeled = level.edges.map((e, i) => ({ e, i, parts: labelParts(e) })).filter((x) => x.parts && paths.has("e" + x.i)).sort((p, q) => priority(q.e) - priority(p.e));
    for (const { i, parts } of labeled) {
      const w = labelWidth(parts), h = 18, path = paths.get("e" + i);
      let at = bezierAt(path, 0.5);
      for (const t of [0.5, 0.42, 0.58, 0.34, 0.66, 0.26, 0.74, 0.18, 0.82]) {
        const p = bezierAt(path, t);
        if (!overlaps({ x: p.x - w / 2, y: p.y - h / 2, w, h })) { at = p; break; }
      }
      placed.push({ x: at.x - w / 2, y: at.y - h / 2, w, h });
      labels.set("e" + i, at);
    }
    return labels;
  }

  const SPLINE = { pad: 24, margin: 28, bow: 0.16, maxBow: 90 };

  // Whether blocks and curves settle together. ?relax=0 in the address turns it off for comparison.
  const RELAX_ON = new URLSearchParams(location.search).get("relax") !== "0";

  // The settings of the settling simulation. Distances are in layout units.
  // samples: the points along each arc where blocks and other arcs push. steps: the iterations, as the step size cools.
  // range: how far a block's push reaches beyond its edge. push: the strength of that push. reaction: the share of the
  // push a block feels in return. hold: how hard an arc returns to its resting bow. maxBow: how far an arc's control
  // point may stand from the middle of its chord, as a share of the chord. spacing and apart: how close arcs may come
  // before pushing apart. pull, length and gap: how connected blocks draw together and how far apart any two keep.
  const RELAX = { samples: 9, steps: 180, range: 34, push: 2.4, reaction: 0.3, hold: 0.05, maxBow: 0.8, spacing: 18, apart: 0.5, pull: 0.003, length: 380, gap: 70 };

  // Returns the resting control point of an arc between two points: off the middle of the chord, to the left of the
  // direction of travel, so that an edge and its reverse bow to opposite sides.
  function restingBow(a, b) {
    const dx = b.x - a.x, dy = b.y - a.y, len = Math.hypot(dx, dy) || 1;
    const bow = 2 * Math.min(SPLINE.maxBow, SPLINE.bow * len);
    return { x: (a.x + b.x) / 2 + dy / len * bow, y: (a.y + b.y) / 2 - dx / len * bow };
  }

  // Lets the blocks and the curves of a spline layout settle together, and returns each edge's control point.
  // Each curve is one quadratic arc with a single control point, so it can only bow one way and never snakes.
  // Blocks push on points sampled along the arc, and the pushes move the control point, so an arc bows further out to
  // clear what is in its way. A soft hold draws each arc back toward its resting bow, and arcs keep a little apart.
  // Each block feels a share of every push it gives, so a block that many arcs would cross drifts aside. Connected
  // blocks pull toward a working distance and every pair of blocks keeps a gap. The simulation has no randomness,
  // so the same level always settles the same way.
  function relax(level, rects) {
    const R = RELAX;
    const nodes = new Map([...rects].map(([k, r]) => [k, { k, x: r.x + r.w / 2, y: r.y + r.h / 2, hw: r.w / 2, hh: r.h / 2, fx: 0, fy: 0 }]));
    const list = [...nodes.values()];
    const arcs = level.edges.map((e) => {
      const a = nodes.get(e.from), b = nodes.get(e.to);
      return { a, b, q: { ...restingBow(a, b), fx: 0, fy: 0 }, pts: Array.from({ length: R.samples }, () => ({ x: 0, y: 0 })) };
    });
    // A sample at fraction t moves with the control point by the weight 2t(1 - t), the quadratic's basis function.
    const ts = Array.from({ length: R.samples }, (_, k) => (k + 1) / (R.samples + 1));
    const weight = ts.map((t) => 2 * t * (1 - t));
    // Returns the push a block gives a point, away from the block's nearest point and fading to nothing at the range,
    // or null when the point is out of reach. The result is one shared object, so it is read before the next call.
    // (nx, ny) is the unit normal to the left of the arc's chord.
    const push = { x: 0, y: 0 };
    const pushFrom = (n, p, nx, ny) => {
      if (Math.abs(p.x - n.x) >= n.hw + R.range || Math.abs(p.y - n.y) >= n.hh + R.range) return null;
      const qx = Math.max(n.x - n.hw, Math.min(p.x, n.x + n.hw)), qy = Math.max(n.y - n.hh, Math.min(p.y, n.y + n.hh));
      const dx = p.x - qx, dy = p.y - qy, d = Math.hypot(dx, dy);
      if (d === 0) {
        // Inside the block: across the chord, away from the block's center, at full strength. Pushing out through the
        // nearest side instead fails when the arc runs through the middle, where the nearest sides lie along the arc
        // and their pushes cancel. An arc through the exact center goes to its resting side, the left.
        const side = (p.x - n.x) * nx + (p.y - n.y) * ny < 0 ? -1 : 1;
        push.x = nx * side * R.push * R.range * 0.5; push.y = ny * side * R.push * R.range * 0.5;
        return push;
      }
      if (d >= R.range) return null;
      const f = R.push * (R.range - d) * 0.5;
      push.x = dx / d * f; push.y = dy / d * f;
      return push;
    };
    const flat = arcs.flatMap((c) => c.pts.map((p, k) => ({ c, p, k })));
    const grid = new Map();
    for (let step = 0; step < R.steps; step++) {
      const heat = 1 - step / R.steps;
      for (const n of list) { n.fx = 0; n.fy = 0; }
      for (const c of arcs) {
        c.q.fx = 0; c.q.fy = 0;
        ts.forEach((t, k) => {
          const u = 1 - t, p = c.pts[k];
          p.x = u * u * c.a.x + 2 * t * u * c.q.x + t * t * c.b.x;
          p.y = u * u * c.a.y + 2 * t * u * c.q.y + t * t * c.b.y;
        });
      }
      // Blocks push the samples, which moves the control point, and feel a share of it. An arc's own ends do not push it.
      for (const c of arcs) {
        const len = Math.hypot(c.b.x - c.a.x, c.b.y - c.a.y) || 1, nx = (c.b.y - c.a.y) / len, ny = (c.a.x - c.b.x) / len;
        c.pts.forEach((p, k) => {
          for (const n of list) {
            if (n === c.a || n === c.b) continue;
            const f = pushFrom(n, p, nx, ny);
            if (!f) continue;
            c.q.fx += f.x * weight[k]; c.q.fy += f.y * weight[k];
            n.fx -= f.x * R.reaction; n.fy -= f.y * R.reaction;
          }
        });
      }
      // Samples of different arcs keep apart. A grid of cells as wide as the spacing limits the comparisons to
      // samples in the same or a neighboring cell.
      for (const cell of grid.values()) cell.length = 0;
      flat.forEach((o, i) => {
        // A cell's key packs its two coordinates into one number, which a map looks up faster than text.
        const key = Math.floor(o.p.x / R.spacing) * 65536 + Math.floor(o.p.y / R.spacing);
        if (!grid.has(key)) grid.set(key, []);
        grid.get(key).push(i);
      });
      flat.forEach((o, i) => {
        const cx = Math.floor(o.p.x / R.spacing), cy = Math.floor(o.p.y / R.spacing);
        for (let gx = cx - 1; gx <= cx + 1; gx++) for (let gy = cy - 1; gy <= cy + 1; gy++) {
          for (const j of grid.get(gx * 65536 + gy) || []) {
            const u = flat[j];
            if (j <= i || u.c === o.c) continue;
            const dx = o.p.x - u.p.x, dy = o.p.y - u.p.y, d = Math.hypot(dx, dy) || 0.01;
            if (d >= R.spacing) continue;
            const f = R.apart * (R.spacing - d) / d;
            o.c.q.fx += dx * f * weight[o.k]; o.c.q.fy += dy * f * weight[o.k];
            u.c.q.fx -= dx * f * weight[u.k]; u.c.q.fy -= dy * f * weight[u.k];
          }
        }
      });
      // Each arc is drawn back toward its resting bow between where its ends now stand.
      for (const c of arcs) {
        const r = restingBow(c.a, c.b);
        c.q.fx += R.hold * (r.x - c.q.x); c.q.fy += R.hold * (r.y - c.q.y);
      }
      // Connected blocks pull toward a working distance.
      for (const c of arcs) {
        const dx = c.b.x - c.a.x, dy = c.b.y - c.a.y, d = Math.hypot(dx, dy) || 1, f = R.pull * (d - R.length) / d;
        c.a.fx += dx * f; c.a.fy += dy * f; c.b.fx -= dx * f; c.b.fy -= dy * f;
      }
      // Every pair of blocks keeps the gap.
      for (let i = 0; i < list.length; i++) for (let j = i + 1; j < list.length; j++) {
        const a = list[i], b = list[j];
        const ox = a.hw + b.hw + R.gap - Math.abs(a.x - b.x), oy = a.hh + b.hh + R.gap - Math.abs(a.y - b.y);
        if (ox <= 0 || oy <= 0) continue;
        if (ox < oy) { const f = ox * 0.25 * Math.sign(b.x - a.x || 1); a.fx -= f; b.fx += f; }
        else { const f = oy * 0.25 * Math.sign(b.y - a.y || 1); a.fy -= f; b.fy += f; }
      }
      const limit = 2 + 14 * heat;
      const move = (o, k) => {
        const m = Math.hypot(o.fx, o.fy), s = m > limit ? limit / m : 1;
        o.x += o.fx * s * k; o.y += o.fy * s * k;
      };
      for (const c of arcs) {
        move(c.q, 1);
        // No arc bows further than the limit allows, however hard it is pushed.
        const mx = (c.a.x + c.b.x) / 2, my = (c.a.y + c.b.y) / 2, len = Math.hypot(c.b.x - c.a.x, c.b.y - c.a.y) || 1;
        const ox = c.q.x - mx, oy = c.q.y - my, off = Math.hypot(ox, oy), max = R.maxBow * len;
        if (off > max) { c.q.x = mx + ox / off * max; c.q.y = my + oy / off * max; }
      }
      for (const n of list) move(n, heat);
    }
    for (const n of list) { const r = rects.get(n.k); r.x = n.x - n.hw; r.y = n.y - n.hh; }
    return arcs.map((c) => ({ x: c.q.x, y: c.q.y }));
  }

  // Lays out a level with every edge drawn as one free curve from the edge of its source block to the edge of its
  // target block. ELK's stress layout places the blocks, which then settle together with the curves. Each curve bows
  // a little to its left as it travels, so an edge and its reverse take opposite sides. Pins stand among the blocks.
  async function splineLayout(level) {
    const res = await elk.layout({
      id: "g",
      layoutOptions: { "elk.algorithm": "stress", "elk.stress.desiredEdgeLength": "360", "elk.stress.dimension": "XY" },
      children: [
        ...level.children.map((c) => ({ id: c.key, width: BLOCK.w, height: BLOCK.h })),
        ...level.pins.map((p) => ({ id: p.key, width: pinWidth(p.name), height: PIN_H })),
      ],
      edges: level.edges.map((e, i) => ({ id: "e" + i, sources: [e.from], targets: [e.to] })),
    });
    const rects = new Map(res.children.map((n) => [n.id, { x: n.x, y: n.y, w: n.width, h: n.height }]));
    separate(rects, SPLINE.margin);
    const bows = RELAX_ON ? relax(level, rects) : null;
    if (bows) separate(rects, 12);
    // Moves everything so the layout starts at the padding.
    const all = [...rects.values()];
    const x0 = Math.min(...all.map((r) => r.x)), y0 = Math.min(...all.map((r) => r.y));
    for (const r of all) { r.x += SPLINE.pad - x0; r.y += SPLINE.pad - y0; }
    const center = (r) => ({ x: r.x + r.w / 2, y: r.y + r.h / 2 });
    // Returns the point where a ray from a rect's center toward a point leaves the rect.
    const exit = (r, toward) => {
      const c = center(r), dx = toward.x - c.x, dy = toward.y - c.y;
      const t = Math.min(dx ? r.w / 2 / Math.abs(dx) : Infinity, dy ? r.h / 2 / Math.abs(dy) : Infinity);
      return { x: c.x + dx * t, y: c.y + dy * t };
    };
    const paths = new Map();
    level.edges.forEach((e, i) => {
      const ra = rects.get(e.from), rb = rects.get(e.to);
      const ca = center(ra), cb = center(rb);
      // A settled control point moved with the layout, so it is shifted the same way. The quadratic arc through it is
      // drawn as the cubic with the same shape, starting and ending where the arc's tangents leave the blocks.
      const q = bows ? { x: bows[i].x + SPLINE.pad - x0, y: bows[i].y + SPLINE.pad - y0 } : restingBow(ca, cb);
      const a = exit(ra, q), b = exit(rb, q);
      paths.set("e" + i, [a, { x: a.x + (q.x - a.x) * 2 / 3, y: a.y + (q.y - a.y) * 2 / 3 }, { x: b.x + (q.x - b.x) * 2 / 3, y: b.y + (q.y - b.y) * 2 / 3 }, b]);
    });
    const labels = placeLabels(level, paths, (e) => Math.max(e.base, e.head), [...rects.values()]);
    const w = Math.max(...all.map((r) => r.x + r.w)) + SPLINE.pad, h = Math.max(...all.map((r) => r.y + r.h)) + SPLINE.pad;
    return { rects, labels, paths, w, h };
  }

  // Pushes overlapping rects apart along the axis where they overlap least, until each pair keeps the margin.
  function separate(rects, margin) {
    const list = [...rects.values()];
    for (let pass = 0; pass < 200; pass++) {
      let moved = false;
      for (let i = 0; i < list.length; i++) for (let j = i + 1; j < list.length; j++) {
        const a = list[i], b = list[j];
        const ox = Math.min(a.x + a.w, b.x + b.w) + margin - Math.max(a.x, b.x);
        const oy = Math.min(a.y + a.h, b.y + b.h) + margin - Math.max(a.y, b.y);
        if (ox <= 0 || oy <= 0) continue;
        moved = true;
        if (ox < oy) { const d = ox / 2 * Math.sign((b.x + b.w / 2) - (a.x + a.w / 2) || 1); a.x -= d; b.x += d; }
        else { const d = oy / 2 * Math.sign((b.y + b.h / 2) - (a.y + a.h / 2) || 1); a.y -= d; b.y += d; }
      }
      if (!moved) break;
    }
  }

  // Frame geometry around the laid-out graph, with room above for the title.
  const FRAME_PAD = { left: 30, right: 30, top: 64, bottom: 40 };

  function frameOf(layout) {
    return { x: FRAME_PAD.left, y: FRAME_PAD.top, w: layout.w, h: layout.h,
      total: { w: FRAME_PAD.left + layout.w + FRAME_PAD.right, h: FRAME_PAD.top + layout.h + FRAME_PAD.bottom } };
  }

  // Returns the drawn rect of a child or pin, in stage coordinates.
  function rectOf(level, layout, key) {
    const f = frameOf(layout);
    const r = layout.rects.get(key);
    return r ? { x: f.x + r.x, y: f.y + r.y, w: r.w, h: r.h } : null;
  }

  // Returns the SVG path of an edge in stage coordinates, drawn as the chain of cubic Bézier curves the layout made.
  function routeOf(level, layout, i, edge) {
    const f = frameOf(layout);
    const P = (p) => `${p.x.toFixed(1)},${p.y.toFixed(1)}`;
    const q = (layout.paths.get("e" + i) || []).map((p) => ({ x: f.x + p.x, y: f.y + p.y }));
    if (q.length < 4) return null;
    let d = "M" + P(q[0]);
    for (let k = 1; k + 2 < q.length; k += 3) d += ` C${P(q[k])} ${P(q[k + 1])} ${P(q[k + 2])}`;
    return { d, pts: [q[0], q[q.length - 1]] };
  }

  // Returns the width-to-height ratio of the stage as it is drawn on screen. The view always takes this ratio,
  // so a level fitted to the view is fitted to what is visible.
  function stageAspect() {
    const b = $("#stage").getBoundingClientRect();
    return b.width > 0 && b.height > 0 ? b.width / b.height : state.aspect;
  }

  function fitRect(r, pad, aspect = state.aspect) {
    let x = r.x - pad, y = r.y - pad, w = r.w + 2 * pad, h = r.h + 2 * pad;
    if (w / h < aspect) { const nw = h * aspect; x -= (nw - w) / 2; w = nw; }
    else { const nh = w / aspect; y -= (nh - h) / 2; h = nh; }
    return { x, y, w, h };
  }

  // ---------- DOM helpers ----------

  const $ = (sel) => document.querySelector(sel);
  function el(tag, attrs, ...kids) {
    const e = document.createElement(tag);
    for (const [k, v] of Object.entries(attrs || {})) {
      if (k === "class") e.className = v;
      else if (k.startsWith("on")) e.addEventListener(k.slice(2), v);
      else if (v !== null && v !== undefined && v !== false) e.setAttribute(k, v === true ? "" : v);
    }
    for (const k of kids) if (k !== null && k !== undefined && k !== false) e.append(k);
    return e;
  }
  function svg(tag, attrs, ...kids) {
    const e = document.createElementNS(SVGNS, tag);
    for (const [k, v] of Object.entries(attrs || {})) {
      if (k.startsWith("on")) e.addEventListener(k.slice(2), v);
      else if (v !== null && v !== undefined) e.setAttribute(k, v);
    }
    for (const k of kids) if (k !== null && k !== undefined) e.append(k);
    return e;
  }
  function text(x, y, str, attrs) {
    const t = svg("text", Object.assign({ x, y }, attrs || {}));
    t.textContent = str;
    return t;
  }
  // A hidden path that measures the length of path data, which the flow arrows need to keep an even pace.
  const meter = (() => {
    const box = document.createElementNS(SVGNS, "svg");
    box.setAttribute("style", "position:absolute;width:0;height:0;overflow:hidden;visibility:hidden");
    box.setAttribute("aria-hidden", "true");
    const path = document.createElementNS(SVGNS, "path");
    box.append(path);
    document.body.append(box);
    return path;
  })();
  const FLOW = { spacing: 46, speed: 24 };

  // Returns a group of chevrons that glide along a path at an even pace, spaced evenly and turned with the curve,
  // to show which way a dependency runs. Their size follows the width of the edge they ride on.
  function flowArrows(d, width) {
    meter.setAttribute("d", d);
    const len = meter.getTotalLength();
    const g = svg("g", { class: "flow-arrows" });
    if (len < 24) return g;
    const n = Math.max(1, Math.floor(len / FLOW.spacing)), dur = len / FLOW.speed, size = 3 + width * 0.7;
    for (let k = 0; k < n; k++) {
      const arrow = svg("path", { d: `M${-size},${-size} L0,0 L${-size},${size}`, class: "flow-arrow" });
      arrow.append(svg("animateMotion", { dur: dur.toFixed(2) + "s", repeatCount: "indefinite", rotate: "auto", path: d, begin: (-k * dur / n).toFixed(2) + "s" }));
      g.append(arrow);
    }
    return g;
  }

  // The advance of one character of the monospaced text in a block and in a level's frame: 0.6 em, plus any letter spacing.
  const MONO = { meta: 10.5 * 0.6, title: 12 * 0.6 + 12 * 0.08, badge: 9 * 0.6 + 9 * 0.08, frameTitle: 12 * 0.6 + 12 * 0.12, frameMeta: 10.5 * 0.6 + 10.5 * 0.06 };
  // Returns text cut to fit a width in the monospaced font, ending in an ellipsis when it was cut.
  const fitText = (str, width, advance) => truncate(str, Math.max(1, Math.floor(width / advance)));

  const truncate = (s, n) => s.length > n ? s.slice(0, n - 1) + "…" : s;
  const plural = (n, one, many) => `${n} ${n === 1 ? one : many}`;
  const fmtDelta = (b, h) => h === b ? "" : (h > b ? "+" : "−") + Math.abs(h - b);
  const statusClass = (st) => st === "unchanged" ? "" : " st-" + st;
  const strokeWidth = (n) => (1 + Math.log2(1 + n) / 2.2).toFixed(2);

  // ---------- State ----------

  const state = {
    trail: [{ level: ROOT }],
    selected: null,
    // The selections the details panel came from, most recent last, so that its Back button can return to them.
    back: [],
    // The talk option keeps only the blocks with unresolved threads in the Changes panel.
    opts: { threshold: "1", externals: false, dim: true, mute: true, talk: false },
    // The tab the page shows: "map" or "conversation".
    tab: "map",
    // The lines selected to comment on: a path, a hunk index, a side, and the first and last line indexes, or null.
    compose: null,
    // The reviewed files whose diffs the reader has shown, by path.
    expanded: new Set(),
    // The consequences whose diffs the reader has shown, by hunk identifier.
    expandedHunks: new Set(),
    // The selected decision's identifier, whose consequences the details panel lists and the map marks, or null.
    decision: null,
    // What the last mark action of a decision did: the decision's identifier and the text that says so, or null.
    decisionNote: null,
    // The Changes panel's content, "path", "changes", "glossary" or "tests", or undefined before the first render chooses.
    panel: undefined,
    // The leaves whose Tests sections the reader has opened.
    testsOpen: new Set(),
    // The decision whose tests the details panel shows opened, or null.
    decisionTests: null,
    // The pattern the details panel shows, as its identifier and the type it was opened from, or null.
    pattern: null,
    // The blocks whose instances the pattern's section in the details panel shows opened.
    patternBlocks: new Set(),
    // The block the Glossary is narrowed to, or null for every term, and the key of the term it marks, or null.
    glossaryBlock: null,
    term: null,
    current: null, // { level, layout }
    view: { x: 0, y: 0, w: 1600, h: 1000 },
    aspect: 1.6,
    saved: new Map(), // the last view of each location, by layout key
  };
  const loc = () => state.trail[state.trail.length - 1];

  // ---------- Rendering: schematic ----------

  function labelPoint(pts) {
    let best = null, len = -1;
    for (let i = 1; i < pts.length; i++) {
      const l = Math.abs(pts[i].x - pts[i - 1].x) + Math.abs(pts[i].y - pts[i - 1].y);
      if (l > len) { len = l; best = { x: (pts[i].x + pts[i - 1].x) / 2, y: (pts[i].y + pts[i - 1].y) / 2 }; }
    }
    return best;
  }

  // Returns the area inside a block that holds its preview.
  const previewBox = (box) => ({ x: box.x + 10, y: box.y + 54, w: box.w - 20, h: box.h - 62 });

  // Returns the cached layout of the level a child opens, or null for a leaf or a level not yet laid out.
  function subLayoutOf(child) {
    const target = childLoc(child);
    return target.leaf ? null : layoutCache.get(layoutKey(target, state.opts)) || null;
  }

  // Returns how a level's layout is scaled and centered into a preview area, or null when the layout has no blocks.
  // The zoom transition uses the same placement, so the miniatures line up with the blocks of the level they open.
  function previewPlacement(inner, sub) {
    const rs = [...sub.rects.entries()].filter(([k]) => !k.startsWith("pin:")).map(([k, r]) => ({ k, r }));
    if (!rs.length || inner.h < 10) return null;
    const bb = rs.reduce((b, { r }) => ({ x0: Math.min(b.x0, r.x), y0: Math.min(b.y0, r.y), x1: Math.max(b.x1, r.x + r.w), y1: Math.max(b.y1, r.y + r.h) }), { x0: Infinity, y0: Infinity, x1: -Infinity, y1: -Infinity });
    const sc = Math.min(inner.w / (bb.x1 - bb.x0), inner.h / (bb.y1 - bb.y0));
    const ox = inner.x + (inner.w - (bb.x1 - bb.x0) * sc) / 2, oy = inner.y + (inner.h - (bb.y1 - bb.y0) * sc) / 2;
    return { rs, bb, sc, ox, oy };
  }

  function previewInto(g, child, box) {
    const inner = previewBox(box);
    if (inner.h < 10) return;
    if (child.kind === "node" && isLeaf(child.id) || child.kind === "unsorted") {
      // One bar per type, colored by what happened to it.
      // A leaf of Other files has no types, so its bars stand for its files.
      const items = isOther(child.id) ? otherFilesOf(child.id).map((f) => fileStatus(f.path))
        : typesOf(child.id).slice().sort((a, b) => (typeTouched(b) ? 1 : 0) - (typeTouched(a) ? 1 : 0)).map(typeStatus);
      const shown = Math.min(items.length, 12);
      const cols = items.length > 6 ? 2 : 1;
      const rows = Math.ceil(shown / cols);
      const gap = Math.min(5, inner.h / Math.max(rows, 1));
      for (let i = 0; i < shown; i++) {
        const ts = items[i];
        const cls = "mini-type" + (ts === "unchanged" ? "" : ["added", "removed", "reviewed"].includes(ts) ? " mini-type-" + ts : " mini-type-changed");
        const col = i % cols, row = Math.floor(i / cols);
        const cw = (inner.w - 6 * (cols - 1)) / cols;
        g.append(svg("rect", { x: inner.x + col * (cw + 6), y: inner.y + row * gap, width: Math.max(8, cw * (0.55 + ((i * 7) % 5) / 10)), height: 2, class: cls }));
      }
      return;
    }
    // A container or group: the layout of the level it opens as unlabeled miniatures, or a grid of its leaves.
    const sub = subLayoutOf(child);
    const place = sub && previewPlacement(inner, sub);
    if (place) {
      const { rs, bb, sc, ox, oy } = place;
      for (const { k, r } of rs) {
        g.append(svg("rect", { x: ox + (r.x - bb.x0) * sc, y: oy + (r.y - bb.y0) * sc, width: Math.max(2, r.w * sc), height: Math.max(2, r.h * sc), class: "mini" + statusClass(statusOf(k)) + familyClass(k), rx: 1 }));
      }
      return;
    }
    const leaves = child.leaves;
    const cols = Math.min(leaves.length, Math.max(3, Math.ceil(Math.sqrt(leaves.length * 2.5))));
    const cw = (inner.w - 4 * (cols - 1)) / cols, ch = Math.min(10, (inner.h - 4 * 2) / Math.max(1, Math.ceil(leaves.length / cols)));
    leaves.forEach((l, i) => {
      const col = i % cols, row = Math.floor(i / cols);
      g.append(svg("rect", { x: inner.x + col * (cw + 4), y: inner.y + row * (ch + 4), width: cw, height: ch, class: "mini" + statusClass(statusOf(l)) + familyClass(l), rx: 1 }));
    });
  }

  function renderLevel(level, layout) {
    const root = svg("g", { class: "level" });
    const f = frameOf(layout);
    root.append(svg("rect", { x: f.x, y: f.y, width: f.w, height: f.h, class: "frame" }));
    // The title stands at the frame's left and the counts at its right. A frame too narrow for both on one line, as a
    // level of two or three blocks can be, puts the counts on a line of their own under the title.
    const title = locName(level.loc).toUpperCase();
    const meta = `${level.children.length} blocks · ${level.edges.length} edges, ${changedText(level.edges)} · ${level.cycles.length} cycle${level.cycles.length === 1 ? "" : "s"}`;
    const stacked = title.length * MONO.frameTitle + 24 + meta.length * MONO.frameMeta > f.w;
    root.append(text(f.x, f.y - 34, title, { class: "frame-title" }));
    root.append(text(stacked ? f.x : f.x + f.w, stacked ? f.y - 16 : f.y - 34, meta, { class: "frame-meta", "text-anchor": stacked ? "start" : "end" }));

    // Edges.
    const edgeLayer = svg("g", { class: "edges" }), labelLayer = svg("g", { class: "edge-labels" });
    level.edges.forEach((e, i) => {
      const route = routeOf(level, layout, i, e);
      if (!route) return;
      const { d, pts } = route;
      const external = e.to.startsWith("pin:out:") && isExternal(e.to.slice(8));
      const sel = e.key === state.selected;
      const w = strokeWidth(e.status === "removed" ? e.base : e.head);
      const flows = e.status === "added" || (e.status === "modified" && !isMinor(e));
      const g = svg("g", { class: "edge" + statusClass(e.status) + (isMinor(e) ? " st-minor" : "") + (flows ? " flows" : "") + (external ? " external" : "") + (sel ? " selected" : ""), "data-key": e.key,
        "data-from": e.from, "data-to": e.to,
        onclick: (ev) => clickSelect(ev, e.key),
        onmouseenter: () => hover(e.key), onmouseleave: () => hover(null) });
      // With narratives, the page's own tooltip shows the counts, and a native one would cover it.
      if (NARR.state === "off") g.append(svg("title", {}, `${nameOf(stripPin(e.from))} → ${nameOf(stripPin(e.to))}: ${e.base === e.head ? e.head : e.base + " → " + e.head} references`));
      g.append(svg("path", { d, class: "edge-hit" }));
      g.append(svg("path", { d, class: "edge-line", "stroke-width": w, "marker-end": `url(#arrow-${sel ? "selected" : e.status})`, "data-marker": e.status }));
      // Arrowheads that glide from source to target show the direction of the dependency. A removed edge carries none.
      // An edge that shows them only in focus gets them the first time it comes into focus.
      if (flows) g.append(flowArrows(d, +w));
      else if (e.status !== "removed") { g.dataset.d = d; g.dataset.w = w; }
      edgeLayer.append(g);
      const parts = labelParts(e);
      if (parts) {
        const lp = layout.labels.get("e" + i);
        const mid = lp ? { x: frameOf(layout).x + lp.x, y: frameOf(layout).y + lp.y } : labelPoint(pts);
        const w = labelWidth(parts);
        const lg = svg("g", { class: "edge-label" + statusClass(e.status), "data-key": e.key + ":label", onclick: (ev) => clickSelect(ev, e.key),
          onmouseenter: () => hover(e.key), onmouseleave: () => hover(null) });
        lg.append(svg("rect", { x: mid.x - w / 2, y: mid.y - 9, width: w, height: 18, rx: 2, class: "lbl-bg" }));
        let x = mid.x - w / 2 + 6;
        for (const p of parts) { lg.append(text(x, mid.y + 4, p.t, { class: p.cls })); x += p.t.length * 6.6 + 6.6; }
        labelLayer.append(lg);
      }
    });
    root.append(edgeLayer);

    // Blocks. The block the selected decision is in is marked as its source, and those that hold its consequences as
    // its reach.
    const nodeLayer = svg("g", { class: "nodes" });
    const selectedDecision = state.decision && DECISIONS.has(state.decision) ? DECISIONS.get(state.decision) : null;
    const reach = selectedDecision ? reachOf(selectedDecision).blocks : new Set();
    const source = new Set(selectedDecision ? sourceLeavesOf(selectedDecision) : []);
    for (const c of level.children) {
      const r = rectOf(level, layout, c.key);
      const st = statusOf(c.key);
      const leaf = c.kind === "unsorted" || (c.kind === "node" && isLeaf(c.id));
      const cat = categoryOf(c.id);
      const cls = "node" + statusClass(st) + familyClass(c.id) + (c.kind === "unsorted" ? " unsorted" : "") + (isOther(c.id) ? " other" : "") + (leaf ? " leaf" : " container") +
        (level.inCycle.has(c.key) ? " in-cycle" : "") + (c.key === state.selected ? " selected" : "") +
        (c.leaves.some((l) => source.has(l)) ? " source" : c.leaves.some((l) => reach.has(l)) ? " reach" : "");
      const g = svg("g", { class: cls, "data-key": c.key, tabindex: 0,
        onclick: (ev) => clickSelect(ev, c.key),
        onmouseenter: () => hover(c.key), onmouseleave: () => hover(null),
        ondblclick: (ev) => { ev.stopPropagation(); enter(c); },
        onkeydown: (ev) => { if (ev.key === "Enter") { ev.stopPropagation(); enter(c); } } });
      g.append(svg("rect", { x: r.x, y: r.y, width: r.w, height: r.h, rx: 2, class: "box" }));
      // The header band and icon chip carry the category. The border and the badge on the top edge carry the change.
      g.append(svg("path", { d: `M${r.x + 2},${r.y} H${r.x + r.w - 2} Q${r.x + r.w},${r.y} ${r.x + r.w},${r.y + 2} V${r.y + 28} H${r.x} V${r.y + 2} Q${r.x},${r.y} ${r.x + 2},${r.y} Z`, class: "band" }));
      if (cat) g.append(categoryChip(cat, r.x + 7, r.y + 6, 16));
      // The title runs from after the icon chip to before the cycle mark and the open glyph.
      const titleX = r.x + (cat ? 30 : 10);
      g.append(text(titleX, r.y + 19, fitText(nameOf(c.id).toUpperCase(), r.x + r.w - 32 - titleX, MONO.title), { class: "title" }));
      const m = metricsOf(c.key), content = c.kind === "group" ? groupContent(c) : contentOf(c.key);
      const changedFiles = content.added + content.removed + content.modified;
      // The status pill on the block's top edge says what happened to it, with the count of changed files when there is
      // one. The pills on its bottom edge count what lies under it: the unresolved threads, the check problems, the
      // decisions and, while the Tests switch is on, the changed test files. The two edges never share a pill.
      const badge = st === "added" ? "added" : st === "removed" ? "removed"
        : st === "modified" ? (changedFiles ? plural(changedFiles, "file changed", "files changed") : "modified")
        : st === "children" ? (changedFiles ? `${changedFiles} changed inside` : "changes inside")
        : st === "reviewed" ? (leaf ? "reviewed" : "reviewed inside") : null;
      if (badge) {
        const bw = badge.length * MONO.badge + 12;
        const bg = svg("g", { class: "badge" });
        bg.append(svg("rect", { x: r.x + r.w - bw - 8, y: r.y - 8, width: bw, height: 15, rx: 7.5, class: "badge-bg" }));
        bg.append(text(r.x + r.w - bw / 2 - 8, r.y + 3, badge.toUpperCase(), { class: "badge-text", "text-anchor": "middle" }));
        g.append(bg);
      }
      const talk = openThreadsUnder(c.key).length, problems = problemsUnder(c.key).length;
      const decisions = decisionsUnder(c.leaves);
      const tests = state.panel === "tests" ? TEST_FILES.filter((f) => f.subject && c.leaves.includes(f.subject)).length : 0;
      const pills = [];
      if (talk) pills.push({ cls: "talk", label: String(talk), w: String(talk).length * MONO.badge + 26,
        title: `${plural(talk, "unresolved review thread", "unresolved review threads")} on files under ${nameOf(c.id)}` });
      if (problems) pills.push({ cls: "problem", label: "✕ " + problems,
        title: `${plural(problems, "check failure or warning", "check failures and warnings")} on files under ${nameOf(c.id)}` });
      if (decisions.length) pills.push({ cls: "decision", label: "◆ " + decisions.length, title: `${plural(decisions.length, "decision", "decisions")} in files under ${nameOf(c.id)}` });
      if (tests) pills.push({ cls: "tests", label: "✓ " + tests, title: `${plural(tests, "changed test file", "changed test files")} of types under ${nameOf(c.id)}` });
      const bottom = r.y + r.h - 7;
      let left = r.x + 8;
      for (const p of pills) {
        const w = p.w || p.label.length * MONO.badge + 12;
        const pg = svg("g", { class: p.cls + "-badge" });
        pg.append(svg("rect", { x: left, y: bottom, width: w, height: 15, rx: 7.5, class: p.cls + "-bg" }));
        if (p.cls === "talk") {
          pg.append(svg("path", { d: `M${left + 7},${bottom + 4} h9 v6 h-5 l-2.5,2.5 v-2.5 h-1.5 z`, class: "talk-icon" }));
          pg.append(text(left + 19, bottom + 11, p.label, { class: "talk-text" }));
        } else pg.append(text(left + w / 2, bottom + 11, p.label, { class: p.cls + "-text", "text-anchor": "middle" }));
        pg.append(svg("title", {}, p.title));
        g.append(pg);
        left += w + 6;
      }
      const types = m.head ? m.head.types : c.leaves.reduce((s, l) => s + ((METRICS[l] || {}).head || {}).types || 0, 0);
      const meta = isOther(c.id) ? plural(content.files, "file", "files") : `${plural(types, "type", "types")} · ${plural(content.files, "file", "files")}`;
      g.append(text(r.x + 10, r.y + 43, fitText(meta, r.w - 20, MONO.meta), { class: "meta" }));
      g.append(svg("circle", { cx: r.x + r.w - 26, cy: r.y + 14, r: 3, class: "cycle-mark" }));
      const glyph = text(r.x + r.w - 9, r.y + 19, leaf ? "≡" : "⤵", { class: "enter-glyph", "text-anchor": "end",
        onclick: (ev) => { ev.stopPropagation(); enter(c); } });
      glyph.append(svg("title", {}, leaf ? "Open types and diffs" : "Open " + nameOf(c.id)));
      g.append(glyph);
      previewInto(g, c, r);
      nodeLayer.append(g);
    }
    root.append(nodeLayer);
    if (selectedDecision) root.append(reachArrows(level, layout, selectedDecision));

    // Pins.
    const pinLayer = svg("g", { class: "pins" });
    for (const p of level.pins) {
      const r = rectOf(level, layout, p.key);
      const g = svg("g", { class: "pin" + (p.changed ? " changed" : "") + (p.kind === "external" ? " external" : "") + familyClass(p.id) + (p.key === state.selected ? " selected" : ""), "data-key": p.key,
        onclick: (ev) => clickSelect(ev, p.key),
        onmouseenter: () => hover(p.key), onmouseleave: () => hover(null) });
      // A pin stands for something outside the level, so it is a hexagonal tag, a shape no block takes.
      // An external assembly is a grey tag, and any other pin carries its category's color beside its name.
      const k = r.h / 2;
      g.append(svg("polygon", { points: `${r.x + k},${r.y} ${r.x + r.w - k},${r.y} ${r.x + r.w},${r.y + k} ${r.x + r.w - k},${r.y + r.h} ${r.x + k},${r.y + r.h} ${r.x},${r.y + k}`, class: "pin-box" }));
      const cat = categoryOf(p.id);
      if (cat) {
        // The icon chip sits just inside the left point, and the label follows it.
        const cx = r.x + k + PIN_PAD - 4;
        g.append(categoryChip(cat, cx, r.y + (r.h - PIN_CHIP) / 2, PIN_CHIP));
        g.append(text(cx + PIN_CHIP + 6, r.y + r.h / 2 + 4, truncate(p.name, 22), { class: "pin-label" }));
      } else {
        g.append(text(r.x + r.w / 2, r.y + r.h / 2 + 4, truncate(p.name, 22), { class: "pin-label", "text-anchor": "middle" }));
      }
      g.append(svg("title", {}, `${p.dir === "in" ? "Used by" : "Uses"} ${p.name}: ${p.edges} edge${p.edges === 1 ? "" : "s"}`));
      pinLayer.append(g);
    }
    root.append(pinLayer);
    root.append(labelLayer);
    return root;
  }

  // Returns the block of a level that each leaf is drawn in, by key.
  function ownerMap(level) {
    const owner = new Map();
    for (const c of level.children) for (const l of c.leaves) owner.set(l, c.key);
    return owner;
  }
  // Returns the keys of the blocks a decision is drawn from and reaches at a level, and the number of its consequence
  // hunks in each reached block. A block that is both source and reach counts as the source alone.
  function decisionBlocks(level, d) {
    const owner = ownerMap(level);
    const source = new Set(sourceLeavesOf(d).map((l) => owner.get(l)).filter(Boolean));
    const reach = new Map();
    d.consequences.forEach((c, i) => {
      const f = fileOf(c.file);
      for (const leaf of f ? countedLeaves(f) : []) {
        const k = owner.get(leaf);
        if (!k || source.has(k)) continue;
        if (!reach.has(k)) reach.set(k, new Set());
        reach.get(k).add(i);
      }
    });
    return { source, reach };
  }
  // Returns the point on a rectangle's border where the line from its center toward a point leaves it.
  function borderPoint(r, to) {
    const cx = r.x + r.w / 2, cy = r.y + r.h / 2, dx = to.x - cx, dy = to.y - cy;
    if (!dx && !dy) return { x: cx, y: cy };
    const k = Math.min(dx ? Math.abs(r.w / 2 / dx) : Infinity, dy ? Math.abs(r.h / 2 / dy) : Infinity);
    return { x: cx + dx * k, y: cy + dy * k };
  }
  // Returns the arrows from the block a decision is drawn from to each block that holds its consequences, each
  // labeled with the number of consequence hunks it points at. They are drawn above the blocks, straight and dashed, so
  // that cause pointing at effect is told apart from the dependency edges the layout routed.
  function reachArrows(level, layout, d) {
    const g = svg("g", { class: "reach-arrows" });
    const { source, reach } = decisionBlocks(level, d);
    for (const sk of source) {
      const a = rectOf(level, layout, sk);
      if (!a) continue;
      const ac = { x: a.x + a.w / 2, y: a.y + a.h / 2 };
      for (const [tk, hunks] of reach) {
        const b = rectOf(level, layout, tk);
        if (!b) continue;
        const bc = { x: b.x + b.w / 2, y: b.y + b.h / 2 };
        const p = borderPoint(a, bc), q = borderPoint(b, ac);
        g.append(svg("path", { d: `M${p.x.toFixed(1)},${p.y.toFixed(1)} L${q.x.toFixed(1)},${q.y.toFixed(1)}`, class: "reach-line", "marker-end": "url(#arrow-reach)" }));
        const label = plural(hunks.size, "hunk", "hunks"), w = label.length * MONO.badge + 12;
        const mx = p.x + (q.x - p.x) * 0.55, my = p.y + (q.y - p.y) * 0.55;
        g.append(svg("rect", { x: mx - w / 2, y: my - 8, width: w, height: 16, rx: 8, class: "reach-lbl" }));
        g.append(text(mx, my + 3.5, label, { class: "reach-lbl-text", "text-anchor": "middle" }));
      }
    }
    return g;
  }

  function groupContent(c) {
    const acc = { files: 0, added: 0, removed: 0, modified: 0 };
    for (const l of c.leaves) { const k = contentOf(l); acc.files += k.files; acc.added += k.added; acc.removed += k.removed; acc.modified += k.modified; }
    return acc;
  }
  const stripPin = (k) => k.startsWith("pin:") ? k.replace(/^pin:(in|out):/, "") : k;

  // ---------- Rendering: leaf view ----------

  function parseHeader(h) {
    const m = /@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@/.exec(h);
    return { a: m ? +m[1] : 1, b: m ? +m[2] : 1 };
  }
  // Pairs a hunk's lines into the rows of a split diff. Each side of a row holds the index of its line in the hunk, or
  // null where that side has no line.
  function pairLines(lines) {
    const rows = [];
    let i = 0;
    while (i < lines.length) {
      if (lines[i][0] === " ") { rows.push({ l: i, r: i, kind: "ctx" }); i++; continue; }
      const dels = [], adds = [];
      while (i < lines.length && lines[i][0] === "-") dels.push(i++);
      while (i < lines.length && lines[i][0] === "+") adds.push(i++);
      const n = Math.max(dels.length, adds.length);
      for (let j = 0; j < n; j++) rows.push({ l: j < dels.length ? dels[j] : null, r: j < adds.length ? adds[j] : null, kind: "chg" });
    }
    return rows;
  }

  // Highlight.js names for file extensions it does not know as aliases of its own.
  const LANGUAGE_ALIASES = { csproj: "xml", props: "xml", targets: "xml", slnx: "xml", resx: "xml", config: "xml", nuspec: "xml", xaml: "xml" };
  // Returns the highlight.js language for a file path, or null when highlight.js is not loaded or knows no language
  // for the file's extension.
  function languageOf(path) {
    if (typeof hljs === "undefined") return null;
    const ext = (/\.([^./\\]+)$/.exec(path) || [])[1];
    if (!ext) return null;
    const name = LANGUAGE_ALIASES[ext.toLowerCase()] || ext.toLowerCase();
    return hljs.getLanguage(name) ? name : null;
  }
  // Returns the highlighted HTML of one side of a hunk, one string per hunk line, with null for the lines of the other
  // side. The side is highlighted as one text, so a comment or string that spans lines is colored on every line it
  // covers. A span open at a line break closes at the end of that line and opens again at the start of the next.
  function highlightSide(lines, ops, language) {
    const picked = lines.map((line, i) => ops.includes(line[0]) ? i : -1).filter((i) => i >= 0);
    const out = new Array(lines.length).fill(null);
    if (!picked.length) return out;
    const html = hljs.highlight(picked.map((i) => lines[i][1]).join("\n"), { language, ignoreIllegals: true }).value;
    const open = [];
    let line = "", k = 0;
    for (const part of html.split(/(<span[^>]*>|<\/span>|\n)/)) {
      if (part === "\n") { out[picked[k++]] = line + "</span>".repeat(open.length); line = open.join(""); }
      else if (part.startsWith("<span")) { open.push(part); line += part; }
      else if (part === "</span>") { open.pop(); line += part; }
      else line += part;
    }
    out[picked[k]] = line;
    return out;
  }
  // Returns the highlighted HTML of every line of a hunk for each side of a split diff, or null when the file's
  // language is unknown and the lines show as plain text.
  function highlightHunk(path, lines) {
    const language = languageOf(path);
    if (!language) return null;
    return { left: highlightSide(lines, " -", language), right: highlightSide(lines, " +", language) };
  }

  // Returns a cell of code for one side of a split diff. The line's indentation becomes the cell's left padding, and a
  // line too long for its half wraps with a hanging indent two columns deeper, so a continuation sits under its code.
  // When html is given, the cell shows that highlighted markup of the line with its indentation removed.
  function codeCell(kind, line, html) {
    if (line === null) return el("td", { class: "code " + kind });
    const lead = /^[ 	]*/.exec(line)[0];
    const cols = lead.replace(/	/g, "    ").length;
    const td = el("td", { class: "code " + kind, style: `padding-left: calc(8px + ${cols + 2}ch); text-indent: -2ch` });
    if (!html) { td.append(line.slice(lead.length)); return markTerms(td, kind); }
    td.innerHTML = html;
    let strip = lead.length;
    const walker = document.createTreeWalker(td, NodeFilter.SHOW_TEXT);
    while (strip > 0 && walker.nextNode()) {
      const node = walker.currentNode, n = Math.min(strip, node.data.length);
      node.data = node.data.slice(n);
      strip -= n;
    }
    return markTerms(td, kind);
  }

  // Returns the split diff of one hunk of a file. The threads given, keyed by hunk and line index as threadsOf keys
  // them, show under their lines.
  // The annotations given, keyed the same way, show under their lines after the threads.
  function diffTable(path, hunk, index, threads, notes) {
    const nums = parseHeader(hunk.header);
    let la = nums.a, lb = nums.b;
    // The columns are fixed: each line-number column fits the hunk's largest line number, and the two code columns
    // split the rest evenly, so the halves stay equal however long the lines on either side are.
    const rows = pairLines(hunk.lines);
    const digits = String(Math.max(nums.a, nums.b) + rows.length).length;
    const ln = () => el("col", { style: `width: calc(${digits}ch + 17px)` });
    const trivial = hunk.trivial && state.opts.mute;
    const table = el("table", { class: "diff" + (trivial ? " trivial" : "") }, el("caption", {}, hunk.header + (trivial ? " · " + (reasonText(hunk.reason, hunk) || "no code changed") : "")),
      el("colgroup", {}, ln(), el("col", {}), ln(), el("col", {})));
    const body = el("tbody");
    const lit = highlightHunk(path, hunk.lines);
    const text = (i) => i === null ? null : hunk.lines[i][1];
    // While the pull request takes comments, a line number selects its line to comment on. Dragging from a line number
    // or Shift-clicking one selects a range of lines on the same side of the hunk, as on GitHub.
    const writable = canWrite() && index !== undefined;
    const c = state.compose && state.compose.path === path && state.compose.hunk === index ? state.compose : null;
    const picked = (side, i) => c && c.side === side && i !== null && i >= c.first && i <= c.last;
    const number = (side, i, n, kind) => {
      const td = el("td", { class: "ln " + kind + (writable && i !== null ? " pick" : "") + (picked(side, i) ? " picked" : "") }, i === null ? "" : String(n));
      if (writable && i !== null) {
        td.title = "Comment on this line. Drag or Shift-click to select a range.";
        td.addEventListener("mousedown", (ev) => startPick(ev, table, path, index, side, i));
      }
      return td;
    };
    for (const row of rows) {
      const lk = row.l === null ? "empty" : row.kind === "ctx" ? "ctx" : "del";
      const rk = row.r === null ? "empty" : row.kind === "ctx" ? "ctx" : "add";
      const left = codeCell(lk, text(row.l), lit && row.l !== null ? lit.left[row.l] : null), right = codeCell(rk, text(row.r), lit && row.r !== null ? lit.right[row.r] : null);
      if (picked("LEFT", row.l)) left.classList.add("picked");
      if (picked("RIGHT", row.r)) right.classList.add("picked");
      if (c && (c.side === "LEFT" ? row.l : row.r) === c.last) c.number = c.side === "LEFT" ? la : lb;
      body.append(el("tr", { "data-l": row.l, "data-r": row.r }, number("LEFT", row.l, la, lk), left, number("RIGHT", row.r, lb, rk), right));
      if (row.l !== null) la++;
      if (row.r !== null) lb++;
      if (c && (c.side === "LEFT" ? row.l : row.r) === c.last) body.append(el("tr", { class: "thread-row" }, el("td", { class: "thread-cell", colspan: 4 }, lineComposer(path, hunk))));
      const at = [...new Set([row.l, row.r])].filter((i) => i !== null).map((i) => index + ":" + i);
      const marks = notes ? at.flatMap((k) => notes.get(k) || []) : [];
      if (marks.length) body.append(el("tr", { class: "thread-row" }, el("td", { class: "thread-cell", colspan: 4 }, ...marks.map((a) => annotationBox(a)))));
      const here = threads ? at.flatMap((k) => threads.get(k) || []) : [];
      if (here.length) body.append(el("tr", { class: "thread-row" }, el("td", { class: "thread-cell", colspan: 4 }, ...here.map((t) => threadBox(t)))));
    }
    table.append(body);
    return el("div", { class: "diff-scroll" }, table);
  }

  function evidenceList(pairs, limit) {
    const ul = el("ul", { class: "evidence" });
    const shown = limit ? pairs.slice(0, limit) : pairs;
    for (const p of shown) {
      const st = p.base === 0 ? "added" : p.head === 0 ? "removed" : "modified";
      ul.append(el("li", { class: "st-" + st, title: `${p.from} → ${p.to}` },
        el("span", {}, `${shortName(p.from)} → ${shortName(p.to)}`),
        el("span", { class: "n" }, `${p.base} → ${p.head}`)));
    }
    if (limit && pairs.length > limit) ul.append(el("li", { class: "more" }, `${pairs.length - limit} more pairs`));
    return ul;
  }

  // Orders items by rank, lowest first, keeping the order within each rank.
  const byRank = (items, rank) => items.map((x, i) => [rank(x), i, x]).sort((a, b) => a[0] - b[0] || a[1] - b[1]).map((p) => p[2]);
  // Ranks a file in the leaf view: files with decisions or other edits first, then files whose hunks all follow from
  // decisions, then reviewed files.
  const fileRank = (path) => fileStatus(path) === "reviewed" ? 2 : consequenceOnly(path) ? 1 : 0;

  // Returns the header of a decision's hunk: what changed, whether it is public, its reach, and the action that marks
  // the files that follow from it alone.
  function decisionHead(d) {
    const lead = leadOf(d), others = d.declarations.length - 1;
    const head = el("div", { class: "decision-head" + (state.decision === d.id ? " selected" : "") },
      el("span", { class: "chip chip-decision" }, "decision"),
      el("span", {}, CHANGE_TEXT[lead.change] + " ", el("code", { title: lead.symbol }, declText(lead)), others ? ` and ${plural(others, "other", "others")}` : ""));
    if (d.public) head.append(el("span", { class: "public-mark", title: "Callers outside the solution may exist that the changeset cannot show." }, "public"));
    head.append(el("button", { class: "link", onclick: () => selectDecision(d.id), title: "List the consequences by block and mark them on the map" },
      d.consequences.length ? reachText(d) : "no consequences"));
    head.append(testsLine(d) || "");
    return head;
  }

  // ---------- Tests of decisions ----------
  // A decision lists the test methods that reference its declarations, directly or through a helper, each with its
  // status in the change. The header counts the two ways apart, since a shared fixture would otherwise make every
  // decision "referenced by 400 tests", and names the changed methods, whose names are often the sentence a reviewer
  // needs. See docs/tests.md section 5.1.
  const NO_TESTS_TIP = "No test method names this decision's declarations, directly or through a helper. A test can still reach it through code of the system that those references do not name.";
  const testName = (t) => memberName(t.method, "method");
  // The number of changed test methods a decision's header names before it counts the rest, so that a change to many
  // tests does not push the code down the page.
  const TESTS_NAMED = 2;
  const testTitle = (t) => t.method + (t.helper ? `\nThrough the helper ${memberName(t.helper, "method")}` : "") + `\n${t.status}`;
  // Opens the decision in the details panel with its tests shown.
  function showDecisionTests(d) {
    state.decision = d.id;
    state.decisionTests = d.id;
    redraw();
  }
  // Returns the line that says which test methods reference a decision, or "no test references it" for a public
  // decision that none does, or null for a decision that is neither tested nor public.
  function testsLine(d) {
    const tests = d.tests || [];
    if (!tests.length) return d.public ? el("span", { class: "tests-line none", title: NO_TESTS_TIP }, "no test references it") : null;
    const direct = tests.filter((t) => t.way === "direct").length, helper = tests.length - direct;
    const counts = ["added", "removed", "changed"].map((s) => [s, tests.filter((t) => t.status === s).length]).filter((x) => x[1]).map(([s, n]) => `${n} ${s}`);
    const link = (text, title) => el("button", { class: "link", onclick: () => showDecisionTests(d), title }, text);
    const line = el("span", { class: "tests-line" }, "referenced by ");
    if (direct) line.append(link(`${plural(direct, "test", "tests")} directly`, "List the test methods whose own lines reference it"));
    if (direct && helper) line.append(" and ");
    if (helper) line.append(link(direct ? `${helper} through helpers` : `${plural(helper, "test", "tests")} through helpers`, "List the test methods that reach it through a helper they call"));
    if (counts.length) line.append(": " + counts.join(", "));
    const changed = tests.filter((t) => t.status !== "unchanged");
    if (changed.length) {
      const named = changed.slice(0, TESTS_NAMED), more = changed.length - named.length;
      line.append(" · ", ...named.flatMap((t, i) => [i ? ", " : "", el("code", { title: testTitle(t) }, testName(t))]));
      if (more) line.append(" and ", link(`${more} more`, "List every test method in the details panel"));
    }
    return line;
  }
  // Returns the details panel's lists of a decision's test methods, those that reference it directly and those that
  // reach it through a helper, each method naming its status and, on hover, its helper.
  function decisionTestsBlock(d) {
    const tests = d.tests || [];
    const box = el("div", { class: "decision-tests" });
    if (!tests.length) { box.append(el("p", { class: "hint", title: NO_TESTS_TIP }, d.public ? "No test references it." : "No test references it, and it is not public.")); return box; }
    for (const [way, title] of [["direct", "Referenced directly"], ["helper", "Referenced through helpers"]]) {
      const list = tests.filter((t) => t.way === way);
      if (!list.length) continue;
      box.append(el("h3", {}, `${title}: ${list.length}`));
      box.append(el("ul", { class: "test-methods" }, ...list.map((t) => el("li", { class: "st-" + (METHOD_STATUS[t.status] || "unchanged"), title: testTitle(t) },
        el("span", { class: "row-change" }, t.status + " "), el("code", {}, testName(t)), t.helper ? el("span", { class: "row-change" }, ` via ${memberName(t.helper, "method")}`) : ""))));
    }
    return box;
  }

  // Returns a consequence's hunk collapsed to one line that names the decision it follows from, which shows the diff on
  // a click. A consequence with threads or check annotations on its lines starts out shown.
  function consequenceBlock(path, hunk, index, table, flagged) {
    const key = hunkId(path, index), d = DECISIONS.get(hunk.follows[0]), lead = leadOf(d);
    const box = el("div", { class: "consequence", "data-hunk": key });
    const shown = state.expandedHunks.has(key) || flagged;
    const toggle = el("button", { class: "link", onclick: () => {
      const show = !box.querySelector(".diff-scroll");
      if (show) { box.append(table()); state.expandedHunks.add(key); } else { box.querySelector(".diff-scroll").remove(); state.expandedHunks.delete(key); }
      toggle.textContent = show ? "Hide diff" : "Show diff";
    } }, shown ? "Hide diff" : "Show diff");
    const more = hunk.follows.length - 1;
    box.append(el("div", { class: "consequence-head" }, el("span", { class: "chip chip-consequence" }, "consequence"), el("span", {}, hunk.header),
      el("span", {}, "follows from ", el("button", { class: "link", title: lead.symbol, onclick: () => openHunk(d.file, d.hunk) }, declText(lead)), more ? ` and ${plural(more, "other decision", "other decisions")}` : ""),
      toggle));
    if (shown) box.append(table());
    return box;
  }

  // Returns one hunk of a file for the leaf view: a decision under its header, a consequence collapsed, or the diff alone.
  function hunkBlock(path, hunk, index, threads, notes) {
    const table = () => diffTable(path, hunk, index, threads, notes);
    const key = hunkId(path, index), d = DECISIONS.get(key);
    if (d) return el("div", { class: "decision", "data-hunk": key }, decisionHead(d), table());
    if (hunk.follows && DECISIONS.has(hunk.follows[0])) {
      const flagged = [threads, notes].some((m) => m && [...m.keys()].some((k) => k.startsWith(index + ":")));
      return consequenceBlock(path, hunk, index, table, flagged);
    }
    const t = table();
    t.dataset.hunk = key;
    return t;
  }

  // Selects a decision and scrolls the Changes panel to its row in the list of decisions, switching the panel from the
  // path or the glossary to the changes. The panel scrolls once it is drawn again.
  function revealDecision(d) {
    state.decision = d.id;
    state.panel = "changes";
    state.revealDecision = d.id;
    redraw();
  }

  // Selects a decision, or clears the selection when it is already selected or when the identifier is null.
  function selectDecision(id) {
    state.decision = id && state.decision !== id ? id : null;
    redraw();
  }

  // Selects a decision and shows it on the map, at the level that draws the block it is in and every block it reaches
  // as separate blocks, unless the level on screen already draws them all.
  async function showDecision(d) {
    state.decision = d.id;
    const sources = sourceLeavesOf(d), leaves = [...sources, ...reachOf(d).blocks];
    if (state.current && !loc().leaf) {
      // The level on screen serves when it draws every leaf involved, and draws the reach apart from the source
      // wherever the reach goes beyond the source's own leaves.
      const owner = ownerMap(state.current.level);
      const beyond = [...reachOf(d).blocks].some((l) => !sources.includes(l));
      if (leaves.every((l) => owner.has(l)) && (!beyond || decisionBlocks(state.current.level, d).reach.size)) { redraw(); return; }
    }
    const level = levelShowing(leaves);
    rememberView();
    state.trail = [{ level: ROOT }, ...ancestors(level).map((x) => ({ level: x }))];
    state.selected = null;
    const m = await mount();
    if (m) setViewBox(arrive(m));
  }

  // Marks reviewed every file whose hunks all follow from a decision alone or are trivial, and says how many it marked
  // and how many files with consequences it left because they also carry other edits.
  async function markConsequences(d) {
    const todo = d.marks.filter((p) => !REVIEWED.has(p));
    const left = [...new Set(d.consequences.map((c) => c.file))].filter((p) => !d.marks.includes(p)).length;
    const already = d.marks.length - todo.length;
    state.decisionNote = { id: d.id, text: `Marked ${plural(todo.length, "file", "files")}` + (already ? `, ${already} already reviewed` : "") +
      (left ? `; left ${plural(left, "file that also carries", "files that also carry")} other edits` : "") + "." };
    if (todo.length) await setReviewed(todo, true);
    else redraw();
  }

  // Opens the leaf view at a hunk and scrolls to it, showing it when it is a collapsed consequence or in a reviewed file.
  async function openHunk(path, index) {
    const hunk = (HUNKS.get(path) || [])[index];
    if (hunk && hunk.follows) state.expandedHunks.add(hunkId(path, index));
    if (REVIEWED.has(path)) state.expanded.add(path);
    await openFile(path);
    const target = document.querySelector(`[data-hunk="${CSS.escape(hunkId(path, index))}"]`);
    if (!target) return;
    // The hunk is outlined for a moment, since one near the end of the page cannot scroll to the top.
    target.scrollIntoView({ block: "start" });
    target.classList.remove("flash");
    void target.offsetWidth;
    target.classList.add("flash");
  }

  // Returns the details panel's section for the selected decision: its declarations, and its consequences grouped by
  // the blocks that hold them, each opening its hunk.
  function decisionPanel(d) {
    const box = el("section", { class: "decision-panel" });
    box.append(el("div", { class: "decision-panel-head" }, el("h2", {}, "Decision", help(HELP.decision)), el("button", { class: "link", onclick: () => selectDecision(null) }, "Close")));
    box.append(el("p", { class: "ident" }, el("button", { class: "link", onclick: () => openHunk(d.file, d.hunk) }, `${d.file} · hunk ${d.hunk + 1}`),
      loc().leaf && d.consequences.length ? el("span", {}, " · ", el("button", { class: "link", onclick: () => showDecision(d) }, "Show reach on the map")) : null,
      " · ", el("button", { class: "link", onclick: () => revealDecision(d), title: "Show this decision in the Changes panel" }, "Show in Changes")));
    for (const x of d.declarations) {
      box.append(kv(CHANGE_TEXT[x.change], el("span", {}, el("code", { title: x.symbol }, declText(x)), ` · ${x.kind}, ${x.accessibility || "default"} · sites ${x.sitesBase === x.sitesHead ? x.sitesHead : x.sitesBase + " → " + x.sitesHead}`)));
    }
    if (d.public) box.append(el("p", { class: "warn" }, "Public: callers outside the solution may exist that the changeset cannot show."));
    const tests = el("details", { class: "decision-tests-box", open: state.decisionTests === d.id, ontoggle: (ev) => { state.decisionTests = ev.target.open ? d.id : null; } },
      el("summary", {}, (d.tests || []).length ? `Tests: ${plural(d.tests.length, "test method", "test methods")}` : "Tests: none"), decisionTestsBlock(d));
    box.append(tests);
    if (state.decisionTests === d.id) requestAnimationFrame(() => tests.scrollIntoView({ block: "nearest" }));
    if (!d.consequences.length) { box.append(el("p", { class: "hint" }, "No other hunk follows from this decision alone.")); return box; }
    box.append(el("h3", {}, reachText(d)));
    const byBlock = new Map();
    // Consequences in test files are grouped apart, last, since test files count under no block.
    const IN_TESTS = "~in-tests";
    for (const c of d.consequences) {
      const f = fileOf(c.file);
      for (const leaf of !f ? [] : f.test ? [IN_TESTS] : countedLeaves(f)) { if (!byBlock.has(leaf)) byBlock.set(leaf, []); byBlock.get(leaf).push(c); }
    }
    const ul = el("ul", { class: "list" });
    for (const [leaf, list] of [...byBlock].sort((a, b) => (a[0] === IN_TESTS) - (b[0] === IN_TESTS) || b[1].length - a[1].length || nameOf(a[0]).localeCompare(nameOf(b[0])))) {
      ul.append(el("li", {}, leaf === IN_TESTS
        ? el("div", { class: "row" }, el("span", { class: "tree-icon" }), el("span", { class: "row-name" }, "In tests"), el("span", { class: "row-meta" }, plural(list.length, "hunk", "hunks")))
        : el("button", { class: "row", onclick: () => showInContext(leaf), title: leaf }, treeIcon(leaf), el("span", { class: "row-name" }, nameOf(leaf)),
          el("span", { class: "row-meta" }, plural(list.length, "hunk", "hunks")))));
      for (const c of list) {
        const hunk = (HUNKS.get(c.file) || [])[c.hunk];
        ul.append(el("li", {}, el("button", { class: "row reach-hunk" + (REVIEWED.has(c.file) ? " st-reviewed" : ""), onclick: () => openHunk(c.file, c.hunk), title: c.file },
          el("span", { class: "row-name" }, c.file.split("/").pop() + (hunk ? " " + hunk.header : "")), el("span", { class: "row-meta" }, plural(c.lines, "line", "lines")))));
      }
    }
    box.append(ul);
    box.append(el("button", { class: "btn", onclick: () => markConsequences(d) }, "Mark consequences reviewed"));
    if (state.decisionNote && state.decisionNote.id === d.id) box.append(el("p", { class: "hint" }, state.decisionNote.text));
    return box;
  }

  // Returns a file's header and diffs for the leaf view. A changed file's header carries its reviewed checkbox, and a
  // reviewed file's diffs stay collapsed while reviewed files are muted, until the reader shows them. A renamed file
  // names its path at base, and a path at base that a type still lists links to the renamed file. When types are given,
  // a line under the header names them, with the status of each that was added, removed or moved.
  function fileBlock(path, types) {
    const f = fileOf(path), fs = fileStatus(path);
    if (f && f.path !== path) {
      return el("div", { class: "file file-" + fs },
        el("div", { class: "file-head" }, el("span", { class: "chip chip-" + fs }, fs), el("span", { class: "file-path" }, path),
          el("span", { class: "file-note" }, "renamed to"), el("button", { class: "link", onclick: () => openFile(f.path), title: "Show the renamed file's diff" }, f.path)));
    }
    const changed = !!f && f.status !== "unchanged";
    const done = changed && REVIEWED.has(path);
    const hunks = HUNKS.get(path) || [];
    const head = el("div", { class: "file-head" });
    if (changed) head.append(el("input", { type: "checkbox", checked: done, "aria-label": "Reviewed", title: done ? "Reviewed. Clear it to review the file again." : "Mark the file reviewed",
      onchange: (ev) => setReviewed([path], ev.target.checked) }));
    const chip = fs !== "reviewed" && consequenceOnly(path) ? "consequence" : fs;
    head.append(el("span", { class: "chip chip-" + chip }, chip), el("span", { class: "file-path" }, path));
    if (f && f.previousPath) head.append(el("span", { class: "file-note" }, "renamed from " + f.previousPath));
    if (f && f.test) head.append(...signalsOf(path).map((s) => signalChip(s, true)));
    if (done) head.append(reasonNote(path, REVIEWED.get(path)) || "");
    const talk = threadsOf(path), notes = annotationsOf(path);
    const problems = [...notes.lines.values()].flat().concat(notes.top).filter(isProblem).length;
    if (problems) head.append(el("span", { class: "file-note problem-note" }, plural(problems, "check problem", "check problems")));
    const open = [...talk.lines.values()].flat().concat(talk.top).filter((t) => !t.isResolved).length;
    if (open) head.append(el("span", { class: "file-note talk-note" }, plural(open, "unresolved thread", "unresolved threads")));
    const muted = done && state.opts.mute;
    const block = el("div", { class: "file file-" + fs, "data-file": path }, head);
    if (types && types.length) block.append(declaresLine(types));
    const diffs = el("div", { class: "file-diffs" });
    const fill = () => { if (!diffs.childElementCount) hunks.forEach((h, i) => diffs.append(hunkBlock(path, h, i, talk.lines, notes.lines))); };
    if (muted && !state.expanded.has(path)) diffs.hidden = true;
    else fill();
    if (muted && hunks.length) {
      head.append(el("button", { class: "link", onclick: (ev) => {
        const show = diffs.hidden;
        if (show) { fill(); state.expanded.add(path); } else state.expanded.delete(path);
        diffs.hidden = !show;
        ev.target.textContent = show ? "Hide diff" : "Show diff";
      } }, diffs.hidden ? "Show diff" : "Hide diff"));
    }
    if (notes.top.length) block.append(el("div", { class: "file-threads" }, ...notes.top.map((a) => annotationBox(a))));
    if (talk.top.length) block.append(el("div", { class: "file-threads" }, ...talk.top.map((t) => threadBox(t, { top: true }))));
    block.append(diffs);
    return block;
  }

  // Returns the line under a file's header that names the types it declares in the leaf on screen. A type that was
  // added, removed or moved carries that status, since a file's diff does not show it. A type that only changed shows
  // no status, since the changeset does not say which of a file's types its hunks touch. A type that the changeset
  // reports as an instance of a pattern carries a chip for each such pattern, which is the only place the leaf view
  // shows patterns.
  function declaresLine(types) {
    const line = el("div", { class: "file-types" }, "declares ");
    types.forEach((t, i) => {
      if (i) line.append(" · ");
      line.append(el("code", { title: t.name }, shortName(t.name)));
      if (t.status !== "unchanged") line.append(" ", el("span", { class: "chip chip-" + t.status, title: t.status === "moved" ? `from ${t.leafBase} to ${t.leafHead}` : "" }, t.status));
      for (const x of PATTERNS_OF.get(t.name) || []) line.append(" ", patternChip(x.pattern, x.instance));
    });
    return line;
  }

  // Returns the chip that marks a type as an instance of a pattern, with the number of the pattern's norms the change
  // makes it depart from, which shows the pattern in the details panel.
  function patternChip(p, instance) {
    const on = !!state.pattern && state.pattern.id === p.id && state.pattern.type === instance.type;
    const n = instance.departures.length;
    return el("button", { class: "chip chip-pattern" + (n ? " departs" : "") + (on ? " on" : ""), onclick: () => showPattern(on ? null : { id: p.id, type: instance.type }),
      title: `An instance of the pattern ${p.name}, which ${plural(p.count, "type", "types")} played before this change` +
        (n ? `. It departs from ${plural(n, "norm", "norms")}: ${departureText(p, instance)}.` : ".") + " Click to show the pattern." },
      p.name + (n ? ` · ${plural(n, "departure", "departures")}` : ""));
  }

  // Returns the words for an instance's departures from its pattern's norms, such as "no IUnitOfWork, no test".
  const departureText = (p, instance) => instance.departures.map((f) => (p.norms.find((n) => n.feature === f) || { absence: f }).absence).join(", ");

  // Shows a pattern in the details panel, given as its identifier and the type it is opened from, or closes it for null.
  function showPattern(selection) {
    state.pattern = selection;
    redraw();
  }

  // Returns the norms of a pattern as a table, each with its share and a check or a cross for an instance, the
  // departures first, then the norms the instance lacked before the change, then those it has. A companion norm names
  // the instance's companion when it has one. The instance's unusual traits follow in a line of their own.
  function normsBlock(p, instance) {
    const box = el("div", { class: "pattern-norms" });
    if (!p.norms.length) {
      box.append(el("p", { class: "hint" }, p.count < 5 ? `No norms: a pattern needs five instances before what they share counts as usual.`
        : "No norms: no feature is shared by four in five instances."));
    } else {
      const rank = (n) => instance.departures.includes(n.feature) ? 0 : instance.lacks.includes(n.feature) ? 1 : 2;
      const table = el("table", { class: "norms" });
      for (const n of p.norms.slice().sort((a, b) => rank(a) - rank(b))) {
        const r = rank(n);
        const companion = n.feature.startsWith("companion:") ? instance.companions[n.feature.slice("companion:".length)] : null;
        table.append(el("tr", { class: ["departure", "lacked", "kept"][r], title: ["The change makes this instance depart from the norm.",
          "This instance lacked the norm before the change, so it is not a departure.", "This instance has the norm."][r] },
          el("td", { class: "mark" }, r === 2 ? "✓" : "✗"),
          el("td", {}, n.text, companion ? el("span", { class: "row-meta" }, " · " + shortName(companion)) : ""),
          el("td", { class: "share" }, `${n.share} of ${p.count}`)));
      }
      box.append(el("h3", {}, instance.departures.length ? `Norms: ${plural(instance.departures.length, "departure", "departures")}` : "Norms"), table);
    }
    if (instance.unusual.length) {
      box.append(el("p", { class: "unusual" }, el("span", { class: "k" }, "Only this instance "),
        ...instance.unusual.flatMap((k, i) => [i ? ", " : "", el("code", { title: k }, traitName(k))])));
    }
    return box;
  }

  // Returns the short name of a trait given as a key: the type's last segment, and an attribute in brackets without its
  // Attribute suffix, as [Obsolete].
  function traitName(key) {
    const name = key.slice(key.indexOf(":") + 1).split(".").pop();
    return key.startsWith("attribute:") ? `[${name.replace(/(.)Attribute$/, "$1")}]` : name;
  }

  // Opens the leaf view that holds a type, at its first changed file when it has one.
  function openType(name) {
    const t = CS.types.find((x) => x.name === name);
    if (!t) return;
    const changed = t.files.find((p) => fileStatus(p) !== "unchanged");
    return changed ? openFile(changed) : openLeaf(t.leafHead || t.leafBase);
  }

  // Returns the details panel's section for the selected pattern: its name, purpose and keys, its norms with a check or a
  // cross for the instance it was opened from, that instance's unusual traits, its added and changed instances, and
  // every other instance grouped by the block that holds it, each block collapsed to a count until opened.
  function patternPanel(p, type) {
    const box = el("section", { class: "decision-panel pattern-panel" });
    box.append(el("div", { class: "decision-panel-head" }, el("h2", {}, "Pattern", help(HELP.pattern)), el("button", { class: "link", onclick: () => showPattern(null) }, "Close")));
    box.append(el("p", { class: "pattern-name" }, p.name), el("p", { class: "purpose" }, p.purpose || (p.by === "key" ? "Named from its first key." : "")));
    box.append(kv(p.keys.length === 1 ? "Key" : "Keys", el("span", {}, ...p.keys.flatMap((k, i) => [i ? ", " : "", el("code", {}, k)]))));
    box.append(kv("Before", plural(p.count, "instance", "instances")));
    const instance = p.instances.find((i) => i.type === type);
    if (instance) box.append(normsBlock(p, instance));
    const row = (name, status) => {
      const t = CS.types.find((x) => x.name === name);
      return el("li", {}, el("button", { class: "row" + (name === type ? " current" : ""), onclick: () => openType(name), title: name },
        status ? el("span", { class: "chip chip-" + status }, status) : "", el("span", { class: "row-name" }, shortName(name)),
        el("span", { class: "row-meta" }, t ? nameOf(t.leafHead || t.leafBase) : "")));
    };
    box.append(el("h3", {}, `In this change: ${plural(p.instances.length, "instance", "instances")}`));
    box.append(el("ul", { class: "list" }, ...p.instances.map((i) => row(i.type, i.status))));
    if (!p.others.length) return box;
    box.append(el("h3", {}, `Other instances: ${p.others.length}`));
    const byBlock = new Map();
    for (const o of p.others) { if (!byBlock.has(o.leaf)) byBlock.set(o.leaf, []); byBlock.get(o.leaf).push(o.type); }
    for (const [leaf, names] of [...byBlock].sort((a, b) => b[1].length - a[1].length || nameOf(a[0]).localeCompare(nameOf(b[0])))) {
      box.append(el("details", { class: "pattern-block", open: state.patternBlocks.has(leaf),
        ontoggle: (ev) => { if (ev.target.open) state.patternBlocks.add(leaf); else state.patternBlocks.delete(leaf); } },
        el("summary", { title: leaf }, `${nameOf(leaf)} · ${names.length}`), el("ul", { class: "list" }, ...names.map((n) => row(n, null)))));
    }
    return box;
  }

  // Returns the line that says how many of a node's changed files are reviewed, with a button that marks them all, or
  // unmarks them all once they all are. It is null for a node without changed files.
  function reviewedBar(key) {
    const r = reviewedCount(key);
    if (!r.changed) return null;
    const all = r.reviewed === r.changed;
    return el("div", { class: "reviewed-bar" }, el("span", {}, `${r.reviewed} of ${plural(r.changed, "changed file", "changed files")} reviewed`),
      el("button", { class: "btn", onclick: () => setReviewed(r.paths, !all) }, all ? "Unmark all" : "Mark all reviewed"));
  }

  // Returns a leaf's Tests section, which holds the changed test files whose subject binds to the leaf, or null when it
  // has none. It stays collapsed to one line that counts them until the reader opens it, and then shows each file as
  // the leaf view shows any file.
  function testsSection(id) {
    const files = byRank(testFilesOf(id).map((f) => f.path), fileRank);
    if (!files.length) return null;
    const reviewed = files.filter((p) => REVIEWED.has(p)).length;
    const box = el("details", { class: "tests-section", open: state.testsOpen.has(id),
      ontoggle: (ev) => { if (ev.target.open) state.testsOpen.add(id); else state.testsOpen.delete(id); } },
      el("summary", {}, `Tests: ${plural(files.length, "changed test file", "changed test files")}`, (() => { const n = files.flatMap(signalsOf).length; return n ? ` · ${plural(n, "signal", "signals")}` : ""; })(),
        ` · ${reviewed} reviewed`, help(HELP.testsSection)));
    const card = el("section", { class: "typecard" });
    for (const path of files) card.append(fileBlock(path));
    box.append(card);
    return box;
  }

  function renderLeaf(id) {
    const wrap = el("div", { class: "leaf" });
    const m = metricsOf(id), content = contentOf(id);
    wrap.append(el("div", {},
      el("div", { class: "leaf-title" }, nameOf(id).toUpperCase()),
      el("div", { class: "leaf-sub" }, purposeOf(id))));
    if (isOther(id) || id === TESTS) {
      // Files that no type binds, and the files of test infrastructure, have no types and no edges in the map, so the
      // leaf lists the files themselves.
      const files = byRank((id === TESTS ? testFilesOf(TESTS) : otherFilesOf(id)).map((f) => f.path), fileRank);
      wrap.append(reviewedBar(id) || "", el("h3", {}, plural(files.length, "changed file", "changed files")));
      const card = el("section", { class: "typecard" });
      for (const path of files) card.append(fileBlock(path));
      wrap.append(card);
      return wrap;
    }
    const metrics = el("div", { class: "metrics" });
    const mb = m.base, mh = m.head;
    const stat = (label, b, h, fmt = (x) => String(x)) => {
      const span = el("span", {}, label + " ", el("b", {}, h === null ? "—" : fmt(h)));
      if (b !== null && h !== null && b !== h) span.append(" ", el("span", { class: "delta" }, `from ${fmt(b)}`));
      return span;
    };
    metrics.append(stat("types", mb ? mb.types : null, mh ? mh.types : null));
    metrics.append(el("span", {}, "files ", el("b", {}, String(content.files)), content.added + content.removed + content.modified ? ` · +${content.added} −${content.removed} ~${content.modified}` : ""));
    metrics.append(stat("internal refs", mb ? mb.internal : null, mh ? mh.internal : null));
    metrics.append(stat("crossing refs", mb ? mb.crossing : null, mh ? mh.crossing : null));
    wrap.append(metrics);
    const bar = reviewedBar(id);
    if (bar) wrap.append(bar);

    // The files of the changed types, each once, however many of the leaf's types it declares. A path at base that a
    // type still lists stands for its renamed file. Files keep the order of their types within a rank, so the files of
    // a partial type stay together.
    const types = typesOf(id);
    const declared = new Map();
    for (const t of types.filter(typeTouched)) {
      for (const p of t.files) {
        const path = fileOf(p) ? fileOf(p).path : p;
        if (!declared.has(path)) declared.set(path, []);
        if (!declared.get(path).includes(t)) declared.get(path).push(t);
      }
    }
    const files = byRank([...declared.keys()], fileRank);
    wrap.append(el("h3", {}, files.length ? plural(files.length, "file", "files") : `No changes among ${types.length} types`, help(HELP.types)));
    if (files.length) {
      const card = el("section", { class: "typecard" });
      for (const path of files) card.append(fileBlock(path, declared.get(path)));
      wrap.append(card);
    }
    wrap.append(testsSection(id) || "");

    // Edges in and out of this leaf, changed first.
    const out = CS.edges.filter((e) => e.from === id && e.to !== id), inn = CS.edges.filter((e) => e.to === id && e.from !== id);
    const self = CS.edges.find((e) => e.from === id && e.to === id);
    const order = (a, b) => (a.status === "unchanged") - (b.status === "unchanged") || Math.abs(b.head - b.base) - Math.abs(a.head - a.base) || b.head - a.head;
    const col = (title, list, other) => {
      const box = el("div", {}, el("h3", {}, `${title} (${list.length})`));
      for (const e of list.sort(order)) {
        const row = el("div", { class: "edge-row" + statusClass(e.status), onclick: () => { const ev = row.querySelector(".evidence"); if (ev) ev.hidden = !ev.hidden; } });
        const top = el("div", { class: "top" }, el("span", { class: "dot" + (e.status === "unchanged" ? "" : "") , style: e.status === "unchanged" ? "visibility:hidden" : "" }), el("span", {}, nameOf(other(e)) + " ", el("span", { class: "typens" }, other(e))),
          el("span", { class: "n" }, e.base === e.head ? String(e.head) : `${e.base} → ${e.head}`));
        row.append(top);
        if (e.evidence && e.evidence.length) { const ev = evidenceList(e.evidence, 10); ev.hidden = true; row.append(ev); }
        box.append(row);
      }
      return box;
    };
    const cols = el("div", { class: "edge-cols" }, col("Uses", out, (e) => e.to), col("Used by", inn, (e) => e.from));
    cols.querySelector("h3").append(help(HELP.uses));
    wrap.append(cols);
    if (self) {
      const d = el("details", {}, el("summary", {}, `Internal references: ${self.base === self.head ? self.head : self.base + " → " + self.head}`));
      if (self.evidence && self.evidence.length) d.append(evidenceList(self.evidence, 20));
      wrap.append(d);
    }

    // The whole type roster.
    const all = el("details", {}, el("summary", {}, `All ${types.length} types`));
    const tl = el("div", { class: "typelist" });
    for (const t of types.slice().sort((a, b) => shortName(a.name).localeCompare(shortName(b.name)))) {
      const ts = typeStatus(t);
      tl.append(el("span", { class: ts === "added" ? "t-added" : ts === "reviewed" ? "t-reviewed" : ts !== "unchanged" ? "t-changed" : "", title: t.name + "\n" + t.files.join("\n") }, shortName(t.name)));
    }
    all.append(tl);
    wrap.append(all);
    return wrap;
  }

  // ---------- Panels ----------

  function renderBreadcrumb() {
    const bc = $("#crumbs");
    bc.replaceChildren();
    state.trail.forEach((l, i) => {
      const last = i === state.trail.length - 1;
      bc.append(el("button", { class: "crumb" + (last ? " current" : ""), disabled: last, onclick: () => goTo(i) }, locName(l)));
      if (!last) bc.append(el("span", { class: "crumb-sep" }, "›"));
    });
    const seg = $("#viewseg");
    const l = loc();
    const views = l.leaf ? [] : viewsAt(l.level);
    seg.hidden = views.length === 0;
    seg.replaceChildren();
    if (views.length) {
      // The views come first, since the page opens on the first of them, and the map's own hierarchy follows.
      for (const v of views) seg.append(el("button", { class: l.view === v ? "on" : "", onclick: () => switchView(v), title: VIEWS[v].purpose || VIEWS[v].name }, VIEWS[v].name));
      seg.append(el("button", { class: !l.view ? "on" : "", onclick: () => switchView(null), title: "The map's own hierarchy: each component and the parts it owns." }, "By component"));
    }
  }

  // Returns a small inline icon of a node's category for the side panels, or nothing when it has none.
  function treeIcon(id) {
    const c = categoryOf(id);
    if (!c) return el("span", { class: "tree-icon" });
    const box = document.createElementNS(SVGNS, "svg");
    box.setAttribute("viewBox", "0 0 16 16");
    box.setAttribute("class", "tree-icon");
    box.append(categoryChip(c, 0, 0, 16));
    return box;
  }

  function changeTree(id) {
    const st = statusOf(id);
    if (st === "unchanged") return null;
    const talk = openThreadsUnder(id).length;
    if (state.opts.talk && !talk) return null;
    const leaves = leavesUnder(id);
    const c = contentOf(id);
    const li = el("li");
    const edges = changedEdgesUnder(leaves);
    const isCur = !loc().leaf && !loc().view && loc().level === id || loc().leaf === id;
    li.append(el("button", { class: "row st-" + st + (isCur ? " current" : ""), onclick: () => showInContext(id) },
      el("span", { class: "dot" }), treeIcon(id), el("span", { class: "row-name" }, nameOf(id)),
      el("span", { class: "row-meta" }, state.opts.talk ? plural(talk, "thread", "threads") : st === "added" ? "new" : st === "removed" ? "removed" : `${c.added + c.removed + c.modified} files · ${edges} edges`)));
    if (!isLeaf(id)) {
      const kids = childrenOf(id).map(changeTree).filter(Boolean);
      if (kids.length) li.append(el("ul", {}, ...kids));
    }
    return li;
  }

  // ---------- The reading path ----------
  // The order in which to read the changeset, as the changeset plans it: the surprises first, each with its signal,
  // then the other changed blocks with the decisions and files under them, and last what is set aside. A step is read
  // when every changed file it names is reviewed. See docs/attention.md section 6.
  const PATH = CS.path || [];
  const TIER_TITLE = { first: "Read first", then: "Then", aside: "Set aside" };
  const stepRead = (s) => s.files.length > 0 && s.files.every((p) => REVIEWED.has(p));
  const countedSteps = () => PATH.filter((s) => s.tier !== "aside");
  // Returns the narrative of an edge, cycle or node step once it has arrived, or null.
  function stepNarrative(s) {
    if (NARR.state === "off") return null;
    if (s.kind === "node") return NARR.items.get("node:" + s.target) || null;
    if (s.kind === "edge" || s.kind === "cycle") return NARR.items.get("edge:" + s.target) || null;
    return null;
  }
  // Returns the step to open next: the first unread step after the current one, or else the first unread step from
  // the top, or null when every step is read.
  function nextStep() {
    const steps = countedSteps();
    const at = steps.findIndex((s) => s.id === state.step);
    return steps.slice(at + 1).find((s) => !stepRead(s)) || steps.find((s) => !stepRead(s)) || null;
  }
  // Returns the step before the current one in reading order, read or not, so that a step passed over is one press
  // away, or null at the first step or when none is current.
  function previousStep() {
    const steps = countedSteps();
    const at = steps.findIndex((s) => s.id === state.step);
    return at > 0 ? steps[at - 1] : null;
  }
  // Opens a step where it lives and makes it the current step: an edge or a cycle's closing edge at its level or
  // view, selected; a block in its level; a decision at its hunk, with its reach in the details panel; a file in its
  // leaf. A set-aside step opens the leaf of its first file.
  async function openStep(s) {
    state.step = s.id;
    if (state.tab !== "map") showTab("map");
    if (s.kind === "node") return showInContext(s.target);
    if (s.kind === "pattern") {
      // A pattern step opens its instance's file with the pattern in the details panel.
      state.pattern = { id: s.pattern, type: s.target };
      return openType(s.target);
    }
    if (s.kind === "decision") {
      const d = DECISIONS.get(s.target);
      return d ? openDecision(d) : undefined;
    }
    if (s.kind === "test" || s.kind === "tests") {
      // A test type's step opens the first hunk with a signal of its own that touches changed code, and the closing step
      // the first hunk with a signal that touches none, rather than the top of the file.
      const at = signalHunk(s.files, (x) => s.kind === "test" ? x.touches.length > 0 && isMethodOf(x.method, s.target) : !x.touches.length);
      if (at) return openHunk(at.path, at.index);
    }
    if (s.kind === "file" || s.kind === "test" || s.kind === "tests") return s.files.length ? openFile(s.files[0]) : renderChanges();
    if (s.kind === "aside" || s.kind === "more") return s.files.length ? openFile(s.files[0]) : renderChanges();
    const loc = s.location || ROOT;
    rememberView();
    if (loc.startsWith("view:")) {
      const v = loc.slice(5), level = VIEWS[v] ? VIEWS[v].level : ROOT;
      state.trail = [{ level: ROOT }, ...ancestors(level).map((x) => ({ level: x })), { level, view: v }];
    } else state.trail = [{ level: ROOT }, ...ancestors(loc).map((x) => ({ level: x }))];
    state.selected = null;
    const m = await mount();
    if (!m) return;
    setViewBox(arrive(m));
    if (m.level.edges.some((e) => e.key === "edge:" + s.target)) select("edge:" + s.target);
  }
  // Returns the current step's files with their reviewed checkboxes, each opening its diff, and the action that marks
  // them all, so that what finishes the step is in front of the reader.
  function stepFiles(s) {
    const box = el("div", { class: "step-files" });
    const left = s.files.filter((p) => !REVIEWED.has(p));
    for (const p of s.files) {
      box.append(el("label", { class: "step-file" + (REVIEWED.has(p) ? " done" : "") },
        el("input", { type: "checkbox", checked: REVIEWED.has(p), onchange: (ev) => setReviewed([p], ev.target.checked) }),
        el("button", { class: "link", onclick: () => openFile(p), title: p }, p.split("/").pop())));
    }
    // The action keeps its place once the step is read, so that the list below it does not jump.
    box.append(left.length
      ? el("button", { class: "btn", onclick: () => setReviewed(left, true), title: "Mark every file of this step reviewed, which makes the step read" },
        left.length === s.files.length ? "Mark read" : `Mark the other ${plural(left.length, "file", "files")}`)
      : el("button", { class: "btn", disabled: true, title: "Every file of this step is reviewed" }, "Read"));
    return box;
  }
  // Renders the path into the panel, with its arrows and count in the panel's pinned head.
  function renderPath(box, head) {
    const steps = countedSteps(), read = steps.filter(stepRead).length;
    const next = nextStep(), previous = previousStep();
    head.append(el("div", { class: "path-head" },
      el("button", { class: "btn step-arrow", disabled: !previous, onclick: () => previous && openStep(previous), "aria-label": "Previous step",
        title: previous ? "Previous step, read or not" : "No step before this one" }, "←"),
      el("button", { class: "btn step-arrow", disabled: !next, onclick: () => next && openStep(next), "aria-label": "Next step",
        title: next ? "Next step whose files are not all reviewed" : "Every step is read" }, "→"),
      el("span", {}, read === steps.length ? "Every step is read" : `${read} of ${plural(steps.length, "step", "steps")} read`)));
    box.append(el("p", { class: "hint" }, "A step opens where it lives, and is read once its files are reviewed."));
    for (const tier of ["first", "then", "aside"]) {
      const list = PATH.filter((s) => s.tier === tier);
      if (!list.length) continue;
      box.append(el("h3", {}, TIER_TITLE[tier]));
      const ul = el("ul", { class: "list" });
      for (const s of list) {
        const done = tier !== "aside" && stepRead(s);
        const current = s.id === state.step;
        const reviewed = s.files.filter((p) => REVIEWED.has(p)).length;
        const n = stepNarrative(s);
        const text = el("span", { class: "step-text" }, s.signal);
        if (n) text.append(el("span", { class: "step-why" }, ...richText(n.text)));
        const meta = s.files.length ? el("span", { class: "step-meta" }, tier === "aside" ? `${reviewed} of ${s.files.length} reviewed` : done ? "read" : `${reviewed}/${s.files.length} files`) : null;
        const li = el("li", {}, el("button", { class: "row step" + (done ? " read" : "") + (current ? " current" : ""), onclick: () => openStep(s),
          title: s.files.length ? `${plural(s.files.length, "file", "files")}, ${reviewed} reviewed` : "" },
          el("span", { class: "step-kind " + s.kind }, s.kind === "node" ? "block" : s.kind === "aside" ? "aside" : s.kind), text, meta));
        if (current && s.members && s.kind === "tests") li.append(el("ul", { class: "step-members" }, ...s.members.map((m) => el("li", {}, m))));
        if (current && s.files.length && tier !== "aside") li.append(stepFiles(s));
        ul.append(li);
      }
      box.append(ul);
    }
    revealCurrentStep(box, head);
  }
  // Scrolls the panel so that the current step is in view with its files and its mark action, below the pinned head.
  // A block taller than the room under the head is anchored at its bottom, where the action is, rather than its top.
  function revealCurrentStep(box, head) {
    const cur = box.querySelector(".row.step.current");
    const aside = box.closest("aside");
    if (!cur || !aside) return;
    const block = cur.closest("li").getBoundingClientRect(), a = aside.getBoundingClientRect(), style = getComputedStyle(aside);
    const top = a.top + head.getBoundingClientRect().height;
    const bottom = a.bottom - parseFloat(style.paddingBottom) - parseFloat(style.borderBottomWidth);
    if (block.height > bottom - top || block.bottom > bottom) aside.scrollTop += block.bottom - bottom;
    else if (block.top < top) aside.scrollTop -= top - block.top;
  }

  // ---------- Tests ----------
  // The Tests switch lists the changed test types by the block their subjects bind to, in the order the path first
  // reaches each block, and test infrastructure last. See docs/tests.md section 7.3.
  const TESTS_ONLY = TEST_FILES.length > 0 && CHANGED_FILES.every((f) => f.test || (!f.leavesBase.length && !f.leavesHead.length));
  const SUBJECT_RULE = { name: "placed by its name", references: "placed by the type its references reach most" };
  const METHOD_STATUS = { added: "added", removed: "removed", changed: "modified" };
  // Returns where the path first reaches a leaf, by a step on the leaf itself, under it, or on a block that holds it.
  function pathOrder(leaf) {
    if (leaf === TESTS) return Infinity;
    const i = PATH.findIndex((s) => s.target === leaf || s.under === leaf || (s.kind === "node" && isUnder(leaf, s.target)));
    return i < 0 ? PATH.length : i;
  }
  function renderTests(box) {
    const groups = new Map();
    for (const t of TEST_TYPES) {
      const key = t.subject ? t.subject.leaf : TESTS;
      if (!groups.has(key)) groups.set(key, []);
      groups.get(key).push(t);
    }
    const keys = [...groups.keys()].sort((a, b) => pathOrder(a) - pathOrder(b) || nameOf(a).localeCompare(nameOf(b)));
    const reviewed = TEST_FILES.filter((f) => REVIEWED.has(f.path)).length;
    box.append(el("p", { class: "hint" }, `${plural(TEST_TYPES.length, "test type", "test types")} changed in ${plural(TEST_FILES.length, "test file", "test files")}, ${reviewed} reviewed.`));
    for (const key of keys) {
      box.append(el("h3", {}, el("button", { class: "link", onclick: () => openLeaf(key), title: key === TESTS ? purposeOf(TESTS) : `Open ${nameOf(key)} with its tests` }, nameOf(key))));
      const ul = el("ul", { class: "list tests" });
      for (const t of groups.get(key)) {
        const done = t.files.every((p) => REVIEWED.has(p)) && state.opts.mute;
        const li = el("li", {}, el("button", { class: "row st-" + (done ? "reviewed" : t.status), onclick: () => openTestType(t),
          title: t.name + (t.subject ? `\nTests ${t.subject.type}, ${SUBJECT_RULE[t.subject.by] || t.subject.by}` : "") + "\n" + t.files.join("\n") },
          el("span", { class: "dot" }), el("span", { class: "row-name" }, el("code", {}, shortName(t.name))),
          el("span", { class: "row-meta" }, t.methods.length ? plural(t.methods.length, "method", "methods") : t.status)));
        if (t.methods.length) li.append(el("ul", { class: "test-methods" }, ...t.methods.map((m) =>
          el("li", { class: "st-" + METHOD_STATUS[m.status] }, el("span", { class: "row-change" }, m.status + " "), el("code", {}, memberName(m.symbol, "method")),
            ...t.files.flatMap(signalsOf).filter((s) => s.method === m.symbol).map((s) => signalChip(s, false)),
            (m.declarations || []).length ? el("span", { class: "tested" }, "references ", ...m.declarations.flatMap((x, i) => [i ? ", " : "", testedDeclaration(x)])) : ""))));
        ul.append(li);
      }
      box.append(ul);
    }
  }
  // Returns a changed declaration that a test method references, with how it changed, which opens the decision that
  // holds it when there is one. A rewritten declaration is no decision, so it is only named.
  function testedDeclaration(x) {
    const d = DECISION_LIST.find((d) => d.declarations.some((y) => y.symbol === x.symbol));
    const name = el("code", { title: x.symbol }, memberName(x.symbol, "member"));
    const text = [name, ` ${CHANGE_TEXT[x.change] || x.change}`];
    return d ? el("button", { class: "link", onclick: () => openDecision(d), title: "Open the decision" }, ...text) : el("span", {}, ...text);
  }
  // Opens a leaf's view with its Tests section open, or the test infrastructure leaf.
  async function openLeaf(leaf) {
    if (leaf !== TESTS) state.testsOpen.add(leaf);
    rememberView();
    state.trail = [{ level: ROOT }, ...ancestors(parentOf(leaf)).map((x) => ({ level: x })), { leaf }];
    state.selected = null;
    await mount();
  }

  // ---------- The glossary ----------
  // The names the change introduces, as the changeset lists them: added types and members, renamed declarations, parts
  // added to the map, and assemblies referenced for the first time, each with where it is declared, its documentation
  // summary, how often it is used and where first. The panel lists them by block, a block's details panel lists its
  // own, and an introduced name in a diff shows its entry on hover. See docs/attention.md section 8.
  const GLOSSARY = CS.glossary || [];
  const TERMS = new Map(GLOSSARY.map((t) => [t.key, t]));
  // The group of the terms that no block holds: the parts added to the map and the assemblies.
  const MAP_TERMS = "~map";
  const DECLARATION_KINDS = new Set(["type", "method", "constructor", "property", "field", "event", "indexer", "operator"]);
  const isDeclarationTerm = (t) => DECLARATION_KINDS.has(t.kind);
  const termGroup = (t) => t.block || MAP_TERMS;
  const termGroupName = (g) => g === MAP_TERMS ? "Map and assemblies" : nameOf(g);
  // Returns the name a declaration shows in code: its own, without its type, namespace or type arguments.
  const codeName = (symbol) => memberName(symbol, "type").replace(/<.*$/, "");
  // Declaration terms by the name they show in code at head, and renamed ones by their name at base too, so that both
  // sides of a rename find the entry.
  const TERMS_AT_HEAD = new Map(), TERMS_AT_BASE = new Map();
  for (const t of GLOSSARY.filter(isDeclarationTerm)) {
    const add = (map, name) => { if (!map.has(name)) map.set(name, []); map.get(name).push(t); };
    add(TERMS_AT_HEAD, codeName(t.symbol));
    if (t.previous) add(TERMS_AT_BASE, codeName(t.previous));
  }
  // Returns a pattern that matches any name of a map as a whole identifier, or null when the map has none.
  function termPattern(map) {
    const names = [...map.keys()].filter((n) => /^[A-Za-z_]\w*$/.test(n)).sort((a, b) => b.length - a.length);
    return names.length ? new RegExp(`(?<!\\w)(${names.join("|")})(?!\\w)`) : null;
  }
  const TERM_PATTERN = { head: termPattern(TERMS_AT_HEAD), base: termPattern(TERMS_AT_BASE) };
  // Wraps each introduced name in a cell of a changed line in a span that shows the name's entry on hover and opens the
  // Glossary on a click. A removed line is matched against the names at base, an added line against those at head,
  // and a context line is left alone. Returns the cell.
  function markTerms(td, kind) {
    const side = kind === "del" ? "base" : kind === "add" ? "head" : null;
    const re = side && TERM_PATTERN[side];
    if (!re) return td;
    const map = side === "head" ? TERMS_AT_HEAD : TERMS_AT_BASE;
    const walker = document.createTreeWalker(td, NodeFilter.SHOW_TEXT), nodes = [];
    while (walker.nextNode()) if (re.test(walker.currentNode.data)) nodes.push(walker.currentNode);
    for (const node of nodes) {
      const frag = document.createDocumentFragment();
      node.data.split(re).forEach((part, i) => frag.append(i % 2 ? el("span", { class: "term", "data-term": map.get(part).map((t) => t.key).join(" ") }, part) : part));
      node.replaceWith(frag);
    }
    return td;
  }

  const fileName = (path) => path.split("/").pop();
  const TERM_KIND_TEXT = { node: "map node", view: "map view", external: "external", assembly: "assembly" };
  const termKind = (t) => TERM_KIND_TEXT[t.kind] || (t.accessibility ? `${t.accessibility} ${t.kind}` : t.kind);
  // Returns what a term is shown as: its name, after its previous name when it was renamed.
  function termName(t) {
    const name = (s) => isDeclarationTerm(t) ? memberName(s, t.kind) : t.name;
    return t.previous ? el("span", { class: "term-name" }, el("code", {}, name(t.previous)), " is now ", el("code", {}, name(t.symbol))) : el("code", { class: "term-name" }, name(t.symbol));
  }
  // Returns the line of a term's entry that says what it is: its documentation summary or a map part's purpose,
  // Claude's definition when a declaration has none, or that it has no documentation, which is itself worth a
  // reviewer's attention in code a model wrote. An assembly has none of these.
  function termText(t, inTip) {
    if (t.summary) return el("p", { class: "term-text" }, t.summary);
    if (t.kind === "assembly") return null;
    if (!isDeclarationTerm(t)) return el("p", { class: "term-text undocumented" }, "No purpose given.");
    const n = NARR.state === "off" ? null : NARR.items.get(t.key);
    const busy = NARR.pending.has(t.key);
    if (n) {
      const p = el("p", { class: "term-text" }, ...richText(n.text), " ", el("span", { class: "by" }, "written by Claude"));
      if (!inTip) p.append(" ", el("button", { class: "link", disabled: busy, onclick: () => regenerate(t.key), title: "Write the definition again" }, busy ? "Writing…" : "Regenerate"));
      return p;
    }
    const p = el("p", { class: "term-text undocumented" }, "No documentation.");
    if (busy || NARR.state === "writing" || NARR.state === "waiting") p.append(" ", el("span", { class: "by" }, "Claude is writing a definition…"));
    else if (NARR.errors.has(t.key)) p.append(" ", el("span", { class: "by" }, NARR.errors.get(t.key)));
    return p;
  }
  // Returns the line of a term's entry that places it: where it is declared, how often it is used and where first
  // outside its own type, and the decision that introduced it. Each place opens in the leaf view, except in the
  // tooltip, which is read and not clicked.
  function termMeta(t, inTip) {
    const meta = el("div", { class: "term-meta" });
    const place = (text, title, open) => inTip ? el("span", { title }, text) : el("button", { class: "link", title, onclick: open }, text);
    const refs = t.kind === "assembly" || t.kind === "external";
    if (t.file) meta.append(place(`declared in ${fileName(t.file)}:${t.line}`, t.file, () => openLine(t.file, t.line)));
    if (t.kind === "node") meta.append(place("show on the map", t.symbol, () => showInContext(t.symbol)));
    if (isDeclarationTerm(t) || refs) meta.append(el("span", {}, t.uses ? plural(t.uses, refs ? "reference" : "use", refs ? "references" : "uses") : refs ? "no references" : "no uses"));
    if (t.firstUse) meta.append(place(`first ${refs ? "referenced" : "used"} in ${fileName(t.firstUse.file)}:${t.firstUse.line}`, `${t.firstUse.file}: the first use outside the declaring type`, () => openLine(t.firstUse.file, t.firstUse.line)));
    if (t.decision && DECISIONS.has(t.decision)) meta.append(place("decision", "Open the decision that introduced it", () => openDecision(DECISIONS.get(t.decision))));
    return meta;
  }
  // Returns a term's entry: its kind and name, what it is, and where it is. In the tooltip the entry also names the
  // term's block, since the tooltip stands alone, and has no links.
  function termEntry(t, inTip) {
    const entry = el("div", { class: "term-entry" },
      el("div", { class: "term-head" }, el("span", { class: "term-kind" }, termKind(t)), termName(t), inTip && t.block && t.kind !== "node" ? el("span", { class: "term-block" }, nameOf(t.block)) : null));
    entry.append(termText(t, inTip) || "", termMeta(t, inTip));
    return entry;
  }

  // Returns the index of the hunk of a file that holds a line at head, or -1 when no hunk does.
  function hunkAt(path, line) {
    return (HUNKS.get(path) || []).findIndex((h) => { const start = parseHeader(h.header).b, count = h.lines.filter((l) => l[0] !== "-").length; return line >= start && line < start + count; });
  }
  // Opens a file in the leaf view at the hunk that holds a line at head, or at its top when no hunk holds the line.
  function openLine(path, line) {
    const i = hunkAt(path, line);
    return i >= 0 ? openHunk(path, i) : openFile(path);
  }
  // Selects a decision and opens its hunk, with its reach in the details panel.
  function openDecision(d) {
    state.decision = d.id;
    return openHunk(d.file, d.hunk);
  }
  // Opens a term where it lives: a declaration at its line in the leaf view, a map node on the map, and anything else
  // in the Glossary.
  function openTerm(t) {
    if (t.file) return openLine(t.file, t.line);
    if (t.kind === "node") return showInContext(t.symbol);
    return showGlossary(null, t.key);
  }
  // Returns the terms under a block, a group of a view, or the whole map.
  function termsUnder(id) {
    if (id === ROOT || id === null || id === undefined) return GLOSSARY;
    const leaves = id.startsWith("g:") ? groupLeaves(id) : [id];
    return GLOSSARY.filter((t) => t.block && leaves.some((l) => isUnder(t.block, l)));
  }
  // Opens the Glossary panel on the terms under a block, or on every term when the block is null, with a term marked.
  function showGlossary(block, term) {
    state.panel = "glossary";
    state.glossaryBlock = block;
    state.term = term || null;
    renderChanges();
  }
  // Returns the details panel's section for the names a block introduces, or null when it introduces none: up to six,
  // each opening where it lives, and the Glossary for all of them.
  function glossarySection(id) {
    const terms = termsUnder(id);
    if (!terms.length) return null;
    const box = el("section", { class: "glossary-section" }, el("h3", {}, `Glossary: ${plural(terms.length, "new name", "new names")}`, help(HELP.terms)));
    const ul = el("ul", { class: "list" });
    for (const t of terms.slice(0, 6)) {
      ul.append(el("li", {}, el("button", { class: "row", onclick: () => openTerm(t), title: t.summary || (isDeclarationTerm(t) ? "No documentation." : "") },
        el("span", { class: "term-kind" }, t.kind), el("span", { class: "row-name" }, termName(t)), el("span", { class: "row-meta" }, t.uses ? String(t.uses) : ""))));
    }
    box.append(ul, el("button", { class: "link", onclick: () => showGlossary(id) }, terms.length > 6 ? `All ${terms.length} in the Glossary` : "Show in the Glossary"));
    return box;
  }
  // Renders the glossary into the panel: the terms by block, the most used first, with the count in the pinned head.
  function renderGlossary(box, head) {
    const block = state.glossaryBlock;
    const terms = block ? termsUnder(block) : GLOSSARY;
    head.append(el("div", { class: "path-head" }, el("span", {}, block ? `${plural(terms.length, "term", "terms")} in ${nameOf(block)}` : plural(GLOSSARY.length, "new name", "new names")),
      block ? el("button", { class: "link", onclick: () => showGlossary(null) }, "Show all") : null));
    if (!terms.length) { box.append(el("p", { class: "hint" }, block ? "The change introduces no name here." : "The change introduces no new name.")); return; }
    box.append(el("p", { class: "hint" }, "The names the change introduces, by block, the most used first. In a diff, an introduced name has a dotted underline and shows its entry on hover."));
    let group, ul;
    for (const t of terms) {
      const g = termGroup(t);
      if (g !== group) { group = g; box.append(el("h3", {}, termGroupName(g))); ul = el("ul", { class: "list glossary" }); box.append(ul); }
      ul.append(el("li", { class: state.term === t.key ? "current" : "" }, termEntry(t, false)));
    }
    const cur = box.querySelector("li.current");
    if (cur) cur.scrollIntoView({ block: "nearest" });
  }
  // Shows a term's entry in a tooltip beside an introduced name under the pointer in the leaf view, and opens the
  // Glossary on the name when it is clicked.
  let termTip = null;
  function installTermTips() {
    const view = $("#leafview"), tip = el("div", { class: "tip term-tip", id: "termtip", role: "tooltip", hidden: true });
    document.body.append(tip);
    const hide = () => { termTip = null; tip.hidden = true; };
    const termAt = (ev) => ev.target instanceof Element ? ev.target.closest(".term") : null;
    view.addEventListener("mouseover", (ev) => {
      const span = termAt(ev);
      if (!span) { if (termTip) hide(); return; }
      if (span === termTip) return;
      termTip = span;
      showTermTip();
    });
    view.addEventListener("mouseleave", hide);
    view.addEventListener("scroll", hide, { passive: true });
    view.addEventListener("click", (ev) => {
      const span = termAt(ev);
      const t = span && TERMS.get(span.dataset.term.split(" ")[0]);
      if (!t) return;
      hide();
      showGlossary(t.block || null, t.key);
    });
  }
  // Fills the term tooltip with the entries of the names under the pointer, up to three, and places it below them, or
  // above when there is no room below.
  function showTermTip() {
    const tip = $("#termtip"), span = termTip;
    if (!tip || !span || !span.isConnected) { termTip = null; if (tip) tip.hidden = true; return; }
    const terms = span.dataset.term.split(" ").map((k) => TERMS.get(k)).filter(Boolean);
    tip.replaceChildren(...terms.slice(0, 3).map((t) => termEntry(t, true)));
    showBeside(tip, span);
  }
  // Shows a fixed tooltip below an element, or above it when there is no room below, kept inside the window.
  function showBeside(tip, target) {
    tip.hidden = false;
    const r = target.getBoundingClientRect(), w = tip.offsetWidth, h = tip.offsetHeight;
    const top = r.bottom + 6 + h > window.innerHeight ? r.top - 6 - h : r.bottom + 6;
    tip.style.left = Math.max(4, Math.min(r.left, window.innerWidth - w - 4)) + "px";
    tip.style.top = Math.max(4, top) + "px";
  }
  // Shows the explanation of a question mark made by help in a tooltip while the pointer is over the mark or the mark
  // has the keyboard focus. Scrolling, a click elsewhere and Escape hide it.
  function installHelpTips() {
    const tip = el("div", { class: "tip help-tip", role: "tooltip", hidden: true });
    document.body.append(tip);
    let shown = null;
    const show = (mark) => {
      if (mark === shown) return;
      shown = mark;
      tip.textContent = mark.dataset.help;
      showBeside(tip, mark);
    };
    const hide = () => { shown = null; tip.hidden = true; };
    const markAt = (ev) => ev.target instanceof Element ? ev.target.closest(".help[data-help]") : null;
    document.addEventListener("mouseover", (ev) => { const m = markAt(ev); if (m) show(m); else if (shown && shown !== document.activeElement) hide(); });
    document.addEventListener("focusin", (ev) => { const m = markAt(ev); if (m) show(m); else if (shown) hide(); });
    document.addEventListener("focusout", (ev) => { if (markAt(ev) === shown) hide(); });
    document.addEventListener("keydown", (ev) => { if (ev.key === "Escape" && shown) { hide(); ev.stopImmediatePropagation(); } });
    document.addEventListener("scroll", () => { if (shown) hide(); }, { capture: true, passive: true });
  }

  const PANEL_TITLE = { path: "Path", changes: "Changes", glossary: "Glossary", tests: "Tests" };
  // The explanations that the question marks beside the page's major sections show on hover, by section.
  const HELP = {
    path: "The reading path lists the parts of the change in the order to read them. Read first holds the surprises, such as new edges, new cycles and the decisions that reach furthest. " +
      "Then holds the other changed blocks. Set aside counts the consequences, trivial files and files already reviewed. A step is read once all its files are reviewed.",
    changes: "This panel lists every changed block and its changed files. Below them are the decisions, the edges whose reference counts changed, and the changes to the map itself.",
    glossary: "The Glossary lists the names this change introduces, such as new types, new members and renamed declarations, grouped by block. Click a name to go to its declaration. " +
      "A name without a doc comment gets a definition from Claude.",
    decisions: "A decision is a hunk that adds, removes, renames or changes the signature of a declaration. Its consequences are other hunks whose changes all follow from it, such as updated call sites. " +
      "Decisions that reach the most blocks come first. Hover a row to see its reach on the map, and click it to go there.",
    edges: "An edge is the set of references from one block to another. This list shows the edges whose reference counts changed, with new and removed edges first. Click an edge to show it on the map.",
    map: "This list shows the blocks and views that were added, removed or changed in the map, and the types that moved to a different block.",
    decision: "This section shows the selected decision's declarations and the hunks that follow from it, grouped by block. Click a hunk to open it.",
    pattern: "A pattern is a role that several types play, found from what they declare about themselves: a base class, an interface or an attribute they share. " +
      "The patterns come from the code before this change, and a type joins one when it carries every key. A norm is what four in five of five or more instances share. " +
      "A departure is a norm that a new instance lacks, or that a changed instance had and no longer has. A departure is a difference to read, not a fault. Click an instance to open its type.",
    narrative: "This is Claude's explanation of why this part changed. Any evidence it relies on is listed below it.",
    terms: "These are the names this block introduces. The Glossary lists the names from the whole change.",
    tests: "Tests lists the changed test types by the block that holds the type each one tests, with the test methods the change added, removed or changed. " +
      "Tests are never drawn on the map. While this list is open, a block's badge counts the changed test files of the types it holds. Test infrastructure, such as fixtures and builders, comes last.",
    testsSection: "These are the changed test files whose test types test a type in this block. They count apart from the block's own files in the progress.",
    types: "These are the files that declare this block's changed types, each once, with the types it declares and the hunks of the file. " +
      "Files with decisions or their own edits come first, then files that only follow from decisions, then reviewed files.",
    uses: "Uses lists the blocks this block references, and Used by lists the blocks that reference it. Each count is the number of references at base and at head. " +
      "Click a row to see the type pairs behind it.",
    threads: "These review threads on files in this block are not resolved yet. Click one to go to its lines.",
    problems: "The pull request's checks reported these problems on lines in this block's files. Click one to go to its line.",
    checks: "These are the pull request's checks on its latest commit. A failing check lists the problems it found and can show its log.",
  };
  // Returns a question mark that shows an explanation of a section of the page on hover or focus, in the tooltip that
  // installHelpTips draws.
  function help(text) { return el("span", { class: "help", tabindex: 0, "data-help": text, "aria-label": text }, "?"); }
  function renderChanges() {
    const box = $("#changes");
    box.replaceChildren();
    // The panel's head holds the title and, on the path, the arrows; everything after the title is drawn afresh.
    const head = $("#changes-head"), title = $("#changes-title");
    for (const n of [...head.children]) if (n !== title) n.remove();
    // With a path, a glossary or changed tests, the panel switches among the path, the changes, the glossary and the
    // tests. It opens on the tests when every changed file is a test file or a file that no type binds, and otherwise
    // on the path when the changeset has one. Switching to or from the tests draws the map again, for its test badges.
    if (PATH.length || GLOSSARY.length || TEST_FILES.length) {
      if (state.panel === undefined) state.panel = TESTS_ONLY ? "tests" : PATH.length ? "path" : "changes";
      const tab = (name, label) => el("button", { class: state.panel === name ? "on" : "", onclick: () => {
        const badges = (state.panel === "tests") !== (name === "tests");
        state.panel = name;
        if (badges) redraw(); else renderChanges();
      } }, label);
      title.replaceChildren(el("span", { class: "panel-switch" }, el("span", {}, PANEL_TITLE[state.panel], help(HELP[state.panel])), el("span", { class: "seg", role: "tablist" },
        PATH.length ? tab("path", "Path") : null, tab("changes", "Changes"), GLOSSARY.length ? tab("glossary", "Glossary") : null, TEST_FILES.length ? tab("tests", "Tests") : null)));
      if (state.panel === "path") { renderPath(box, head); return; }
      if (state.panel === "glossary") { renderGlossary(box, head); return; }
      if (state.panel === "tests") { renderTests(box); return; }
    } else title.replaceChildren(PANEL_TITLE.changes, help(HELP.changes));
    const talk = openThreadsUnder(ROOT).length;
    if (talk || state.opts.talk) box.append(el("label", { class: "toggle talk-filter", title: "Keep only the blocks whose changed files have unresolved review threads" },
      el("input", { type: "checkbox", checked: state.opts.talk, onchange: (ev) => { state.opts.talk = ev.target.checked; renderChanges(); } }), ` Only unresolved threads (${talk})`));
    const tree = changeTree(ROOT);
    box.append(tree ? el("ul", { class: "tree" }, tree) : el("p", { class: "hint" }, "No file changed under any leaf."));

    // Decisions, widest reach first, under the account of how many hunks they explain. Hovering a row shows the blocks
    // it is drawn from and reaches, and a click selects it and shows it on the map.
    if (DECISION_LIST.length) {
      const t = hunkTally();
      box.append(el("h3", {}, `Decisions: ${DECISION_LIST.length}`, help(HELP.decisions)));
      box.append(el("p", { class: "hint tally" }, `Of ${plural(t.total, "changed hunk", "changed hunks")}: ${t.decisions} ${t.decisions === 1 ? "is a decision" : "are decisions"}, ` +
        `${t.consequences} ${t.consequences === 1 ? "follows" : "follow"} from them, ${t.trivial} ${t.trivial === 1 ? "is" : "are"} trivial, ${t.other} ${t.other === 1 ? "is an edit" : "are edits"} on ${t.other === 1 ? "its" : "their"} own.`));
      const dl = el("ul", { class: "list scroll" });
      for (const d of DECISION_LIST) {
        const lead = leadOf(d), others = d.declarations.length - 1, r = reachOf(d);
        dl.append(el("li", {}, el("button", { class: "row decision-row" + (REVIEWED.has(d.file) && state.opts.mute ? " st-reviewed" : "") + (state.decision === d.id ? " current" : ""),
          "data-decision": d.id, onclick: () => showDecision(d), onmouseenter: () => hoverDecision(d), onmouseleave: () => hoverDecision(null),
          title: `${d.file} · hunk ${d.hunk + 1}\n${CHANGE_TEXT[lead.change]} ${lead.symbol}` + (others ? ` and ${plural(others, "other", "others")}` : "") +
            (d.public ? "\nPublic: callers outside the solution may exist that the changeset cannot show." : "") },
          // The diamond is hollow for a public decision, since the row has no room for the word.
          el("span", { class: "dot decision-dot" + (d.public ? " public" : "") }),
          el("span", { class: "row-name" }, el("span", { class: "row-change" }, CHANGE_TEXT[lead.change] + " "), el("code", {}, declText(lead)), others ? ` +${others}` : ""),
          el("span", { class: "row-meta" }, d.consequences.length ? `${d.consequences.length} in ${plural(r.blocks.size, "block", "blocks")}` : "none"))));
      }
      box.append(dl);
      const shown = state.revealDecision && dl.querySelector(`[data-decision="${CSS.escape(state.revealDecision)}"]`);
      if (shown) {
        state.revealDecision = null;
        shown.scrollIntoView({ block: "nearest" });
        shown.classList.add("flash");
      }
    }

    box.append(el("h3", {}, `Edges: ${changedText(CHANGED_EDGES)}`, help(HELP.edges)));
    const ul = el("ul", { class: "list scroll" });
    const rank = (e) => (e.status === "added" || e.status === "removed" ? 1e9 : 0) + (isMinor(e) ? 0 : 1e6) + Math.abs(e.head - e.base);
    for (const e of CHANGED_EDGES.slice().sort((a, b) => rank(b) - rank(a))) {
      ul.append(el("li", {}, el("button", { class: "row st-" + e.status, onclick: () => showEdge(e), title: `${e.from} → ${e.to}` },
        el("span", { class: "dot" + (isMinor(e) ? " minor" : "") }), el("span", { class: "row-edge" }, `${nameOf(e.from)} → ${nameOf(e.to)}`),
        el("span", { class: "row-meta" }, e.status === "added" ? `+${e.head} new` : e.status === "removed" ? `−${e.base} gone` : `${e.base}→${e.head}`))));
    }
    box.append(ul);

    box.append(el("h3", {}, "Map", help(HELP.map)));
    const s = CS.structural;
    if (!s.nodes.length && !s.views.length && !s.types.length) box.append(el("p", { class: "hint" }, "The map did not change, and no type moved between leaves."));
    const sl = el("ul", { class: "list" });
    for (const n of s.nodes) sl.append(el("li", {}, el("button", { class: "row st-" + n.op, onclick: () => showInContext(n.id) }, el("span", { class: "dot" }), el("span", { class: "row-name" }, `${n.op} ${n.kind} ${n.id}`), el("span", { class: "row-meta" }, n.detail || ""))));
    for (const v of s.views) sl.append(el("li", {}, el("button", { class: "row st-" + v.op }, el("span", { class: "dot" }), el("span", { class: "row-name" }, `${v.op} view ${v.id}`), el("span", { class: "row-meta" }, v.detail || ""))));
    for (const t of s.types) sl.append(el("li", {}, el("button", { class: "row st-modified", onclick: () => showInContext(t.leafHead || t.leafBase), title: t.name }, el("span", { class: "dot" }), el("span", { class: "row-name" }, shortName(t.name)), el("span", { class: "row-meta" }, `${t.leafBase || "∅"} → ${t.leafHead || "∅"}`))));
    box.append(sl);
  }

  function kv(k, v) { return el("div", { class: "kv" }, el("span", { class: "k" }, k), el("span", { class: "v" }, v)); }
  function deltaText(b, h, fmt = (x) => String(x)) {
    const span = el("span", {}, fmt(h));
    if (b !== null && b !== undefined && b !== h) span.append(" ", el("span", { class: "delta " + (h > b ? "up" : "down") }, `(${fmt(b)} → ${fmt(h)})`));
    return span;
  }

  function renderDetails() {
    const box = $("#details");
    box.replaceChildren();
    if (state.decision && DECISIONS.has(state.decision)) box.append(decisionPanel(DECISIONS.get(state.decision)));
    if (state.pattern && PATTERNS.has(state.pattern.id)) box.append(patternPanel(PATTERNS.get(state.pattern.id), state.pattern.type));
    const l = loc();
    const cur = state.current;
    if (l.leaf) {
      box.append(el("h2", {}, nameOf(l.leaf)), el("p", { class: "ident" }, l.leaf), el("p", { class: "purpose" }, purposeOf(l.leaf)));
      if (contentOf(l.leaf).status !== "unchanged") box.append(narrativeBlock(NARR.items.get("node:" + l.leaf), "node:" + l.leaf) || "");
      box.append(glossarySection(l.leaf) || "");
      box.append(threadList(l.leaf) || "", problemList(l.leaf) || "");
      box.append(el("p", { class: "hint" }, "Changed types show their diffs. Click an edge row to see the type pairs behind it. Esc goes back to the schematic."));
      return;
    }
    if (!cur) return;
    const level = cur.level;
    const sel = state.selected;
    // Returns the name the panel gives a selection: a block, a pin, an edge, or nothing, which is the level itself.
    const labelOf = (key) => {
      if (key === null) return locName(l);
      const child = level.children.find((c) => c.key === key);
      if (child) return nameOf(child.id);
      const pin = level.pins.find((p) => p.key === key);
      if (pin) return pin.name;
      const edge = level.edges.find((e) => e.key === key);
      return edge ? `${labelOf(edge.from)} → ${labelOf(edge.to)}` : key;
    };
    if (state.back.length) {
      const prev = state.back[state.back.length - 1];
      box.append(el("button", { class: "back-link", onclick: () => select(state.back.pop(), "back") }, "← " + labelOf(prev)));
    }
    if (!sel) {
      const id = l.group ? `g:${l.view}/${l.group}` : l.level;
      box.append(el("h2", {}, locName(l)), el("p", { class: "ident" }, locKey(l)), el("p", { class: "purpose" }, l.view && !l.group ? VIEWS[l.view].purpose || "" : purposeOf(id)));
      if (!l.view && id !== ROOT && contentOf(id).status !== "unchanged") box.append(narrativeBlock(NARR.items.get("node:" + id), "node:" + id) || "");
      if (id !== ROOT) box.append(glossarySection(id) || "");
      const bar = reviewedBar(id);
      if (bar) box.append(bar);
      box.append(threadList(id) || "", problemList(id) || "");
      const m = metricsOf(id);
      if (m.head) box.append(kv("Types", deltaText(m.base ? m.base.types : null, m.head.types)));
      else if (id === ROOT) box.append(kv("Types", deltaText(CS.types.filter((t) => t.leafBase).length, CS.types.filter((t) => t.leafHead).length)));
      box.append(kv("Edges drawn", `${level.edges.length} · ${changedText(level.edges)}`));
      box.append(kv("Cycles", String(level.cycles.length)));
      level.cycles.forEach((cyc, i) => {
        box.append(el("h3", {}, `Cycle ${i + 1}`));
        const ul = el("ul", { class: "list" });
        for (const k of cyc) ul.append(el("li", {}, el("button", { class: "row", onclick: () => select(k, "follow") }, el("span", { class: "row-name" }, nameOf(k)))));
        box.append(ul);
      });
      box.append(el("p", { class: "hint" }, "Click a block to select it. Double-click or press Enter to open it. Ctrl+wheel zooms, drag pans, F fits, Esc goes up a level."));
      return;
    }
    const child = level.children.find((c) => c.key === sel);
    if (child) {
      box.append(el("h2", {}, nameOf(child.id)), el("p", { class: "ident" }, child.id), el("p", { class: "purpose" }, purposeOf(child.id)));
      const st = statusOf(child.key);
      box.append(kv("Status", st === "children" ? "contains changes" : st));
      if (child.kind !== "group" && contentOf(child.id).status !== "unchanged") box.append(narrativeBlock(NARR.items.get("node:" + child.id), "node:" + child.id) || "");
      box.append(glossarySection(child.kind === "group" ? child.key : child.id) || "");
      const m = metricsOf(child.key), content = child.kind === "group" ? groupContent(child) : contentOf(child.key);
      if (m.head) {
        box.append(kv("Types", deltaText(m.base ? m.base.types : null, m.head.types)));
        box.append(kv("Internal refs", deltaText(m.base ? m.base.internal : null, m.head.internal)));
        box.append(kv("Crossing refs", deltaText(m.base ? m.base.crossing : null, m.head.crossing)));
      }
      box.append(kv("Files", `${content.files}` + (content.added + content.removed + content.modified ? ` · +${content.added} −${content.removed} ~${content.modified}` : "")));
      const bar = reviewedBar(child.key);
      if (bar) box.append(bar);
      box.append(threadList(child.key) || "", problemList(child.key) || "");
      if (level.inCycle.has(child.key)) {
        const cyc = level.cycles.find((c) => c.includes(child.key));
        box.append(el("p", { class: "warn" }, "In a cycle with " + cyc.filter((k) => k !== child.key).map(nameOf).join(", ") + "."));
      }
      const touching = level.edges.filter((e) => e.from === child.key || e.to === child.key);
      const changed = touching.filter((e) => e.status !== "unchanged").sort((a, b) => isMinor(a) - isMinor(b) || Math.abs(b.head - b.base) - Math.abs(a.head - a.base));
      if (changed.length) {
        box.append(el("h3", {}, `Edges: ${changedText(touching)}`));
        const ul = el("ul", { class: "list" });
        for (const e of changed) ul.append(el("li", {}, el("button", { class: "row st-" + e.status, onclick: () => select(e.key, "follow") }, el("span", { class: "dot" + (isMinor(e) ? " minor" : "") }),
          el("span", { class: "row-edge" }, e.from === child.key ? `→ ${nameOf(stripPin(e.to))}` : `← ${nameOf(stripPin(e.from))}`),
          el("span", { class: "row-meta" }, `${e.base}→${e.head}`))));
        box.append(ul);
      }
      box.append(el("button", { class: "btn", onclick: () => enter(child) }, isLeaf(child.id) || child.kind === "unsorted" ? "Open types and diffs" : "Open " + nameOf(child.id)));
      return;
    }
    const pin = level.pins.find((p) => p.key === sel);
    if (pin) {
      box.append(el("h2", {}, pin.name), el("p", { class: "ident" }, pin.id), el("p", { class: "purpose" }, purposeOf(pin.id)));
      box.append(kv("Pin", pin.dir === "in" ? "incoming: it uses blocks here" : "outgoing: blocks here use it"));
      const through = level.edges.filter((e) => e.from === pin.key || e.to === pin.key);
      box.append(el("h3", {}, `${through.length} edge${through.length === 1 ? "" : "s"} through this pin`));
      const ul = el("ul", { class: "list" });
      for (const e of through.sort((a, b) => (a.status === "unchanged") - (b.status === "unchanged") || b.head - a.head)) ul.append(el("li", {}, el("button", { class: "row" + statusClass(e.status), onclick: () => select(e.key, "follow") }, el("span", { class: "dot", style: e.status === "unchanged" ? "visibility:hidden" : "" }),
        el("span", { class: "row-edge" }, nameOf(e.from === pin.key ? e.to : e.from)), el("span", { class: "row-meta" }, e.base === e.head ? String(e.head) : `${e.base}→${e.head}`))));
      box.append(ul);
      if (pin.kind !== "external") box.append(el("button", { class: "btn", onclick: () => showInContext(pin.id) }, "Show " + pin.name + " in context"));
      return;
    }
    const edge = level.edges.find((e) => e.key === sel);
    if (edge) {
      const from = stripPin(edge.from), to = stripPin(edge.to);
      box.append(el("h2", {}, `${nameOf(from)} → ${nameOf(to)}`), el("p", { class: "ident" }, `${from} → ${to}`));
      box.append(kv("Status", edge.status));
      box.append(kv("References", deltaText(edge.base, edge.head)));
      box.append(kv("Leaf pairs", String(edge.members.length)));
      if (edge.status !== "unchanged") { const n = edgeNarrative(edge); box.append(narrativeBlock(n, n ? n.key : null) || ""); }
      if (edge.members.length > 1 || edge.members[0].from !== from || edge.members[0].to !== to) {
        box.append(el("h3", {}, "Made of"));
        const ul = el("ul", { class: "evidence" });
        for (const m of edge.members.slice().sort((a, b) => (a.status === "unchanged") - (b.status === "unchanged") || b.head - a.head).slice(0, 25)) {
          ul.append(el("li", { class: statusClass(m.status).trim() }, el("span", {}, `${m.from} → ${m.to}`), el("span", { class: "n" }, m.base === m.head ? String(m.head) : `${m.base} → ${m.head}`)));
        }
        if (edge.members.length > 25) ul.append(el("li", { class: "more" }, `${edge.members.length - 25} more pairs`));
        box.append(ul);
      }
      const evidence = edge.members.flatMap((m) => m.evidence || []).sort((a, b) => Math.abs(b.head - b.base) - Math.abs(a.head - a.base));
      if (evidence.length) {
        box.append(el("h3", {}, "Type pairs that changed"));
        box.append(evidenceList(evidence, 15));
      } else if (edge.status === "unchanged") {
        box.append(el("p", { class: "hint" }, "Unchanged. The changeset carries type pairs only for edges that changed."));
      }
    }
  }

  // ---------- Navigation ----------

  const stage = () => $("#stage");
  function setViewBox(r) {
    state.view = r;
    stage().setAttribute("viewBox", `${r.x} ${r.y} ${r.w} ${r.h}`);
    const paper = $("#paper");
    paper.setAttribute("x", r.x - r.w); paper.setAttribute("y", r.y - r.h); paper.setAttribute("width", r.w * 3); paper.setAttribute("height", r.h * 3);
  }
  const easeOut = (t) => 1 - Math.pow(1 - t, 3);
  const easeInOut = (t) => t < 0.5 ? 4 * t * t * t : 1 - Math.pow(-2 * t + 2, 3) / 2;

  // Animates the view between two rectangles, calling onFrame with the eased progress of each frame.
  // The size changes geometrically, so a deep zoom runs at an even pace instead of rushing through the first steps,
  // and the center moves in step with the size, so the area being zoomed into stays put on screen.
  function animateViewBox(from, to, ms, onFrame, ease = easeOut) {
    return new Promise((resolve) => {
      if (reduceMotion) { setViewBox(to); if (onFrame) onFrame(1); resolve(); return; }
      const t0 = performance.now();
      const lerp = (a, b, k) => a + (b - a) * k;
      function step(now) {
        const t = Math.min(1, (now - t0) / ms), k = ease(t);
        const w = from.w * Math.pow(to.w / from.w, k), h = from.h * Math.pow(to.h / from.h, k);
        const z = Math.abs(to.w - from.w) > 1e-6 ? (w - from.w) / (to.w - from.w) : k;
        const cx = lerp(from.x + from.w / 2, to.x + to.w / 2, z), cy = lerp(from.y + from.h / 2, to.y + to.h / 2, z);
        setViewBox({ x: cx - w / 2, y: cy - h / 2, w, h });
        if (onFrame) onFrame(k);
        if (t < 1) requestAnimationFrame(step); else resolve();
      }
      requestAnimationFrame(step);
    });
  }
  // Returns the view that fits a whole level into the stage.
  function fitView(layout) {
    const f = frameOf(layout);
    return fitRect({ x: 0, y: 0, w: f.total.w, h: f.total.h }, 10, stageAspect());
  }
  // Returns the view that fits a whole level into the stage, and records the stage's current aspect.
  function fitAll(layout) {
    state.aspect = stageAspect();
    return fitView(layout);
  }

  let mountToken = 0;
  async function mount() {
    state.back = [];
    const token = ++mountToken;
    const l = loc();
    const s = stage(), leafView = $("#leafview"), wrap = $(".stage-wrap");
    if (l.leaf) {
      wrap.hidden = true; leafView.hidden = false; $("#legend-panel").hidden = true;
      leafView.replaceChildren(renderLeaf(l.leaf));
      state.current = null;
      refreshChrome();
      return null;
    }
    wrap.hidden = false; leafView.hidden = true; $("#legend-panel").hidden = false;
    const { level, layout } = await prepare(l);
    if (token !== mountToken) return null;
    for (const old of s.querySelectorAll(".level")) old.remove();
    $("#dots").removeAttribute("patternTransform");
    state.current = { level, layout };
    s.append(renderLevel(level, layout));
    s.classList.remove("hovering");
    s.classList.toggle("focused", markFocus(state.selected, "in-focus"));
    document.body.classList.toggle("dim", state.opts.dim);
    $("#statusline").textContent = `${locKey(l)} · ${level.children.length} blocks · ${level.edges.length} edges`;
    refreshChrome();
    return { level, layout };
  }

  // Builds and lays out a level, together with the layouts that its container and group children preview.
  async function prepare(l) {
    const level = buildLevel(l, state.opts);
    const layout = await layoutFor(level, state.opts);
    await Promise.all(level.children.filter((c) => !childLoc(c).leaf).map((c) => layoutFor(buildLevel(childLoc(c), state.opts), state.opts)));
    return { level, layout };
  }

  // Zooms the view out, or in, to show the whole of the current level. A leaf has no diagram, so it does nothing there.
  function zoomToFit() {
    if (!state.current) return;
    animateViewBox(state.view, fitAll(state.current.layout), 280);
  }

  // Records the view of the level being left, so that returning to it restores where the viewer was. The key includes
  // the options that shape the layout, so a level drawn another way counts as a different place.
  function rememberView() {
    if (state.current) state.saved.set(layoutKey(state.current.level.loc, state.opts), { ...state.view });
  }

  // Returns the view a level arrives at: where the viewer last left it, adjusted to the stage's current aspect,
  // or the whole level fitted to the stage on a first visit.
  function arrivalView(level, layout) {
    const v = state.saved.get(layoutKey(level.loc, state.opts));
    if (!v) return fitView(layout);
    const h = v.w / stageAspect();
    return { x: v.x, y: v.y + v.h / 2 - h / 2, w: v.w, h };
  }
  // Returns the arrival view of a mounted level, and records the stage's current aspect.
  function arrive(m) {
    state.aspect = stageAspect();
    return arrivalView(m.level, m.layout);
  }


  function refreshChrome() {
    $("#fit").disabled = !state.current;
    renderBreadcrumb();
    renderDetails();
    renderChanges();
    try { history.replaceState(null, "", "#" + locKey(loc())); } catch (e) { /* ignore */ }
  }

  // Returns the location that a drawn child opens: a level, a group of a view, or a leaf.
  function childLoc(c) {
    if (c.kind === "group") { const [v, g] = c.id.slice(2).split("/"); return { level: VIEWS[v].level, view: v, group: g }; }
    if (isLeaf(c.id) || c.kind === "unsorted") return { leaf: c.id };
    return { level: c.id };
  }

  // Returns the similarity that maps an inner level's stage coordinates onto the preview drawn inside its block
  // at the outer level, or null when the block draws no preview of that layout.
  function previewMap(outer, c, innerLayout) {
    const r = outer.layout.rects.get(c.key) ? rectOf(outer.level, outer.layout, c.key) : null;
    const p = r && previewPlacement(previewBox(r), innerLayout);
    if (!p) return null;
    const f = frameOf(innerLayout);
    return { k: p.sc, dx: p.ox - (f.x + p.bb.x0) * p.sc, dy: p.oy - (f.y + p.bb.y0) * p.sc };
  }
  const mapRect = (m, r) => ({ x: r.x * m.k + m.dx, y: r.y * m.k + m.dy, w: r.w * m.k, h: r.h * m.k });
  const clamp01 = (x) => Math.max(0, Math.min(1, x));

  // Zooms between a level and the level that one of its blocks opens, working in the outer level's coordinates.
  // The inner level is drawn scaled into the block's preview, so the miniatures grow into the blocks they stand for
  // while the outer level fades. Going in, the zoom starts from the current view. Going out, it starts from the
  // current view of the inner level, mapped into the block, and ends on the whole outer level.
  async function morph(outer, inner, map, dir) {
    const s = stage();
    for (const old of s.querySelectorAll(".level")) old.remove();
    const outerG = renderLevel(outer.level, outer.layout), innerG = renderLevel(inner.level, inner.layout);
    innerG.setAttribute("transform", `matrix(${map.k} 0 0 ${map.k} ${map.dx} ${map.dy})`);
    s.append(outerG, innerG);
    s.classList.add("zooming");
    s.classList.remove("hovering", "focused");
    const inView = dir === "in" ? mapRect(map, arrivalView(inner.level, inner.layout)) : mapRect(map, state.view);
    const outView = dir === "in" ? state.view : arrivalView(outer.level, outer.layout);
    const dots = $("#dots"), span = Math.log(inView.w / outView.w);
    const fade = () => {
      // The depth of the zoom from 0 at the outer level to 1 at the inner level, on a log scale so that it tracks
      // what the eye sees. The outer level is gone before its text grows large, and the grid of dots shrinks with
      // the zoom so that it arrives at the inner level's spacing.
      const t = clamp01(Math.log(state.view.w / outView.w) / span);
      innerG.style.opacity = clamp01(t * 3);
      outerG.style.opacity = clamp01(1 - (t - 0.1) / 0.35);
      dots.setAttribute("patternTransform", `scale(${Math.pow(map.k, t)})`);
    };
    setViewBox(dir === "in" ? outView : inView);
    fade();
    if (dir === "in") await animateViewBox(outView, inView, 520, fade, easeInOut);
    else await animateViewBox(inView, outView, 520, fade, easeInOut);
    s.classList.remove("zooming");
  }

  async function enter(c) {
    rememberView();
    const cur = state.current;
    const target = childLoc(c);
    if (cur && !target.leaf && !reduceMotion) {
      const inner = await prepare(target);
      const map = previewMap(cur, c, inner.layout);
      if (map) {
        state.trail.push(target); state.selected = null;
        await morph(cur, inner, map, "in");
        const m = await mount();
        if (m) setViewBox(arrive(m));
        return;
      }
    }
    if (cur) {
      const r = cur.layout.rects.get(c.key) ? rectOf(cur.level, cur.layout, c.key) : null;
      if (r) {
        stage().classList.add("zooming");
        await animateViewBox(state.view, fitRect(r, 12), 240);
      }
    }
    state.trail.push(target); state.selected = null;
    const m = await mount();
    stage().classList.remove("zooming");
    if (m) setViewBox(arrive(m));
  }

  async function goTo(index) {
    rememberView();
    const from = loc(), cur = state.current;
    state.trail = state.trail.slice(0, index + 1); state.selected = null;
    const to = loc();
    if (cur && !from.leaf && !to.leaf && !reduceMotion) {
      const outer = await prepare(to);
      const c = outer.level.children.find((x) => locKey(childLoc(x)) === locKey(from));
      const map = c && previewMap(outer, c, cur.layout);
      if (map) {
        await morph(outer, cur, map, "out");
        const m = await mount();
        if (m) { setViewBox(arrive(m)); select(c.key); }
        return;
      }
    }
    const m = await mount();
    if (!m) return;
    const fromKey = from.leaf || (from.group ? `g:${from.view}/${from.group}` : from.level);
    const r = m.layout.rects.get(fromKey) ? rectOf(m.level, m.layout, fromKey) : null;
    if (r) {
      setViewBox(fitRect(r, 12));
      await animateViewBox(state.view, arrive(m), 240);
      select(fromKey);
    } else setViewBox(arrive(m));
  }

  function up() { if (state.trail.length > 1) goTo(state.trail.length - 2); }
  // Returns the trail of the page's home, where it opens without a location in the address: the top level's first
  // view, or the top level's tree when it has no view.
  function homeTrail() {
    const view = viewsAt(ROOT)[0];
    return view ? [{ level: ROOT }, { level: ROOT, view }] : [{ level: ROOT }];
  }
  // Goes to the page's home, as homeTrail gives it, from wherever the page is. When the trail already passes through
  // the home, it goes back along the trail, as a breadcrumb does.
  async function toTop() {
    const home = homeTrail(), at = home.length - 1;
    if (state.trail.length > at && locKey(state.trail[at]) === locKey(home[at])) { if (state.trail.length > home.length) goTo(at); return; }
    rememberView();
    state.trail = home; state.selected = null;
    const m = await mount();
    if (m) setViewBox(arrive(m));
  }

  async function switchView(view) {
    rememberView();
    const l = loc();
    state.trail[state.trail.length - 1] = view ? { level: l.level, view } : { level: l.level };
    state.selected = null;
    const m = await mount();
    if (m) setViewBox(arrive(m));
  }

  // Shows a node selected at the level that draws it, descending the tree.
  async function showInContext(id) {
    rememberView();
    if (id === ROOT) { state.trail = [{ level: ROOT }]; state.selected = null; const m = await mount(); if (m) setViewBox(arrive(m)); return; }
    if (isExternal(id)) return;
    const trail = [{ level: ROOT }];
    const parent = parentOf(id);
    for (const anc of ancestors(parent)) trail.push({ level: anc });
    state.trail = trail; state.selected = id;
    const m = await mount();
    if (m) setViewBox(arrive(m));
  }
  // Opens the leaf view that holds a changed file, by its leaves at head or else at base, and scrolls to the file.
  async function openFile(path) {
    const f = fileOf(path);
    if (!f) return;
    const leaf = f.test ? f.subject || TESTS : f.leavesHead[0] || f.leavesBase[0] || otherLeafOf(f.path);
    if (f.test) state.testsOpen.add(leaf);
    rememberView();
    state.trail = [{ level: ROOT }, ...ancestors(parentOf(leaf)).map((x) => ({ level: x })), { leaf }];
    state.selected = null;
    await mount();
    const target = document.querySelector(`[data-file="${CSS.escape(f.path)}"]`);
    if (target) target.scrollIntoView({ block: "start" });
  }

  function ancestors(id) { const out = []; for (let x = id; x && x !== ROOT; x = parentOf(x)) out.unshift(x); return out; }

  // Shows a leaf-pair edge at the lowest tree level that draws it as an edge between two blocks or a block and a pin.
  async function showEdge(e) {
    rememberView();
    const a = e.from, b = e.to;
    let level;
    if (isExternal(b) || b === UNSORTED || a === UNSORTED) { level = isExternal(b) ? parentOf(a) : ROOT; if (isExternal(b)) state.opts.externals = true, $("#externals").checked = true; }
    else {
      const pa = ancestors(a), pb = ancestors(b);
      let i = 0;
      while (i < pa.length - 1 && i < pb.length - 1 && pa[i] === pb[i]) i++;
      level = i === 0 ? ROOT : pa[i - 1];
    }
    state.trail = [{ level: ROOT }, ...ancestors(level).map((x) => ({ level: x }))];
    state.selected = null;
    const m = await mount();
    if (!m) return;
    setViewBox(arrive(m));
    const hit = m.level.edges.find((g) => g.members.includes(e));
    if (hit) select(hit.key);
  }

  // Selects on click unless the click ended a drag.
  let dragged = false, dragging = false;
  function clickSelect(ev, key) {
    ev.stopPropagation();
    if (!dragged) select(key);
  }

  // Updates the selection in place, so that a second click lands on the same element and double-click works. A
  // selection made by following a row in the details panel remembers the one before it, a return pops it, and any
  // other selection starts afresh.
  function select(key, how) {
    if (how === "follow") { if (key !== state.selected) state.back.push(state.selected); }
    else if (how !== "back") state.back = [];
    state.selected = key;
    const s = stage();
    for (const n of s.querySelectorAll(".selected")) {
      n.classList.remove("selected");
      const line = n.querySelector(".edge-line");
      if (line) line.setAttribute("marker-end", `url(#arrow-${line.getAttribute("data-marker")})`);
    }
    if (key) {
      for (const target of s.querySelectorAll(`[data-key="${CSS.escape(key)}"]`)) {
        target.classList.add("selected");
        const line = target.querySelector(".edge-line");
        if (line) line.setAttribute("marker-end", "url(#arrow-selected)");
      }
    }
    // Bring the neighborhood of the selection out of the dimming.
    s.classList.toggle("focused", markFocus(key, "in-focus"));
    renderDetails();
  }

  // Marks a block, pin or edge with a class, together with the edges that touch it and the things at their other ends.
  // Returns a value indicating whether anything was marked.
  function markFocus(key, cls) {
    const s = stage();
    for (const n of s.querySelectorAll("." + cls)) n.classList.remove(cls);
    if (!key || !state.current) return false;
    const mark = (k) => {
      for (const n of s.querySelectorAll(`[data-key="${CSS.escape(k)}"]`)) {
        n.classList.add(cls);
        if (n.dataset.d) { n.append(flowArrows(n.dataset.d, +n.dataset.w)); delete n.dataset.d; }
      }
    };
    const markEdge = (e) => { mark(e.key); mark(e.key + ":label"); mark(e.from); mark(e.to); };
    const edges = state.current.level.edges;
    if (key.startsWith("edge:")) {
      const e = edges.find((x) => x.key === key);
      if (!e) return false;
      markEdge(e);
      return true;
    }
    mark(key);
    for (const e of edges) if (e.from === key || e.to === key) markEdge(e);
    return true;
  }

  // Brings the blocks a decision is drawn from and reaches out of the drawing while its row is under the pointer, and
  // fades everything else. A null decision ends it. A leaf view has no drawing, so it does nothing there.
  function hoverDecision(d) {
    if (dragging || !state.current) return;
    const s = stage();
    for (const n of s.querySelectorAll(".hot")) n.classList.remove("hot");
    const keys = d ? (({ source, reach }) => [...source, ...reach.keys()])(decisionBlocks(state.current.level, d)) : [];
    for (const k of keys) for (const n of s.querySelectorAll(`[data-key="${CSS.escape(k)}"]`)) n.classList.add("hot");
    s.classList.toggle("hovering", keys.length > 0);
  }

  // Shows the neighborhood of the block, pin or edge under the pointer and fades everything else.
  function hover(key) {
    if (dragging) return;
    stage().classList.toggle("hovering", markFocus(key, "hot"));
    showTip(key);
  }

  // ---------- Pan and zoom ----------

  function installPanZoom() {
    const s = stage();
    let drag = null;
    const toUser = (ev) => {
      const b = s.getBoundingClientRect();
      return { x: state.view.x + (ev.clientX - b.left) / b.width * state.view.w, y: state.view.y + (ev.clientY - b.top) / b.height * state.view.h };
    };
    s.addEventListener("wheel", (ev) => {
      if (!state.current || !(ev.ctrlKey || ev.metaKey)) return;
      ev.preventDefault();
      const p = toUser(ev);
      const k = Math.exp(ev.deltaY * 0.0012);
      const v = state.view;
      const w = Math.min(Math.max(v.w * k, 200), 20000), h = w / state.aspect;
      setViewBox({ x: p.x - (p.x - v.x) * (w / v.w), y: p.y - (p.y - v.y) * (h / v.h), w, h });
    }, { passive: false });
    // The pointer is captured only once a drag has begun. Capturing on pointerdown would
    // send the click and double-click that follow to the stage instead of the block under them.
    s.addEventListener("pointerdown", (ev) => {
      if (ev.button !== 0) return;
      drag = { x: ev.clientX, y: ev.clientY, view: { ...state.view }, moved: false, pointerId: ev.pointerId };
    });
    s.addEventListener("pointermove", (ev) => {
      if (!drag) return;
      const b = s.getBoundingClientRect();
      const dx = (ev.clientX - drag.x) / b.width * drag.view.w, dy = (ev.clientY - drag.y) / b.height * drag.view.h;
      if (!drag.moved && Math.hypot(ev.clientX - drag.x, ev.clientY - drag.y) < 4) return;
      if (!drag.moved) { hover(null); dragging = true; try { s.setPointerCapture(drag.pointerId); } catch (e) { /* ignore */ } }
      drag.moved = true;
      setViewBox({ x: drag.view.x - dx, y: drag.view.y - dy, w: drag.view.w, h: drag.view.h });
    });
    const end = (ev) => {
      if (!drag) return;
      if (drag.moved) { dragged = true; setTimeout(() => { dragged = false; }, 0); }
      dragging = false;
      drag = null;
      try { s.releasePointerCapture(ev.pointerId); } catch (e) { /* ignore */ }
    };
    s.addEventListener("pointerup", end);
    s.addEventListener("pointercancel", end);
    s.addEventListener("click", () => { if (!dragged) select(null); });
    s.addEventListener("dblclick", (ev) => { if (ev.target === s || ev.target.id === "paper") { zoomToFit(); } });
  }

  // ---------- Theme ----------

  // Wires the theme control. "auto" follows the system's color scheme; "light" and "dark" override it by setting
  // data-theme on the root element, which the style sheet honors over the system setting. The choice is remembered
  // in this browser only.
  // The theme choice. Served by the vibecheck command, the server keeps it across sessions, since each session has a
  // port of its own and the browser keeps a page's storage per port; opened from a file, the browser keeps it.
  function installThemeChoice() {
    const served = !!META.settings;
    const read = () => {
      if (served) return META.settings.theme || "auto";
      try { return localStorage.getItem("vibecheck-theme") || "auto"; } catch (e) { return "auto"; }
    };
    const apply = (choice) => {
      if (choice === "auto") delete document.documentElement.dataset.theme;
      else document.documentElement.dataset.theme = choice;
      for (const b of $("#theme").querySelectorAll("button")) b.classList.toggle("on", b.dataset.themeChoice === choice);
    };
    for (const b of $("#theme").querySelectorAll("button")) {
      b.addEventListener("click", () => {
        const choice = b.dataset.themeChoice;
        if (served) fetch("/api/settings", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ theme: choice }) }).catch(() => null);
        else try { if (choice === "auto") localStorage.removeItem("vibecheck-theme"); else localStorage.setItem("vibecheck-theme", choice); } catch (e) { /* storage unavailable */ }
        apply(choice);
      });
    }
    apply(read());
  }

  // ---------- Narratives ----------

  // The narratives Claude wrote for this page's result, by key, as the server's narrative events deliver them. A key
  // is "summary", "node:<id>" or "edge:<from>→<to>", where an end is a node, a group as "g:view/group", or an
  // external. The state is "off" when no narratives come, because the page was opened without the server or the server
  // has no Claude, and otherwise "waiting", "writing", "done" or "failed".
  const NARR = { state: META.review && META.narratives ? "waiting" : "off", items: new Map(), pending: new Set(), errors: new Map(), total: 0, dropped: 0, message: "", expanded: false };
  const API = META.review ? `/api/reviews/${encodeURIComponent(META.review)}` : null;

  // Returns the parts of a text with each `code` span set in monospace.
  function richText(text) { return text.split(/`([^`]+)`/).map((part, i) => i % 2 ? el("code", {}, part) : part); }

  // Returns the block a pin stands for, or null for a key that is not a pin.
  function pinTarget(k) { const m = /^pin:(?:in|out):(.*)$/.exec(k); return m ? m[1] : null; }

  // Returns the narrative of an edge the current location draws, or null. An edge to a pin stands for the edge
  // between this location and the pin's block, which is explained where both are drawn as blocks, so the block inside,
  // then the location, then the location's ancestors are tried in its place.
  function edgeNarrative(e) {
    const a = pinTarget(e.from), b = pinTarget(e.to);
    if (!a && !b) return NARR.items.get(`edge:${e.from}→${e.to}`) || null;
    const l = state.current.level.loc;
    const anchors = [a ? e.to : e.from];
    if (l.group) anchors.push(`g:${l.view}/${l.group}`);
    else for (let x = l.level; x && x !== ROOT; x = parentOf(x)) anchors.push(x);
    for (const x of anchors) {
      const n = NARR.items.get(a ? `edge:${a}→${x}` : `edge:${x}→${b}`);
      if (n) return n;
    }
    return null;
  }

  // Shows a cited type pair by its short names, a file by its path, and a part of the map by its name.
  function citeText(c) { return c.includes(" → ") ? c.split(" → ").map(shortName).join(" → ") : NODES[c] || c === UNSORTED ? nameOf(c) : c; }

  // Returns the details panel's block for a narrative: its text marked as Claude's, the evidence it rests on and a
  // regenerate action. While narratives are being written and this one has not arrived, it says so. Otherwise it is null.
  function narrativeBlock(n, key = n && n.key) {
    // Claude explains the map's parts, and the Other files block is not one of them.
    if (NARR.state === "off" || (key && key.startsWith("node:" + OTHER))) return null;
    const busy = key && NARR.pending.has(key);
    if (!n && !busy && NARR.state !== "writing" && NARR.state !== "waiting") return key && NARR.errors.has(key) ? el("p", { class: "warn" }, NARR.errors.get(key)) : null;
    const box = el("section", { class: "narrative" }, el("h3", {}, "Why it changed", help(HELP.narrative)));
    if (n) {
      box.append(el("p", { class: "narrative-text" }, ...richText(n.text)));
      if (n.cites.length) {
        const ul = el("ul", { class: "evidence cites" });
        for (const c of n.cites.slice(0, 12)) ul.append(el("li", { title: c }, el("span", {}, citeText(c))));
        box.append(el("div", { class: "by" }, "Rests on"), ul);
      }
    }
    if (busy) box.append(el("p", { class: "hint" }, el("span", { class: "spin", "aria-hidden": "true" }), " Claude is writing it again…"));
    else if (!n) box.append(el("p", { class: "hint" }, "Claude is writing an explanation…"));
    if (key && NARR.errors.has(key)) box.append(el("p", { class: "warn" }, NARR.errors.get(key)));
    if (n) box.append(el("div", { class: "narrative-foot" }, el("span", { class: "by" }, "Written by Claude; check it against the code."),
      el("button", { class: "link", disabled: busy, onclick: () => regenerate(key) }, "Regenerate")));
    return box;
  }

  async function regenerate(key) {
    NARR.pending.add(key); NARR.errors.delete(key); showNarratives();
    const r = await fetch(`${API}/narratives`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ key }) });
    if (!r.ok) { NARR.pending.delete(key); NARR.errors.set(key, (await r.json().catch(() => ({}))).message || "It could not be written again."); showNarratives(); }
  }

  async function regenerateAll() {
    NARR.state = "waiting"; showNarratives();
    await fetch(`${API}/narratives`, { method: "POST", headers: { "Content-Type": "application/json" }, body: "{}" });
  }

  // Records a narrative event of this page's run. A "writing" event starts the narratives afresh, since it follows a
  // regeneration of all of them as well as the first writing.
  function narrativeEvent(e) {
    if (e.run !== META.run) return;
    if (e.type === "narratives") {
      NARR.state = e.state; NARR.message = e.message || "";
      if (e.state === "writing") { NARR.items.clear(); NARR.errors.clear(); NARR.total = e.total; }
      if (e.state === "done") NARR.dropped = e.dropped;
    } else if (e.failed) {
      NARR.pending.delete(e.key); NARR.errors.set(e.key, e.message);
    } else {
      NARR.items.set(e.key, { key: e.key, text: e.text, cites: e.cites || [] });
      NARR.pending.delete(e.key); NARR.errors.delete(e.key);
    }
    showNarratives();
  }

  // Redraws what shows narratives once per frame, however many events arrive in it.
  let narrativeFrame = 0;
  function showNarratives() {
    if (narrativeFrame) return;
    narrativeFrame = requestAnimationFrame(() => {
      narrativeFrame = 0;
      renderSummary();
      if (state.current || loc().leaf) renderDetails();
      if (state.panel === "path" || state.panel === "glossary") renderChanges();
      if (tipKey) showTip(tipKey);
      if (termTip) showTermTip();
    });
  }

  // Shows the summary of the whole changeset above the drawing, with how far the other narratives have come.
  function renderSummary() {
    const box = $("#summary");
    if (NARR.state === "off") { box.hidden = true; return; }
    const n = NARR.items.get("summary");
    const writing = NARR.state === "writing" || NARR.state === "waiting";
    const others = [...NARR.items.keys()].filter((k) => k !== "summary").length;
    const status = writing ? `${others} of ${NARR.total ? NARR.total - 1 : "…"} explanations so far`
      : NARR.state === "done" ? `${others} explanations` + (NARR.dropped ? `; ${NARR.dropped} failed their check and are not shown` : "") : "";
    const head = el("div", { class: "summary-head" }, el("span", { class: "summary-label" }, "Summary"), el("span", { class: "by" }, "written by Claude"),
      writing ? el("span", { class: "spin", "aria-hidden": "true" }) : null, el("span", { class: "by" }, status), el("span", { class: "spacer" }),
      el("button", { class: "link", disabled: writing, onclick: regenerateAll, title: "Write every explanation of this changeset again" }, "Regenerate all"));
    box.replaceChildren(head);
    if (n) {
      box.append(el("p", { class: "summary-text" }, ...richText(n.text)));
      const parts = n.cites.filter((c) => NODES[c]);
      // The summary shows its first lines until the reader expands it, so that the drawing keeps its room.
      const more = el("button", { class: "link", onclick: () => { NARR.expanded = !NARR.expanded; renderSummary(); } }, NARR.expanded ? "Less" : "More");
      box.append(el("div", { class: "summary-parts" }, more, parts.length ? el("span", { class: "by" }, "Rests on") : null,
        ...parts.map((c) => el("button", { class: "link", onclick: () => showInContext(c) }, nameOf(c)))));
    } else if (NARR.state === "failed") box.append(el("p", { class: "warn" }, NARR.message));
    else if (writing) box.append(el("p", { class: "hint" }, "Claude is reading the change."));
    if (NARR.errors.has("summary")) box.append(el("p", { class: "warn" }, NARR.errors.get("summary")));
    box.classList.toggle("clamped", !NARR.expanded);
    box.hidden = false;
  }

  // Shows an edge's narrative, or its counts until the narrative arrives, in a tooltip that follows the pointer.
  let tipKey = null;
  function showTip(key) {
    const tip = $("#tip");
    const e = key && key.startsWith("edge:") && state.current ? state.current.level.edges.find((x) => x.key === key) : null;
    tipKey = e ? key : null;
    if (!e) { tip.hidden = true; return; }
    const n = NARR.state === "off" ? null : edgeNarrative(e);
    tip.replaceChildren(el("div", { class: "tip-head" }, el("span", {}, `${nameOf(stripPin(e.from))} → ${nameOf(stripPin(e.to))}`),
      el("span", { class: "n" }, e.base === e.head ? `${e.head} refs` : `${e.base} → ${e.head} refs`)));
    if (n) tip.append(el("p", {}, ...richText(n.text)), el("div", { class: "by" }, "Written by Claude"));
    else if (e.status !== "unchanged" && (NARR.state === "writing" || NARR.state === "waiting")) tip.append(el("div", { class: "by" }, "Claude is writing an explanation…"));
    tip.hidden = false;
  }

  // Keeps the tooltip beside the pointer and inside the drawing.
  function installTip() {
    const wrap = $("#stage").parentElement, tip = $("#tip");
    wrap.addEventListener("pointermove", (ev) => {
      if (tip.hidden) return;
      const b = wrap.getBoundingClientRect();
      const x = ev.clientX - b.left, y = ev.clientY - b.top;
      const left = x + 16 + tip.offsetWidth > b.width ? x - 16 - tip.offsetWidth : x + 16;
      const top = y + 16 + tip.offsetHeight > b.height ? y - 16 - tip.offsetHeight : y + 16;
      tip.style.left = Math.max(4, left) + "px"; tip.style.top = Math.max(4, top) + "px";
    });
    wrap.addEventListener("pointerleave", () => showTip(null));
  }

  // ---------- Pull request ----------

  // The review's pull request as the server's pull events deliver it, and why it is stale when GitHub cannot be read.
  // The bar under the top bar shows its state, and a notice offers a new review when the author pushes. See
  // docs/pull-requests.md sections 2, 3.3 and 4.6.
  // Each thread's place in this review's diffs arrives with the snapshot, by thread identifier (section 7.1).
  // The places of the annotations of the checks arrive the same way, keyed by check and annotation (section 9).
  const PULL = { snapshot: META.pull || null, stale: META.pullStale || null, places: META.pullThreads || {}, notes: META.pullAnnotations || {} };
  // How the bar words GitHub's check rollup states, review decisions and merge states, each with the pill's tone.
  const CHECK_TEXT = { SUCCESS: ["checks pass", "good"], FAILURE: ["checks fail", "bad"], ERROR: ["checks fail", "bad"], PENDING: ["checks pending", "wait"], EXPECTED: ["checks pending", "wait"] };
  const DECISION_TEXT = { APPROVED: ["approved", "good"], CHANGES_REQUESTED: ["changes requested", "bad"], REVIEW_REQUIRED: ["review required", "wait"] };
  const MERGE_TEXT = { BLOCKED: "merging is blocked", BEHIND: "behind its base branch", DIRTY: "has conflicts", UNSTABLE: "mergeable, with failing checks", CLEAN: "ready to merge", HAS_HOOKS: "ready to merge" };

  // Returns a pill with a text and its tone, or null when there is no text.
  const pill = (pair) => pair ? el("span", { class: "pill " + pair[1] }, pair[0]) : null;

  // Shows the tabs, the pull request's number, title, author, state, checks and review decision, and the size of the
  // viewer's pending review in the bar, and the new-push notice when its head has moved past the review's.
  function renderPull() {
    const bar = $("#pullbar"), p = PULL.snapshot, d = discussion();
    if (!p) { bar.hidden = true; $("#pushnotice").hidden = true; return; }
    const tab = (name, label) => el("button", { class: state.tab === name ? "on" : "", onclick: () => showTab(name) }, label);
    const pr = p.state === "OPEN" ? (p.isDraft ? "draft" : "open") : p.state.toLowerCase();
    bar.replaceChildren(...[
      el("span", { class: "seg", role: "tablist" }, tab("map", "Map"), tab("conversation", "Conversation")),
      el("a", { class: "pull-number", href: p.url, target: "_blank", rel: "noopener", title: "Open the pull request on GitHub" }, "#" + p.number),
      el("span", { class: "pull-title" }, p.title),
      p.author ? el("span", { class: "by" }, "by " + p.author) : null,
      pill([pr, pr === "open" ? "good" : pr === "merged" ? "" : "wait"]),
      pill(CHECK_TEXT[p.checks] || ["no checks", ""]),
      pill(DECISION_TEXT[p.reviewDecision]),
      d && d.pending ? pill([`your pending review: ${plural(d.pending.comments, "comment", "comments")}`, "wait"]) : null,
      p.state === "OPEN" && MERGE_TEXT[p.mergeState] ? el("span", { class: "by" }, MERGE_TEXT[p.mergeState]) : null,
      el("span", { class: "spacer" }),
      PULL.stale ? el("span", { class: "stale", title: PULL.stale }, "Stale: " + PULL.stale) : null,
      canWrite() ? el("button", { class: "btn primary", onclick: toggleReviewBox, title: "Submit your review: a comment, an approval or a request for changes" }, "Review")
        : API && p.state !== "OPEN" ? el("span", { class: "by" }, "read-only") : null,
      API ? el("button", { class: "link", onclick: refreshPull, title: "Read the pull request from GitHub now" }, "Refresh") : null].filter(Boolean));
    bar.hidden = false;
    renderPush();
  }

  // Asks the server to read the pull request at once. The answer arrives as a pull event.
  function refreshPull() {
    fetch(`${API}/pull/refresh`, { method: "POST", headers: { "Content-Type": "application/json" }, body: "{}" });
  }

  // Shows the notice of a push: how many commits arrived since the review began, with a review of the new head and,
  // when the reviewer has reviewed before, a review of the commits since that review.
  function renderPush() {
    const box = $("#pushnotice"), p = PULL.snapshot;
    if (!API || !p || p.state !== "OPEN" || !CS.head || p.headOid === CS.head) { box.hidden = true; return; }
    const added = META.pullCommits != null ? p.commits - META.pullCommits : 0;
    const what = added > 0 ? `${plural(added, "new commit was", "new commits were")} pushed to #${p.number}.` : `#${p.number} was force-pushed.`;
    const since = p.latestReviewCommit && p.latestReviewCommit !== p.headOid
      ? el("button", { class: "btn", onclick: () => openPull(p.latestReviewCommit) }, "Review the commits since your last review") : null;
    box.replaceChildren(...[el("span", {}, what), el("button", { class: "btn", onclick: () => openPull(null) }, "Review the new head"), since].filter(Boolean));
    box.hidden = false;
  }

  // Starts a review of the pull request's current head, from its base or from a given commit, and follows it on the
  // progress page. This review stays open until the reviewer leaves it.
  async function openPull(from) {
    const request = from ? { pull: PULL.snapshot.number, from } : { pull: PULL.snapshot.number };
    const r = await fetch("/api/reviews", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(request) });
    const body = await r.json().catch(() => ({}));
    if (r.ok) { location.href = "/?review=" + encodeURIComponent(body.id); return; }
    $("#pushnotice").replaceChildren(el("span", {}, body.message || `The review could not start (${r.status}).`));
  }

  // ---------- Discussion ----------

  // The pull request's review threads, and where each goes in this review's diffs. The server places them: a thread
  // with a hunk shows under its line, a thread without one is listed at the top of its file, and a thread on a file
  // this review's diff does not hold appears only in the conversation. See docs/pull-requests.md sections 6 and 7.
  const discussion = () => (PULL.snapshot && PULL.snapshot.discussion) || null;
  const allThreads = () => { const d = discussion(); return d ? d.threads : []; };

  // Returns the threads on a file of this review: those under each line, keyed by hunk and line index, and those
  // listed at the top of the file.
  function threadsOf(path) {
    const lines = new Map(), top = [];
    for (const t of allThreads()) {
      const p = PULL.places[t.id];
      if (!p || p.path !== path) continue;
      if (p.hunk === null || p.hunk === undefined) { top.push(t); continue; }
      const k = p.hunk + ":" + p.line;
      if (!lines.has(k)) lines.set(k, []);
      lines.get(k).push(t);
    }
    return { lines, top };
  }

  // Returns the unresolved threads on the changed files under a node or a group, which count by the rule of
  // reviewedCount, the rule of section 5.3.
  function openThreadsUnder(key) {
    const threads = allThreads();
    if (!threads.length) return [];
    const paths = new Set(reviewedCount(key).paths);
    return threads.filter((t) => !t.isResolved && PULL.places[t.id] && paths.has(PULL.places[t.id].path));
  }

  // The elements a comment body keeps, and those it drops with everything inside them. Any other element is replaced
  // by its contents.
  const BODY_TAGS = new Set("a abbr b blockquote br code dd del details div dl dt em h1 h2 h3 h4 h5 h6 hr i img input ins kbd li ol p pre q s samp small span strong sub summary sup table tbody td tfoot th thead tr tt ul".split(" "));
  const BODY_DROPPED = new Set("script style iframe frame frameset object embed form template svg math noscript link meta base textarea select button audio video".split(" "));

  // Returns a comment body that GitHub rendered as HTML, as nodes that are safe to insert. GitHub sanitizes the HTML,
  // and the page sanitizes it again, so that a gap in either does not become script in a page that can call the
  // server. The HTML is parsed in an inert document, and only the allowed elements and attributes are copied. Links
  // open in a new tab, and only web and mail links and https images survive. See docs/pull-requests.md section 11.
  function safeBody(html) {
    const doc = new DOMParser().parseFromString(html || "", "text/html");
    const base = PULL.snapshot ? PULL.snapshot.url : location.href;
    const address = (value, schemes) => { try { const u = new URL(value || "", base); return schemes.includes(u.protocol) ? u.href : null; } catch (e) { return null; } };
    const copy = (from, to) => {
      for (const n of from.childNodes) {
        if (n.nodeType === Node.TEXT_NODE) { to.append(n.data); continue; }
        if (n.nodeType !== Node.ELEMENT_NODE || BODY_DROPPED.has(n.localName)) continue;
        if (!BODY_TAGS.has(n.localName)) { copy(n, to); continue; }
        const tag = n.localName, e = document.createElement(tag);
        if (tag === "a") {
          const href = address(n.getAttribute("href"), ["http:", "https:", "mailto:"]);
          if (href) { e.href = href; e.target = "_blank"; e.rel = "noopener noreferrer"; }
        } else if (tag === "img") {
          const src = address(n.getAttribute("src"), ["https:"]);
          if (!src) continue;
          e.src = src; e.alt = n.getAttribute("alt") || ""; e.loading = "lazy"; e.referrerPolicy = "no-referrer";
        } else if (tag === "input") {
          if (n.getAttribute("type") !== "checkbox") continue;
          e.type = "checkbox"; e.checked = n.hasAttribute("checked"); e.disabled = true;
        } else if (tag === "ol" && /^\d+$/.test(n.getAttribute("start") || "")) e.start = +n.getAttribute("start");
        else if ((tag === "td" || tag === "th") && /^(left|right|center)$/.test(n.getAttribute("align") || "")) e.style.textAlign = n.getAttribute("align");
        else if (tag === "details" && n.hasAttribute("open")) e.open = true;
        if (n.hasAttribute("title")) e.title = n.getAttribute("title");
        copy(n, e);
        to.append(e);
      }
    };
    const out = document.createDocumentFragment();
    copy(doc.body, out);
    return out;
  }

  const when = (at) => at ? new Date(at).toLocaleString(undefined, { dateStyle: "medium", timeStyle: "short" }) : "";
  const shortOid = (oid) => oid ? oid.slice(0, 7) : "";

  // Returns a person's avatar from GitHub, or a circle with the initial of their name when GitHub gives none. Only an
  // https address is loaded, and without a referrer, as images in bodies are.
  function avatar(url, who) {
    if (url && url.startsWith("https://")) return el("img", { class: "avatar", src: url, alt: "", loading: "lazy", referrerpolicy: "no-referrer" });
    return el("span", { class: "avatar avatar-none", "aria-hidden": "true" }, (who || "?").slice(0, 1).toUpperCase());
  }

  // Returns a comment with its author and their avatar, its time, whether it is pending, and its body.
  function commentBox(c) {
    return el("div", { class: "comment" + (c.pending ? " pending" : "") },
      el("div", { class: "comment-head" }, avatar(c.avatar, c.author), el("b", {}, c.author || "ghost"), el("a", { class: "when", href: c.url, target: "_blank", rel: "noopener" }, when(c.at)),
        c.pending ? el("span", { class: "pill wait" }, "pending") : null),
      el("div", { class: "body" }, safeBody(c.bodyHtml)));
  }

  // Returns where a thread was written: the whole file, a line, or a range of lines, on the side it was written on.
  function threadLines(t) {
    if (t.onFile) return "the file";
    const end = t.isOutdated ? t.originalLine : t.line, start = t.isOutdated ? t.originalStartLine : t.startLine;
    const where = start && start !== end ? `lines ${start}–${end}` : `line ${end}`;
    return t.side === "LEFT" ? where + " at base" : where;
  }

  // Returns the last lines of the hunk a thread was written against, for a thread whose lines this diff does not show.
  function hunkTail(diffHunk, count) {
    const lines = diffHunk.split("\n").filter((l) => !l.startsWith("@@")).slice(-count);
    return el("pre", { class: "thread-hunk" }, ...lines.map((l) => el("span", { class: l[0] === "+" ? "add" : l[0] === "-" ? "del" : "" }, l + "\n")));
  }

  // Returns a review thread with its comments. A resolved thread is collapsed to one line. A thread listed at the top
  // of its file, or shown in the conversation, says where it was written and shows the hunk it was written against.
  // Wherever it is shown, it takes replies and resolutions while the pull request is open. The options are top, for
  // the list at the top of a file, and away, for the conversation.
  function threadBox(t, opts = {}) {
    const placed = PULL.places[t.id];
    const why = t.isResolved ? "resolved" + (t.resolvedBy ? " by " + t.resolvedBy : "") : t.isOutdated ? "outdated"
      : opts.top && !t.onFile ? "not on this review's lines" : null;
    const replies = t.comments.length - 1;
    const box = el("details", { class: "thread" + (t.isResolved ? " resolved" : "") + (t.comments.some((c) => c.pending) ? " pending" : ""), open: !t.isResolved, "data-thread": opts.away ? null : t.id },
      el("summary", {},
        el("span", { class: "thread-where" }, opts.away ? `${t.path} · ${threadLines(t)}` : threadLines(t)),
        why ? el("span", { class: "pill" + (t.isResolved ? " good" : "") }, why) : null,
        el("span", { class: "thread-count" }, replies > 0 ? plural(replies, "reply", "replies") : ""),
        t.isResolved && t.comments[0] ? el("span", { class: "thread-excerpt" }, `${t.comments[0].author || "ghost"}: ${t.comments[0].bodyHtml.replace(/<[^>]*>/g, " ").replace(/\s+/g, " ").trim().slice(0, 90)}`) : null));
    if (opts.away && placed) box.append(el("button", { class: "link thread-show", onclick: () => openThread(t) }, "Show in the diff"));
    if ((opts.top || opts.away) && !t.onFile && t.comments[0] && t.comments[0].diffHunk) box.append(hunkTail(t.comments[0].diffHunk, 4));
    for (const c of t.comments) box.append(commentBox(c));
    if (canWrite()) box.append(threadControls(t));
    return box;
  }

  // Returns the list of a node's unresolved threads for the details panel, each opening its line, or null when it has
  // none.
  function threadList(key) {
    const open = openThreadsUnder(key);
    if (!open.length) return null;
    const ul = el("ul", { class: "list" });
    for (const t of open) {
      const path = PULL.places[t.id].path;
      ul.append(el("li", {}, el("button", { class: "row", onclick: () => openThread(t), title: path },
        el("span", { class: "row-name" }, path.slice(path.lastIndexOf("/") + 1)), el("span", { class: "row-meta" }, threadLines(t)))));
    }
    return el("div", {}, el("h3", {}, plural(open.length, "unresolved thread", "unresolved threads"), help(HELP.threads)), ul);
  }

  // Opens the Map tab on the diff that shows a thread, and scrolls to the thread.
  async function openThread(t) {
    const p = PULL.places[t.id];
    if (!p) return;
    if (state.tab !== "map") showTab("map");
    state.expanded.add(p.path);
    await openFile(p.path);
    const target = document.querySelector(`[data-thread="${CSS.escape(t.id)}"]`);
    if (!target) return;
    target.open = true;
    target.scrollIntoView({ block: "center" });
    target.classList.remove("flash");
    void target.offsetWidth;
    target.classList.add("flash");
  }

  // Shows the Map tab or the Conversation tab.
  function showTab(tab) {
    state.tab = tab;
    $(".main").hidden = tab !== "map";
    $("#conversation").hidden = tab !== "conversation";
    renderPull();
    renderConversation();
  }

  const REVIEW_TEXT = { APPROVED: ["approved these changes", "good"], CHANGES_REQUESTED: ["requested changes", "bad"], COMMENTED: ["reviewed", ""], DISMISSED: ["reviewed, since dismissed", ""] };

  // Returns one entry of the timeline. A description, comment or review is a card with its body, and a review lists
  // the threads that started in it. Any other entry is one line.
  function timelineEntry(item, threads) {
    const face = avatar(item.avatar, item.author), who = el("b", {}, item.author || "ghost");
    const time = item.url ? el("a", { class: "when", href: item.url, target: "_blank", rel: "noopener" }, when(item.at)) : el("span", { class: "when" }, when(item.at));
    const line = (...kids) => el("div", { class: "talk-event" }, face, who, " ", ...kids, time);
    switch (item.kind) {
      case "description":
      case "comment":
      case "review": {
        const verb = item.kind === "description" ? ["opened this pull request", ""] : item.kind === "comment" ? ["commented", ""] : REVIEW_TEXT[item.state] || ["reviewed", ""];
        const card = el("section", { class: "card" + (item.kind === "review" ? " review st-" + (item.state || "").toLowerCase() : "") },
          el("header", {}, face, who, el("span", { class: verb[1] ? "verb " + verb[1] : "verb" }, verb[0]), item.kind === "review" && item.commit ? el("code", {}, shortOid(item.commit)) : null, time));
        if (item.bodyHtml && item.bodyHtml.trim()) card.append(el("div", { class: "body" }, safeBody(item.bodyHtml)));
        else if (item.kind === "description") card.append(el("div", { class: "body hint" }, "No description provided."));
        if (threads.length) card.append(el("div", { class: "card-threads" }, ...threads.map((t) => threadBox(t, { away: true }))));
        return card;
      }
      case "force-push": return line("force-pushed from ", el("code", {}, shortOid(item.from)), " to ", el("code", {}, shortOid(item.commit)));
      case "base-changed": return line("changed the base from ", el("code", {}, item.from), " to ", el("code", {}, item.to));
      case "ready": return line("marked this ready for review");
      case "draft": return line("converted this to a draft");
      case "closed": return line("closed this");
      case "reopened": return line("reopened this");
      case "merged": return line("merged ", el("code", {}, shortOid(item.commit)), " into ", el("code", {}, item.to || ""));
      default: return line(item.kind);
    }
  }

  // Returns a run of pushed commits as one entry, with each commit's headline.
  function pushEntry(commits) {
    const authors = [...new Set(commits.map((c) => c.author || "ghost"))];
    const list = el("ul", { class: "talk-commits" }, ...commits.map((c) => el("li", {}, el("a", { href: c.url, target: "_blank", rel: "noopener" }, el("code", {}, shortOid(c.commit))), " ", c.text || "")));
    return el("div", { class: "talk-event" }, avatar(commits[0].avatar, authors[0]), el("b", {}, authors.join(", ")), ` pushed ${plural(commits.length, "commit", "commits")}`, el("span", { class: "when" }, when(commits[commits.length - 1].at)), list);
  }

  // Shows the Conversation tab: the checks, then the timeline, oldest first, starting with the description. Each
  // review lists the threads that started in it, and threads of the viewer's pending review follow at the end.
  function renderConversation() {
    const box = $("#conversation"), p = PULL.snapshot, d = discussion();
    if (box.hidden) return;
    const col = el("div", { class: "talk-col" });
    box.replaceChildren(col);
    if (!p) return;
    // The list of checks below says what the rollup says, so the line above it gives the rollup only when there is no list.
    const listed = allChecks().length > 0;
    col.append(el("div", { class: "talk-checks" }, listed ? null : el("span", {}, "Checks"), listed ? null : pill(CHECK_TEXT[p.checks] || ["no checks", ""]), pill(DECISION_TEXT[p.reviewDecision]),
      p.state === "OPEN" && MERGE_TEXT[p.mergeState] ? el("span", { class: "by" }, MERGE_TEXT[p.mergeState]) : null,
      el("span", { class: "spacer" }), el("a", { href: p.url, target: "_blank", rel: "noopener" }, "Open on GitHub")));
    const checks = checkList();
    if (checks) col.append(checks);
    if (!d) { col.append(el("p", { class: "hint" }, "GitHub has not sent the discussion yet.")); return; }
    const reviews = new Set(d.timeline.filter((i) => i.kind === "review").map((i) => i.id));
    const started = new Map();
    const rest = [];
    for (const t of d.threads) {
      const review = t.comments[0] && t.comments[0].review;
      if (review && reviews.has(review)) { if (!started.has(review)) started.set(review, []); started.get(review).push(t); }
      else rest.push(t);
    }
    let commits = [];
    const flush = () => { if (commits.length) col.append(pushEntry(commits)); commits = []; };
    for (const item of d.timeline) {
      if (item.kind === "commit") { commits.push(item); continue; }
      flush();
      col.append(timelineEntry(item, started.get(item.id) || []));
    }
    flush();
    const pending = rest.filter((t) => t.comments.some((c) => c.pending)), other = rest.filter((t) => !pending.includes(t));
    if (pending.length) col.append(el("section", { class: "card review pending" }, el("header", {}, el("b", {}, "Your pending review"),
      el("span", { class: "verb wait" }, plural(d.pending ? d.pending.comments : pending.length, "comment", "comments") + ", visible only to you")),
      el("div", { class: "card-threads" }, ...pending.map((t) => threadBox(t, { away: true })))));
    if (other.length) col.append(el("section", { class: "card" }, el("header", {}, el("b", {}, "Other review threads")),
      el("div", { class: "card-threads" }, ...other.map((t) => threadBox(t, { away: true })))));
    if (canWrite()) col.append(conversationComposer());
  }

  // ---------- Checks ----------

  // The checks of the pull request's head commit, and the annotations their runs left. The server places each
  // annotation in this review's diffs as it places threads: under its last line in a review of the head, at the top of
  // its file otherwise, and only in its check's row when the diff does not hold its path. Failures and warnings count on
  // the map, so a failing build points at the blocks it concerns. See docs/pull-requests.md section 9.
  const allChecks = () => (PULL.snapshot && PULL.snapshot.checkList) || [];
  const CHECK_ORDER = { failure: 0, pending: 1, cancelled: 2, neutral: 3, success: 4, skipped: 5 };
  const CHECK_TONE = { failure: "bad", pending: "wait", cancelled: "", neutral: "", success: "good", skipped: "" };
  const isProblem = (a) => a.level === "FAILURE" || a.level === "WARNING";

  // Returns every annotation with its check and the key the server places it by.
  function allAnnotations() {
    return allChecks().flatMap((check) => check.annotations.map((a, i) => ({ ...a, check, key: check.id + "/" + i })));
  }

  // Returns the annotations on a file of this review: those under each line, keyed by hunk and line index as threadsOf
  // keys threads, and those listed at the top of the file.
  function annotationsOf(path) {
    const lines = new Map(), top = [];
    for (const a of allAnnotations()) {
      const p = PULL.notes[a.key];
      if (!p || p.path !== path) continue;
      if (p.hunk === null || p.hunk === undefined) { top.push(a); continue; }
      const k = p.hunk + ":" + p.line;
      if (!lines.has(k)) lines.set(k, []);
      lines.get(k).push(a);
    }
    return { lines, top };
  }

  // Returns the failures and warnings on the changed files under a node or a group, by the rule of reviewedCount.
  function problemsUnder(key) {
    const notes = allAnnotations().filter((a) => isProblem(a) && PULL.notes[a.key]);
    if (!notes.length) return [];
    const paths = new Set(reviewedCount(key).paths);
    return notes.filter((a) => paths.has(PULL.notes[a.key].path));
  }

  // Returns an annotation: its level, its check, its lines and its message, which is plain text. The away option shows
  // its path too, for a check's row in the conversation.
  function annotationBox(a, opts = {}) {
    const lines = a.startLine !== a.endLine ? `lines ${a.startLine}–${a.endLine}` : `line ${a.endLine}`;
    const box = el("div", { class: "annotation level-" + a.level.toLowerCase(), "data-note": opts.away ? null : a.key },
      el("div", { class: "annotation-head" }, el("span", { class: "pill " + (a.level === "FAILURE" ? "bad" : a.level === "WARNING" ? "wait" : "") }, a.level.toLowerCase()),
        el("b", {}, a.check.name), el("span", { class: "thread-where" }, opts.away ? `${a.path} · ${lines}` : lines), a.title ? el("span", {}, a.title) : null),
      el("div", { class: "annotation-message" }, a.message));
    if (opts.away && PULL.notes[a.key]) box.append(el("button", { class: "link", onclick: () => openNote(a) }, "Show in the diff"));
    return box;
  }

  // Opens the Map tab on the diff that shows an annotation, and scrolls to it.
  async function openNote(a) {
    const p = PULL.notes[a.key];
    if (!p) return;
    if (state.tab !== "map") showTab("map");
    state.expanded.add(p.path);
    await openFile(p.path);
    const target = document.querySelector(`[data-note="${CSS.escape(a.key)}"]`);
    if (target) target.scrollIntoView({ block: "center" });
  }

  // Returns the list of a node's failures and warnings for the details panel, each opening its line, or null when it
  // has none.
  function problemList(key) {
    const problems = problemsUnder(key);
    if (!problems.length) return null;
    const ul = el("ul", { class: "list" });
    for (const a of problems) {
      const path = PULL.notes[a.key].path;
      ul.append(el("li", {}, el("button", { class: "row", onclick: () => openNote(a), title: `${a.check.name}: ${a.message}` },
        el("span", { class: "row-name" }, path.slice(path.lastIndexOf("/") + 1)), el("span", { class: "row-meta" }, `${a.level.toLowerCase()} · line ${a.endLine}`))));
    }
    return el("div", {}, el("h3", {}, plural(problems.length, "check problem", "check problems"), help(HELP.problems)), ul);
  }

  // Returns how long a check ran, such as "1m 24s", or nothing when it has not finished.
  function duration(check) {
    if (!check.started || !check.completed) return "";
    const s = Math.max(0, Math.round((new Date(check.completed) - new Date(check.started)) / 1000));
    return s >= 60 ? `${Math.floor(s / 60)}m ${s % 60}s` : `${s}s`;
  }

  // Returns the checks for the top of the Conversation tab, failing first: each with its state, name, workflow,
  // whether it is required, how long it ran, and its page. A failed GitHub Actions job shows the end of its log on
  // request, and annotations that no diff line of this review shows are listed under their check.
  function checkList() {
    const checks = allChecks().slice().sort((a, b) => (CHECK_ORDER[a.state] ?? 9) - (CHECK_ORDER[b.state] ?? 9) || (b.required - a.required));
    if (!checks.length) return null;
    const failing = checks.filter((c) => c.state === "failure").length;
    const list = el("div", { class: "checks" });
    for (const c of checks) {
      const row = el("div", { class: "check st-" + c.state },
        el("span", { class: "pill " + (CHECK_TONE[c.state] || "") }, c.state),
        el("span", { class: "check-name" }, c.name), c.workflow ? el("span", { class: "check-workflow" }, c.workflow) : null,
        c.required ? el("span", { class: "chip" }, "required") : null, c.description ? el("span", { class: "check-workflow" }, c.description) : null,
        el("span", { class: "spacer" }), el("span", { class: "check-time" }, duration(c)),
        c.url ? el("a", { href: c.url, target: "_blank", rel: "noopener" }, "Details") : null);
      const box = el("div", { class: "check-box" }, row);
      if (c.state === "failure" && c.jobId && API) {
        const log = el("pre", { class: "check-log" });
        log.hidden = true;
        row.append(el("button", { class: "link", onclick: async (ev) => {
          if (!log.hidden) { log.hidden = true; ev.target.textContent = "Show log"; return; }
          log.hidden = false; ev.target.textContent = "Hide log";
          if (log.childElementCount || log.textContent) return;
          log.textContent = "Reading the log…";
          const r = await fetch(`${API}/pull/checks/${c.jobId}/log`).catch(() => null);
          const body = r ? await r.json().catch(() => ({})) : {};
          log.textContent = r && r.ok ? body.lines.join("\n") : body.message || "The log could not be read.";
          log.scrollTop = log.scrollHeight;
        } }, "Show log"));
        box.append(log);
      }
      const away = c.annotations.map((a, i) => ({ ...a, check: c, key: c.id + "/" + i })).filter((a) => !(PULL.notes[a.key] && PULL.notes[a.key].hunk !== null && PULL.notes[a.key].hunk !== undefined));
      if (away.length) box.append(el("div", { class: "check-notes" }, ...away.map((a) => annotationBox(a, { away: true }))));
      list.append(box);
    }
    return el("section", { class: "card" }, el("header", {}, el("b", {}, "Checks"), help(HELP.checks),
      el("span", { class: failing ? "verb bad" : "verb" }, failing ? `${failing} of ${checks.length} failing` : plural(checks.length, "check", "checks"))), list);
  }

  // ---------- Writing ----------

  // The reviewer comments on lines, replies to threads, resolves them, and submits a review, all through the server,
  // which writes to GitHub as the reviewer and reads the pull request again, so the page updates from the pull event
  // that follows. Unsent text survives a reload through sessionStorage, and nothing else keeps it. See
  // docs/pull-requests.md section 8.

  // Returns a value indicating whether the page can write to the pull request: it is served, attached to a pull
  // request, and the pull request is open. A closed or merged pull request is shown read-only.
  const canWrite = () => !!(API && PULL.snapshot && PULL.snapshot.state === "OPEN");
  const pendingReview = () => { const d = discussion(); return d ? d.pending : null; };

  // Sends JSON to a path of the server and returns whether it succeeded, with the answer's body.
  async function send(path, body, method = "POST") {
    const r = await fetch(path, { method, headers: { "Content-Type": "application/json" }, body: JSON.stringify(body || {}) }).catch(() => null);
    if (!r) return { ok: false, body: { message: "The vibecheck server could not be reached." } };
    return { ok: r.ok, body: await r.json().catch(() => ({})) };
  }

  // Draws what pull events brought while a comment box held the focus.
  function flushPull() {
    if (!PULL.dirty) return;
    PULL.dirty = false;
    redraw();
    renderConversation();
  }

  // Reads and writes the unsent text of a comment box, by a key that names the box.
  const draftKey = (key) => `vibecheck-draft:${META.review}:${key}`;
  const readDraft = (key) => { try { return sessionStorage.getItem(draftKey(key)) || ""; } catch (e) { return ""; } };
  const saveDraft = (key, text) => { try { if (text) sessionStorage.setItem(draftKey(key), text); else sessionStorage.removeItem(draftKey(key)); } catch (e) { /* storage unavailable */ } };

  // Returns a comment box: a text area that takes Markdown, a preview that GitHub renders, and buttons. Each action has
  // a label and a run function that takes the text and returns the server's answer. A successful action clears the
  // box's draft and calls done. The options are key, placeholder, suggestion, the text that Suggest change puts in a
  // suggestion block, extra, nodes shown beside the buttons, check, which returns a reason the text cannot be sent,
  // and onCancel.
  function composer(opts) {
    const area = el("textarea", { class: "composer-text", rows: 4, placeholder: opts.placeholder || "Leave a comment. Markdown is supported." });
    area.value = readDraft(opts.key);
    const preview = el("div", { class: "body composer-preview" });
    preview.hidden = true;
    const error = el("div", { class: "composer-error", role: "alert" });
    const write = el("button", { class: "on", onclick: () => show(false) }, "Write");
    const look = el("button", { onclick: () => show(true) }, "Preview");
    async function show(previewing) {
      write.className = previewing ? "" : "on"; look.className = previewing ? "on" : "";
      area.hidden = previewing; preview.hidden = !previewing;
      if (!previewing) { area.focus(); return; }
      preview.replaceChildren(el("span", { class: "hint" }, "Rendering…"));
      const r = await send("/api/markdown", { text: area.value });
      preview.replaceChildren(r.ok ? (area.value.trim() ? safeBody(r.body.html) : el("span", { class: "hint" }, "Nothing to preview.")) : el("span", { class: "composer-error" }, r.body.message || "The preview failed."));
    }
    const buttons = opts.actions.map((a) => el("button", { class: "btn" + (a.primary ? " primary" : ""), onclick: async (ev) => {
      const text = area.value;
      const why = opts.check ? opts.check(text, a) : !text.trim() ? "Write something first." : null;
      if (why) { error.textContent = why; return; }
      for (const b of buttons) b.disabled = true;
      error.textContent = "";
      // The server sends the pull event that shows the write before it answers, and the redraw that follows rebuilds
      // this box from its draft, so the draft goes first and comes back only when the write fails.
      saveDraft(opts.key, "");
      const r = await a.run(text);
      for (const b of buttons) b.disabled = false;
      if (!r.ok) { saveDraft(opts.key, text); error.textContent = r.body.message || "GitHub refused it."; return; }
      area.value = "";
      if (opts.done) opts.done();
      flushPull();
    } }, a.label));
    area.addEventListener("input", () => saveDraft(opts.key, area.value));
    // The page waits to redraw until the reviewer leaves the box, so that a pull event does not take the text away
    // mid-word. A box that closes is left without a blur event, so sending and cancelling draw what waited as well.
    area.addEventListener("blur", flushPull);
    const tools = el("div", { class: "composer-tools" }, el("span", { class: "seg" }, write, look));
    if (opts.suggestion !== undefined) tools.append(el("button", { class: "link", title: "Put the selected lines in a suggestion block, for you to edit", onclick: () => {
      area.value += (area.value && !area.value.endsWith("\n") ? "\n" : "") + "```suggestion\n" + opts.suggestion + "\n```\n";
      saveDraft(opts.key, area.value);
      show(false);
    } }, "Suggest change"));
    const row = el("div", { class: "composer-actions" }, ...(opts.extra || []), el("span", { class: "spacer" }), error,
      opts.onCancel ? el("button", { class: "link", onclick: () => { opts.onCancel(); flushPull(); } }, "Cancel") : null, ...buttons);
    const box = el("div", { class: "composer" }, tools, area, preview, row);
    requestAnimationFrame(() => { if (opts.focus && box.isConnected) area.focus(); });
    return box;
  }

  // Starts selecting lines to comment on from a line number pressed in a hunk's table. With Shift, it extends the
  // current selection at once. Otherwise the selection follows the pointer over the hunk's rows on the same side until
  // the button is released, and only then draws the comment box, so that the page is drawn once per selection.
  function startPick(ev, table, path, hunk, side, line) {
    if (ev.button !== 0) return;
    ev.preventDefault();
    if (ev.shiftKey) { pick(path, hunk, side, line, true); return; }
    const key = side === "LEFT" ? "l" : "r", at = side === "LEFT" ? 0 : 2;
    let last = line;
    const show = () => {
      const lo = Math.min(line, last), hi = Math.max(line, last);
      for (const tr of table.tBodies[0].rows) {
        if (tr.dataset[key] === undefined) continue;
        const on = +tr.dataset[key] >= lo && +tr.dataset[key] <= hi;
        tr.cells[at].classList.toggle("picked", on);
        tr.cells[at + 1].classList.toggle("picked", on);
      }
    };
    const over = (e) => {
      const tr = e.target.closest && e.target.closest("tr");
      if (tr && tr.parentElement === table.tBodies[0] && tr.dataset[key] !== undefined) { last = +tr.dataset[key]; show(); }
    };
    const up = () => {
      document.removeEventListener("mouseover", over);
      document.removeEventListener("mouseup", up);
      state.compose = { path, hunk, side, first: Math.min(line, last), last: Math.max(line, last) };
      redraw();
    };
    for (const td of document.querySelectorAll("table.diff td.picked")) td.classList.remove("picked");
    document.addEventListener("mouseover", over);
    document.addEventListener("mouseup", up);
    show();
  }

  // Selects a line of a diff to comment on, or extends the selection to it with Shift on the same side of the same
  // hunk, and draws the comment box under the last selected line.
  function pick(path, hunk, side, line, extend) {
    const c = state.compose;
    if (extend && c && c.path === path && c.hunk === hunk && c.side === side) state.compose = { path, hunk, side, first: Math.min(c.first, line), last: Math.max(c.last, line) };
    else state.compose = { path, hunk, side, first: line, last: line };
    redraw();
  }

  // Returns the box that comments on the selected lines: posted at once, or added to the pending review, which the
  // first such comment starts.
  function lineComposer(path, hunk) {
    const c = state.compose, lines = hunk.lines.slice(c.first, c.last + 1);
    const pending = pendingReview();
    const target = { path, hunk: c.hunk, first: c.first, last: c.last, side: c.side };
    const close = () => { state.compose = null; redraw(); };
    // The selection's lines on its side, which a range's first line number counts back from.
    const counted = lines.filter((l) => l[0] !== (c.side === "RIGHT" ? "-" : "+")).length;
    const label = c.first === c.last ? `Comment on line ${c.number}` : `Comment on lines ${c.number - counted + 1}–${c.number}`;
    return el("div", { class: "line-composer" }, el("div", { class: "composer-title" }, label + (c.side === "LEFT" ? " at base" : "")), composer({
      key: "line:" + path, focus: true, onCancel: close, done: close,
      suggestion: c.side === "RIGHT" ? lines.filter((l) => l[0] !== "-").map((l) => l[1]).join("\n") : undefined,
      actions: [
        { label: "Add single comment", run: (text) => send(`${API}/pull/comments`, { ...target, body: text }) },
        { label: pending ? "Add review comment" : "Start a review", primary: true, run: (text) => send(`${API}/pull/comments`, { ...target, body: text, review: true }) },
      ],
    }));
  }

  // Returns a thread's controls: a reply box behind a button, and Resolve or Unresolve. A reply joins the pending
  // review when there is one, as on GitHub.
  function threadControls(t) {
    const pending = pendingReview();
    const bar = el("div", { class: "thread-actions" });
    const resolve = el("button", { class: "btn", onclick: async () => {
      resolve.disabled = true;
      const r = await send(`${API}/pull/threads/${encodeURIComponent(t.id)}/resolved`, { resolved: !t.isResolved });
      resolve.disabled = false;
      if (!r.ok) bar.append(el("span", { class: "composer-error" }, r.body.message || "GitHub refused it."));
    } }, t.isResolved ? "Unresolve" : "Resolve conversation");
    // A box opened by the reviewer takes the focus. One restored from its draft by a redraw does not, since a focused
    // box holds back the redraws that pull events ask for.
    const open = (focus) => {
      bar.replaceChildren(composer({ key: "reply:" + t.id, placeholder: "Reply…", focus, onCancel: () => bar.replaceChildren(...idle()), done: () => bar.replaceChildren(...idle()),
        actions: [{ label: pending ? "Add reply to review" : "Reply", primary: true, run: (text) => send(`${API}/pull/threads/${encodeURIComponent(t.id)}/replies`, { body: text }) }] }));
    };
    const idle = () => [el("button", { class: "reply-open", onclick: () => open(true) }, "Reply…"), resolve];
    if (readDraft("reply:" + t.id)) open(false); else bar.append(...idle());
    return bar;
  }

  const EVENT_TEXT = { COMMENT: "Comment", APPROVE: "Approve", REQUEST_CHANGES: "Request changes" };

  // Shows or hides the review box under the bar: a summary, the choice of comment, approval or request for changes,
  // and the pending review's discard. GitHub refuses an approval or a request for changes from the author, and a
  // comment or request for changes with no summary and no comments, so the box says so before it sends.
  function toggleReviewBox() {
    const box = $("#reviewbox");
    if (!box.hidden) { box.hidden = true; return; }
    renderReviewBox();
    box.hidden = false;
  }
  function renderReviewBox() {
    const box = $("#reviewbox"), p = PULL.snapshot, pending = pendingReview();
    if (!p || !canWrite()) { box.hidden = true; return; }
    let choice = "COMMENT";
    const choices = el("div", { class: "review-choices" }, ...Object.keys(EVENT_TEXT).map((e) => {
      const blocked = e !== "COMMENT" && p.viewerDidAuthor;
      return el("label", { class: blocked ? "blocked" : "", title: blocked ? "GitHub does not let the author approve their own pull request or request changes to it." : "" },
        el("input", { type: "radio", name: "review-event", value: e, checked: e === "COMMENT", disabled: blocked, onchange: () => { choice = e; } }), " " + EVENT_TEXT[e]);
    }));
    const discard = pending ? el("button", { class: "link danger", onclick: () => {
      discard.replaceWith(el("span", { class: "confirm" }, `Discard ${plural(pending.comments, "pending comment", "pending comments")}?`,
        el("button", { class: "btn danger", onclick: async (ev) => {
          ev.target.disabled = true;
          const r = await send(`${API}/pull/review`, {}, "DELETE");
          if (r.ok) box.hidden = true; else { ev.target.disabled = false; ev.target.after(el("span", { class: "composer-error" }, r.body.message || "GitHub refused it.")); }
        } }, "Discard")));
    } }, "Discard review") : null;
    box.replaceChildren(
      el("div", { class: "review-head" }, el("b", {}, "Finish your review"), el("span", { class: "hint" }, pending ? `${plural(pending.comments, "pending comment", "pending comments")} will be submitted with it` : "No pending comments"),
        p.viewerDidAuthor ? el("span", { class: "hint" }, "· you opened this pull request, so you can only comment") : null),
      composer({ key: "review", placeholder: "Leave a summary", focus: true, extra: [choices, discard],
        check: (text) => choice !== "APPROVE" && !text.trim() && !(pending && pending.comments) ? `${EVENT_TEXT[choice]} needs a summary or comments.` : null,
        done: () => { box.hidden = true; },
        actions: [{ label: "Submit review", primary: true, run: (text) => send(`${API}/pull/review`, { event: choice, body: text }) }] }));
  }

  // Returns the box at the bottom of the conversation that adds a comment to it.
  function conversationComposer() {
    return el("section", { class: "card" }, el("header", {}, el("b", {}, "Add a comment")),
      el("div", { class: "card-threads" }, composer({ key: "conversation",
        actions: [{ label: "Comment", primary: true, run: (text) => send(`${API}/pull/comments`, { body: text }) }] })));
  }

  // ---------- Map notices ----------

  // Follows the review's event stream when the vibecheck server serves the page. A concept map that Claude is making, or
  // has made, shows as a notice under the top bar. Switching to it reruns the review on the server, and the page
  // reloads when the rerun is ready. Events up to META.events were sent before the page loaded, so a "ready" among
  // them is the one that opened this page; map events are always read, since a job may have started before it.
  function followReview() {
    const box = $("#notice");
    const api = `/api/reviews/${encodeURIComponent(META.review)}`;
    let switching = false, shown = null, dismissed = null;
    const spinner = () => el("span", { class: "spin", "aria-hidden": "true" });
    function show(kind, key, ...kids) {
      shown = key;
      if (key === dismissed) return;
      box.className = "notice " + kind;
      box.replaceChildren(...kids, el("button", { class: "close", title: "Dismiss", "aria-label": "Dismiss", onclick: () => { dismissed = shown; box.hidden = true; } }, "×"));
      box.hidden = false;
    }
    async function post(action) {
      const r = await fetch(`${api}/map`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ action }) });
      return { ok: r.ok, body: await r.json().catch(() => ({})) };
    }
    function switchMap() {
      switching = true;
      show("busy", "switch", spinner(), el("span", {}, "Switching to the concept map…"));
      post("switch");
    }
    async function save() {
      const r = await post("save");
      show(r.ok ? "info" : "error", "save", el("span", {}, r.body.message || "The map could not be saved."));
    }
    // Shows what the page has when no job has news: the generated map's version and the save action, or nothing.
    function settle() {
      if (META.mapKind !== "generated") { shown = null; box.hidden = true; return; }
      show("info", "generated", el("span", {}, `Drawn with the concept map Claude generated for this repository (version ${META.mapVersion}).`),
        el("button", { class: "btn", onclick: save }, "Save to repository"));
    }
    settle();
    const source = new EventSource(`${api}/events`);
    source.onmessage = (m) => {
      const e = JSON.parse(m.data);
      if (e.type === "narrative" || e.type === "narratives") { narrativeEvent(e); return; }
      const fresh = e.seq >= META.events;
      // The marks sent before the page loaded are already in META.reviewed.
      if (e.type === "reviewed") { if (fresh) applyMarks(e.marks); return; }
      // The snapshot and the places sent before the page loaded are already in META.
      if (e.type === "pull") {
        if (!fresh) return;
        if (e.stale) PULL.stale = e.message;
        else { PULL.snapshot = e.snapshot; PULL.places = e.threads || {}; PULL.notes = e.annotations || {}; PULL.stale = null; }
        renderPull();
        if (!$("#reviewbox").hidden) renderReviewBox();
        if (e.stale) return;
        if (document.activeElement && document.activeElement.matches(".composer-text")) PULL.dirty = true;
        else { renderConversation(); redraw(); }
        return;
      }
      if (e.type === "ready" && fresh) { source.close(); location.reload(); }
      else if (e.type === "stage" && fresh && switching) show("busy", "switch", spinner(), el("span", {}, `Switching to the concept map: ${e.message}`));
      else if (e.type === "map" && !switching) {
        if (e.state === "generating" || e.state === "placing") show("busy", "job", spinner(), el("span", {}, e.message));
        else if (e.state === "ready" && e.version > (META.mapVersion || 0)) show("ready", "ready" + e.version, el("span", {}, e.message), el("button", { class: "btn", onclick: switchMap }, "Switch to it"));
        else if (e.state === "ready" || e.state === "done") settle();
        else if (e.state === "failed" || e.state === "unavailable") show("error", e.state, el("span", {}, e.message));
      } else if (e.type === "map" && e.state === "failed") { switching = false; show("error", "failed", el("span", {}, e.message)); }
    };
  }

  // ---------- Reviewed marks ----------

  // Shows how many of the changeset's changed files are reviewed, with the test files counted apart so that a change
  // with many tests does not hide how much of the system's change is read.
  function renderProgress() {
    const files = CHANGED_FILES.filter((f) => !f.test), done = files.filter((f) => REVIEWED.has(f.path)).length;
    const tests = TEST_FILES.filter((f) => REVIEWED.has(f.path)).length;
    const steps = countedSteps();
    $("#progress").textContent = [files.length ? `${done} of ${files.length} files reviewed` : "",
      TEST_FILES.length ? `${tests} of ${plural(TEST_FILES.length, "test file", "test files")}` : "",
      steps.length ? `${steps.filter(stepRead).length} of ${plural(steps.length, "step", "steps")} read` : ""].filter(Boolean).join(" · ");
  }

  // Lays marks over the page's, where each maps a path to its reason, or to nothing when the file is not reviewed.
  function applyMarks(marks) {
    for (const [path, why] of Object.entries(marks || {})) { if (why) REVIEWED.set(path, why); else REVIEWED.delete(path); }
    renderProgress();
    redraw();
  }

  // Marks files reviewed or not. The page changes at once. The server, when there is one, stores the marks, answers
  // with the review's marks, which replace the page's, and tells the other pages that show the same changes.
  async function setReviewed(paths, reviewed) {
    const before = new Map(REVIEWED);
    applyMarks(Object.fromEntries(paths.map((p) => [p, reviewed ? REVIEWED.get(p) || "reviewer" : ""])));
    if (!API) return;
    const r = await fetch(`${API}/reviewed`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ paths, reviewed }) }).catch(() => null);
    const body = r && r.ok ? await r.json().catch(() => null) : null;
    REVIEWED.clear();
    for (const [path, why] of body ? Object.entries(body.marks || {}) : before) REVIEWED.set(path, why);
    renderProgress();
    if (!body) $("#progress").textContent += " · the last mark could not be saved";
    redraw();
  }

  // Draws the current location again once per frame, keeping the view, the selection and the leaf view's scroll.
  let redrawFrame = 0;
  function redraw() {
    if (redrawFrame) return;
    redrawFrame = requestAnimationFrame(async () => {
      redrawFrame = 0;
      const leafView = $("#leafview"), top = leafView.scrollTop, view = { ...state.view }, back = state.back;
      const m = await mount();
      if (m) setViewBox(view);
      state.back = back;
      leafView.scrollTop = top;
      renderDetails();
    });
  }

  // ---------- Boot ----------

  async function boot() {
    $("#pr").replaceChildren(el("span", {}, `${META.solution || MAP.solution} · `), el("code", {}, META.range || `${(CS.base || "").slice(0, 7)}..${(CS.head || "").slice(0, 7)}`), el("span", {}, META.title ? ` · ${META.title}` : ""));
    $("#pr").title = META.title || "";
    // Served by the vibecheck command, the page links back to the range picker and says where the map came from.
    if (META.home) $("#pr").prepend(el("a", { href: META.home, title: "Choose another range" }, "Ranges"), " · ");
    if (META.map) {
      // The tag follows the range rather than the title, so that a long title cannot push it out of view.
      $("#pr code").after(el("span", {}, META.mapKind === "skeleton" ? " · skeleton map" : " · map"));
      $("#pr").title = [META.title, "Map: " + META.map, ...(META.warnings || [])].filter(Boolean).join("\n");
    }
    renderSummary();
    renderPull();
    installTip();
    installTermTips();
    installHelpTips();
    if (META.review) followReview();
    $("#dim").addEventListener("change", (ev) => { state.opts.dim = ev.target.checked; document.body.classList.toggle("dim", state.opts.dim); });
    $("#mute").addEventListener("change", (ev) => { state.opts.mute = ev.target.checked; redraw(); });
    renderProgress();
    $("#flow").addEventListener("change", (ev) => document.body.classList.toggle("flow", ev.target.checked));
    installThemeChoice();
    document.body.classList.toggle("flow", $("#flow").checked);
    $("#externals").addEventListener("change", async (ev) => { rememberView(); state.opts.externals = ev.target.checked; const m = await mount(); if (m) setViewBox(arrive(m)); });
    $("#threshold").addEventListener("change", async (ev) => { rememberView(); state.opts.threshold = ev.target.value; const m = await mount(); if (m) setViewBox(arrive(m)); });
    $("#up").addEventListener("click", up);
    $("#top").addEventListener("click", toTop);
    $("#fit").addEventListener("click", zoomToFit);
    document.addEventListener("keydown", (ev) => {
      // Keys typed into a text field or a list belong to it. A checkbox or button does not take Escape or the arrows.
      if (ev.target.matches("input:not([type=checkbox]):not([type=radio]), select, textarea") || state.tab !== "map") return;
      if (ev.key === "Escape" || ev.key === "Backspace") { ev.preventDefault(); up(); }
      else if (ev.key === "Home") { ev.preventDefault(); toTop(); }
      else if (ev.key === "Enter" && state.selected && state.current) {
        const c = state.current.level.children.find((x) => x.key === state.selected);
        if (c) enter(c);
      } else if ((ev.key === "ArrowRight" || ev.key === "ArrowLeft") && state.current) {
        const keys = state.current.level.children.map((c) => c.key);
        const i = keys.indexOf(state.selected);
        select(i < 0 ? keys[0] : keys[(i + (ev.key === "ArrowRight" ? 1 : keys.length - 1)) % keys.length]);
      } else if (ev.key === "f") zoomToFit();
    });
    installPanZoom();
    // Lists the categories the map uses, in the specification's order.
    const used = new Set(Object.keys(NODES).map(categoryOf).filter(Boolean));
    const legend = $("#legend-categories");
    for (const c of Object.keys(CATEGORY).filter((c) => used.has(c))) {
      const box = document.createElementNS(SVGNS, "svg");
      box.setAttribute("viewBox", "0 0 16 16"); box.setAttribute("class", "legend-icon");
      box.append(categoryChip(c, 0, 0, 16));
      legend.append(el("span", {}, box, CATEGORY[c].label.toLowerCase()));
    }
    // Keeps the view's aspect equal to the stage's when the window resizes, holding the center and the horizontal scale.
    new ResizeObserver(() => {
      const a = stageAspect();
      if (!state.current || Math.abs(a - state.aspect) < 1e-3) return;
      const v = state.view, h = v.w / a;
      state.aspect = a;
      setViewBox({ x: v.x, y: v.y + v.h / 2 - h / 2, w: v.w, h });
    }).observe($("#stage"));
    // Without a location in the address, the page opens on the top level's first view, or on the tree when it has none.
    const hash = decodeURIComponent((location.hash || "").slice(1));
    const home = homeTrail().at(-1);
    // parseLoc reads an address it does not recognize as the top level, so only "root" itself asks for the components.
    const parsed = hash ? parseLoc(hash) : home;
    const start = parsed.level === ROOT && !parsed.view && hash !== ROOT ? home : parsed;
    state.trail = [{ level: ROOT }];
    // A change of tests alone opens on the Tests switch, whose badges the first drawing of the map already shows.
    if (TESTS_ONLY) state.panel = "tests";
    if (start.leaf) { for (const anc of ancestors(parentOf(start.leaf))) state.trail.push({ level: anc }); state.trail.push(start); }
    else { for (const anc of ancestors(start.level)) state.trail.push({ level: anc }); if (start.view) state.trail.push({ level: start.level, view: start.view }); if (start.group) state.trail.push(start); }
    const m = await mount();
    if (m) setViewBox(arrive(m));
  }

  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", boot);
  else boot();
})();
