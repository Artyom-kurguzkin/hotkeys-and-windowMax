// The bridge between the original table editor (md-table.js, md-editor.js: unchanged) and the WindowManager host (WebView2 popup).
//   page -> host: {type:"ready"}  {type:"shape", rects:[{x,y,w,h,r}], anchor:{x,y}}  {type:"close", text, reason:"escape"|"collect", asText}
//   host -> page: {type:"open", text, caret}  {type:"collect"}
// The host clips the (large, transparent) window to `rects`, so only the cards (the tile, the ribbon, the /table picker) are ever seen.
(function () {
  const T = rtm.mdTable;
  const ta = document.querySelector("textarea.desc-in");
  const tile = ta.closest(".tile");
  const wv = window.chrome && window.chrome.webview;
  const post = (m) => { if (wv) wv.postMessage(m); };
  const SHADOW = 4, HALO = 6;            // the cards' hard shadow; the --paper mat round every card (style review 2026-11-20), px
  const MIN_W = 612;                      // the textarea's least width, CSS px
  const raf = () => new Promise((res) => requestAnimationFrame(() => res()));

  let openText = "";                      // the text of the last open (its trailing newlines are the baseline for close)
  let asText = false;                     // "Edit as text" was clicked since the last open
  let used = false;                       // an open has happened: the editor may hold state (the overlay's "off" flag, a picker) to reset
  let wide = MIN_W;                       // the widest the textarea has been since the open (it does not shrink while typing)
  let token = 0;

  // The host writes every change into the app, which moves the focus out of this window for a moment. md-editor closes its grid when the
  // textarea loses the focus (a click elsewhere on the old page): a blur because the whole window lost the focus is not that, so it is hidden
  // from md-editor, the same way md-editor hides the textarea's blur into a cell input. A click into the app is a click-away the host handles.
  for (const type of ["blur", "focusout"])
    addEventListener(type, (e) => { if (e.target === ta && !document.hasFocus()) e.stopImmediatePropagation(); }, true);
  addEventListener("focus", () => {   // back from a write: the keyboard goes where it was (the cell being edited, else the textarea)
    const input = document.querySelector(".md-in");
    if (input) input.focus(); else if (document.activeElement !== ta) ta.focus({ preventScroll: true });
  });

  // The picker sizes itself on mouse-over. The popup appears wherever the user is typing, often under a pointer that has not moved: Chromium
  // then fires mouse-over at that pointer and the picker took a size nobody chose (measured: Enter gave 6 or 7 columns). Mouse-over counts
  // only after the pointer has really moved since the open.
  let pointerMoved = false, lastPointer = "";
  addEventListener("mousemove", (e) => { const at = e.screenX + "," + e.screenY; if (lastPointer && at !== lastPointer) pointerMoved = true; lastPointer = at; }, true);
  addEventListener("mouseover", (e) => { if (!pointerMoved && e.target.closest && e.target.closest(".md-grid")) e.stopImmediatePropagation(); }, true);
  // A click on a picker cell picks that cell's size even when the pointer did not move over it first (its mouse-over was ignored above).
  addEventListener("mousedown", (e) => {
    const c = e.target.closest && e.target.closest(".md-gc");
    if (c) { pointerMoved = true; c.dispatchEvent(new MouseEvent("mouseover", { bubbles: true })); }   // always: the size is the clicked cell's (measured: a quick move + click inserted 3 x 3)
  }, true);

  // The host writes each change into the app, which takes the focus away for a moment: never while the user is typing. Every key, and every
  // start or end of a cell edit, is reported; the host waits until no cell is being edited and the keys have paused (measured: a write in
  // the middle of a cell edit lost the first half of what was typed).
  let editingNow = false, lastActivity = 0;
  function activity(force) {
    const editing = !!document.querySelector(".md-in");
    const now = Date.now();
    if (!force && editing === editingNow && now - lastActivity < 80) return;
    editingNow = editing; lastActivity = now;
    post({ type: "activity", editing });
  }
  addEventListener("keydown", () => activity(true), true);
  new MutationObserver(() => { if (!!document.querySelector(".md-in") !== editingNow) activity(true); }).observe(document.body, { subtree: true, childList: true });

  rtm.markdownEditor(ta);

  // ── size: the textarea grows with its content ──

  let ctx = null;
  function fit() {
    const cs = getComputedStyle(ta);
    ctx = ctx || document.createElement("canvas").getContext("2d");
    ctx.font = cs.fontStyle + " " + cs.fontWeight + " " + cs.fontSize + " " + cs.fontFamily;
    let longest = 0;
    for (const line of ta.value.split("\n")) longest = Math.max(longest, ctx.measureText(line).width);
    const padX = (parseFloat(cs.paddingLeft) || 0) + (parseFloat(cs.paddingRight) || 0);
    const tcs = getComputedStyle(tile);
    const frame = (parseFloat(tcs.paddingLeft) || 0) + (parseFloat(tcs.paddingRight) || 0) + (parseFloat(tcs.borderLeftWidth) || 0) + (parseFloat(tcs.borderRightWidth) || 0);
    const left = parseFloat(getComputedStyle(document.body).paddingLeft) || 0;
    const room = Math.max(MIN_W, innerWidth - left - SHADOW - 4 - frame);   // never wider than the window: past that the textarea wraps
    wide = Math.max(wide, Math.min(room, Math.ceil(longest + padX + 4)), MIN_W);
    tile.style.width = wide + frame + "px";
    ta.style.height = "0px";
    const bodyTop = parseFloat(getComputedStyle(document.body).paddingTop) || 0;
    const most = Math.max(220, innerHeight - bodyTop - 2 * 12 - SHADOW - 8);
    const h = ta.scrollHeight;
    ta.style.height = Math.min(h, most) + "px";
    ta.style.overflowY = h > most ? "auto" : "hidden";
  }
  ta.addEventListener("input", fit);

  // ── shape: where the cards are ──

  const num = (v) => Math.round(v * 100) / 100;
  const radius = (el) => parseFloat(getComputedStyle(el).borderTopLeftRadius) || 0;
  const visible = (el) => { const cs = getComputedStyle(el); return cs.display !== "none" && cs.visibility !== "hidden"; };
  function card(el) {
    const b = el.getBoundingClientRect();
    return { x: num(b.left - HALO), y: num(b.top - HALO), w: num(b.width + SHADOW + 2 * HALO), h: num(b.height + SHADOW + 2 * HALO), r: num(radius(el) + HALO) };
  }

  // Only the original's editor is shown: the ribbon, the /table picker and the grid overlay (cut to the table's own width), never the tile and
  // textarea behind them. The overlay sits where the table's first line is, so the anchor (the textarea's first text line) puts the grid
  // over the table in the app. Nothing left to show (the picker closed, the grid left with Esc or an arrow): the editor is done.
  let armed = false;                      // something has been shown since the open
  function shape() {
    const rects = [];
    for (const sel of [".md-pop.md-bar", ".md-pop.md-grid"]) {
      for (const el of document.querySelectorAll(sel)) if (visible(el)) rects.push(card(el));
    }
    for (const el of document.querySelectorAll(".md-ov")) {
      if (!visible(el)) continue;
      rects.push(card(el));   // the grid is a card of its own now (editor.html), framed like the ribbon and the picker
    }
    const cs = getComputedStyle(ta), b = ta.getBoundingClientRect();
    const anchor = { x: num(b.left + ta.clientLeft + (parseFloat(cs.paddingLeft) || 0)), y: num(b.top + ta.clientTop + (parseFloat(cs.paddingTop) || 0)) };
    if (rects.length) armed = true;
    else if (armed) { armed = false; collect("left"); return; }
    post({ type: "shape", rects, anchor });
  }

  // Every committed change (md-editor rewrites the textarea through an input event) goes to the host at once, which writes it into the app.
  let lastSent = "";
  function change() {
    const text = tidy(ta.value);
    if (text === lastSent) return;
    lastSent = text;
    post({ type: "change", text });
  }
  ta.addEventListener("input", () => { if (!T.slashAt(ta.value, ta.selectionStart)) setTimeout(change, 0); });

  let pending = false;
  function schedule() {   // coalesced: one shape per frame, after the layout and md-editor's own refresh (a frame earlier) have settled
    if (pending) return;
    pending = true;
    requestAnimationFrame(() => requestAnimationFrame(() => { pending = false; shape(); }));
    setTimeout(() => { if (pending) shape(); }, 100);   // a hidden / throttled window may not run frames: report what is there now; the frame that finally runs posts the settled shape
  }
  new MutationObserver(schedule).observe(document.body, { subtree: true, childList: true, attributes: true, characterData: true });
  const ro = new ResizeObserver(schedule);
  ro.observe(tile); ro.observe(ta); ro.observe(document.documentElement);
  addEventListener("scroll", schedule, true);
  addEventListener("resize", () => { fit(); schedule(); });

  // ── open / collect / close ──

  async function open(text, caret) {
    const mine = ++token;
    text = String(text ?? "").replace(/\r\n/g, "\n");
    openText = text;
    lastSent = text;
    pointerMoved = false; lastPointer = "";
    armed = false;
    asText = false;
    wide = MIN_W;
    if (used) {   // take the caret out of any table / picker first so md-editor closes its overlay, ribbon and picker and drops the "edit as text" state
      ta.blur();
      ta.value = "";
      fit();
      ta.focus();
      ta.dispatchEvent(new Event("keyup"));
      await Promise.race([raf().then(raf), new Promise((res) => setTimeout(res, 60))]);   // the timer: a hidden window may not run frames
      if (mine !== token) return;
    }
    used = true;
    ta.value = text;
    fit();
    ta.focus();
    caret = Math.max(0, Math.min(text.length, caret | 0));
    ta.setSelectionRange(caret, caret);
    if (T.slashAt(ta.value, caret)) ta.dispatchEvent(new Event("input", { bubbles: true }));   // what typing "/table" did: the size picker opens
    else ta.dispatchEvent(new Event("keyup"));                                                  // what a click / arrow key did: the overlay and ribbon when in a table
    schedule();
  }

  const trailing = (s) => s.length - s.replace(/\n+$/, "").length;
  function tidy(text) {   // T.leave adds an empty line after a table at the end: not the user's
    const keep = trailing(openText);
    return trailing(text) > keep ? text.replace(/\n+$/, "") + "\n".repeat(keep) : text;
  }
  let closing = false;
  function collect(reason) {
    if (closing) return;
    closing = true;
    setTimeout(() => { closing = false; }, 0);
    post({ type: "close", text: tidy(ta.value), reason, asText });
  }

  // Esc that md-editor did not use (it prevents and stops it when it closes the picker, leaves the grid or cancels a cell edit)
  document.addEventListener("keydown", (e) => { if (e.key === "Escape" && !e.defaultPrevented) collect("escape"); });
  // "Edit as text" (the ribbon's mode button: Grid / Edit as text) since the last open
  // (the raw markdown is in the app itself, so choosing it closes the editor and leaves the user there)
  document.addEventListener("click", (e) => {
    const b = e.target.closest && e.target.closest(".md-b[data-op='mode']");
    if (b && b.textContent.trim() === "Edit as text") { asText = true; e.stopPropagation(); e.preventDefault(); collect("astext"); }
  }, true);

  if (wv) wv.addEventListener("message", (e) => {
    const m = e.data;
    if (!m) return;
    if (m.type === "open") open(m.text, m.caret);
    else if (m.type === "collect") collect("collect");
  });

  window.wm = { open, collect: () => collect("collect"), shape };
  post({ type: "ready" });
})();
