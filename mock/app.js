// Viewer mock over a real changeset. Every level is drawn from the leaf-pair edges in
// the changeset, aggregated to the children of the level, with ELK laying the blocks
// out and routing the traces. Edges that leave the level end at pins on the frame.
// A view regroups the leaves under a level; a leaf opens its types, files and diffs.

(function () {
  "use strict";

  const CS = window.CHANGESET;
  const META = window.CHANGESET_META || {};
  const MAP = CS.map;
  const SVGNS = "http://www.w3.org/2000/svg";
  const reduceMotion = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
  const ROOT = "root", UNSORTED = "unsorted";
  const BLOCK = { w: 190, h: 104 }, PIN_H = 18;
  const pinWidth = (name) => Math.min(name.length, 22) * 6.6 + 16;
  const elk = new ELK();

  // ---------- Model ----------

  const NODES = MAP.nodes, EXTERNALS = MAP.externals, VIEWS = MAP.views;
  const isExternal = (id) => !!EXTERNALS[id];
  const isLeaf = (id) => id === UNSORTED || !!(NODES[id] && NODES[id].bind);
  const parentOf = (id) => id === ROOT ? null : id.lastIndexOf(".") < 0 ? ROOT : id.slice(0, id.lastIndexOf("."));
  const isUnder = (id, anc) => anc === ROOT || id === anc || id.startsWith(anc + ".");
  const shortName = (type) => type.replace(/^.*::/, "").split(".").pop();
  const namespaceOf = (type) => { const t = type.replace(/^.*::/, ""); const i = t.lastIndexOf("."); return i < 0 ? "" : t.slice(0, i); };

  const hasUnsorted = CS.types.some((t) => t.leafHead === UNSORTED || t.leafBase === UNSORTED);
  const CHILDREN = {};
  for (const id of Object.keys(NODES)) (CHILDREN[parentOf(id)] = CHILDREN[parentOf(id)] || []).push(id);
  if (hasUnsorted) (CHILDREN[ROOT] = CHILDREN[ROOT] || []).push(UNSORTED);
  for (const k of Object.keys(CHILDREN)) CHILDREN[k].sort();
  const childrenOf = (id) => CHILDREN[id] || [];
  const ALL_LEAVES = Object.keys(NODES).filter(isLeaf).concat(hasUnsorted ? [UNSORTED] : []);
  const leavesUnder = (id) => isLeaf(id) ? [id] : ALL_LEAVES.filter((l) => l !== UNSORTED && isUnder(l, id)).concat(id === ROOT && hasUnsorted ? [UNSORTED] : []);

  function nameOf(id) {
    if (id === ROOT) return META.solution || MAP.solution;
    if (id === UNSORTED) return "Unsorted";
    if (NODES[id]) return NODES[id].name;
    if (EXTERNALS[id]) return EXTERNALS[id].name;
    if (id.startsWith("g:")) { const [v, g] = id.slice(2).split("/"); return VIEWS[v].groups[g].name; }
    return id;
  }
  function purposeOf(id) {
    if (id === ROOT) return META.title ? `Changeset ${META.range || ""}: ${META.title}` : "";
    if (id === UNSORTED) return "Types that no leaf binds. The map should be extended to place them.";
    if (NODES[id]) return NODES[id].purpose || "";
    if (EXTERNALS[id]) return EXTERNALS[id].purpose || "External assembly.";
    if (id.startsWith("g:")) { const [v, g] = id.slice(2).split("/"); return VIEWS[v].groups[g].purpose || ""; }
    return "";
  }

  // Indexes over the changeset.
  const FILES = new Map(CS.files.map((f) => [f.path, f]));
  const HUNKS = new Map(CS.hunks.map((h) => [h.path, h.hunks]));
  const TYPES_BY_LEAF = new Map();
  for (const t of CS.types) {
    const leaf = t.leafHead || t.leafBase;
    if (!TYPES_BY_LEAF.has(leaf)) TYPES_BY_LEAF.set(leaf, []);
    TYPES_BY_LEAF.get(leaf).push(t);
  }
  const typesOf = (leaf) => TYPES_BY_LEAF.get(leaf) || [];
  const typeTouched = (t) => t.status !== "unchanged" || t.files.some((p) => (FILES.get(p) || {}).status !== "unchanged");
  const CHANGED_EDGES = CS.edges.filter((e) => e.status !== "unchanged");
  const CONTENT = CS.content, METRICS = CS.metrics;

  function contentOf(id) { return CONTENT[id] || { files: 0, added: 0, removed: 0, modified: 0, status: "unchanged" }; }
  function statusOf(key) {
    if (key.startsWith("g:")) {
      const leaves = groupLeaves(key);
      return leaves.some((l) => contentOf(l).status !== "unchanged") ? "children" : "unchanged";
    }
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
    if (!(level === ROOT || (NODES[level] && !isLeaf(level)))) return { level: ROOT };
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

  // The children a location draws, each with the leaves it stands for.
  function drawnChildren(loc) {
    if (loc.group) return VIEWS[loc.view].groups[loc.group].leaves.map((l) => ({ key: l, id: l, kind: "node", leaves: [l] }));
    if (loc.view) {
      const v = VIEWS[loc.view];
      return Object.keys(v.groups).map((g) => ({ key: `g:${loc.view}/${g}`, id: `g:${loc.view}/${g}`, kind: "group", leaves: v.groups[g].leaves }));
    }
    return childrenOf(loc.level).map((c) => ({ key: c, id: c, kind: c === UNSORTED ? "unsorted" : "node", leaves: leavesUnder(c) }));
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

  async function layoutLevel(level, opts) {
    const key = layoutKey(level.loc, opts);
    if (layoutCache.has(key)) return layoutCache.get(key);
    const graph = {
      id: "g",
      layoutOptions: {
        "elk.algorithm": "layered",
        "elk.direction": "DOWN",
        "elk.edgeRouting": "ORTHOGONAL",
        "elk.spacing.nodeNode": "30",
        "elk.layered.spacing.nodeNodeBetweenLayers": "72",
        "elk.layered.spacing.edgeNodeBetweenLayers": "28",
        "elk.spacing.edgeNode": "22",
        "elk.spacing.edgeEdge": "12",
        "elk.layered.nodePlacement.strategy": "NETWORK_SIMPLEX",
        "elk.layered.crossingMinimization.thoroughness": "40",
        "elk.layered.mergeEdges": "true",
        "elk.padding": "[top=24,left=24,bottom=24,right=24]",
      },
      children: [
        ...level.children.map((c) => ({ id: c.key, width: BLOCK.w, height: BLOCK.h })),
        ...level.pins.map((p) => ({ id: p.key, width: pinWidth(p.name), height: PIN_H, layoutOptions: { "elk.layered.layering.layerConstraint": p.dir === "in" ? "FIRST" : "LAST" } })),
      ],
      edges: level.edges.map((e, i) => ({ id: "e" + i, sources: [e.from], targets: [e.to] })),
    };
    const res = await elk.layout(graph);
    const rects = new Map();
    for (const n of res.children) rects.set(n.id, { x: n.x, y: n.y, w: n.width, h: n.height });
    const routes = new Map();
    for (const e of res.edges || []) {
      const s = e.sections && e.sections[0];
      if (!s) continue;
      routes.set(e.id, [s.startPoint, ...(s.bendPoints || []), s.endPoint].map((p) => ({ x: p.x, y: p.y })));
    }
    const out = { rects, routes, w: res.width, h: res.height };
    layoutCache.set(key, out);
    return out;
  }

  // Frame geometry around the laid-out graph: room above for the title and the incoming tabs, below for the outgoing tabs.
  const FRAME_PAD = { left: 30, right: 30, top: 64, bottom: 40 };

  function frameOf(layout) {
    return { x: FRAME_PAD.left, y: FRAME_PAD.top, w: layout.w, h: layout.h,
      total: { w: FRAME_PAD.left + layout.w + FRAME_PAD.right, h: FRAME_PAD.top + layout.h + FRAME_PAD.bottom } };
  }

  // Returns the drawn rect of a child or pin. Pins are tabs that straddle the frame line.
  function rectOf(level, layout, key) {
    const f = frameOf(layout);
    const r = layout.rects.get(key);
    if (!r) return null;
    if (key.startsWith("pin:in:")) return { x: f.x + r.x, y: f.y - PIN_H / 2, w: r.w, h: PIN_H };
    if (key.startsWith("pin:out:")) return { x: f.x + r.x, y: f.y + f.h - PIN_H / 2, w: r.w, h: PIN_H };
    return { x: f.x + r.x, y: f.y + r.y, w: r.w, h: r.h };
  }

  function routeOf(level, layout, i, edge) {
    const f = frameOf(layout);
    const pts = (layout.routes.get("e" + i) || []).map((p) => ({ x: f.x + p.x, y: f.y + p.y }));
    if (!pts.length) return pts;
    if (edge.from.startsWith("pin:in:")) pts.unshift({ x: pts[0].x, y: f.y });
    if (edge.to.startsWith("pin:out:")) pts.push({ x: pts[pts.length - 1].x, y: f.y + f.h });
    return pts;
  }

  // The stage takes the aspect of the level it shows, within limits, so a deep stack is tall and a flat level is wide.
  function aspectOf(layout) {
    const f = frameOf(layout);
    return Math.min(2, Math.max(0.6, f.total.w / f.total.h));
  }

  function fitRect(r, pad) {
    const aspect = state.aspect;
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
  const truncate = (s, n) => s.length > n ? s.slice(0, n - 1) + "…" : s;
  const fmtDelta = (b, h) => h === b ? "" : (h > b ? "+" : "−") + Math.abs(h - b);
  const statusClass = (st) => st === "unchanged" ? "" : " st-" + st;
  const strokeWidth = (n) => (1 + Math.log2(1 + n) / 2.2).toFixed(2);

  // ---------- State ----------

  const state = {
    trail: [{ level: ROOT }],
    selected: null,
    opts: { threshold: "1", externals: false, dim: true },
    current: null, // { level, layout }
    view: { x: 0, y: 0, w: 1600, h: 1000 },
    aspect: 1.6,
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

  function previewInto(g, child, box) {
    const inner = { x: box.x + 10, y: box.y + 60, w: box.w - 20, h: box.h - 68 };
    if (inner.h < 10) return;
    if (child.kind === "node" && isLeaf(child.id) || child.kind === "unsorted") {
      // One bar per type, colored by what happened to it.
      const types = typesOf(child.id).slice().sort((a, b) => (typeTouched(b) ? 1 : 0) - (typeTouched(a) ? 1 : 0));
      const shown = Math.min(types.length, 12);
      const cols = types.length > 6 ? 2 : 1;
      const rows = Math.ceil(shown / cols);
      const gap = Math.min(5, inner.h / Math.max(rows, 1));
      for (let i = 0; i < shown; i++) {
        const t = types[i];
        const cls = "mini-type" + (t.status === "added" ? " mini-type-added" : t.status === "removed" ? " mini-type-removed" : typeTouched(t) ? " mini-type-changed" : "");
        const col = i % cols, row = Math.floor(i / cols);
        const cw = (inner.w - 6 * (cols - 1)) / cols;
        g.append(svg("rect", { x: inner.x + col * (cw + 6), y: inner.y + row * gap, width: Math.max(8, cw * (0.55 + ((i * 7) % 5) / 10)), height: 2, class: cls }));
      }
      return;
    }
    // A container or group: its own tree layout as unlabeled miniatures, or a grid of its leaves.
    const sub = child.kind === "group" ? null : layoutCache.get(layoutKey({ level: child.id }, state.opts));
    if (sub && sub.rects.size) {
      const rs = [...sub.rects.entries()].filter(([k]) => !k.startsWith("pin:")).map(([k, r]) => ({ k, r }));
      const bb = rs.reduce((b, { r }) => ({ x0: Math.min(b.x0, r.x), y0: Math.min(b.y0, r.y), x1: Math.max(b.x1, r.x + r.w), y1: Math.max(b.y1, r.y + r.h) }), { x0: Infinity, y0: Infinity, x1: -Infinity, y1: -Infinity });
      const sc = Math.min(inner.w / (bb.x1 - bb.x0), inner.h / (bb.y1 - bb.y0));
      const ox = inner.x + (inner.w - (bb.x1 - bb.x0) * sc) / 2, oy = inner.y + (inner.h - (bb.y1 - bb.y0) * sc) / 2;
      for (const { k, r } of rs) {
        g.append(svg("rect", { x: ox + (r.x - bb.x0) * sc, y: oy + (r.y - bb.y0) * sc, width: Math.max(2, r.w * sc), height: Math.max(2, r.h * sc), class: "mini" + statusClass(statusOf(k)), rx: 1 }));
      }
      return;
    }
    const leaves = child.leaves;
    const cols = Math.min(leaves.length, Math.max(3, Math.ceil(Math.sqrt(leaves.length * 2.5))));
    const cw = (inner.w - 4 * (cols - 1)) / cols, ch = Math.min(10, (inner.h - 4 * 2) / Math.max(1, Math.ceil(leaves.length / cols)));
    leaves.forEach((l, i) => {
      const col = i % cols, row = Math.floor(i / cols);
      g.append(svg("rect", { x: inner.x + col * (cw + 4), y: inner.y + row * (ch + 4), width: cw, height: ch, class: "mini" + statusClass(contentOf(l).status), rx: 1 }));
    });
  }

  function cohesionOf(key) {
    const m = metricsOf(key);
    return { base: m.base ? m.base.cohesion : null, head: m.head ? m.head.cohesion : null };
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
      const pts = routeOf(level, layout, i, e);
      if (pts.length < 2) return;
      const d = "M" + pts.map((p) => `${p.x.toFixed(1)},${p.y.toFixed(1)}`).join(" L");
      const external = e.to.startsWith("pin:out:") && isExternal(e.to.slice(8));
      const sel = e.key === state.selected;
      const g = svg("g", { class: "edge" + statusClass(e.status) + (isMinor(e) ? " st-minor" : "") + (external ? " external" : "") + (sel ? " selected" : ""), "data-key": e.key,
        onclick: (ev) => clickSelect(ev, e.key) });
      g.append(svg("title", {}, `${nameOf(stripPin(e.from))} → ${nameOf(stripPin(e.to))}: ${e.base === e.head ? e.head : e.base + " → " + e.head} references`));
      g.append(svg("path", { d, class: "edge-hit" }));
      g.append(svg("path", { d, class: "edge-line", "stroke-width": strokeWidth(e.status === "removed" ? e.base : e.head), "marker-end": `url(#arrow-${sel ? "selected" : e.status})`, "data-marker": e.status }));
      edgeLayer.append(g);
      if (e.status !== "unchanged" && !isMinor(e)) {
        const mid = labelPoint(pts);
        const parts = e.status === "added" ? [{ t: "+" + e.head, cls: "lbl-delta" }, { t: "new", cls: "lbl-count" }]
          : e.status === "removed" ? [{ t: "−" + e.base, cls: "lbl-delta" }, { t: "gone", cls: "lbl-count" }]
          : [{ t: String(e.head), cls: "lbl-count" }, { t: fmtDelta(e.base, e.head) || "±0", cls: "lbl-delta" }];
        const str = parts.map((p) => p.t).join(" ");
        const w = str.length * 6.6 + 12;
        const lg = svg("g", { class: "edge-label" + statusClass(e.status), onclick: (ev) => clickSelect(ev, e.key) });
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
      const cls = "node" + statusClass(st) + (c.kind === "unsorted" ? " unsorted" : "") + (leaf ? " leaf" : " container") +
        (level.inCycle.has(c.key) ? " in-cycle" : "") + (c.key === state.selected ? " selected" : "");
      const g = svg("g", { class: cls, "data-key": c.key, tabindex: 0,
        onclick: (ev) => clickSelect(ev, c.key),
        ondblclick: (ev) => { ev.stopPropagation(); enter(c); },
        onkeydown: (ev) => { if (ev.key === "Enter") { ev.stopPropagation(); enter(c); } } });
      g.append(svg("rect", { x: r.x, y: r.y, width: r.w, height: r.h, rx: 2, class: "box" }));
      g.append(svg("rect", { x: r.x, y: r.y, width: 4, height: r.h, class: "stripe" }));
      g.append(text(r.x + 12, r.y + 20, truncate(nameOf(c.id).toUpperCase(), 20), { class: "title" }));
      const m = metricsOf(c.key), content = c.kind === "group" ? groupContent(c) : contentOf(c.key);
      const types = m.head ? m.head.types : c.leaves.reduce((s, l) => s + ((METRICS[l] || {}).head || {}).types || 0, 0);
      const changedFiles = content.added + content.removed + content.modified;
      let meta = `${types} types · ${content.files} files`;
      if (changedFiles) meta += ` · ${changedFiles} changed`;
      g.append(text(r.x + 12, r.y + 35, meta, { class: "meta" }));
      const coh = cohesionOf(c.key);
      if (coh.head !== null) {
        g.append(svg("rect", { x: r.x + 12, y: r.y + 44, width: 70, height: 4, class: "cohesion-track", rx: 1 }));
        g.append(svg("rect", { x: r.x + 12, y: r.y + 44, width: Math.max(1, 70 * coh.head), height: 4, class: "cohesion-fill", rx: 1 }));
        const delta = coh.base !== null && coh.base !== coh.head ? ` (${coh.base.toFixed(2)} → ${coh.head.toFixed(2)})` : "";
        g.append(text(r.x + 88, r.y + 49, `cohesion ${coh.head.toFixed(2)}${delta}`, { class: "meta" }));
      }
      g.append(svg("circle", { cx: r.x + r.w - 28, cy: r.y + 16, r: 3, class: "cycle-mark" }));
      const glyph = text(r.x + r.w - 10, r.y + 20, leaf ? "≡" : "⤵", { class: "enter-glyph", "text-anchor": "end",
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
      const g = svg("g", { class: "pin" + (p.changed ? " changed" : "") + (p.kind === "external" ? " external" : "") + (p.key === state.selected ? " selected" : ""), "data-key": p.key,
        onclick: (ev) => clickSelect(ev, p.key) });
      g.append(svg("rect", { x: r.x, y: r.y, width: r.w, height: r.h, rx: 2, class: "pin-box" }));
      g.append(text(r.x + r.w / 2, r.y + r.h / 2 + 3.5, truncate(p.name, 22), { class: "pin-label", "text-anchor": "middle" }));
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
  function pairLines(lines) {
    const rows = [];
    let i = 0;
    while (i < lines.length) {
      if (lines[i][0] === " ") { rows.push({ l: lines[i][1], r: lines[i][1], kind: "ctx" }); i++; continue; }
      const dels = [], adds = [];
      while (i < lines.length && lines[i][0] === "-") dels.push(lines[i++][1]);
      while (i < lines.length && lines[i][0] === "+") adds.push(lines[i++][1]);
      const n = Math.max(dels.length, adds.length);
      for (let j = 0; j < n; j++) rows.push({ l: j < dels.length ? dels[j] : null, r: j < adds.length ? adds[j] : null, kind: "chg" });
    }
    return rows;
  }
  function diffTable(hunk) {
    const nums = parseHeader(hunk.header);
    let la = nums.a, lb = nums.b;
    const table = el("table", { class: "diff" }, el("caption", {}, hunk.header));
    const body = el("tbody");
    for (const row of pairLines(hunk.lines)) {
      const lk = row.l === null ? "empty" : row.kind === "ctx" ? "ctx" : "del";
      const rk = row.r === null ? "empty" : row.kind === "ctx" ? "ctx" : "add";
      body.append(el("tr", {},
        el("td", { class: "ln " + lk }, row.l === null ? "" : String(la++)),
        el("td", { class: "code " + lk }, row.l === null ? "" : row.l),
        el("td", { class: "ln " + rk }, row.r === null ? "" : String(lb++)),
        el("td", { class: "code " + rk }, row.r === null ? "" : row.r)));
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

  function renderLeaf(id) {
    const wrap = el("div", { class: "leaf" });
    const m = metricsOf(id), content = contentOf(id);
    wrap.append(el("div", {},
      el("div", { class: "leaf-title" }, nameOf(id).toUpperCase()),
      el("div", { class: "leaf-sub" }, purposeOf(id))));
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
    metrics.append(stat("cohesion", mb ? mb.cohesion : null, mh ? mh.cohesion : null, (x) => x.toFixed(2)));
    wrap.append(metrics);

    // Changed types with their files and hunks.
    const types = typesOf(id);
    const touched = types.filter(typeTouched);
    wrap.append(el("h3", {}, touched.length ? `${touched.length} of ${types.length} types changed` : `No changes among ${types.length} types`));
    for (const t of touched) {
      const card = el("section", { class: "typecard" + statusClass(t.status === "unchanged" ? "modified" : t.status) });
      const head = el("header", {},
        el("span", { class: "chip chip-" + (t.status === "unchanged" ? "modified" : t.status) }, t.status === "unchanged" ? "modified" : t.status),
        el("span", { class: "typename" }, shortName(t.name)),
        el("span", { class: "typens" }, namespaceOf(t.name)));
      if (t.status === "moved") head.append(el("span", { class: "typens" }, `from ${t.leafBase} to ${t.leafHead}`));
      card.append(head);
      for (const path of t.files) {
        const f = FILES.get(path) || { status: "unchanged" };
        const fileEl = el("div", { class: "file file-" + f.status },
          el("div", { class: "file-head" }, el("span", { class: "chip chip-" + f.status }, f.status), el("span", { class: "file-path" }, path)));
        for (const h of HUNKS.get(path) || []) fileEl.append(diffTable(h));
        card.append(fileEl);
      }
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
      tl.append(el("span", { class: t.status === "added" ? "t-added" : typeTouched(t) ? "t-changed" : "", title: t.name + "\n" + t.files.join("\n") }, shortName(t.name)));
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
      seg.append(el("button", { class: !l.view ? "on" : "", onclick: () => switchView(null) }, "Tree"));
      for (const v of views) seg.append(el("button", { class: l.view === v ? "on" : "", onclick: () => switchView(v) }, VIEWS[v].name));
    }
  }

  function changeTree(id) {
    const st = contentOf(id).status;
    if (st === "unchanged") return null;
    const leaves = leavesUnder(id);
    const c = contentOf(id);
    const li = el("li");
    const edges = changedEdgesUnder(leaves);
    const isCur = !loc().leaf && !loc().view && loc().level === id || loc().leaf === id;
    li.append(el("button", { class: "row st-" + st + (isCur ? " current" : ""), onclick: () => showInContext(id) },
      el("span", { class: "dot" }), el("span", { class: "row-name" }, nameOf(id)),
      el("span", { class: "row-meta" }, st === "added" ? "new" : st === "removed" ? "removed" : `${c.added + c.removed + c.modified} files · ${edges} edges`)));
    if (!isLeaf(id)) {
      const kids = childrenOf(id).map(changeTree).filter(Boolean);
      if (kids.length) li.append(el("ul", {}, ...kids));
    }
    return li;
  }

  function renderChanges() {
    const box = $("#changes");
    box.replaceChildren();
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
      box.append(el("p", { class: "hint" }, "Changed types show their diffs. Click an edge row to see the type pairs behind it. Esc goes back to the schematic."));
      return;
    }
    if (!cur) return;
    const level = cur.level;
    const sel = state.selected;
    if (!sel) {
      const id = l.group ? `g:${l.view}/${l.group}` : l.level;
      box.append(el("h2", {}, locName(l)), el("p", { class: "ident" }, locKey(l)), el("p", { class: "purpose" }, l.view && !l.group ? VIEWS[l.view].purpose || "" : purposeOf(id)));
      const m = metricsOf(id);
      if (m.head) box.append(kv("Types", deltaText(m.base ? m.base.types : null, m.head.types)));
      else if (id === ROOT) box.append(kv("Types", deltaText(CS.types.filter((t) => t.leafBase).length, CS.types.filter((t) => t.leafHead).length)));
      if (m.head && !l.view) box.append(kv("Cohesion", deltaText(m.base ? m.base.cohesion : null, m.head.cohesion, (x) => x.toFixed(2))));
      box.append(kv("Edges drawn", `${level.edges.length} · ${changedText(level.edges)}`));
      box.append(kv("Cycles", String(level.cycles.length)));
      level.cycles.forEach((cyc, i) => {
        box.append(el("h3", {}, `Cycle ${i + 1}`));
        const ul = el("ul", { class: "list" });
        for (const k of cyc) ul.append(el("li", {}, el("button", { class: "row", onclick: () => select(k) }, el("span", { class: "row-name" }, nameOf(k)))));
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
      const m = metricsOf(child.key), content = child.kind === "group" ? groupContent(child) : contentOf(child.key);
      if (m.head) {
        box.append(kv("Types", deltaText(m.base ? m.base.types : null, m.head.types)));
        box.append(kv("Internal refs", deltaText(m.base ? m.base.internal : null, m.head.internal)));
        box.append(kv("Crossing refs", deltaText(m.base ? m.base.crossing : null, m.head.crossing)));
        box.append(kv("Cohesion", deltaText(m.base ? m.base.cohesion : null, m.head.cohesion, (x) => x.toFixed(2))));
      }
      box.append(kv("Files", `${content.files}` + (content.added + content.removed + content.modified ? ` · +${content.added} −${content.removed} ~${content.modified}` : "")));
      if (level.inCycle.has(child.key)) {
        const cyc = level.cycles.find((c) => c.includes(child.key));
        box.append(el("p", { class: "warn" }, "In a cycle with " + cyc.filter((k) => k !== child.key).map(nameOf).join(", ") + "."));
      }
      const touching = level.edges.filter((e) => e.from === child.key || e.to === child.key);
      const changed = touching.filter((e) => e.status !== "unchanged").sort((a, b) => isMinor(a) - isMinor(b) || Math.abs(b.head - b.base) - Math.abs(a.head - a.base));
      if (changed.length) {
        box.append(el("h3", {}, `Edges: ${changedText(touching)}`));
        const ul = el("ul", { class: "list" });
        for (const e of changed) ul.append(el("li", {}, el("button", { class: "row st-" + e.status, onclick: () => select(e.key) }, el("span", { class: "dot" + (isMinor(e) ? " minor" : "") }),
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
      for (const e of through.sort((a, b) => (a.status === "unchanged") - (b.status === "unchanged") || b.head - a.head)) ul.append(el("li", {}, el("button", { class: "row" + statusClass(e.status), onclick: () => select(e.key) }, el("span", { class: "dot", style: e.status === "unchanged" ? "visibility:hidden" : "" }),
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
  function animateViewBox(from, to, ms) {
    return new Promise((resolve) => {
      if (reduceMotion) { setViewBox(to); resolve(); return; }
      const t0 = performance.now();
      const ease = (t) => 1 - Math.pow(1 - t, 3);
      function step(now) {
        const t = Math.min(1, (now - t0) / ms), k = ease(t);
        setViewBox({ x: from.x + (to.x - from.x) * k, y: from.y + (to.y - from.y) * k, w: from.w + (to.w - from.w) * k, h: from.h + (to.h - from.h) * k });
        if (t < 1) requestAnimationFrame(step); else resolve();
      }
      requestAnimationFrame(step);
    });
  }
  function fitAll(layout) {
    const f = frameOf(layout);
    state.aspect = aspectOf(layout);
    return fitRect({ x: 0, y: 0, w: f.total.w, h: f.total.h }, 10);
  }

  let mountToken = 0;
  async function mount() {
    const token = ++mountToken;
    const l = loc();
    const s = stage(), leafView = $("#leafview"), wrap = $(".stage-wrap");
    if (l.leaf) {
      wrap.hidden = true; leafView.hidden = false; $("#legend").hidden = true;
      leafView.replaceChildren(renderLeaf(l.leaf));
      state.current = null;
      refreshChrome();
      return null;
    }
    wrap.hidden = false; leafView.hidden = true; $("#legend").hidden = false;
    const level = buildLevel(l, state.opts);
    const layout = await layoutLevel(level, state.opts);
    if (token !== mountToken) return null;
    // Container children preview their own layout, so make sure those exist.
    await Promise.all(level.children.filter((c) => c.kind === "node" && !isLeaf(c.id)).map((c) => layoutLevel(buildLevel({ level: c.id }, state.opts), state.opts)));
    if (token !== mountToken) return null;
    const old = s.querySelector(".level");
    if (old) old.remove();
    state.current = { level, layout };
    s.append(renderLevel(level, layout));
    document.body.classList.toggle("dim", state.opts.dim);
    $("#statusline").textContent = `${locKey(l)} · ${level.children.length} blocks · ${level.edges.length} edges`;
    refreshChrome();
    return { level, layout };
  }

  function refreshChrome() {
    renderBreadcrumb();
    renderDetails();
    renderChanges();
    try { history.replaceState(null, "", "#" + locKey(loc())); } catch (e) { /* ignore */ }
  }

  function childLoc(c) {
    if (c.kind === "group") { const [v, g] = c.id.slice(2).split("/"); return { level: loc().level, view: v, group: g }; }
    if (isLeaf(c.id) || c.kind === "unsorted") return { leaf: c.id };
    return { level: c.id };
  }

  async function enter(c) {
    const cur = state.current;
    const target = childLoc(c);
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
    if (m) setViewBox(fitAll(m.layout));
  }

  async function goTo(index) {
    const from = loc();
    state.trail = state.trail.slice(0, index + 1); state.selected = null;
    const m = await mount();
    if (!m) return;
    const fromKey = from.leaf || (from.group ? `g:${from.view}/${from.group}` : from.level);
    const r = m.layout.rects.get(fromKey) ? rectOf(m.level, m.layout, fromKey) : null;
    if (r) {
      setViewBox(fitRect(r, 12));
      await animateViewBox(state.view, fitAll(m.layout), 240);
      select(fromKey);
    } else setViewBox(fitAll(m.layout));
  }

  function up() { if (state.trail.length > 1) goTo(state.trail.length - 2); }

  async function switchView(view) {
    const l = loc();
    state.trail[state.trail.length - 1] = view ? { level: l.level, view } : { level: l.level };
    state.selected = null;
    const m = await mount();
    if (m) setViewBox(fitAll(m.layout));
  }

  // Shows a node selected at the level that draws it, descending the tree.
  async function showInContext(id) {
    if (id === ROOT) { state.trail = [{ level: ROOT }]; state.selected = null; const m = await mount(); if (m) setViewBox(fitAll(m.layout)); return; }
    if (isExternal(id)) return;
    const trail = [{ level: ROOT }];
    const parent = parentOf(id);
    for (const anc of ancestors(parent)) trail.push({ level: anc });
    state.trail = trail; state.selected = id;
    const m = await mount();
    if (m) setViewBox(fitAll(m.layout));
  }
  function ancestors(id) { const out = []; for (let x = id; x && x !== ROOT; x = parentOf(x)) out.unshift(x); return out; }

  // Shows a leaf-pair edge at the lowest tree level that draws it as an edge between two blocks or a block and a pin.
  async function showEdge(e) {
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
    setViewBox(fitAll(m.layout));
    const hit = m.level.edges.find((g) => g.members.includes(e));
    if (hit) select(hit.key);
  }

  // Selects on click unless the click ended a drag.
  let dragged = false;
  function clickSelect(ev, key) {
    ev.stopPropagation();
    if (!dragged) select(key);
  }

  // Updates the selection in place, so that a second click lands on the same element and double-click works.
  function select(key) {
    state.selected = key;
    const s = stage();
    for (const n of s.querySelectorAll(".selected")) {
      n.classList.remove("selected");
      const line = n.querySelector(".edge-line");
      if (line) line.setAttribute("marker-end", `url(#arrow-${line.getAttribute("data-marker")})`);
    }
    for (const n of s.querySelectorAll(".in-focus")) n.classList.remove("in-focus");
    if (key) {
      const target = s.querySelector(`[data-key="${CSS.escape(key)}"]`);
      if (target) {
        target.classList.add("selected");
        const line = target.querySelector(".edge-line");
        if (line) line.setAttribute("marker-end", "url(#arrow-selected)");
      }
      // Bring the neighborhood of a block out of the dimming.
      if (state.current && !key.startsWith("edge:")) {
        for (const e of state.current.level.edges) {
          if (e.from === key || e.to === key) {
            const g = s.querySelector(`[data-key="${CSS.escape(e.key)}"]`); if (g) g.classList.add("in-focus");
            const other = s.querySelector(`[data-key="${CSS.escape(e.from === key ? e.to : e.from)}"]`); if (other) other.classList.add("in-focus");
          }
        }
      }
    }
    renderDetails();
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
    s.addEventListener("pointerdown", (ev) => {
      if (ev.button !== 0) return;
      drag = { x: ev.clientX, y: ev.clientY, view: { ...state.view }, moved: false };
      s.setPointerCapture(ev.pointerId);
    });
    s.addEventListener("pointermove", (ev) => {
      if (!drag) return;
      const b = s.getBoundingClientRect();
      const dx = (ev.clientX - drag.x) / b.width * drag.view.w, dy = (ev.clientY - drag.y) / b.height * drag.view.h;
      if (!drag.moved && Math.hypot(ev.clientX - drag.x, ev.clientY - drag.y) < 4) return;
      drag.moved = true;
      setViewBox({ x: drag.view.x - dx, y: drag.view.y - dy, w: drag.view.w, h: drag.view.h });
    });
    const end = (ev) => {
      if (!drag) return;
      if (drag.moved) { dragged = true; setTimeout(() => { dragged = false; }, 0); }
      drag = null;
      try { s.releasePointerCapture(ev.pointerId); } catch (e) { /* ignore */ }
    };
    s.addEventListener("pointerup", end);
    s.addEventListener("pointercancel", end);
    s.addEventListener("click", () => { if (!dragged) select(null); });
    s.addEventListener("dblclick", (ev) => { if (ev.target === s || ev.target.id === "paper") { if (state.current) setViewBox(fitAll(state.current.layout)); } });
  }

  // ---------- Boot ----------

  async function boot() {
    $("#pr").replaceChildren(el("span", {}, `${META.solution || MAP.solution} · `), el("code", {}, META.range || `${(CS.base || "").slice(0, 7)}..${(CS.head || "").slice(0, 7)}`), el("span", {}, META.title ? ` · ${META.title}` : ""));
    $("#pr").title = META.title || "";
    $("#dim").addEventListener("change", (ev) => { state.opts.dim = ev.target.checked; document.body.classList.toggle("dim", state.opts.dim); });
    $("#externals").addEventListener("change", async (ev) => { state.opts.externals = ev.target.checked; const m = await mount(); if (m) setViewBox(fitAll(m.layout)); });
    $("#threshold").addEventListener("change", async (ev) => { state.opts.threshold = ev.target.value; const m = await mount(); if (m) setViewBox(fitAll(m.layout)); });
    $("#up").addEventListener("click", up);
    document.addEventListener("keydown", (ev) => {
      if (["INPUT", "SELECT", "TEXTAREA"].includes(ev.target.tagName)) return;
      if (ev.key === "Escape" || ev.key === "Backspace") { ev.preventDefault(); up(); }
      else if (ev.key === "Enter" && state.selected && state.current) {
        const c = state.current.level.children.find((x) => x.key === state.selected);
        if (c) enter(c);
      } else if ((ev.key === "ArrowRight" || ev.key === "ArrowLeft") && state.current) {
        const keys = state.current.level.children.map((c) => c.key);
        const i = keys.indexOf(state.selected);
        select(i < 0 ? keys[0] : keys[(i + (ev.key === "ArrowRight" ? 1 : keys.length - 1)) % keys.length]);
      } else if (ev.key === "f" && state.current) setViewBox(fitAll(state.current.layout));
    });
    installPanZoom();
    const start = parseLoc(decodeURIComponent((location.hash || "").slice(1)));
    state.trail = [{ level: ROOT }];
    if (start.leaf) { for (const anc of ancestors(parentOf(start.leaf))) state.trail.push({ level: anc }); state.trail.push(start); }
    else { for (const anc of ancestors(start.level)) state.trail.push({ level: anc }); if (start.view) state.trail.push({ level: start.level, view: start.view }); if (start.group) state.trail.push(start); }
    const m = await mount();
    if (m) setViewBox(fitAll(m.layout));
  }

  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", boot);
  else boot();
})();
