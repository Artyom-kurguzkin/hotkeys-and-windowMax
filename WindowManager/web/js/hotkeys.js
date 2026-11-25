// The hotkey menu page (host: HelpPopup.cs). Styled by the table editor's own app.css; this file only renders the list and reports the panel.
//   host -> page: {type:"open", version, update:{text, canApply}|null, groups:[{name, rows:[{keys, what}]}]}  {type:"reset"} (the menu was hidden)
//   page -> host: {type:"ready"}  {type:"shape", rects:[{x,y,w,h,r}], anchor:{x:innerWidth, y:innerHeight}}  {type:"close"}  {type:"update"}
// The host clips the (large, transparent) window to `rects`: the one .panel.
(function () {
  const wv = window.chrome && window.chrome.webview;
  const post = (m) => { if (wv) wv.postMessage(m); };
  const DOT = ["--sun", "--sage", "--sky", "--lilac", "--peach"];   // one pastel per category in turn, as the section label's dot

  const el = (tag, cls, text) => { const e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; };

  // "Alt+Q  or  Win+Alt+X", "Win+Alt  (tap)", "Alt+[  /  Alt+]", "Win+Left / Right / Up / Down", "Alt, Alt, Alt  (quickly)", "Both Alt keys":
  // chords become key caps (split at +), "or" and "/" and "," stay small grey words, "(tap)" a grey note.
  function keys(text) {
    const box = el("div", "k");
    for (const part of String(text).split(/(\s{2,}or\s{2,}|\s+\/\s+|,\s+|\s{2,})/)) {
      const t = part.trim();
      if (!t) continue;
      if (t === "or" || t === "/" || t === ",") box.append(el("span", "sep", t));
      else if (t.startsWith("(")) box.append(el("span", "note", t));
      else for (const cap of t.split(/\s*\+\s*(?=\S)/)) box.append(el("kbd", "", cap));
    }
    return box;
  }

  const wait = (ms) => new Promise((res) => setTimeout(res, ms));
  let token = 0;
  let live = false;                       // between an open and the next reset: only then is a shape the menu's, not a stale one from the last showing
  async function open(m) {
    const mine = ++token;
    document.getElementById("version").textContent = m.version || "";
    const upd = document.getElementById("upd");
    upd.hidden = !m.update;
    document.getElementById("upd-text").textContent = m.update ? m.update.text : "";
    document.getElementById("upd-btn").hidden = !(m.update && m.update.canApply);

    const cols = document.getElementById("cols");
    cols.replaceChildren();
    (m.groups || []).forEach((g, i) => {
      const sec = el("section", "section");
      sec.style.setProperty("--dot", `var(${DOT[i % DOT.length]})`);
      sec.append(el("h3", "", g.name));
      for (const r of g.rows) { const row = el("div", "row"); row.append(el("div", "w", r.what), keys(r.keys)); sec.append(row); }
      cols.append(sec);
    });
    // the text is laid out with the web font; wait for it (briefly) so the panel is the size it will stay
    await Promise.race([document.fonts ? document.fonts.ready : Promise.resolve(), wait(400)]);
    if (mine !== token) return;
    live = true;
    schedule();
  }

  // ── shape: where the panel is ──
  const num = (v) => Math.round(v * 100) / 100;
  const radius = (e) => parseFloat(getComputedStyle(e).borderTopLeftRadius) || 0;
  function shape() {
    if (!live) return;
    const rects = [];
    for (const e of document.querySelectorAll(".panel")) {
      const b = e.getBoundingClientRect();
      rects.push({ x: num(b.left), y: num(b.top), w: num(b.width), h: num(b.height), r: num(radius(e)) });
    }
    // anchor carries the viewport: the host ignores a shape laid out before the window was resized to its size (the next one follows)
    post({ type: "shape", rects, anchor: { x: innerWidth, y: innerHeight } });
  }
  let pending = false;
  function schedule() {   // one shape per frame; a hidden window may not run frames, so a timer reports what is there
    if (pending) return;
    pending = true;
    requestAnimationFrame(() => requestAnimationFrame(() => { if (pending) { pending = false; shape(); } }));
    setTimeout(() => { if (pending) { pending = false; shape(); } }, 100);
  }
  new ResizeObserver(schedule).observe(document.documentElement);

  document.addEventListener("keydown", (e) => { if (e.key === "Escape") post({ type: "close" }); });
  document.getElementById("upd-btn").addEventListener("click", () => post({ type: "update" }));

  function reset() { token++; live = false; document.getElementById("cols").replaceChildren(); }

  if (wv) wv.addEventListener("message", (e) => {
    const m = e.data;
    if (m && m.type === "open") open(m);
    else if (m && m.type === "reset") reset();
  });
  window.wm = { open, shape };
  post({ type: "ready" });
})();
