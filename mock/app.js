// Viewer mock. Renders one container at a time as a schematic, with ports for edges
// that leave the container, previews of each child's own layout, a change-first
// navigation tree, and a leaf view with side-by-side diffs.

(function () {
  "use strict";

  const W = 1200, H = 800;
  const FRAME = { x: 90, y: 40, w: 1020, h: 720 };
  const SVGNS = "http://www.w3.org/2000/svg";
  const reduceMotion = window.matchMedia("(prefers-reduced-motion: reduce)").matches;

  // ---------- Model ----------

  function parentOf(id) {
    if (id === "root") return null;
    const i = id.lastIndexOf(".");
    return i < 0 ? "root" : id.slice(0, i);
  }

  function childrenOf(id) {
    return Object.keys(NODES).filter((k) => parentOf(k) === id);
  }

  function isContainer(id) {
    return id === "root" || (!NODES[id].leaf && !NODES[id].external);
  }

  function edgeStatus(e) {
    if (e.status) return e.status;
    if (e.base === 0 && e.head > 0) return "added";
    if (e.head === 0 && e.base > 0) return "removed";
    if (e.base !== e.head) return "modified";
    return "unchanged";
  }

  function policyOf(id) {
    return id === "root" ? "declared" : NODES[id].policy || "observed";
  }

  // Aggregate counts and derive container status bottom-up.
  const AGG = {};
  function aggregate(id) {
    if (AGG[id]) return AGG[id];
    const n = id === "root" ? {} : NODES[id];
    let a;
    if (id !== "root" && (n.leaf || n.external)) {
      const removed = n.status === "removed";
      a = {
        files: removed ? 0 : n.files || 0,
        changed: n.changed || 0,
        status: n.status || ((n.changed || 0) > 0 ? "modified" : "unchanged"),
      };
    } else {
      const kids = childrenOf(id).map(aggregate);
      const chunk = CHUNKS[id] || { edges: [], ports: [] };
      const edgesChanged = chunk.edges.some((e) => edgeStatus(e) !== "unchanged");
      const kidsChanged = kids.some((k) => k.status !== "unchanged");
      a = {
        files: kids.reduce((s, k) => s + k.files, 0),
        changed: kids.reduce((s, k) => s + k.changed, 0),
        status: n.status || (kidsChanged || edgesChanged ? "children" : "unchanged"),
      };
    }
    a.structural = STRUCTURAL.filter((s) => {
      const where = s.kind === "node" ? s.id : s.at;
      return id === "root" || where === id || where.startsWith(id + ".");
    }).length;
    AGG[id] = a;
    return a;
  }
  aggregate("root");

  const policyCounts = (() => {
    let declared = 0, observed = 0;
    for (const id of ["root", ...Object.keys(NODES)]) {
      if (!isContainer(id)) continue;
      if (id !== "root" && NODES[id].status === "removed") continue;
      if (policyOf(id) === "declared") declared++; else observed++;
    }
    return { declared, observed };
  })();

  // ---------- State ----------

  const state = { current: "root", selected: null, dim: false };

  // ---------- DOM helpers ----------

  const $ = (sel) => document.querySelector(sel);
  function el(tag, attrs, ...kids) {
    const e = document.createElement(tag);
    for (const [k, v] of Object.entries(attrs || {})) {
      if (k === "class") e.className = v;
      else if (k.startsWith("on")) e.addEventListener(k.slice(2), v);
      else if (v !== null && v !== undefined) e.setAttribute(k, v);
    }
    for (const k of kids) if (k !== null && k !== undefined) e.append(k);
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
  function truncate(s, n) {
    return s.length > n ? s.slice(0, n - 1) + "…" : s;
  }

  // ---------- Layout and routing ----------

  function rectOf(id, chunk) {
    const [x, y, w, h] = chunk.layout[id];
    return { x, y, w, h };
  }

  function fitRect(r, pad) {
    // Expands a rect to the canvas aspect ratio, for zoom transitions.
    let x = r.x - pad, y = r.y - pad, w = r.w + 2 * pad, h = r.h + 2 * pad;
    const ar = W / H;
    if (w / h < ar) { const nw = h * ar; x -= (nw - w) / 2; w = nw; }
    else { const nh = w / ar; y -= (nh - h) / 2; h = nh; }
    return { x, y, w, h };
  }

  // Reports whether a vertical or horizontal segment crosses any obstacle rect.
  function segmentBlocked(obstacles, fixed, from, to, vertical) {
    const lo = Math.min(from, to), hi = Math.max(from, to);
    return obstacles.some((r) => vertical
      ? fixed > r.x - 6 && fixed < r.x + r.w + 6 && hi > r.y - 6 && lo < r.y + r.h + 6
      : fixed > r.y - 6 && fixed < r.y + r.h + 6 && hi > r.x - 6 && lo < r.x + r.w + 6);
  }

  // Counts the segments of a polyline that cross an obstacle.
  function blockedSegments(obstacles, pts) {
    let n = 0;
    for (let i = 1; i < pts.length; i++) {
      const a = pts[i - 1], b = pts[i];
      if (a.x === b.x && a.y === b.y) continue;
      const vertical = a.x === b.x;
      if (segmentBlocked(obstacles, vertical ? a.x : a.y, vertical ? a.y : a.x, vertical ? b.y : b.x, vertical)) n++;
    }
    return n;
  }

  function pathLength(pts) {
    let l = 0;
    for (let i = 1; i < pts.length; i++) l += Math.abs(pts[i].x - pts[i - 1].x) + Math.abs(pts[i].y - pts[i - 1].y);
    return l;
  }

  // Chooses a polyline from s to t. Candidates are Z-shaped paths with the middle
  // segment at several positions, plus detours that go around the obstacles that lie
  // between the endpoints. The path with the fewest blocked segments wins, and the
  // shortest among equals.
  function pickRoute(obstacles, s, t, kind, i) {
    const nudge = ((i % 5) - 2) * 6;
    const h = kind === "h";
    const a = h ? s.x : s.y, b = h ? t.x : t.y;
    const mid = (a + b) / 2 + nudge;
    const dir = Math.sign(b - a) || 1;
    const jogs = [mid, a + dir * 24, b - dir * 24, (a + mid) / 2, (mid + b) / 2, a + dir * 48, b - dir * 48];
    const candidates = jogs.map((j) => h ? [s, { x: j, y: s.y }, { x: j, y: t.y }, t] : [s, { x: s.x, y: j }, { x: t.x, y: j }, t]);

    // Detours around every obstacle whose extent lies between the endpoints.
    const lo = Math.min(a, b), hi = Math.max(a, b);
    const between = obstacles.filter((r) => h ? r.x + r.w > lo && r.x < hi : r.y + r.h > lo && r.y < hi);
    if (between.length) {
      const lanes = [];
      for (const r of between) {
        lanes.push((h ? r.y : r.x) - 20 + nudge, (h ? r.y + r.h : r.x + r.w) + 20 + nudge);
      }
      for (const c of lanes) {
        candidates.push(h
          ? [s, { x: a + dir * 24, y: s.y }, { x: a + dir * 24, y: c }, { x: b - dir * 24, y: c }, { x: b - dir * 24, y: t.y }, t]
          : [s, { x: s.x, y: a + dir * 24 }, { x: c, y: a + dir * 24 }, { x: c, y: b - dir * 24 }, { x: t.x, y: b - dir * 24 }, t]);
      }
    }
    let best = null, score = Infinity;
    for (const pts of candidates) {
      const sc = blockedSegments(obstacles, pts) * 10000 + pathLength(pts);
      if (sc < score) { score = sc; best = pts; }
    }
    return best;
  }

  // Returns the midpoint of the longest of the three segments of a Z path, which is
  // where a label is least likely to sit on a bend or inside a block.
  function labelPoint(pts) {
    let best = null, len = -1;
    for (let i = 1; i < pts.length; i++) {
      const l = Math.abs(pts[i].x - pts[i - 1].x) + Math.abs(pts[i].y - pts[i - 1].y);
      if (l > len) { len = l; best = { x: (pts[i].x + pts[i - 1].x) / 2, y: (pts[i].y + pts[i - 1].y) / 2 }; }
    }
    return best;
  }

  // Chooses sides for each edge, spreads endpoints along each side, and returns
  // orthogonal paths. Endpoints are {id, rect}. Edges are {a, b, key}. Obstacles
  // are the block rects that the middle segment of a path should not cross.
  function routeAll(edges, obstacles) {
    const sides = {};
    function slot(id, side, other) {
      sides[id] = sides[id] || { left: [], right: [], top: [], bottom: [] };
      const entry = { other, pos: 0 };
      sides[id][side].push(entry);
      return entry;
    }
    const plans = edges.map((e) => {
      const a = e.a.rect, b = e.b.rect;
      let sa, sb, kind;
      if (b.x >= a.x + a.w + 24) { sa = "right"; sb = "left"; kind = "h"; }
      else if (a.x >= b.x + b.w + 24) { sa = "left"; sb = "right"; kind = "h"; }
      else if (b.y >= a.y + a.h) { sa = "bottom"; sb = "top"; kind = "v"; }
      else { sa = "top"; sb = "bottom"; kind = "v"; }
      return { e, kind, ea: slot(e.a.id, sa, b), eb: slot(e.b.id, sb, a), sa, sb };
    });
    // Spread endpoints along each side, ordered by the other end's position.
    for (const id of Object.keys(sides)) {
      const r = edges.find((e) => e.a.id === id)?.a.rect || edges.find((e) => e.b.id === id).b.rect;
      for (const side of ["left", "right", "top", "bottom"]) {
        const list = sides[id][side];
        if (!list.length) continue;
        const horiz = side === "left" || side === "right";
        list.sort((p, q) => horiz ? (p.other.y + p.other.h / 2) - (q.other.y + q.other.h / 2) : (p.other.x + p.other.w / 2) - (q.other.x + q.other.w / 2));
        const len = horiz ? r.h : r.w;
        list.forEach((entry, i) => {
          const t = (i + 1) / (list.length + 1);
          const along = len * (0.2 + 0.6 * t);
          entry.pos = horiz
            ? { x: side === "left" ? r.x : r.x + r.w, y: r.y + along }
            : { x: r.x + along, y: side === "top" ? r.y : r.y + r.h };
        });
      }
    }
    return plans.map((p, i) => {
      const s = p.ea.pos, t = p.eb.pos;
      const others = obstacles.filter((r) => r !== p.e.a.rect && r !== p.e.b.rect);
      const pts = pickRoute(others, s, t, p.kind, i);
      const d = `M${pts[0].x},${pts[0].y} ` + pts.slice(1).map((q) => `L${q.x},${q.y}`).join(" ");
      return { e: p.e, d, mid: labelPoint(pts) };
    });
  }

  // ---------- Rendering: container view ----------

  function strokeWidth(n) {
    return (1 + Math.log2(1 + n) / 2).toFixed(2);
  }

  function statusClass(st) {
    return st === "unchanged" ? "" : " st-" + st;
  }

  function previewInto(g, id, box) {
    // Draws the child's own layout as unlabeled miniature rectangles.
    const n = NODES[id];
    const inner = { x: box.x + 10, y: box.y + 44, w: box.w - 20, h: box.h - 54 };
    if (inner.h < 16) return;
    if (n.external) {
      g.append(svg("rect", { x: inner.x, y: inner.y, width: inner.w, height: inner.h, class: "mini mini-ext", rx: 1 }));
      return;
    }
    if (n.leaf) {
      const files = Math.min(n.files || 0, 14);
      const changed = n.status === "removed" ? files : Math.min(n.changed || 0, files);
      const gap = Math.min(6, inner.h / Math.max(files, 1));
      for (let i = 0; i < files; i++) {
        const cls = "mini-file" + (i < changed ? " mini-file-changed" : "");
        g.append(svg("rect", { x: inner.x, y: inner.y + i * gap, width: Math.min(inner.w, 46 + (i * 13) % 30), height: 2, class: cls }));
      }
      return;
    }
    const chunk = CHUNKS[id];
    if (!chunk) return;
    const kids = Object.keys(chunk.layout);
    const bb = kids.reduce((b, k) => {
      const r = rectOf(k, chunk);
      return { x0: Math.min(b.x0, r.x), y0: Math.min(b.y0, r.y), x1: Math.max(b.x1, r.x + r.w), y1: Math.max(b.y1, r.y + r.h) };
    }, { x0: Infinity, y0: Infinity, x1: -Infinity, y1: -Infinity });
    const sc = Math.min(inner.w / (bb.x1 - bb.x0), inner.h / (bb.y1 - bb.y0));
    const ox = inner.x + (inner.w - (bb.x1 - bb.x0) * sc) / 2;
    const oy = inner.y + (inner.h - (bb.y1 - bb.y0) * sc) / 2;
    for (const k of kids) {
      const r = rectOf(k, chunk);
      const st = aggregate(k).status;
      g.append(svg("rect", {
        x: ox + (r.x - bb.x0) * sc, y: oy + (r.y - bb.y0) * sc, width: r.w * sc, height: r.h * sc,
        class: "mini" + statusClass(st), rx: 1,
      }));
    }
  }

  function renderContainer(id) {
    const chunk = CHUNKS[id];
    const root = svg("g", { class: "level" });
    const isRoot = id === "root";

    // Frame and ports for a non-root container.
    const portRects = {};
    if (!isRoot) {
      const pol = policyOf(id);
      root.append(svg("rect", { x: FRAME.x, y: FRAME.y, width: FRAME.w, height: FRAME.h, class: "frame frame-" + pol }));
      root.append(text(FRAME.x + 14, FRAME.y + 22, NODES[id].name.toUpperCase(), { class: "frame-title" }));
      root.append(text(FRAME.x + FRAME.w - 14, FRAME.y + 22, pol === "declared" ? "DECLARED LEVEL" : "OBSERVED LEVEL", { class: "frame-policy frame-policy-" + pol, "text-anchor": "end" }));

      const groups = { in: new Map(), out: new Map() };
      for (const p of chunk.ports) {
        const m = groups[p.dir];
        if (!m.has(p.outside)) m.set(p.outside, []);
        m.get(p.outside).push(p);
      }
      for (const dir of ["in", "out"]) {
        const keys = [...groups[dir].keys()];
        keys.forEach((outside, i) => {
          const y = FRAME.y + 60 + (FRAME.h - 120) * ((i + 1) / (keys.length + 1));
          const x = dir === "in" ? FRAME.x : FRAME.x + FRAME.w;
          const pr = { x: x - 7, y: y - 9, w: 14, h: 18 };
          portRects[dir + ":" + outside] = pr;
          const anyChanged = groups[dir].get(outside).some((p) => edgeStatus(p) !== "unchanged");
          root.append(svg("rect", { x: pr.x, y: pr.y, width: pr.w, height: pr.h, class: "port" + (anyChanged ? " port-changed" : "") }));
          const label = NODES[outside] ? NODES[outside].name : outside;
          root.append(text(dir === "in" ? x - 12 : x + 12, y + 4, label, { class: "port-label", "text-anchor": dir === "in" ? "end" : "start" }));
        });
      }
    }

    // Edge list: sibling edges plus port edges.
    const endpoints = {};
    const endpoint = (nid, rect) => (endpoints[nid] = endpoints[nid] || { id: nid, rect });
    for (const k of Object.keys(chunk.layout)) endpoint(k, rectOf(k, chunk));
    const edges = chunk.edges.map((e, i) => ({ a: endpoint(e.from), b: endpoint(e.to), key: "e:" + id + ":" + i, data: e, sibling: true }));
    chunk.ports.forEach((p, i) => {
      const pr = portRects[p.dir + ":" + p.outside];
      const port = endpoint(p.dir + ":" + p.outside, pr);
      const child = endpoint(p.child);
      edges.push(p.dir === "in" ? { a: port, b: child, key: "p:" + id + ":" + i, data: p } : { a: child, b: port, key: "p:" + id + ":" + i, data: p });
    });

    const edgeLayer = svg("g", { class: "edges" });
    const labelLayer = svg("g", { class: "edge-labels" });
    const obstacles = Object.keys(chunk.layout).map((k) => endpoints[k].rect);
    for (const r of routeAll(edges, obstacles)) {
      const d = r.e.data;
      const st = edgeStatus(d);
      const undeclared = st === "added" && policyOf(id) === "observed" && r.e.sibling;
      const cls = "edge" + statusClass(st) + (undeclared ? " st-undeclared" : "") + (d.asserted ? " asserted" : "") + (r.e.key === state.selected ? " selected" : "");
      const marker = r.e.key === state.selected ? "selected" : undeclared ? "undeclared" : st;
      const g = svg("g", { class: cls, "data-key": r.e.key,
        onclick: (ev) => { ev.stopPropagation(); select(r.e.key); } });
      g.append(svg("path", { d: r.d, class: "edge-hit" }));
      g.append(svg("path", { d: r.d, class: "edge-line", "stroke-width": strokeWidth(st === "removed" ? d.base : d.head), "marker-end": `url(#arrow-${marker})` }));
      edgeLayer.append(g);

      // Label: payload, label, delta, undeclared badge.
      const parts = [];
      if (d.payload) parts.push({ t: d.payload, cls: "lbl-payload" });
      else if (d.label) parts.push({ t: truncate(d.label, 28), cls: "lbl-text" });
      if (st === "modified") parts.push({ t: (d.head - d.base > 0 ? "+" : "−") + Math.abs(d.head - d.base), cls: "lbl-delta" });
      if (st === "added" && !d.payload && !d.label) parts.push({ t: "+" + d.head, cls: "lbl-delta" });
      if (st === "removed") parts.push({ t: "−" + d.base, cls: "lbl-delta" });
      if (undeclared) parts.push({ t: "UNDECLARED", cls: "lbl-undeclared" });
      if (parts.length) {
        const lg = svg("g", { class: "edge-label" + statusClass(st), onclick: (ev) => { ev.stopPropagation(); select(r.e.key); } });
        const str = parts.map((p) => p.t).join("  ");
        const w = str.length * 6.4 + 12;
        lg.append(svg("rect", { x: r.mid.x - w / 2, y: r.mid.y - 9, width: w, height: 18, rx: 2, class: "lbl-bg" }));
        let x = r.mid.x - w / 2 + 6;
        for (const p of parts) {
          const t = text(x, r.mid.y + 4, p.t, { class: p.cls });
          lg.append(t);
          x += p.t.length * 6.4 + 12.8;
        }
        labelLayer.append(lg);
      }
    }
    root.append(edgeLayer);

    // Nodes.
    const nodeLayer = svg("g", { class: "nodes" });
    for (const k of Object.keys(chunk.layout)) {
      const n = NODES[k];
      const r = rectOf(k, chunk);
      const a = aggregate(k);
      const cls = "node" + statusClass(a.status) + (n.external ? " external" : "") + (n.leaf ? " leaf" : " container") +
        (k === state.selected ? " selected" : "") + (!n.leaf && !n.external && policyOf(k) === "observed" ? " observed" : "");
      const g = svg("g", { class: cls, "data-id": k, tabindex: 0,
        onclick: (ev) => { ev.stopPropagation(); select(k); },
        ondblclick: (ev) => { ev.stopPropagation(); enter(k); },
        onkeydown: (ev) => { if (ev.key === "Enter") { ev.stopPropagation(); enter(k); } } });
      if (n.external) g.append(svg("rect", { x: r.x - 4, y: r.y - 4, width: r.w + 8, height: r.h + 8, rx: 3, class: "ext-outline" }));
      g.append(svg("rect", { x: r.x, y: r.y, width: r.w, height: r.h, rx: 2, class: "box" }));
      g.append(svg("rect", { x: r.x, y: r.y, width: 4, height: r.h, class: "stripe" }));
      g.append(text(r.x + 12, r.y + 20, n.name.toUpperCase(), { class: "title" }));
      let meta;
      if (n.external) meta = "external";
      else if (a.status === "removed") meta = `removed · ${n.changed || n.files || 0} files`;
      else if (a.status === "added") meta = `new · ${a.files} files`;
      else meta = `${a.files} files` + (a.changed ? ` · ${a.changed} changed` : "") + (a.structural && !n.leaf ? ` · ${a.structural} structural` : "");
      g.append(text(r.x + 12, r.y + 34, meta, { class: "meta" }));
      if (!n.leaf && !n.external) g.append(text(r.x + r.w - 10, r.y + 20, "⤵", { class: "enter-glyph", "text-anchor": "end" }));
      previewInto(g, k, r);
      nodeLayer.append(g);
    }
    root.append(nodeLayer);
    root.append(labelLayer);
    return root;
  }

  // ---------- Rendering: leaf view ----------

  function parseHeader(h) {
    const m = /@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@/.exec(h);
    return { a: m ? +m[1] : 1, b: m ? +m[2] : 1 };
  }

  function pairLines(lines) {
    // Pairs removed and added runs side by side; context lines pair with themselves.
    const rows = [];
    let i = 0;
    while (i < lines.length) {
      const [m] = lines[i];
      if (m === " ") { rows.push({ l: lines[i][1], r: lines[i][1], kind: "ctx" }); i++; continue; }
      const dels = [], adds = [];
      while (i < lines.length && lines[i][0] === "-") dels.push(lines[i++][1]);
      while (i < lines.length && lines[i][0] === "+") adds.push(lines[i++][1]);
      const n = Math.max(dels.length, adds.length);
      for (let j = 0; j < n; j++) rows.push({ l: j < dels.length ? dels[j] : null, r: j < adds.length ? adds[j] : null, kind: "chg" });
    }
    return rows;
  }

  function renderLeaf(id) {
    const n = NODES[id];
    const leaf = LEAVES[id] || { files: [] };
    const wrap = el("div", { class: "leaf" });
    wrap.append(el("div", { class: "leaf-head" },
      el("div", { class: "leaf-title" }, n.name.toUpperCase()),
      el("div", { class: "leaf-sub" }, n.purpose)));
    const files = leaf.files.length ? leaf.files : Array.from({ length: n.files || 0 }, (_, i) => ({ path: `(file ${i + 1} of ${n.files}, not in mock data)`, status: "unchanged" }));
    for (const f of files) {
      const card = el("section", { class: "file file-" + f.status });
      card.append(el("header", { class: "file-head" },
        el("span", { class: "chip chip-" + f.status }, f.status),
        el("span", { class: "file-path" }, f.path)));
      if (f.hunks) {
        for (const h of f.hunks) {
          const nums = parseHeader(h.header);
          let la = nums.a, lb = nums.b;
          const table = el("table", { class: "diff" });
          const cap = el("caption", {}, h.header);
          table.append(cap);
          const body = el("tbody");
          for (const row of pairLines(h.lines)) {
            const tr = el("tr", { class: row.kind === "ctx" ? "ctx" : "" });
            const lk = row.l === null ? "empty" : row.kind === "ctx" ? "ctx" : "del";
            const rk = row.r === null ? "empty" : row.kind === "ctx" ? "ctx" : "add";
            tr.append(el("td", { class: "ln " + lk }, row.l === null ? "" : String(la++)));
            tr.append(el("td", { class: "code " + lk }, row.l === null ? "" : row.l));
            tr.append(el("td", { class: "ln " + rk }, row.r === null ? "" : String(lb++)));
            tr.append(el("td", { class: "code " + rk }, row.r === null ? "" : row.r));
            body.append(tr);
          }
          table.append(body);
          card.append(el("div", { class: "diff-scroll" }, table));
        }
      }
      wrap.append(card);
    }
    return wrap;
  }

  // ---------- Panels ----------

  function renderBreadcrumb() {
    const bc = $("#crumbs");
    bc.replaceChildren();
    const path = [];
    for (let id = state.current; id; id = parentOf(id)) path.unshift(id);
    path.forEach((id, i) => {
      const last = i === path.length - 1;
      const label = id === "root" ? "Orchard" : NODES[id].name;
      bc.append(el("button", { class: "crumb" + (last ? " current" : ""), onclick: () => goTo(id), disabled: last ? "" : null }, label));
      if (!last) bc.append(el("span", { class: "crumb-sep" }, "›"));
    });
  }

  function changeTree(id) {
    const a = aggregate(id);
    if (a.status === "unchanged") return null;
    const n = id === "root" ? { name: "Orchard" } : NODES[id];
    const li = el("li");
    const row = el("button", { class: "chg-row st-" + a.status, onclick: () => showInContext(id) },
      el("span", { class: "chg-dot" }),
      el("span", { class: "chg-name" }, n.name),
      el("span", { class: "chg-meta" }, a.status === "added" ? "new" : a.status === "removed" ? "removed" : `${a.changed} files` + (a.structural ? ` · ${a.structural} structural` : "")));
    li.append(row);
    if (isContainer(id)) {
      const kids = childrenOf(id).map(changeTree).filter(Boolean);
      if (kids.length) li.append(el("ul", {}, ...kids));
    }
    return li;
  }

  function renderChanges() {
    const box = $("#changes");
    box.replaceChildren();
    const tree = changeTree("root");
    box.append(el("ul", { class: "chg-tree" }, tree));

    const sh = el("h2", {}, "Structural changes");
    box.append(sh);
    const ul = el("ul", { class: "struct-list" });
    for (const s of STRUCTURAL) {
      const where = s.kind === "node" ? parentOf(s.id) : s.at;
      const label = s.kind === "node"
        ? `${s.op === "added" ? "+" : "−"} node ${NODES[s.id].name}`
        : `${s.op === "added" ? "+" : "−"} edge ${NODES[s.from].name} → ${NODES[s.to].name}`;
      ul.append(el("li", {}, el("button", { class: "struct-row st-" + s.op, onclick: () => { goTo(where); if (s.kind === "node") select(s.id); } },
        el("span", { class: "chg-dot" }), el("span", {}, label), el("span", { class: "chg-meta" }, where === "root" ? "Orchard" : NODES[where].name))));
    }
    box.append(ul);
  }

  function renderDetails() {
    const box = $("#details");
    box.replaceChildren();
    const sel = state.selected;
    if (!sel) {
      const id = state.current;
      const n = id === "root" ? { name: "Orchard", purpose: "Order platform. PR #412 moves payment retry into Billing core, adds refunds and emits payment events." } : NODES[id];
      const a = aggregate(id);
      box.append(el("h2", {}, n.name), el("p", { class: "purpose" }, n.purpose));
      if (isContainer(id)) box.append(kv("Level policy", policyOf(id)));
      box.append(kv("Files", `${a.files} · ${a.changed} changed`));
      if (isContainer(id)) box.append(kv("Structural changes", String(a.structural)));
      box.append(el("p", { class: "hint" }, isContainer(id)
        ? "Click a block to select it. Double-click or press Enter to open it. Esc goes up a level."
        : "Changed files show their diff. Esc goes back to the schematic."));
      return;
    }
    if (NODES[sel]) {
      const n = NODES[sel], a = aggregate(sel);
      box.append(el("h2", {}, n.name), el("p", { class: "ident" }, sel), el("p", { class: "purpose" }, n.purpose));
      box.append(kv("Status", a.status === "children" ? "contains changes" : a.status));
      if (!n.leaf && !n.external) box.append(kv("Level policy", policyOf(sel)));
      if (!n.external) box.append(kv("Files", `${a.files}` + (a.changed ? ` · ${a.changed} changed` : "")));
      const st = STRUCTURAL.filter((s) => (s.kind === "node" ? s.id : s.at) === sel || (s.kind === "node" ? s.id : s.at).startsWith(sel + "."));
      if (st.length) {
        box.append(el("h3", {}, "Structural changes inside"));
        box.append(el("ul", { class: "evidence" }, ...st.map((s) => el("li", { class: "st-" + s.op },
          s.kind === "node" ? `${s.op} node ${s.id}` : `${s.op} edge ${s.from} → ${s.to}`))));
      }
      if (!n.external) box.append(el("button", { class: "btn", onclick: () => enter(sel) }, n.leaf ? "Open files and diffs" : "Open " + n.name));
      return;
    }
    // Edge.
    const [kind, cid, idx] = sel.split(":");
    const chunk = CHUNKS[cid];
    const d = kind === "e" ? chunk.edges[+idx] : chunk.ports[+idx];
    const st = edgeStatus(d);
    const from = kind === "e" ? d.from : d.dir === "in" ? d.outside : d.child;
    const to = kind === "e" ? d.to : d.dir === "in" ? d.child : d.outside;
    const undeclared = st === "added" && policyOf(cid) === "observed" && kind === "e";
    box.append(el("h2", {}, `${NODES[from].name} → ${NODES[to].name}`), el("p", { class: "ident" }, `${from} → ${to}`));
    if (undeclared) box.append(el("p", { class: "warn" }, "Undeclared. This level is observed, so nobody approved this edge. It appeared with the code change."));
    if (kind === "p") box.append(el("p", { class: "hint" }, `Port edge. The declared edge lives at the ${NODES[parentOf(cid)] ? NODES[parentOf(cid)].name : "Orchard"} level or above.`));
    box.append(kv("Status", st));
    if (d.label) box.append(kv("Label", d.label));
    if (d.payload) box.append(kv("Payload", d.payload));
    if (d.asserted) box.append(kv("Asserted", "the checker cannot see this edge"));
    box.append(kv("Evidence", `${d.base} → ${d.head} references`));
    const s = STRUCTURAL.find((x) => x.kind === "edge" && x.from === from && x.to === to);
    if (s) {
      box.append(el("h3", {}, "Evidence at head"));
      box.append(el("ul", { class: "evidence" }, ...s.evidence.map((e) => el("li", {}, e))));
    }
  }

  function kv(k, v) {
    return el("div", { class: "kv" }, el("span", { class: "k" }, k), el("span", { class: "v" }, v));
  }

  // ---------- Navigation ----------

  const stage = () => $("#stage");

  function setViewBox(r) {
    stage().setAttribute("viewBox", `${r.x} ${r.y} ${r.w} ${r.h}`);
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

  function mount(id) {
    const s = stage();
    const old = s.querySelector(".level");
    if (old) old.remove();
    const leafView = $("#leafview");
    const wrap = $(".stage-wrap");
    if (isContainer(id)) {
      wrap.hidden = false; leafView.hidden = true;
      s.append(renderContainer(id));
      $("#legend").hidden = false;
    } else {
      wrap.hidden = true; leafView.hidden = false;
      leafView.replaceChildren(renderLeaf(id));
      $("#legend").hidden = true;
    }
    document.body.classList.toggle("dim", state.dim);
  }

  function refreshChrome() {
    renderBreadcrumb();
    renderDetails();
    $("#policy-count").textContent = `${policyCounts.declared} declared · ${policyCounts.observed} observed levels`;
    try { history.replaceState(null, "", "#" + state.current); } catch (e) { /* ignore */ }
  }

  async function enter(id) {
    if (NODES[id] && NODES[id].external) return;
    const parent = parentOf(id);
    if (parent === state.current && isContainer(state.current)) {
      const r = fitRect(rectOf(id, CHUNKS[state.current]), 12);
      stage().classList.add("zooming");
      await animateViewBox({ x: 0, y: 0, w: W, h: H }, r, 260);
    }
    state.current = id; state.selected = null;
    mount(id);
    setViewBox({ x: 0, y: 0, w: W, h: H });
    stage().classList.remove("zooming");
    refreshChrome();
  }

  async function goTo(id) {
    const from = state.current;
    state.current = id; state.selected = null;
    mount(id);
    // Ascending: start zoomed on the box we came from, then pull back.
    if (isContainer(id) && parentOf(from) === id && CHUNKS[id].layout[from]) {
      const r = fitRect(rectOf(from, CHUNKS[id]), 12);
      setViewBox(r);
      await animateViewBox(r, { x: 0, y: 0, w: W, h: H }, 260);
      state.selected = from;
      mount(id);
    } else {
      setViewBox({ x: 0, y: 0, w: W, h: H });
    }
    refreshChrome();
  }

  function up() {
    const p = parentOf(state.current);
    if (p) goTo(p);
  }

  function showInContext(id) {
    const parent = parentOf(id);
    if (!parent) { goTo("root"); return; }
    if (state.current !== parent) {
      state.current = parent; state.selected = id;
      mount(parent); setViewBox({ x: 0, y: 0, w: W, h: H }); refreshChrome();
    } else {
      select(id);
    }
  }

  function select(key) {
    state.selected = key;
    mount(state.current);
    renderDetails();
  }

  // ---------- Boot ----------

  function boot() {
    $("#dim").addEventListener("change", (ev) => { state.dim = ev.target.checked; mount(state.current); });
    $("#up").addEventListener("click", up);
    stage().addEventListener("click", () => select(null));
    document.addEventListener("keydown", (ev) => {
      if (ev.target.tagName === "INPUT") return;
      if (ev.key === "Escape" || ev.key === "Backspace") { ev.preventDefault(); up(); }
      else if (ev.key === "Enter" && state.selected && NODES[state.selected]) enter(state.selected);
      else if (ev.key === "ArrowRight" || ev.key === "ArrowLeft") {
        if (!isContainer(state.current)) return;
        const ids = Object.keys(CHUNKS[state.current].layout);
        const i = ids.indexOf(state.selected);
        const next = i < 0 ? ids[0] : ids[(i + (ev.key === "ArrowRight" ? 1 : ids.length - 1)) % ids.length];
        select(next);
      }
    });
    renderChanges();
    const hash = decodeURIComponent((location.hash || "").slice(1));
    const start = hash && (hash === "root" || NODES[hash]) && !(NODES[hash] && NODES[hash].external) ? hash : "root";
    state.current = start;
    mount(start);
    setViewBox({ x: 0, y: 0, w: W, h: H });
    refreshChrome();
  }

  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", boot);
  else boot();
})();
