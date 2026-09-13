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

  function applyAll() {
    var st = loadState();
    var panels = document.querySelectorAll('.blt-panel');
    for (var i = 0; i < panels.length; i++) {
      var k = panelKey(panels[i]);
      var want = st[k] === true;
      if (want !== panels[i].classList.contains('collapsed')) panels[i].classList.toggle('collapsed', want);
    }
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
  }, true);

  // Blazor re-renders can drop the class again — restore on DOM changes and on a slow beat.
  try {
    var obs = new MutationObserver(function () { applyAll(); });
    obs.observe(document.body, { childList: true, subtree: true });
  } catch (e) { }
  setInterval(applyAll, 1200);

  window.bltFold = { apply: applyAll, clear: function () { localStorage.removeItem(LS_KEY); } };
  document.addEventListener('DOMContentLoaded', applyAll);
  applyAll();
})();
