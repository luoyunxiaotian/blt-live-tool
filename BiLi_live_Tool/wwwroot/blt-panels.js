/* ===========================================================================
   BLT 面板折叠 + 侧栏分组记忆（原生 JS，零依赖）
   - 点卡片标题栏（.blt-panel-head）空白处折叠/展开；卡头里的按钮/开关/输入
     不触发折叠。
   - 状态按「路由 + 卡片标题」记在 localStorage，刷新/切页/重渲染后自动恢复。
   - Blazor 重渲染可能覆盖 class，所以用 MutationObserver + 轻量轮询兜底。
   =========================================================================== */
(function () {
  var LS_KEY = 'blt_fold_panels';

  function loadState() {
    try { return JSON.parse(localStorage.getItem(LS_KEY) || '{}') || {}; } catch (e) { return {}; }
  }
  function saveState(st) {
    try { localStorage.setItem(LS_KEY, JSON.stringify(st)); } catch (e) { }
  }
  function panelKey(panel) {
    var head = panel.querySelector('.blt-panel-head');
    var title = head ? (head.querySelector('.t') ? head.querySelector('.t').textContent.trim() : head.textContent.trim()) : '';
    var route = (location.hash || '').replace(/^#/, '') || location.pathname || '/';
    return route + '|' + title;
  }
  function isInteractive(el) {
    while (el && el !== document.body) {
      var tag = (el.tagName || '').toLowerCase();
      if (tag === 'button' || tag === 'input' || tag === 'select' || tag === 'textarea' || tag === 'a') return true;
      if (el.classList && (el.classList.contains('blt-switch') || el.classList.contains('blt-master'))) return true;
      el = el.parentElement;
    }
    return false;
  }

  // 折叠按钮：实心三角用 SVG（固定 18×11 px），比字符可靠 —— 字符的墨迹远小于字号，
  // 放多大都还是一小点；SVG 想多大就多大且不糊，颜色用 currentColor 跟随主题。
  var FOLD_SVG = '<svg viewBox="0 0 10 6" aria-hidden="true">' +
    '<path d="M0 0l5 6 5-6z" fill="currentColor"/></svg>';

  function ensureFoldButtons() {
    var heads = document.querySelectorAll('.blt-panel-head');
    for (var i = 0; i < heads.length; i++) {
      var h = heads[i];
      if (h.querySelector('.blt-fold')) continue;   // Blazor 重渲染可能把它冲掉，这里补回
      var btn = document.createElement('span');
      btn.className = 'blt-fold';
      btn.setAttribute('aria-hidden', 'true');
      btn.innerHTML = FOLD_SVG;
      h.appendChild(btn);   // 追加到末尾：与原先 ::after 的位置一致（卡头最右侧）
    }
  }

  // 折叠按钮是 ::after 伪元素时代留下的说明——现在按钮是真实元素，但伪元素挂不了
  // title，所以提示仍然写在卡头上：悬停即可看到「点击折叠 / 点击展开」。
  function syncTitles() {
    var panels = document.querySelectorAll('.blt-panel');
    for (var i = 0; i < panels.length; i++) {
      var head = panels[i].querySelector('.blt-panel-head');
      if (!head) continue;
      var want = panels[i].classList.contains('collapsed') ? '点击展开这张卡片' : '点击折叠这张卡片';
      if (head.title !== want) head.title = want;
    }
  }

  function applyAll() {
    var st = loadState();
    var panels = document.querySelectorAll('.blt-panel');
    for (var i = 0; i < panels.length; i++) {
      var k = panelKey(panels[i]);
      var want = st[k] === true;
      if (want !== panels[i].classList.contains('collapsed')) panels[i].classList.toggle('collapsed', want);
    }
    ensureFoldButtons();
    syncTitles();
  }

  document.addEventListener('click', function (ev) {
    var head = ev.target.closest ? ev.target.closest('.blt-panel-head') : null;
    if (!head || isInteractive(ev.target)) return;
    var panel = head.closest('.blt-panel');
    if (!panel) return;
    var st = loadState();
    var k = panelKey(panel);
    var now = !panel.classList.contains('collapsed');
    panel.classList.toggle('collapsed', now);
    st[k] = now;
    saveState(st);
    syncTitles();
  }, true);

  // Blazor re-renders can drop the class again — restore on DOM changes and on a slow beat.
  try {
    var obs = new MutationObserver(function () { applyAll(); });
    obs.observe(document.body, { childList: true, subtree: true });
  } catch (e) { }
  setInterval(applyAll, 1200);


  // ── 顶栏时钟：就地更新，避免整个 Blazor 布局每秒重渲染（内存/CPU 优化）──────
  var clockTimer = null;
  function pad2(n) { return n < 10 ? '0' + n : '' + n; }
  function tickClock() {
    var el = document.getElementById('blt-clock');
    if (!el) return;
    var d = new Date();
    el.textContent = pad2(d.getHours()) + ':' + pad2(d.getMinutes()) + ':' + pad2(d.getSeconds());
  }
  window.bltStartClock = function () {
    if (clockTimer) return true;
    tickClock();
    clockTimer = setInterval(tickClock, 1000);
    return true;
  };
  window.bltStopClock = function () { if (clockTimer) { clearInterval(clockTimer); clockTimer = null; } return true; };

  window.bltFold = { apply: applyAll, clear: function () { localStorage.removeItem(LS_KEY); } };
  document.addEventListener('DOMContentLoaded', applyAll);
  applyAll();
})();
