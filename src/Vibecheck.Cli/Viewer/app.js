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
  const OTHER_FILES = CS.files.filter((f) => f.status !== "unchanged" && !f.leavesBase.length && !f.leavesHead.length);
  const otherGroupOf = (path) => OTHER + "/" + (path.includes("/") ? path.slice(0, path.indexOf("/")) : "");
  const OTHER_GROUPS = [...new Set(OTHER_FILES.map((f) => otherGroupOf(f.path)))].sort();
  const OTHER_LEAVES = OTHER_GROUPS.length > 1 ? OTHER_GROUPS : OTHER_FILES.length ? [OTHER] : [];
  const isOther = (id) => id === OTHER || id.startsWith(OTHER + "/");
  // Returns the Other files leaf that holds a path.
  const otherLeafOf = (path) => OTHER_LEAVES.length === 1 ? OTHER : otherGroupOf(path);
  const otherFilesOf = (id) => OTHER_FILES.filter((f) => id === OTHER || otherGroupOf(f.path) === id);

  const isLeaf = (id) => id === UNSORTED || OTHER_LEAVES.includes(id) || !!(NODES[id] && NODES[id].bind);
  const parentOf = (id) => id === ROOT ? null : id.startsWith(OTHER + "/") ? OTHER : id.lastIndexOf(".") < 0 ? ROOT : id.slice(0, id.lastIndexOf("."));
  const isUnder = (id, anc) => anc === ROOT || id === anc || id.startsWith(anc + (anc === OTHER ? "/" : "."));
  const shortName = (type) => type.replace(/^.*::/, "").split(".").pop();
  const namespaceOf = (type) => { const t = type.replace(/^.*::/, ""); const i = t.lastIndexOf("."); return i < 0 ? "" : t.slice(0, i); };

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

  // ---------- Reviewed files ----------
  // A changed file is reviewed when the reviewer has set it aside, or when the tool has and the reviewer has not
  // unmarked it. The server keeps the marks. Each maps a path to the reason the file is reviewed: "reviewer", "trivial"
  // for a file whose edits touch no code, or "renamed" for a file renamed without changes. A reviewed file is set aside
  // wherever it appears while reviewed files are muted. See docs/pull-requests.md section 5.
  const REVIEWED = new Map(Object.entries(META.reviewed || {}));
  const REASON_TEXT = { trivial: "no code changed", renamed: "renamed without changes" };
  const CHANGED_FILES = CS.files.filter((f) => f.status !== "unchanged");

  // Returns the leaves a changed file counts under, as the changeset's content counts it: its leaves at head, its
  // leaves at base when it was removed, and its leaves at both ends when it was renamed. A file that no type binds
  // counts under its Other files leaf.
  function countedLeaves(f) {
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
  const FILES_BY_NODE = filesByNode(CHANGED_FILES);

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

  // The advance of one character of the monospaced text in a block: 0.6 em, plus any letter spacing.
  const MONO = { meta: 10.5 * 0.6, title: 12 * 0.6 + 12 * 0.08, badge: 9 * 0.6 + 9 * 0.08 };
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
    root.append(text(f.x, f.y - 34, locName(level.loc).toUpperCase(), { class: "frame-title" }));
    root.append(text(f.x + f.w, f.y - 34, `${level.children.length} blocks · ${level.edges.length} edges, ${changedText(level.edges)} · ${level.cycles.length} cycle${level.cycles.length === 1 ? "" : "s"}`, { class: "frame-meta", "text-anchor": "end" }));

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

    // Blocks.
    const nodeLayer = svg("g", { class: "nodes" });
    for (const c of level.children) {
      const r = rectOf(level, layout, c.key);
      const st = statusOf(c.key);
      const leaf = c.kind === "unsorted" || (c.kind === "node" && isLeaf(c.id));
      const cat = categoryOf(c.id);
      const cls = "node" + statusClass(st) + familyClass(c.id) + (c.kind === "unsorted" ? " unsorted" : "") + (isOther(c.id) ? " other" : "") + (leaf ? " leaf" : " container") +
        (level.inCycle.has(c.key) ? " in-cycle" : "") + (c.key === state.selected ? " selected" : "");
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
      // The badge on the top edge says what happened, with the count of changed files when there is one.
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
      let left = r.x + 8;
      if (talk) {
        const tw = String(talk).length * MONO.badge + 26, tx = r.x + 8;
        const tg = svg("g", { class: "talk-badge" });
        tg.append(svg("rect", { x: tx, y: r.y - 8, width: tw, height: 15, rx: 7.5, class: "talk-bg" }));
        tg.append(svg("path", { d: `M${tx + 7},${r.y - 4} h9 v6 h-5 l-2.5,2.5 v-2.5 h-1.5 z`, class: "talk-icon" }));
        tg.append(text(tx + 19, r.y + 3, String(talk), { class: "talk-text" }));
        tg.append(svg("title", {}, `${plural(talk, "unresolved review thread", "unresolved review threads")} on files under ${nameOf(c.id)}`));
        g.append(tg);
        left = tx + tw + 6;
      }
      if (problems) {
        const label = "✕ " + problems, pw = label.length * MONO.badge + 12;
        const pg = svg("g", { class: "problem-badge" });
        pg.append(svg("rect", { x: left, y: r.y - 8, width: pw, height: 15, rx: 7.5, class: "problem-bg" }));
        pg.append(text(left + pw / 2, r.y + 3, label, { class: "problem-text", "text-anchor": "middle" }));
        pg.append(svg("title", {}, `${plural(problems, "check failure or warning", "check failures and warnings")} on files under ${nameOf(c.id)}`));
        g.append(pg);
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
    if (!html) { td.append(line.slice(lead.length)); return td; }
    td.innerHTML = html;
    let strip = lead.length;
    const walker = document.createTreeWalker(td, NodeFilter.SHOW_TEXT);
    while (strip > 0 && walker.nextNode()) {
      const node = walker.currentNode, n = Math.min(strip, node.data.length);
      node.data = node.data.slice(n);
      strip -= n;
    }
    return td;
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
    const table = el("table", { class: "diff" + (trivial ? " trivial" : "") }, el("caption", {}, hunk.header + (trivial ? " · no code changed" : "")),
      el("colgroup", {}, ln(), el("col", {}), ln(), el("col", {})));
    const body = el("tbody");
    const lit = highlightHunk(path, hunk.lines);
    const text = (i) => i === null ? null : hunk.lines[i][1];
    // While the pull request takes comments, a line number selects its line to comment on, and Shift extends the selection.
    const writable = canWrite() && index !== undefined;
    const c = state.compose && state.compose.path === path && state.compose.hunk === index ? state.compose : null;
    const picked = (side, i) => c && c.side === side && i !== null && i >= c.first && i <= c.last;
    const number = (side, i, n, kind) => {
      const td = el("td", { class: "ln " + kind + (writable && i !== null ? " pick" : "") + (picked(side, i) ? " picked" : "") }, i === null ? "" : String(n));
      if (writable && i !== null) {
        td.title = "Comment on this line. Shift-click to select a range.";
        td.addEventListener("click", (ev) => pick(path, index, side, i, ev.shiftKey));
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
      body.append(el("tr", {}, number("LEFT", row.l, la, lk), left, number("RIGHT", row.r, lb, rk), right));
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

  // Puts reviewed items after the rest, keeping the order within each part.
  const reviewedLast = (items, isReviewed) => items.filter((x) => !isReviewed(x)).concat(items.filter(isReviewed));

  // Returns a file's header and diffs for the leaf view. A changed file's header carries its reviewed checkbox, and a
  // reviewed file's diffs stay collapsed while reviewed files are muted, until the reader shows them. A renamed file
  // names its path at base, and a path at base that a type still lists links to the renamed file.
  function fileBlock(path) {
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
    head.append(el("span", { class: "chip chip-" + fs }, fs), el("span", { class: "file-path" }, path));
    if (f && f.previousPath) head.append(el("span", { class: "file-note" }, "renamed from " + f.previousPath));
    if (done && REASON_TEXT[REVIEWED.get(path)]) head.append(el("span", { class: "file-note" }, REASON_TEXT[REVIEWED.get(path)]));
    const talk = threadsOf(path), notes = annotationsOf(path);
    const problems = [...notes.lines.values()].flat().concat(notes.top).filter(isProblem).length;
    if (problems) head.append(el("span", { class: "file-note problem-note" }, plural(problems, "check problem", "check problems")));
    const open = [...talk.lines.values()].flat().concat(talk.top).filter((t) => !t.isResolved).length;
    if (open) head.append(el("span", { class: "file-note talk-note" }, plural(open, "unresolved thread", "unresolved threads")));
    const muted = done && state.opts.mute;
    const block = el("div", { class: "file file-" + fs, "data-file": path }, head);
    const diffs = el("div", { class: "file-diffs" });
    const fill = () => { if (!diffs.childElementCount) hunks.forEach((h, i) => diffs.append(diffTable(path, h, i, talk.lines, notes.lines))); };
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

  // Returns the line that says how many of a node's changed files are reviewed, with a button that marks them all, or
  // unmarks them all once they all are. It is null for a node without changed files.
  function reviewedBar(key) {
    const r = reviewedCount(key);
    if (!r.changed) return null;
    const all = r.reviewed === r.changed;
    return el("div", { class: "reviewed-bar" }, el("span", {}, `${r.reviewed} of ${plural(r.changed, "changed file", "changed files")} reviewed`),
      el("button", { class: "btn", onclick: () => setReviewed(r.paths, !all) }, all ? "Unmark all" : "Mark all reviewed"));
  }

  function renderLeaf(id) {
    const wrap = el("div", { class: "leaf" });
    const m = metricsOf(id), content = contentOf(id);
    wrap.append(el("div", {},
      el("div", { class: "leaf-title" }, nameOf(id).toUpperCase()),
      el("div", { class: "leaf-sub" }, purposeOf(id))));
    if (isOther(id)) {
      // Files that no type binds have no types and no edges, so the leaf lists the files themselves.
      const files = reviewedLast(otherFilesOf(id).map((f) => f.path), (p) => fileStatus(p) === "reviewed");
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

    // Changed types with their files and hunks, the reviewed ones last.
    const types = typesOf(id);
    const touched = reviewedLast(types.filter(typeTouched), (t) => typeStatus(t) === "reviewed");
    wrap.append(el("h3", {}, touched.length ? `${touched.length} of ${types.length} types changed` : `No changes among ${types.length} types`));
    for (const t of touched) {
      const ts = typeStatus(t);
      const card = el("section", { class: "typecard" + statusClass(ts) });
      const head = el("header", {},
        el("span", { class: "chip chip-" + ts }, ts),
        el("span", { class: "typename" }, shortName(t.name)),
        el("span", { class: "typens" }, namespaceOf(t.name)));
      if (t.status === "moved") head.append(el("span", { class: "typens" }, `from ${t.leafBase} to ${t.leafHead}`));
      card.append(head);
      for (const path of reviewedLast(t.files, (p) => fileStatus(p) === "reviewed")) card.append(fileBlock(path));
      wrap.append(card);
    }

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

  function renderChanges() {
    const box = $("#changes");
    box.replaceChildren();
    const talk = openThreadsUnder(ROOT).length;
    if (talk || state.opts.talk) box.append(el("label", { class: "toggle talk-filter", title: "Keep only the blocks whose changed files have unresolved review threads" },
      el("input", { type: "checkbox", checked: state.opts.talk, onchange: (ev) => { state.opts.talk = ev.target.checked; renderChanges(); } }), ` Only unresolved threads (${talk})`));
    const tree = changeTree(ROOT);
    box.append(tree ? el("ul", { class: "tree" }, tree) : el("p", { class: "hint" }, "No file changed under any leaf."));

    box.append(el("h3", {}, `Edges: ${changedText(CHANGED_EDGES)}`));
    const ul = el("ul", { class: "list scroll" });
    const rank = (e) => (e.status === "added" || e.status === "removed" ? 1e9 : 0) + (isMinor(e) ? 0 : 1e6) + Math.abs(e.head - e.base);
    for (const e of CHANGED_EDGES.slice().sort((a, b) => rank(b) - rank(a))) {
      ul.append(el("li", {}, el("button", { class: "row st-" + e.status, onclick: () => showEdge(e), title: `${e.from} → ${e.to}` },
        el("span", { class: "dot" + (isMinor(e) ? " minor" : "") }), el("span", { class: "row-edge" }, `${nameOf(e.from)} → ${nameOf(e.to)}`),
        el("span", { class: "row-meta" }, e.status === "added" ? `+${e.head} new` : e.status === "removed" ? `−${e.base} gone` : `${e.base}→${e.head}`))));
    }
    box.append(ul);

    box.append(el("h3", {}, "Map"));
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
    const l = loc();
    const cur = state.current;
    if (l.leaf) {
      box.append(el("h2", {}, nameOf(l.leaf)), el("p", { class: "ident" }, l.leaf), el("p", { class: "purpose" }, purposeOf(l.leaf)));
      if (contentOf(l.leaf).status !== "unchanged") box.append(narrativeBlock(NARR.items.get("node:" + l.leaf), "node:" + l.leaf) || "");
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
    const leaf = f.leavesHead[0] || f.leavesBase[0] || otherLeafOf(f.path);
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
  function installThemeChoice() {
    const read = () => { try { return localStorage.getItem("vibecheck-theme") || "auto"; } catch (e) { return "auto"; } };
    const apply = (choice) => {
      if (choice === "auto") delete document.documentElement.dataset.theme;
      else document.documentElement.dataset.theme = choice;
      for (const b of $("#theme").querySelectorAll("button")) b.classList.toggle("on", b.dataset.themeChoice === choice);
    };
    for (const b of $("#theme").querySelectorAll("button")) {
      b.addEventListener("click", () => {
        const choice = b.dataset.themeChoice;
        try { if (choice === "auto") localStorage.removeItem("vibecheck-theme"); else localStorage.setItem("vibecheck-theme", choice); } catch (e) { /* storage unavailable */ }
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
    const box = el("section", { class: "narrative" }, el("h3", {}, "Why it changed"));
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
      if (tipKey) showTip(tipKey);
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
    return el("div", {}, el("h3", {}, plural(open.length, "unresolved thread", "unresolved threads")), ul);
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
    return el("div", {}, el("h3", {}, plural(problems.length, "check problem", "check problems")), ul);
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
    return el("section", { class: "card" }, el("header", {}, el("b", {}, "Checks"),
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

  // Shows how many of the changeset's changed files are reviewed.
  function renderProgress() {
    const done = CHANGED_FILES.filter((f) => REVIEWED.has(f.path)).length;
    $("#progress").textContent = CHANGED_FILES.length ? `${done} of ${CHANGED_FILES.length} files reviewed` : "";
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
    $("#fit").addEventListener("click", zoomToFit);
    document.addEventListener("keydown", (ev) => {
      // Keys typed into a text field or a list belong to it. A checkbox or button does not take Escape or the arrows.
      if (ev.target.matches("input:not([type=checkbox]):not([type=radio]), select, textarea") || state.tab !== "map") return;
      if (ev.key === "Escape" || ev.key === "Backspace") { ev.preventDefault(); up(); }
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
    const rootView = viewsAt(ROOT)[0];
    const home = rootView ? { level: ROOT, view: rootView } : { level: ROOT };
    // parseLoc reads an address it does not recognize as the top level, so only "root" itself asks for the components.
    const parsed = hash ? parseLoc(hash) : home;
    const start = parsed.level === ROOT && !parsed.view && hash !== ROOT ? home : parsed;
    state.trail = [{ level: ROOT }];
    if (start.leaf) { for (const anc of ancestors(parentOf(start.leaf))) state.trail.push({ level: anc }); state.trail.push(start); }
    else { for (const anc of ancestors(start.level)) state.trail.push({ level: anc }); if (start.view) state.trail.push({ level: start.level, view: start.view }); if (start.group) state.trail.push(start); }
    const m = await mount();
    if (m) setViewBox(arrive(m));
  }

  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", boot);
  else boot();
})();
