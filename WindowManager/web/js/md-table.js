// Markdown pipe tables as pure functions (no DOM): the markdown editors' table helper (md-editor.js) calls these, tests/md-table.test.mjs tests them.
// A table in a document is a run of lines that start with "|", whose second line is the separator row (| --- | :-: |); lines inside code fences do not count.
// The model: { rows: string[][], align: ("" | "left" | "center" | "right")[] }. rows[0] is the header, rows[1..] the body; the separator row is `align`.
// Documents are strings, carets are offsets; every width is the string length (each character counts 1).
(function (root, factory) {
  const api = factory();
  if (typeof module === "object" && module.exports) module.exports = api;
  else (root.rtm = root.rtm || {}).mdTable = api;
})(typeof self !== "undefined" ? self : this, function () {
  const SEP_CELL = /^:?-+:?$/;
  const width = (s) => [...s].length;

  // The cells of one line: text (trimmed), start/end (the trimmed text in the line), from/to (between the pipes). An empty cell has start = end,
  // just after the pipe and its space. A pipe after a backslash or inside a code span is not a divider.
  function splitRow(line) {
    const cells = [];
    let i = 0;
    while (i < line.length && (line[i] === " " || line[i] === "\t")) i++;
    if (line[i] === "|") i++;
    let from = i;
    const push = (to) => {
      let s = from, e = to;
      while (s < e && /\s/.test(line[s])) s++;
      while (e > s && /\s/.test(line[e - 1])) e--;
      if (s === e) s = e = Math.min(from + 1, to);
      cells.push({ text: line.slice(s, e), from, to, start: s, end: e });
    };
    for (let k = i; k < line.length; k++) {
      const c = line[k];
      if (c === "\\") { k++; continue; }
      if (c === "`") {
        let n = 1;
        while (line[k + n] === "`") n++;
        const close = line.indexOf("`".repeat(n), k + n);
        k = close >= 0 ? close + n - 1 : k + n - 1;
        continue;
      }
      if (c === "|") { push(k); from = k + 1; }
    }
    if (line.slice(from).trim() !== "") push(line.length);
    return cells;
  }

  const isSeparator = (line) => { const c = splitRow(line); return c.length > 0 && c.every(x => SEP_CELL.test(x.text)); };
  const alignOf = (cell) => { const l = cell.startsWith(":"), r = cell.endsWith(":"); return l && r ? "center" : l ? "left" : r ? "right" : ""; };

  function parse(text) {
    const lines = text.replace(/\r/g, "").split("\n");
    const head = splitRow(lines[0]).map(c => c.text);
    const sep = lines.length > 1 ? splitRow(lines[1]).map(c => alignOf(c.text)) : [];
    const body = lines.slice(2).map(l => splitRow(l).map(c => c.text));
    const cols = Math.max(1, head.length, sep.length, ...body.map(r => r.length));
    const pad = (r) => Object.assign(Array(cols).fill(""), r);
    return { rows: [pad(head), ...body.map(pad)], align: Object.assign(Array(cols).fill(""), sep) };
  }

  function format(t) {
    const cols = t.rows[0].length;
    const w = Array.from({ length: cols }, (_, c) => Math.max(3 + (t.align[c] === "center" ? 2 : t.align[c] ? 1 : 0), ...t.rows.map(r => width(r[c]))));
    const cell = (s, c) => {
      const room = w[c] - width(s);
      if (t.align[c] === "right") return " ".repeat(room) + s;
      if (t.align[c] === "center") return " ".repeat(Math.floor(room / 2)) + s + " ".repeat(room - Math.floor(room / 2));
      return s + " ".repeat(room);
    };
    const line = (r) => "| " + r.map((s, c) => cell(s, c)).join(" | ") + " |";
    const dashes = (c) => {
      const a = t.align[c];
      return a === "center" ? ":" + "-".repeat(w[c] - 2) + ":" : a === "left" ? ":" + "-".repeat(w[c] - 1) : a === "right" ? "-".repeat(w[c] - 1) + ":" : "-".repeat(w[c]);
    };
    return [line(t.rows[0]), "| " + w.map((_, c) => dashes(c)).join(" | ") + " |", ...t.rows.slice(1).map(line)].join("\n");
  }

  const copy = (t) => ({ rows: t.rows.map(r => [...r]), align: [...t.align] });

  function insertRow(t, r, below) {
    if (r === 0 && !below) return t;
    const n = copy(t);
    n.rows.splice(below ? r + 1 : r, 0, Array(t.rows[0].length).fill(""));
    return n;
  }
  // The header of a new column: "Column N", N one more than the highest "Column <number>" in the header row, else the column count + 1. Never a copy of a neighbour.
  function freshHeader(header) {
    const nums = header.map(h => /^Column (\d+)$/.exec(h)).filter(Boolean).map(m => +m[1]);
    return "Column " + (nums.length ? Math.max(...nums) + 1 : header.length + 1);
  }
  function insertColumn(t, c, right) {
    const n = copy(t), at = right ? c + 1 : c;
    const head = freshHeader(n.rows[0]);
    n.rows.forEach((r, i) => r.splice(at, 0, i === 0 ? head : ""));
    n.align.splice(at, 0, "");
    return n;
  }
  function deleteRow(t, r) {
    if (r === 0) return t;
    const n = copy(t);
    n.rows.splice(r, 1);
    return n;
  }
  function deleteColumn(t, c) {
    if (t.rows[0].length <= 1) return t;
    const n = copy(t);
    n.rows.forEach(r => r.splice(c, 1));
    n.align.splice(c, 1);
    return n;
  }
  function setAlign(t, c, align) {
    const n = copy(t);
    n.align[c] = align === "none" ? "" : align;
    return n;
  }

  // ── operations on a selection { r0, c0, r1, c1 } (rows and columns of the model, r0 <= r1, c0 <= c1): the grid overlay's commands ──

  const escapeCell = (v) => String(v).replace(/\r?\n|\r/g, " ").trim().replace(/(?<!\\)\|/g, "\\|");
  const unescapeCell = (v) => v.replace(/\\\|/g, "|");
  function setCell(t, r, c, value) {
    const n = copy(t);
    n.rows[r][c] = escapeCell(value);
    return n;
  }
  const sel = (r0, c0, r1, c1) => ({ r0, c0, r1, c1 });
  const norm = (s) => sel(Math.min(s.r0, s.r1), Math.min(s.c0, s.c1), Math.max(s.r0, s.r1), Math.max(s.c0, s.c1));

  function toTsv(t, s) {
    const { r0, c0, r1, c1 } = norm(s);
    return t.rows.slice(r0, r1 + 1).map(r => r.slice(c0, c1 + 1).map(unescapeCell).join("\t")).join("\n");
  }

  // A tab-separated block (Excel, a copied range) from (r, c): cells are overwritten, rows and columns are added as it needs them (new columns get a header placeholder).
  function pasteRange(t, r, c, text) {
    const lines = text.replace(/\r\n?/g, "\n").replace(/\n$/, "").split("\n").map(l => l.split("\t"));
    const w = Math.max(...lines.map(l => l.length));
    let n = copy(t);
    while (n.rows[0].length < c + w) n = insertColumn(n, n.rows[0].length - 1, true);
    while (n.rows.length < r + lines.length) n.rows.push(Array(n.rows[0].length).fill(""));
    lines.forEach((l, i) => l.forEach((v, j) => { n.rows[r + i][c + j] = escapeCell(v); }));
    return { table: n, sel: sel(r, c, r + lines.length - 1, c + w - 1) };
  }

  const numeric = (v) => { const x = unescapeCell(v).replace(/[\s,%$€£]/g, ""); return x !== "" && /^[-+]?(\d+\.?\d*|\.\d+)$/.test(x) ? parseFloat(x) : null; };
  function compare(a, b) {
    const x = numeric(a), y = numeric(b);
    return x !== null && y !== null ? x - y : a.localeCompare(b, undefined, { numeric: true, sensitivity: "base" });
  }

  // The result of a command on the selection: { table, sel }, or null when the command does not apply (the header does not move, the last column stays, ...).
  function applyOp(t, s, op, arg) {
    const { r0, c0, r1, c1 } = norm(s), nr = r1 - r0 + 1, nc = c1 - c0 + 1, last = t.rows.length - 1, cols = t.rows[0].length;
    const n = copy(t);
    switch (op) {
      case "insertRowAbove":
        if (r0 === 0) return null;
        n.rows.splice(r0, 0, ...Array.from({ length: nr }, () => Array(cols).fill("")));
        return { table: n, sel: sel(r0, c0, r0 + nr - 1, c1) };
      case "insertRowBelow":
        n.rows.splice(r1 + 1, 0, ...Array.from({ length: nr }, () => Array(cols).fill("")));
        return { table: n, sel: sel(r1 + 1, c0, r1 + nr, c1) };
      case "insertColumnLeft":
      case "insertColumnRight": {
        const at = op === "insertColumnLeft" ? c0 : c1 + 1;
        for (let k = 0; k < nc; k++) { const head = freshHeader(n.rows[0]); n.rows.forEach((r, i) => r.splice(at + k, 0, i === 0 ? head : "")); }
        n.align.splice(at, 0, ...Array(nc).fill(""));
        return { table: n, sel: sel(r0, at, r1, at + nc - 1) };
      }
      case "deleteRow": {
        const a = Math.max(r0, 1);
        if (a > r1) return null;
        n.rows.splice(a, r1 - a + 1);
        const at = Math.min(a, n.rows.length - 1);
        return { table: n, sel: sel(at, c0, at, c1) };
      }
      case "deleteColumn": {
        if (nc >= cols) return null;
        n.rows.forEach(r => r.splice(c0, nc));
        n.align.splice(c0, nc);
        const at = Math.min(c0, cols - nc - 1);
        return { table: n, sel: sel(r0, at, r1, at) };
      }
      case "moveRowUp":
      case "moveRowDown": {
        const up = op === "moveRowUp";
        if (r0 < 1 || (up ? r0 < 2 : r1 >= last)) return null;
        const block = n.rows.splice(r0, nr);
        const at = up ? r0 - 1 : r0 + 1;
        n.rows.splice(at, 0, ...block);
        return { table: n, sel: sel(at, c0, at + nr - 1, c1) };
      }
      case "moveColumnLeft":
      case "moveColumnRight": {
        const left = op === "moveColumnLeft";
        if (left ? c0 < 1 : c1 >= cols - 1) return null;
        const at = left ? c0 - 1 : c0 + 1;
        n.rows.forEach(r => r.splice(at, 0, ...r.splice(c0, nc)));
        n.align.splice(at, 0, ...n.align.splice(c0, nc));
        return { table: n, sel: sel(r0, at, r1, at + nc - 1) };
      }
      case "align":
        for (let c = c0; c <= c1; c++) n.align[c] = arg === "none" ? "" : arg;
        return { table: n, sel: sel(r0, c0, r1, c1) };
      case "sortAsc":
      case "sortDesc": {
        const sign = op === "sortAsc" ? 1 : -1;
        const body = n.rows.slice(1);
        body.sort((a, b) => (a[c0] === "") - (b[c0] === "") || (a[c0] === "" ? 0 : sign * compare(a[c0], b[c0])));
        n.rows = [n.rows[0], ...body];
        return { table: n, sel: sel(r0, c0, r1, c1) };
      }
      case "clear":
        for (let r = r0; r <= r1; r++) for (let c = c0; c <= c1; c++) n.rows[r][c] = "";
        return { table: n, sel: sel(r0, c0, r1, c1) };
      default: return null;
    }
  }

  // The ribbon's buttons: [op, label, title, icon?]; "|" starts a new group. The moves (moveRowUp ...) are not buttons: the grid maps Alt+arrow to them (altMove).
  const ICON = {
    left: '<svg viewBox="0 0 16 16" width="14" height="14"><path d="M2 3h12M2 6.5h8M2 10h12M2 13.5h8" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" fill="none"/></svg>',
    center: '<svg viewBox="0 0 16 16" width="14" height="14"><path d="M2 3h12M4 6.5h8M2 10h12M4 13.5h8" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" fill="none"/></svg>',
    right: '<svg viewBox="0 0 16 16" width="14" height="14"><path d="M2 3h12M6 6.5h8M2 10h12M6 13.5h8" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" fill="none"/></svg>',
  };
  const ribbon = [
    ["insertRowAbove", "Row above", "Insert row(s) above the selection"], ["insertRowBelow", "Row below", "Insert row(s) below the selection"],
    ["insertColumnLeft", "Column left", "Insert column(s) left of the selection"], ["insertColumnRight", "Column right", "Insert column(s) right of the selection"],
    ["deleteRow", "Delete row", "Delete the selected row(s)"], ["deleteColumn", "Delete column", "Delete the selected column(s)"], "|",
    ["align:left", null, "Align column(s) left", ICON.left], ["align:center", null, "Centre column(s)", ICON.center], ["align:right", null, "Align column(s) right", ICON.right], "|",
    ["sortAsc", "Sort A–Z", "Sort the rows by this column, ascending (the header stays)"], ["sortDesc", "Sort Z–A", "Sort the rows by this column, descending (the header stays)"], "|",
    ["mode", "Edit as text", "Edit the table as markdown"],
  ];
  const altMove = (key) => ({ ArrowUp: "moveRowUp", ArrowDown: "moveRowDown", ArrowLeft: "moveColumnLeft", ArrowRight: "moveColumnRight" })[key] ?? null;

  // ── tables in a document ──

  function scan(doc) {
    let at = 0, fence = null;
    return doc.split("\n").map(text => {
      const info = { start: at, text, table: false };
      at += text.length + 1;
      const m = /^ {0,3}(`{3,}|~{3,})/.exec(text);
      if (fence) { if (m && m[1][0] === fence[0] && m[1].length >= fence.length) fence = null; return info; }
      if (m) { fence = m[1]; return info; }
      info.table = /^ {0,3}\|/.test(text);
      return info;
    });
  }

  // The table around the caret, or null: its lines, its text and range in the document, the model row (0 for the header and the separator) and column of the caret.
  function find(doc, caret) {
    const lines = scan(doc);
    const li = lines.findIndex(l => caret >= l.start && caret <= l.start + l.text.length);
    if (li < 0 || !lines[li].table) return null;
    let a = li, b = li;
    while (a > 0 && lines[a - 1].table) a--;
    while (b < lines.length - 1 && lines[b + 1].table) b++;
    if (a + 1 > b || !isSeparator(lines[a + 1].text)) return null;
    const into = li - a, off = caret - lines[li].start;
    const cells = splitRow(lines[li].text);
    const col = Math.max(0, Math.min(cells.length - 1, cells.filter(c => c.from <= off).length - 1));
    return { startLine: a, endLine: b, start: lines[a].start, end: lines[b].start + lines[b].text.length,
      text: doc.slice(lines[a].start, lines[b].start + lines[b].text.length), row: into <= 1 ? 0 : into - 1, col, line: li };
  }

  // Swap the table found by `find` for `t`, formatted; the selection is the content of the cell at (row, col) of the new table.
  function replace(doc, f, t, row, col) {
    const text = format(t), lines = text.split("\n");
    const li = row === 0 ? 0 : row + 1;
    const cell = splitRow(lines[li])[Math.min(col, t.rows[0].length - 1)];
    const before = lines.slice(0, li).reduce((n, l) => n + l.length + 1, 0);
    return { text: doc.slice(0, f.start) + text + doc.slice(f.end), start: f.start + before + cell.start, end: f.start + before + cell.end,
      rangeStart: f.start, rangeEnd: f.end, replacement: text };
  }

  // Where the text of the cell (row, col) of the table `f` (from find) is in the document, whatever the table's spacing: { start, end }.
  function cellOffset(doc, f, row, col) {
    const lines = f.text.split("\n"), li = Math.min(row === 0 ? 0 : row + 1, lines.length - 1);
    const before = lines.slice(0, li).reduce((n, l) => n + l.length + 1, 0), cells = splitRow(lines[li]);
    const cell = cells[Math.min(col, cells.length - 1)];
    const at = f.start + before;
    return cell ? { start: at + cell.start, end: at + cell.end } : { start: at + lines[li].length, end: at + lines[li].length };
  }

  const columns = (t) => t.rows[0].length;

  function apply(doc, caret, op, arg) {
    const f = find(doc, caret);
    if (!f) return null;
    const t = parse(f.text), { row, col } = f;
    switch (op) {
      case "insertRowBelow": return replace(doc, f, insertRow(t, row, true), row + 1, col);
      case "insertRowAbove": return replace(doc, f, insertRow(t, row, false), row === 0 ? 0 : row, col);
      case "insertColumnLeft": return replace(doc, f, insertColumn(t, col, false), row, col);
      case "insertColumnRight": return replace(doc, f, insertColumn(t, col, true), row, col + 1);
      case "deleteRow": { const n = deleteRow(t, row); return replace(doc, f, n, Math.min(row, n.rows.length - 1), col); }
      case "deleteColumn": { const n = deleteColumn(t, col); return replace(doc, f, n, row, Math.min(col, columns(n) - 1)); }
      case "align": return replace(doc, f, setAlign(t, col, arg), row, col);
      case "format": return replace(doc, f, t, row, col);
      default: {   // the commands of the grid overlay, on the caret's cell
        const r = applyOp(t, sel(row, col, row, col), op, arg);
        return r ? replace(doc, f, r.table, r.sel.r0, r.sel.c0) : null;
      }
    }
  }

  // Tab: the next cell (the separator row is skipped); from the last cell a row is added. Shift+Tab: the previous one, staying in the first.
  function nextCell(doc, caret) {
    const f = find(doc, caret);
    if (!f) return null;
    const t = parse(f.text), cols = columns(t);
    if (f.col < cols - 1) return replace(doc, f, t, f.row, f.col + 1);
    if (f.row === t.rows.length - 1) return replace(doc, f, insertRow(t, f.row, true), f.row + 1, 0);
    return replace(doc, f, t, f.row + 1, 0);
  }
  function prevCell(doc, caret) {
    const f = find(doc, caret);
    if (!f) return null;
    const t = parse(f.text);
    if (f.col > 0) return replace(doc, f, t, f.row, f.col - 1);
    if (f.row > 0) return replace(doc, f, t, f.row - 1, columns(t) - 1);
    return replace(doc, f, t, 0, 0);
  }
  // Enter at the end of a row (nothing but its closing pipe after the caret) adds a row below; anywhere else it is a plain Enter (null).
  function enter(doc, caret) {
    const f = find(doc, caret);
    if (!f) return null;
    const lineStart = doc.lastIndexOf("\n", caret - 1) + 1, rest = doc.slice(caret, doc.indexOf("\n", caret) < 0 ? doc.length : doc.indexOf("\n", caret));
    if (!/^\s*\|?\s*$/.test(rest) || lineStart > caret) return null;
    return replace(doc, f, insertRow(parse(f.text), f.row, true), f.row + 1, 0);
  }

  // ── moving in the grid, and leaving it ──

  // Where a navigation key leads from `pos` in a grid of rows x cols cells: { r, c }, or { leave: "after" | "before" } when the arrow goes past the
  // last / first row (the caret leaves the table for the text below / above it); null for any other key. Sideways the cell stays at the edge.
  // Home / End: first / last cell of the row; with ctrl, first / last cell of the table.
  function navigate(key, pos, rows, cols, ctrl) {
    const { r, c } = pos, lr = rows - 1, lc = cols - 1;
    switch (key) {
      case "ArrowUp": return r > 0 ? { r: r - 1, c } : { leave: "before" };
      case "ArrowDown": return r < lr ? { r: r + 1, c } : { leave: "after" };
      case "ArrowLeft": return { r, c: Math.max(0, c - 1) };
      case "ArrowRight": return { r, c: Math.min(lc, c + 1) };
      case "Home": return ctrl ? { r: 0, c: 0 } : { r, c: 0 };
      case "End": return ctrl ? { r: lr, c: lc } : { r, c: lc };
      default: return null;
    }
  }

  // The text change that takes the caret out of the table `f` (from find): to the start of the line after it ("after"), or the end of the line before it
  // ("before"); an empty line is made when the table is at the end / the start of the text. Same shape as replace(): text, rangeStart, rangeEnd, replacement, start, end.
  function leave(doc, f, where) {
    let rs, re = 0, rep = "", at;
    if (where === "before") {
      if (f.start > 0) rs = at = f.start - 1; else { rs = at = 0; rep = "\n"; }
    } else if (doc.length > f.end) { rs = at = f.end + 1; }
    else { rs = f.end; rep = "\n"; at = f.end + 1; }
    re = rs;
    return { text: doc.slice(0, rs) + rep + doc.slice(re), rangeStart: rs, rangeEnd: re, replacement: rep, start: at, end: at };
  }

  // ── /table ──

  function create(bodyRows, cols) {
    return format({ rows: [Array.from({ length: cols }, (_, i) => "Column " + (i + 1)), ...Array.from({ length: bodyRows }, () => Array(cols).fill(""))], align: Array(cols).fill("") });
  }

  // `/table` typed alone on its line (indentation allowed, nothing after the caret): its range, else null.
  function slashAt(doc, caret) {
    const ls = caret > 0 ? doc.lastIndexOf("\n", caret - 1) + 1 : 0;
    const m = /^([ \t]*)\/table$/.exec(doc.slice(ls, caret));
    const nl = doc.indexOf("\n", caret), after = doc.slice(caret, nl < 0 ? doc.length : nl);
    return m && after.trim() === "" ? { start: ls + m[1].length, end: caret } : null;
  }

  // A new table in place of the `/table` at `range`; a blank line goes before it when the line above has text. The selection is the first header cell.
  function insertAt(doc, range, bodyRows, cols) {
    const table = create(bodyRows, cols);
    const lineAbove = range.start > 0 ? doc.slice(doc.lastIndexOf("\n", range.start - 2) + 1, range.start - 1) : "";
    const lead = range.start > 0 && doc[range.start - 1] === "\n" && lineAbove.trim() !== "" ? "\n" : "";
    const first = splitRow(table.split("\n")[0])[0];
    return { text: doc.slice(0, range.start) + lead + table + doc.slice(range.end), rangeStart: range.start, rangeEnd: range.end, replacement: lead + table,
      start: range.start + lead.length + first.start, end: range.start + lead.length + first.end };
  }

  return { parse, format, insertRow, insertColumn, deleteRow, deleteColumn, setAlign, find, apply, applyOp, setCell, toTsv, pasteRange, cellOffset, rewrite: replace, unescapeCell, nextCell, prevCell, enter, navigate, leave, create, slashAt, insertAt, ribbon, altMove };
});
