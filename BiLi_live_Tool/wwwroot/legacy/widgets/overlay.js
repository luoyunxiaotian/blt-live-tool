'use strict';
/*
 * Widgets Overlay — 文字挂件 OBS 浏览器源页
 * ?id=<挂件id> → GET /api/widgets 找到挂件 → 按样式渲染 + 播放效果
 * 配置保存时服务端广播 {type:'widgets'} → 本页热更新（无需在 OBS 里刷新）
 * 效果：static 静态 / marquee 滚动 / typewriter 逐个跳字 / flipX 左右翻页 / flipY 上下翻页 / blink 闪烁
 * 间隔时间 speed（秒）：滚动=一圈秒数 / 跳字=每字间隔 / 翻页=每页停留 / 闪烁=周期
 * 对齐 align：left / center / right（文字与底板在来源内的水平位置）
 */
(function () {
  const qs = new URLSearchParams(location.search);
  const wid = qs.get('id') || '';
  const box = document.getElementById('wg-box');
  const stage = document.getElementById('wg-stage');
  const textEl = document.getElementById('wg-text');
  let w = null;
  let _fxToken = 0;      // effect chain guard: ++ on stop so old chains die naturally
  let marqueeAnim = null;

  function esc(s){ return String(s == null ? '' : s).replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c])); }

  function clearFx(){
    _fxToken++;
    if (marqueeAnim) { try { marqueeAnim.cancel(); } catch (e) {} marqueeAnim = null; }
    textEl.className = '';
    textEl.style.transition = '';
    textEl.style.transform = '';
    textEl.style.opacity = '';
    textEl.style.animation = '';
    textEl.style.whiteSpace = 'pre-wrap';
    textEl.style.display = 'block';
    box.style.width = '';
    box.style.whiteSpace = 'pre-wrap';
  }

  function hexToRgba(hex, alpha) {
    let h = String(hex || '#000000').replace('#', '');
    if (h.length === 3) h = h.split('').map(c => c + c).join('');
    if (h.length === 8) h = h.slice(0, 6);
    const n = parseInt(h, 16);
    if (isNaN(n)) return 'rgba(0,0,0,' + alpha + ')';
    return 'rgba(' + ((n >> 16) & 255) + ',' + ((n >> 8) & 255) + ',' + (n & 255) + ',' + alpha + ')';
  }

  function outlineShadow(cfg) {
    if (!cfg.outline || cfg.outline === 'none' || !cfg.outlineWidth) return 'none';
    const base = cfg.outline === 'black' ? '#000000' : (cfg.outline === 'white' ? '#ffffff' : (cfg.outlineColor || '#000000'));
    const c = hexToRgba(base, cfg.outlineOpacity != null ? cfg.outlineOpacity : 1);
    const w = Number(cfg.outlineWidth) || 2;
    const step = Math.max(1, Math.ceil(w / 2));
    const parts = [];
    for (let dx = -w; dx <= w; dx += step) {
      for (let dy = -w; dy <= w; dy += step) {
        if (dx === 0 && dy === 0) continue;
        parts.push(dx + 'px ' + dy + 'px 0 ' + c);
      }
    }
    return parts.join(',');
  }

  function applyStyle() {
    box.style.fontSize = (w.fontSize || 28) + 'px';
    box.style.fontFamily = '"' + (w.font || '微软雅黑') + '","微软雅黑","Microsoft YaHei",sans-serif';
    box.style.color = hexToRgba(w.color || '#ffffff', w.textOpacity != null ? w.textOpacity : 1);
    const sh = outlineShadow(w);
    textEl.style.textShadow = sh === 'none' ? '' : sh;
    box.style.background = hexToRgba(w.bgColor || '#fb7299', w.bgOpacity != null ? w.bgOpacity : 0.6);
    box.style.borderRadius = (w.rounded != null ? w.rounded : 8) + 'px';
    box.style.padding = Math.round((w.fontSize || 28) * 0.25) + 'px ' + Math.round((w.fontSize || 28) * 0.55) + 'px';
    // 对齐方向：控制文字+底板在 OBS 来源内的水平位置
    const align = w.align || 'center';
    stage.style.justifyContent = align === 'left' ? 'flex-start' : (align === 'right' ? 'flex-end' : 'center');
    box.style.textAlign = align;
  }

  // ---------- effects ----------
  function effectStatic() {
    textEl.textContent = w.text || '';
  }

  function effectMarquee() {
    // 背景底板占满整条来源宽度，文字在其中从右向左滚动
    const text = String(w.text || '').replace(/\s*\n+\s*/g, '　');
    box.style.width = '100%';
    box.style.whiteSpace = 'nowrap';
    textEl.style.whiteSpace = 'nowrap';
    textEl.style.display = 'inline-block';
    textEl.textContent = text;
    requestAnimationFrame(function () {
      if (!w) return;
      const stageW = stage.clientWidth || 800;
      const textW = textEl.offsetWidth || stageW;
      const dur = Math.max(0.5, Number(w.speed) || 15) * 1000;
      marqueeAnim = textEl.animate(
        [{ transform: 'translateX(' + stageW + 'px)' }, { transform: 'translateX(-' + textW + 'px)' }],
        { duration: dur, iterations: Infinity, easing: 'linear' }
      );
    });
  }

  function effectTypewriter() {
    const text = String(w.text || '');
    const per = Math.max(50, (Math.max(0.1, Number(w.speed) || 0.3)) * 1000); // ms per char
    const my = _fxToken;
    let i = 0;
    textEl.textContent = '';
    (function step() {
      if (my !== _fxToken) return;
      if (i >= text.length) {
        setTimeout(function () { if (my !== _fxToken) return; textEl.textContent = ''; i = 0; step(); }, 2000);
        return;
      }
      textEl.textContent += text[i++];
      setTimeout(step, per);
    })();
  }

  function flipEffect(axis) {
    const lines = String(w.text || '').split(/\r?\n/).map(s => s.trim()).filter(Boolean);
    if (!lines.length) { textEl.textContent = ''; return; }
    if (lines.length === 1) { textEl.textContent = lines[0]; return; } // 单行等效静态
    const hold = Math.max(0.5, Number(w.speed) || 5) * 1000;
    const cls = { x: ['flip-out-x', 'flip-enter-x', 'flip-in-x'], y: ['flip-out-y', 'flip-enter-y', 'flip-in-y'] }[axis];
    const my = _fxToken;
    let idx = 0;
    function show() {
      textEl.className = '';
      textEl.textContent = lines[idx];
      setTimeout(function () {
        if (my !== _fxToken) return;
        textEl.classList.add(cls[0]);                       // slide out
        setTimeout(function () {
          if (my !== _fxToken) return;
          idx = (idx + 1) % lines.length;
          textEl.textContent = lines[idx];
          textEl.className = cls[1];                        // enter from opposite side
          requestAnimationFrame(function () { requestAnimationFrame(function () { if (my === _fxToken) textEl.className = cls[2]; }); });
        }, 360);
      }, hold);
    }
    show();
    (function loop() { setTimeout(function () { if (my !== _fxToken) return; show(); loop(); }, hold + 750); })();
  }

  function effectBlink() {
    const period = Math.max(0.4, Number(w.speed) || 2);
    textEl.textContent = w.text || '';
    textEl.style.animation = 'wgBlink ' + period + 's ease-in-out infinite';
    if (!document.getElementById('wg-blink-kf')) {
      const st = document.createElement('style');
      st.id = 'wg-blink-kf';
      st.textContent = '@keyframes wgBlink{0%,100%{opacity:1;}50%{opacity:.25;}}';
      document.head.appendChild(st);
    }
  }

  function render() {
    if (!w) { box.style.display = 'none'; return; }
    box.style.display = '';
    applyStyle();
    clearFx();
    ({ static: effectStatic, marquee: effectMarquee, typewriter: effectTypewriter, flipX: function(){ flipEffect('x'); }, flipY: function(){ flipEffect('y'); }, blink: effectBlink }[w.effect] || effectStatic)();
  }

  async function load() {
    try {
      const r = await fetch('/api/widgets');
      const j = await r.json();
      const nw = ((j.widgets || []).find(x => x.id === wid)) || null;
      if (nw && JSON.stringify(nw) !== JSON.stringify(w)) { w = nw; render(); }
      else if (!nw) { w = null; box.style.display = 'none'; }
    } catch (e) {}
  }

  function connect() {
    const proto = location.protocol === 'https:' ? 'wss' : 'ws';
    const ws = new WebSocket(proto + '://' + location.host + '/ws');
    ws.onmessage = (e) => {
      let m; try { m = JSON.parse(e.data); } catch (x) { return; }
      if (m.type === 'widgets') load();   // 配置变化 → 热更新
    };
    ws.onclose = () => setTimeout(connect, 2500);
  }

  connect();
  load();
  setInterval(load, 60000); // 兜底同步
})();
