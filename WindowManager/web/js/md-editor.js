// The markdown editors' table helper, DOM side (the table logic is md-table.js). rtm.markdownEditor(textarea) is called by each component that shows a
// markdown textarea (the worklog's note and description editors, the note page); calling it again for the same textarea does nothing.
//  - "/table" alone on a line opens a grid at the caret (arrow keys and Enter, or the mouse) that swaps it for a table.
//  - With the caret in a pipe table, a grid editor (the "overlay") is laid exactly over the table's lines: cells to select, edit in place, copy and paste,
//    row and column headers to select whole rows and columns, and a ribbon above it with the row and column commands. "Edit as text" hides it and leaves the
//    raw markdown; the overlay is back when the caret leaves the table and comes in again, or from the ribbon's "Grid" button.
//  - The markdown stays the source of truth: every committed change rewrites the table's lines in the textarea (one undo step), and the overlay re-reads
//    the textarea after any change made there (typing, undo).
// Every change goes through document.execCommand("insertText") on a selection of the replaced range, so it is one step of the textarea's own undo
// and the browser fires the same input event as for typing (Blazor's binding sees it). setRangeText plus a dispatched input event is the fallback
// for a browser that refuses the command (or when the textarea may not take the focus); that one is not undoable.
// The overlay is a child of the textarea's parent (so focus inside it is focus inside the editor for the components' focusout checks), fixed-positioned
// over the table's lines and clipped to the textarea's visible box. While a cell is being edited the focus is in its <input>; a blur that moves the
// focus from the textarea into the overlay is hidden from the page (a Blazor @onblur on the textarea would end the edit).
(function () {
  const T = rtm.mdTable;
  const MAX = 8;
  let gridEl = null, barEl = null, gridFor = null, gridPick = { r: 3, c: 3 }, barFor = null, raf = 0;
  // the overlay: one at a time (the focused editor's)
  const G = { ta: null, el: null, mo: null, model: null, text: "", start: -1, found: null, an: { r: 0, c: 0 }, fo: { r: 0, c: 0 }, editing: null, drag: null, off: false, measure: null };

  // ── the textarea ──

  // Swap a range of the textarea's text (from md-table's replace()) and select the cell it names. noFocus: the textarea may not take the focus (a click elsewhere
  // is under way): the text is set without the undo step and the selection is left alone.
  function edit(ta, r, noFocus) {
    if (!noFocus) ta.focus();
    if (ta.value.slice(r.rangeStart, r.rangeEnd) !== r.replacement) {
      let ok = false;
      if (document.activeElement === ta) {
        ta.setSelectionRange(r.rangeStart, r.rangeEnd);
        try { ok = document.execCommand("insertText", false, r.replacement); } catch { }
      }
      if (!ok || ta.value !== r.text) {
        ta.setRangeText(r.replacement, r.rangeStart, r.rangeEnd, noFocus ? "preserve" : "end");
        ta.dispatchEvent(new Event("input", { bubbles: true }));
      }
    }
    if (!noFocus) ta.setSelectionRange(r.start, r.end);
  }

  // Where a character is on screen: a hidden copy of the textarea's text up to it, in the same font and width.
  function mirror(ta) {
    const cs = getComputedStyle(ta), d = document.createElement("div");
    for (const p of ["paddingTop", "paddingRight", "paddingBottom", "paddingLeft", "fontFamily", "fontSize", "fontWeight", "fontStyle", "letterSpacing", "lineHeight", "wordSpacing", "textIndent", "tabSize", "textTransform"]) d.style[p] = cs[p];
    Object.assign(d.style, { position: "absolute", visibility: "hidden", top: "0", left: "-9999px", boxSizing: "border-box", width: ta.clientWidth + "px", whiteSpace: "pre-wrap", overflowWrap: "break-word", border: "0" });
    return { d, cs };
  }
  function marker() { const m = document.createElement("span"); m.textContent = "​"; return m; }

  function caretBox(ta, pos) {
    const { d, cs } = mirror(ta);
    d.textContent = ta.value.slice(0, pos);
    const mark = marker();
    d.appendChild(mark);
    document.body.appendChild(d);
    const box = ta.getBoundingClientRect();
    const out = { x: box.left + ta.clientLeft + mark.offsetLeft - ta.scrollLeft, y: box.top + ta.clientTop + mark.offsetTop - ta.scrollTop, h: mark.offsetHeight || parseFloat(cs.lineHeight) || 20, box };
    d.remove();
    return out;
  }

  // The vertical extent of the text from..to in the textarea's content (before scrolling): the top of its first line, the bottom of its last. Cached for the same text and width.
  function lineSpan(ta, from, to) {
    const k = G.measure;
    if (k && k.ta === ta && k.value === ta.value && k.from === from && k.to === to && k.w === ta.clientWidth) return k.out;
    const { d, cs } = mirror(ta);
    const lh = parseFloat(cs.lineHeight) || (parseFloat(cs.fontSize) || 14) * 1.4;
    const line = () => { const m = marker(); m.style.cssText = `display:inline-block;width:0;height:${lh}px;vertical-align:top`; return m; };   // exactly one line box high
    const a = line(), b = line();
    d.append(ta.value.slice(0, from), a, ta.value.slice(from, to), b);
    document.body.appendChild(d);
    const out = { top: a.offsetTop, bottom: b.offsetTop + b.offsetHeight };
    d.remove();
    G.measure = { ta, value: ta.value, from, to, w: ta.clientWidth, out };
    return out;
  }

  // The part of the window where the textarea can be seen: its box, cut by every scrolling ancestor.
  function visibleBox(ta) {
    const b = ta.getBoundingClientRect();
    let top = b.top + ta.clientTop, bottom = top + ta.clientHeight, left = b.left, right = b.right;
    for (let e = ta.parentElement; e && e !== document.documentElement; e = e.parentElement) {
      const o = getComputedStyle(e);
      if (/(auto|scroll|hidden|clip)/.test(o.overflowY + o.overflowX)) {
        const r = e.getBoundingClientRect();
        top = Math.max(top, r.top); bottom = Math.min(bottom, r.bottom); left = Math.max(left, r.left); right = Math.min(right, r.right);
      }
    }
    return { top, bottom, left, right };
  }

  // A popup fixed next to a caret box: below it, or above when there is no room; kept inside the window.
  function place(el, at, preferAbove) {
    const w = el.offsetWidth, h = el.offsetHeight, vw = innerWidth, vh = innerHeight;
    let top = preferAbove ? at.y - h - 6 : at.y + at.h + 4;
    if (preferAbove ? top < 4 : top + h > vh - 4) top = preferAbove ? at.y + at.h + 4 : at.y - h - 6;
    el.style.left = Math.max(4, Math.min(at.x, vw - w - 4)) + "px";
    el.style.top = Math.max(4, Math.min(top, vh - h - 4)) + "px";
  }

  // ── the /table grid ──

  function closeGrid() { gridEl?.remove(); gridEl = null; gridFor = null; }

  function paintGrid() {
    if (!gridEl) return;
    gridEl.querySelectorAll(".md-gc").forEach(c => c.classList.toggle("on", +c.dataset.r <= gridPick.r && +c.dataset.c <= gridPick.c));
    gridEl.querySelector(".md-gl").textContent = gridPick.r + " × " + gridPick.c;
  }

  function openGrid(ta) {
    if (gridFor === ta && gridEl) return;
    closeGrid();
    gridFor = ta;
    gridPick = { r: 3, c: 3 };
    gridEl = document.createElement("div");
    gridEl.className = "md-pop md-grid";
    gridEl.setAttribute("role", "dialog");
    gridEl.setAttribute("aria-label", "Insert a table");
    gridEl.innerHTML = '<div class="md-gg">' + Array.from({ length: MAX * MAX }, (_, i) => `<i class="md-gc" data-r="${Math.floor(i / MAX) + 1}" data-c="${(i % MAX) + 1}"></i>`).join("") + '</div><div class="md-gl"></div><div class="md-gh">Arrows to size, Enter to insert, Esc to cancel</div>';
    gridEl.addEventListener("mousedown", (e) => e.preventDefault());   // the textarea keeps the focus (leaving it would end the edit)
    gridEl.addEventListener("mouseover", (e) => { const c = e.target.closest?.(".md-gc"); if (c) { gridPick = { r: +c.dataset.r, c: +c.dataset.c }; paintGrid(); } });
    gridEl.addEventListener("click", (e) => { if (e.target.closest?.(".md-gc")) insertTable(ta); });
    document.body.appendChild(gridEl);
    paintGrid();
    place(gridEl, caretBox(ta, ta.selectionStart), false);
  }

  function insertTable(ta) {
    const slash = T.slashAt(ta.value, ta.selectionStart);
    const { r, c } = gridPick;
    closeGrid();
    if (slash) edit(ta, T.insertAt(ta.value, slash, r, c));
    refresh(ta);
  }

  // ── the ribbon ──

  function closeBar() { barEl?.remove(); barEl = null; barFor = null; }

  function openBar(ta) {
    if (barEl && barFor === ta) return;
    closeBar();
    barFor = ta;
    barEl = document.createElement("div");
    barEl.className = "md-pop md-bar";
    barEl.setAttribute("role", "toolbar");
    barEl.setAttribute("aria-label", "Table");
    const btn = (b) => `<button type="button" class="md-b${b[3] ? " ic" : ""}" data-op="${b[0]}" title="${b[2]}" aria-label="${b[2]}">${b[3] ?? b[1]}</button>`;
    const groups = [[]];
    for (const b of T.ribbon) b === "|" ? groups.push([]) : groups.at(-1).push(b);
    barEl.innerHTML = groups.map(g => `<span class="md-grp">${g.map(btn).join("")}</span>`).join('<span class="md-sep"></span>');   // a group wraps as a whole
    barEl.style.maxWidth = Math.max(420, Math.min(innerWidth - 8, ta.clientWidth)) + "px";   // wraps inside the editor's width, not the window's
    barEl.addEventListener("mousedown", (e) => e.preventDefault());
    barEl.addEventListener("click", (e) => {
      const b = e.target.closest?.(".md-b");
      if (!b) return;
      const [op, arg] = b.dataset.op.split(":");
      ribbon(ta, op, arg);
    });
    document.body.appendChild(barEl);
  }

  function ribbon(ta, op, arg) {
    if (op === "mode") {
      G.off = !G.off;
      if (G.off) { endEdit(); closeOv(); ta.focus(); }
      refresh(ta);
      return;
    }
    if (gridOn(ta)) {
      endEdit();
      const res = T.applyOp(G.model, rect(), op, arg);
      if (res) write(res.table, res.sel);
      return;
    }
    const r = T.apply(ta.value, ta.selectionStart, op, arg);
    if (r) edit(ta, r);
    refresh(ta);
  }

  function updateBar(ta, f) {
    if (!f) { closeBar(); return; }
    openBar(ta);
    const model = gridOn(ta) ? G.model : T.parse(f.text);
    const col = gridOn(ta) ? G.an.c : f.col;
    const align = model.align[col] || "";
    barEl.querySelectorAll(".md-b[data-op^='align']").forEach(b => b.classList.toggle("on", b.dataset.op === "align:" + align));
    const mode = barEl.querySelector(".md-b[data-op='mode']");
    mode.textContent = G.off ? "Grid" : "Edit as text";
    mode.title = mode.ariaLabel = G.off ? "Edit the table in the grid" : "Edit the table as markdown";
    mode.classList.toggle("on", G.off);
    if (gridOn(ta) && G.el) {
      const b = G.el.getBoundingClientRect(), v = visibleBox(ta);
      place(barEl, { x: b.left, y: Math.max(b.top, v.top), h: 0 }, true);
    } else {
      let at = caretBox(ta, f.start);
      if (at.y < at.box.top) at = caretBox(ta, ta.selectionStart);   // the table's first line has scrolled out of the box: sit above the caret line
      place(barEl, at, true);
    }
  }

  // ── the grid overlay ──

  const gridOn = (ta) => !!G.el && G.ta === ta && !G.off;
  const esc = (s) => s.replace(/[&<>"]/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));
  const letters = (n) => { let s = ""; for (n++; n > 0; n = Math.floor((n - 1) / 26)) s = String.fromCharCode(65 + (n - 1) % 26) + s; return s; };
  const rows = () => G.model.rows.length, cols = () => G.model.rows[0].length;
  const clamp = (v, lo, hi) => Math.max(lo, Math.min(hi, v));
  const rect = () => ({ r0: Math.min(G.an.r, G.fo.r), c0: Math.min(G.an.c, G.fo.c), r1: Math.max(G.an.r, G.fo.r), c1: Math.max(G.an.c, G.fo.c) });
  const cellEl = (r, c) => G.el?.querySelector(`td[data-r="${r}"][data-c="${c}"]`);

  function closeOv() {
    G.mo?.disconnect(); G.mo = null;
    G.el?.remove();
    Object.assign(G, { ta: null, el: null, model: null, text: "", start: -1, found: null, editing: null, drag: null, measure: null });
  }

  function createOv(ta) {
    closeOv();
    G.ta = ta;
    G.el = document.createElement("div");
    G.el.className = "md-ov";
    G.el.setAttribute("role", "grid");
    G.el.setAttribute("aria-label", "Table editor");
    ta.parentElement.appendChild(G.el);
    G.mo = new MutationObserver(() => { if (!ta.isConnected || !G.el?.isConnected) { closeOv(); closeBar(); } });
    G.mo.observe(ta.parentElement, { childList: true });
    const el = G.el;
    el.addEventListener("mousedown", ovMouseDown);
    el.addEventListener("mouseover", (e) => { if (G.drag) dragTo(e.target); });
    el.addEventListener("dblclick", (e) => { const td = e.target.closest("td.md-cell"); if (td) startEdit(+td.dataset.r, +td.dataset.c); });
    el.addEventListener("wheel", (e) => {   // the textarea scrolls under the overlay
      if (e.shiftKey || Math.abs(e.deltaX) > Math.abs(e.deltaY) || ta.scrollHeight <= ta.clientHeight) return;
      ta.scrollTop += e.deltaY;
      e.preventDefault();
    }, { passive: false });
  }

  function renderTable() {
    const t = G.model, nc = cols(), keep = G.el.querySelector(".md-gx")?.scrollLeft || 0;
    let h = '<div class="md-gx"><table class="md-t"><tr class="md-lr"><th class="md-corner" title="Select all"></th>';
    for (let c = 0; c < nc; c++) h += `<th class="md-ch" data-c="${c}">${letters(c)}</th>`;
    h += "</tr>";
    t.rows.forEach((row, r) => {
      h += `<tr class="${r === 0 ? "md-hd" : "md-br"}"><th class="md-rh" data-r="${r}">${r + 1}</th>`;
      row.forEach((v, c) => { h += `<td class="md-cell${t.align[c] ? " al-" + t.align[c] : ""}" data-r="${r}" data-c="${c}"><div class="md-v">${esc(T.unescapeCell(v))}</div></td>`; });
      h += "</tr>";
    });
    G.el.innerHTML = h + "</table></div>";
    G.el.querySelector(".md-gx").scrollLeft = keep;
    G.editing = null;
  }

  function paintSel() {
    if (!G.el) return;
    const s = rect();
    G.el.querySelectorAll("td.md-cell").forEach(td => {
      const r = +td.dataset.r, c = +td.dataset.c;
      td.classList.toggle("sel", r >= s.r0 && r <= s.r1 && c >= s.c0 && c <= s.c1);
      td.classList.toggle("act", r === G.an.r && c === G.an.c);
    });
    G.el.querySelectorAll("th.md-rh").forEach(th => th.classList.toggle("sel", +th.dataset.r >= s.r0 && +th.dataset.r <= s.r1));
    G.el.querySelectorAll("th.md-ch").forEach(th => th.classList.toggle("sel", +th.dataset.c >= s.c0 && +th.dataset.c <= s.c1));
  }

  // The background the text sits on: the first opaque one up from the textarea, so the overlay hides the raw lines without showing as a box.
  function surface(ta) {
    for (let e = ta; e && e.nodeType === 1; e = e.parentElement) {
      const bg = getComputedStyle(e).backgroundColor;
      if (bg && !/^(transparent|rgba\(0, 0, 0, 0\))$/.test(bg)) return bg;
    }
    return "var(--card)";
  }

  function position() {
    const ta = G.ta, el = G.el, f = G.found;
    if (!ta || !el || !f) return;
    const cs = getComputedStyle(ta), b = ta.getBoundingClientRect(), v = visibleBox(ta);
    const span = lineSpan(ta, f.start, f.end);
    const padL = parseFloat(cs.paddingLeft) || 0, padR = parseFloat(cs.paddingRight) || 0;
    const fs = parseFloat(cs.fontSize) || 14, lh = parseFloat(cs.lineHeight) || fs * 1.5;
    const top = b.top + ta.clientTop + span.top - ta.scrollTop;
    Object.assign(el.style, { left: b.left + ta.clientLeft + padL + "px", top: top + "px", width: Math.max(80, ta.clientWidth - padL - padR) + "px",
      fontFamily: cs.fontFamily, fontSize: cs.fontSize, background: surface(ta) });
    el.style.setProperty("--lh", lh + "px");
    const h = Math.max(span.bottom - span.top, el.querySelector(".md-gx")?.offsetHeight || 0);
    el.style.height = h + "px";
    const cut = (n) => Math.max(0, n) + "px";   // clip to what the textarea shows
    el.style.clipPath = `inset(${cut(v.top - top)} 0 ${cut(top + h - v.bottom)} 0)`;
    el.style.visibility = top >= v.bottom || top + h <= v.top ? "hidden" : "";
  }

  // Keep the active cell in view: scroll the textarea (the overlay follows it) and the grid sideways.
  function reveal() {
    const td = cellEl(G.fo.r, G.fo.c), ta = G.ta;
    if (!td || !ta) return;
    const v = visibleBox(ta), r = td.getBoundingClientRect();
    if (r.top < v.top) ta.scrollTop -= v.top - r.top; else if (r.bottom > v.bottom) ta.scrollTop += r.bottom - v.bottom;
    const gx = G.el.querySelector(".md-gx"), g = gx.getBoundingClientRect();
    if (r.left < g.left + 40) gx.scrollLeft -= g.left + 40 - r.left; else if (r.right > g.right) gx.scrollLeft += r.right - g.right;
    position();
  }

  // The textarea's caret goes into the active cell's text, so the caret stays in the table (and the raw markdown agrees with the grid).
  function syncCaret() {
    const ta = G.ta;
    if (!ta || !G.found) return;
    const o = T.cellOffset(ta.value, G.found, G.an.r, G.an.c);
    ta.setSelectionRange(o.start, o.end);
  }

  function showOv(ta, f) {
    if (!G.el || G.ta !== ta) {
      createOv(ta);
      G.an = { r: f.row, c: f.col }; G.fo = { ...G.an };
    }
    if (G.editing) { G.found = f; position(); return; }
    if (f.text !== G.text || f.start !== G.start || !G.model) {
      G.model = T.parse(f.text); G.text = f.text; G.start = f.start;
      for (const p of [G.an, G.fo]) { p.r = clamp(p.r, 0, rows() - 1); p.c = clamp(p.c, 0, cols() - 1); }
      G.found = f;
      renderTable();
    }
    G.found = f;
    paintSel();
    position();
  }

  function update(ta) {
    const inOv = !!G.el && G.el.contains(document.activeElement);
    const f = (document.activeElement === ta || inOv) && !gridEl ? T.find(ta.value, ta.selectionStart) : null;
    if (!f) { G.off = false; closeOv(); closeBar(); return; }
    if (G.off) closeOv(); else showOv(ta, f);
    updateBar(ta, f);
  }

  // A change made through the grid: the table's lines are rewritten with the new table (formatted), the selection is shown on it.
  function write(table, s) {
    const ta = G.ta;
    if (!ta) return;
    const f = T.find(ta.value, G.found ? G.found.start : ta.selectionStart) || T.find(ta.value, ta.selectionStart);
    if (!f) return;
    G.an = { r: s.r0, c: s.c0 }; G.fo = { r: s.r1, c: s.c1 };
    edit(ta, T.rewrite(ta.value, f, table, s.r0, s.c0));
    cancelAnimationFrame(raf);
    G.measure = null;
    update(ta);
    reveal();
  }

  // ── the overlay's mouse ──

  function extend(kind, r, c) {
    if (kind === "row") { G.fo = { r, c: cols() - 1 }; }
    else if (kind === "col") { G.fo = { r: rows() - 1, c }; }
    else G.fo = { r, c };
    paintSel();
  }

  function ovMouseDown(e) {
    if (e.target.closest(".md-in")) return;   // the cell being edited is a real input
    e.preventDefault();
    if (e.button !== 0) return;
    endEdit();
    const th = e.target.closest("th,td");
    if (!th || !G.el.contains(th)) return;
    const kind = th.classList.contains("md-corner") ? "all" : th.classList.contains("md-rh") ? "row" : th.classList.contains("md-ch") ? "col" : "cell";
    const r = +th.dataset.r, c = +th.dataset.c;
    if (kind === "all") { G.an = { r: 0, c: 0 }; G.fo = { r: rows() - 1, c: cols() - 1 }; }
    else if (kind === "row") { if (!e.shiftKey) G.an = { r, c: 0 }; else G.an.c = 0; extend("row", r, c); }
    else if (kind === "col") { if (!e.shiftKey) G.an = { r: 0, c }; else G.an.r = 0; extend("col", r, c); }
    else { if (!e.shiftKey) G.an = { r, c }; extend("cell", r, c); }
    G.drag = kind === "all" ? null : kind;
    if (document.activeElement !== G.ta) G.ta.focus();
    syncCaret();
    paintSel();
    if (barEl) updateBar(G.ta, G.found);
  }

  function dragTo(target) {
    const th = target.closest?.("th,td");
    if (!th || !G.el.contains(th) || th.classList.contains("md-corner")) return;
    const r = +th.dataset.r, c = +th.dataset.c;
    if (G.drag === "row" && !isNaN(r)) extend("row", r, 0);
    else if (G.drag === "col" && !isNaN(c)) extend("col", 0, c);
    else if (G.drag === "cell" && !isNaN(r) && !isNaN(c)) extend("cell", r, c);
  }
  addEventListener("mouseup", () => { G.drag = null; });

  // ── editing a cell in place ──

  function startEdit(r, c, initial) {
    if (!G.el || G.editing) return;
    const td = cellEl(r, c);
    if (!td) return;
    G.an = { r, c }; G.fo = { r, c };
    paintSel();
    const input = document.createElement("input");
    input.className = "md-in";
    input.setAttribute("aria-label", "Cell " + letters(c) + (r + 1));
    const orig = T.unescapeCell(G.model.rows[r][c]);   // what the cell holds now: a first typed character (initial) is a change to it, not the starting point
    input.value = initial ?? orig;
    td.appendChild(input);
    G.editing = { r, c, input, orig };
    input.focus();
    input.setSelectionRange(input.value.length, input.value.length);
    input.addEventListener("keydown", (e) => {
      if (e.isComposing) return;
      const k = e.key;
      if (k === "Enter" || k === "Tab") { e.preventDefault(); e.stopPropagation(); finishEdit(k === "Enter" ? (e.shiftKey ? -1 : 1) : 0, k === "Tab" ? (e.shiftKey ? -1 : 1) : 0); }
      else if (k === "ArrowDown" || k === "ArrowUp") { e.preventDefault(); e.stopPropagation(); finishEdit(k === "ArrowDown" ? 1 : -1, 0); }   // a one-line input has no use for them: commit and move; off the last / first row the table is left
      else if (k === "Escape") { e.preventDefault(); e.stopPropagation(); cancelEdit(); }
    });
    input.addEventListener("blur", (e) => {   // the focus went elsewhere (a click on the text, outside the editor): keep what was typed
      if (G.editing?.input !== input) return;
      commit(false, true);
      const to = e.relatedTarget;
      // a click outside the editor leaves the table; later than this blur, so the focusout that follows it still reaches the page (the input is still in the document)
      if (document.hasFocus() && to !== G.ta && !(to && G.el?.contains(to))) setTimeout(() => { if (!G.editing && document.activeElement !== G.ta && !G.el?.contains(document.activeElement)) { closeOv(); closeBar(); } }, 0);
    });
  }

  // Take the typed text into the table (noMove: stay on the cell). Returns whether the textarea changed.
  function commit(refocus, blurred) {
    const ed = G.editing;
    if (!ed) return;
    G.editing = null;
    const ta = G.ta, same = ed.input.value === ed.orig;
    if (refocus && !blurred) ta.focus();
    if (same) { ed.input.remove(); return; }
    const table = T.setCell(G.model, ed.r, ed.c, ed.input.value);
    const f = T.find(ta.value, G.found ? G.found.start : ta.selectionStart);
    if (!f) { ed.input.remove(); return; }
    if (blurred) {
      edit(ta, T.rewrite(ta.value, f, table, ed.r, ed.c), true);
      G.model = table;
      return;
    }
    write(table, { r0: ed.r, c0: ed.c, r1: ed.r, c1: ed.c });
  }

  function endEdit() { if (G.editing) commit(true); }
  function cancelEdit() {
    const ed = G.editing;
    if (!ed) return;
    G.editing = null;
    ed.input.remove();
    G.ta.focus();
  }
  // Enter / Tab: commit and go to the neighbouring cell (dr, dc); Tab from the last cell adds a row.
  function finishEdit(dr, dc) {
    const ed = G.editing;
    if (!ed) return;
    commit(true);
    if (!G.ta || !G.model) return;
    if (dc === 0 && (ed.r + dr < 0 || ed.r + dr >= rows())) exitGrid(G.ta, dr > 0 ? "after" : "before");   // Enter / ArrowDown on the last row, ArrowUp on the first
    else if (dc > 0 && ed.c + dc >= cols() && ed.r === rows() - 1) {
      const n = T.applyOp(G.model, { r0: ed.r, c0: 0, r1: ed.r, c1: cols() - 1 }, "insertRowBelow");
      write(n.table, { r0: ed.r + 1, c0: 0, r1: ed.r + 1, c1: 0 });
    } else go(ed.r + dr, ed.c + dc, false, dc !== 0);
  }

  // ── the textarea's keyboard in grid mode ──

  function go(r, c, extendSel, wrap) {
    if (wrap) { if (c >= cols()) { c = 0; r++; } else if (c < 0) { c = cols() - 1; r--; } }
    r = clamp(r, 0, rows() - 1); c = clamp(c, 0, cols() - 1);
    G.fo = { r, c };
    if (!extendSel) G.an = { r, c };
    syncCaret();
    paintSel();
    reveal();
    if (barEl) updateBar(G.ta, G.found);
  }

  // Leave the grid for the text after ("after") or before ("before") the table: the caret goes to that line (an empty one is made at the end / start of the text).
  function exitGrid(ta, where) {
    G.off = true;
    endEdit();
    const f = T.find(ta.value, G.found ? G.found.start : ta.selectionStart);
    closeOv();
    if (f) edit(ta, T.leave(ta.value, f, where));
    refresh(ta);
  }

  function gridKey(ta, e) {
    const mod = e.ctrlKey || e.metaKey;
    const move = e.altKey && !mod && !e.shiftKey ? T.altMove(e.key) : null;   // Alt+arrow moves the selected rows / columns (no ribbon buttons for it)
    if (move) { endEdit(); const res = T.applyOp(G.model, rect(), move); if (res) write(res.table, res.sel); return true; }
    if (mod) {
      if (!e.altKey && (e.key === "a" || e.key === "A")) { G.an = { r: 0, c: 0 }; G.fo = { r: rows() - 1, c: cols() - 1 }; paintSel(); return true; }
      if (e.altKey || (e.key !== "Home" && e.key !== "End")) return false;   // Ctrl+C / X / V come as clipboard events; Ctrl+Z is the textarea's
    }
    if (e.altKey) return false;
    const a = G.an, f = G.fo, s = e.shiftKey;
    const nav = T.navigate(e.key, f, rows(), cols(), mod);
    if (nav) {   // arrows, Home / End (with Ctrl: of the table); an arrow past the last / first row leaves the table, unless a range is being extended
      if (nav.leave) { if (!s) exitGrid(ta, nav.leave); }
      else go(nav.r, nav.c, s);
      return true;
    }
    switch (e.key) {
      case "Tab": {
        const nc = a.c + (s ? -1 : 1);
        if (!s && nc >= cols() && a.r === rows() - 1) { const r = T.applyOp(G.model, { r0: a.r, c0: 0, r1: a.r, c1: cols() - 1 }, "insertRowBelow"); write(r.table, { r0: a.r + 1, c0: 0, r1: a.r + 1, c1: 0 }); }
        else go(a.r, nc, false, true);
        return true;
      }
      case "Enter": case "F2": startEdit(a.r, a.c); return true;
      case "Delete": case "Backspace": { const r = T.applyOp(G.model, rect(), "clear"); write(r.table, r.sel); return true; }
      case "Escape": exitGrid(ta, "after"); return true;
      default:
        if (e.key.length === 1) { startEdit(a.r, a.c, e.key); return true; }
        return false;
    }
  }

  function refresh(ta) {
    cancelAnimationFrame(raf);
    raf = requestAnimationFrame(() => {
      if (!ta.isConnected) { closeGrid(); closeBar(); closeOv(); return; }
      if (gridFor === ta && !T.slashAt(ta.value, ta.selectionStart)) closeGrid();
      update(ta);
    });
  }

  const again = () => { const ta = G.ta || barFor; if (ta) refresh(ta); };
  addEventListener("scroll", () => { again(); if (gridFor && !gridFor.isConnected) closeGrid(); }, true);
  addEventListener("resize", again);
  // the textarea giving the focus to a cell input is not leaving the editor: hide that blur from the page (an @onblur on the textarea would end the edit)
  for (const type of ["blur", "focusout"]) addEventListener(type, (e) => { if (G.el && e.target === G.ta && e.relatedTarget && G.el.contains(e.relatedTarget)) e.stopImmediatePropagation(); }, true);

  // ── attaching ──

  rtm.markdownEditor = function (ta) {
    if (!ta || ta._mdTable) return;
    ta._mdTable = true;

    ta.addEventListener("input", () => {
      if (T.slashAt(ta.value, ta.selectionStart)) openGrid(ta);
      refresh(ta);
    });
    ta.addEventListener("keyup", () => refresh(ta));
    ta.addEventListener("mouseup", () => refresh(ta));
    ta.addEventListener("focus", () => refresh(ta));
    ta.addEventListener("scroll", () => refresh(ta));
    ta.addEventListener("blur", (e) => {
      if (e.relatedTarget && G.el?.contains(e.relatedTarget)) return;
      closeGrid(); closeBar(); closeOv();
    });

    ta.addEventListener("copy", (e) => {
      if (!gridOn(ta) || G.editing) return;
      e.clipboardData.setData("text/plain", T.toTsv(G.model, rect()));
      e.preventDefault();
    });
    ta.addEventListener("cut", (e) => {
      if (!gridOn(ta) || G.editing) return;
      e.clipboardData.setData("text/plain", T.toTsv(G.model, rect()));
      e.preventDefault();
      const r = T.applyOp(G.model, rect(), "clear");
      write(r.table, r.sel);
    });
    ta.addEventListener("paste", (e) => {
      if (!gridOn(ta) || G.editing || e.clipboardData.files?.length) return;
      const text = e.clipboardData.getData("text/plain");
      if (!text) return;
      e.preventDefault();
      e.stopPropagation();
      const s = rect(), r = T.pasteRange(G.model, s.r0, s.c0, text);
      write(r.table, r.sel);
    });

    ta.addEventListener("keydown", (e) => {
      if (e.isComposing) return;
      if (gridFor === ta && gridEl) {
        const d = { ArrowLeft: [0, -1], ArrowRight: [0, 1], ArrowUp: [-1, 0], ArrowDown: [1, 0] }[e.key];
        if (d) {
          gridPick = { r: Math.max(1, Math.min(MAX, gridPick.r + d[0])), c: Math.max(1, Math.min(MAX, gridPick.c + d[1])) };
          paintGrid();
        } else if (e.key === "Enter") insertTable(ta);
        else if (e.key === "Escape") closeGrid();
        else return;
        e.preventDefault();
        e.stopPropagation();
        return;
      }
      if (gridOn(ta) && G.model && !G.editing) {
        if (gridKey(ta, e)) { e.preventDefault(); e.stopPropagation(); }
        return;
      }
      if (e.ctrlKey || e.metaKey || e.altKey) return;
      let r = null;
      if (e.key === "Tab") r = (e.shiftKey ? T.prevCell : T.nextCell)(ta.value, ta.selectionStart);
      else if (e.key === "Enter" && !e.shiftKey && ta.selectionStart === ta.selectionEnd) r = T.enter(ta.value, ta.selectionStart);
      if (!r) return;
      e.preventDefault();
      edit(ta, r);
      refresh(ta);
    });

    // typing got ahead of the attach (the helper is attached after a render): "/table" is already at the caret, so open the grid now
    if (document.activeElement === ta && T.slashAt(ta.value, ta.selectionStart)) openGrid(ta);
    refresh(ta);
  };
})();
