/* ===========================================================================
   blt-ui.js — shared UI kit (port of the Electron UI's toast/confirm/tooltip/
   btnLoading helpers). Loaded from index.html before blazor.webview.js.
   Public API (all callable from Blazor via IJSRuntime):
     bltToast(msg, ok=true, ms=3200)   bottom-right toast, red variant when ok=false
     bltConfirm(msg, okText, cancelText) -> Promise<boolean>   styled confirm dialog
     bltBusy(idOrElem, busy)            spinner + pointer-events off while working
     Tooltips: any element with data-tip="..." gets a styled tooltip automatically
   =========================================================================== */
(function () {
  'use strict';

  // ---------- tooltip ----------
  var tip = null, tipTimer = null, tipOwner = null;
  function ensureTip() {
    if (!tip) {
      tip = document.createElement('div');
      tip.className = 'blt-tip';
      document.body.appendChild(tip);
    }
    return tip;
  }
  function showTip(el) {
    var text = el.getAttribute('data-tip');
    if (!text) return;
    var t = ensureTip();
    t.textContent = text;
    t.classList.add('show');
    var r = el.getBoundingClientRect();
    var tr = t.getBoundingClientRect();
    var x = r.left + (r.width - tr.width) / 2;
    x = Math.max(6, Math.min(x, window.innerWidth - tr.width - 6));
    var y = r.top - tr.height - 8;
    if (y < 6) y = Math.min(r.bottom + 8, window.innerHeight - tr.height - 6);  // flip below
    t.style.left = x + 'px';
    t.style.top = y + 'px';
  }
  function hideTip() {
    tipOwner = null;
    if (tipTimer) { clearTimeout(tipTimer); tipTimer = null; }
    if (tip) tip.classList.remove('show');
  }
  document.addEventListener('mouseover', function (e) {
    var el = e.target && e.target.closest ? e.target.closest('[data-tip]') : null;
    if (!el || el === tipOwner) return;
    tipOwner = el;
    if (tipTimer) clearTimeout(tipTimer);
    tipTimer = setTimeout(function () { showTip(el); }, 150);
  }, true);
  ['mouseout', 'mousedown', 'wheel', 'scroll'].forEach(function (evt) {
    document.addEventListener(evt, function (e) {
      if (evt === 'mouseout' && e.target && tipOwner && tipOwner.contains(e.target)) return;
      hideTip();
    }, true);
  });
  window.addEventListener('blur', hideTip);

  // ---------- toast ----------
  function toastBox() {
    var box = document.getElementById('blt-toast');
    if (!box) {
      box = document.createElement('div');
      box.id = 'blt-toast';
      document.body.appendChild(box);
    }
    return box;
  }
  window.bltToast = function (msg, ok, ms) {
    try {
      var box = toastBox();
      while (box.children.length >= 4) box.removeChild(box.firstChild);
      var d = document.createElement('div');
      d.className = 'blt-toast' + (ok === false ? ' err' : '') + (ok === 'warn' ? ' warn' : '');
      d.textContent = String(msg == null ? '' : msg);
      box.appendChild(d);
      requestAnimationFrame(function () { d.classList.add('show'); });
      setTimeout(function () {
        d.classList.remove('show');
        setTimeout(function () { if (d.parentNode) d.parentNode.removeChild(d); }, 250);
      }, typeof ms === 'number' && ms > 0 ? ms : 3200);
    } catch (e) { }
  };

  // ---------- confirm ----------
  window.bltConfirm = function (msg, okText, cancelText) {
    return new Promise(function (resolve) {
      var wrap = document.createElement('div');
      wrap.className = 'blt-modal';
      var box = document.createElement('div');
      box.className = 'blt-modal-box';
      var p = document.createElement('div');
      p.className = 'msg';
      p.textContent = String(msg == null ? '' : msg);
      var row = document.createElement('div');
      row.className = 'row';
      var cancel = document.createElement('button');
      cancel.className = 'blt-btn';
      cancel.textContent = cancelText || '取消';
      var ok = document.createElement('button');
      ok.className = 'blt-btn primary';
      ok.textContent = okText || '确定';
      row.appendChild(cancel); row.appendChild(ok);
      box.appendChild(p); box.appendChild(row); wrap.appendChild(box);
      document.body.appendChild(wrap);

      var done = false;
      function finish(v) {
        if (done) return;
        done = true;
        document.removeEventListener('keydown', onKey, true);
        if (wrap.parentNode) wrap.parentNode.removeChild(wrap);
        resolve(v);
      }
      function onKey(e) {
        if (e.key === 'Escape') { e.preventDefault(); finish(false); }
        else if (e.key === 'Enter') { e.preventDefault(); finish(true); }
      }
      cancel.addEventListener('click', function () { finish(false); });
      ok.addEventListener('click', function () { finish(true); });
      wrap.addEventListener('click', function (e) { if (e.target === wrap) finish(false); });
      document.addEventListener('keydown', onKey, true);
      setTimeout(function () { ok.focus(); }, 30);
    });
  };

  // ---------- busy / spinner ----------
  window.bltBusy = function (idOrElem, busy) {
    try {
      var el = typeof idOrElem === 'string' ? document.getElementById(idOrElem) : idOrElem;
      if (!el) return;
      el.classList.toggle('is-busy', !!busy);
      if ('disabled' in el) el.disabled = !!busy;
    } catch (e) { }
  };
})();
