/* ===========================================================================
   BLT 歌单拖拽排序（原生 JS，零依赖）
   - 宿主标记：行元素带 data-blt-drag="playlist" + data-index="<0 基下标>"；
     draggable="true" 由 Blazor 标记渲染。
   - 用 document 级事件委托（dragstart / dragover / drop / dragend，捕获阶段），
     Blazor 重渲染整行后无需重新绑定。
   - 拖拽中源行半透明（.blt-dragging），目标行上边框高亮（.blt-drag-over）；
     本文件自带 <style>，不依赖 blt.css。
   - 落点回调静态 [JSInvokable]：DotNet.invokeMethodAsync('BiLi_live_Tool',
     'ReorderPlaylist', from, to) —— 静态方法不需要 DotNetObjectReference。
   - 幂等：index.html 若另外 <script src="blt-drag.js?v=014"> 也不会重复绑定。
   =========================================================================== */
(function () {
  'use strict';
  if (window.bltDrag) return;   // already mounted (page injection + index.html tag can coexist)

  var SEL = '[data-blt-drag="playlist"]';
  var CLS_DRAGGING = 'blt-dragging';
  var CLS_OVER = 'blt-drag-over';
  var STYLE_ID = 'blt-drag-style';

  function injectStyle() {
    if (document.getElementById(STYLE_ID)) return;
    var st = document.createElement('style');
    st.id = STYLE_ID;
    st.textContent =
      SEL + '{cursor:grab;}' +
      SEL + '.' + CLS_DRAGGING + '{opacity:.45;}' +
      SEL + '.' + CLS_OVER + '{box-shadow:inset 0 2px 0 var(--amber,#ffb224);}';
    (document.head || document.documentElement).appendChild(st);
  }

  function rowOf(target) {
    if (!target || !target.closest) return null;
    var row = target.closest(SEL);
    return row && row.getAttribute('data-index') !== null ? row : null;
  }

  function indexOf(row) {
    var i = parseInt(row.getAttribute('data-index'), 10);
    return isNaN(i) ? -1 : i;
  }

  var from = -1;          // source row index while a drag is in flight
  var overRow = null;     // row carrying the drop highlight

  function clearOver() {
    if (overRow) { overRow.classList.remove(CLS_OVER); overRow = null; }
  }

  function reset() {
    clearOver();
    if (from >= 0) {
      var rows = document.querySelectorAll(SEL);
      for (var i = 0; i < rows.length; i++) rows[i].classList.remove(CLS_DRAGGING);
    }
    from = -1;
  }

  injectStyle();

  document.addEventListener('dragstart', function (e) {
    var row = rowOf(e.target);
    if (!row) return;
    from = indexOf(row);
    if (from < 0) return;
    row.classList.add(CLS_DRAGGING);
    try {
      e.dataTransfer.effectAllowed = 'move';
      e.dataTransfer.setData('text/plain', String(from));   // required by Firefox
    } catch (err) { /* dataTransfer may be null in synthetic events */ }
  }, true);

  document.addEventListener('dragover', function (e) {
    if (from < 0) return;
    var row = rowOf(e.target);
    if (!row) return;
    e.preventDefault();                    // opt in to drop
    try { e.dataTransfer.dropEffect = 'move'; } catch (err) { }
    if (row === overRow) return;
    clearOver();
    if (indexOf(row) !== from) { row.classList.add(CLS_OVER); overRow = row; }
  }, true);

  document.addEventListener('drop', function (e) {
    if (from < 0) return;
    var row = rowOf(e.target);
    if (!row) return;
    e.preventDefault();
    var src = from;
    var to = indexOf(row);
    reset();
    if (to < 0 || to === src) return;
    try {
      var p = DotNet.invokeMethodAsync('BiLi_live_Tool', 'ReorderPlaylist', src, to);
      if (p && p.catch) p.catch(function () { });   // page navigated away mid-drag
    } catch (err) { }
  }, true);

  document.addEventListener('dragend', function () { reset(); }, true);

  window.bltDrag = { version: '014' };
})();
