'use strict';
/*
 * Alert Overlay — OBS 浏览器源页
 * 连接主服务 WS(7360/ws)，按配置过滤事件并弹出横幅/播放音效
 * 队列播放：同类事件 3s 合并在服务端不做，这里逐条排队，maxQueue 防爆
 */
(function () {
  const qs = new URLSearchParams(location.search);
  const stage = document.getElementById('stage');
  let cfg = null;
  let queue = [];
  let playing = false;

  function esc(s){ return String(s == null ? '' : s).replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c])); }
  function fmtMoney(v){ const n = Number(v); return isNaN(n) ? '' : n.toFixed(2); }

  function themeHref(id){
    const t = qs.get('theme') || id || 'pink';
    return './themes/' + String(t).replace(/[^a-z0-9_-]/gi, '') + '.css';
  }
  function applyTheme(id){ document.getElementById('theme-css').href = themeHref(id); }

  async function loadCfg(){
    try {
      const r = await fetch('/api/config');
      const j = await r.json();
      cfg = j.alert || {};
    } catch (e) { cfg = cfg || {}; }
    applyTheme(cfg.theme);
  }

  function connect(){
    const proto = location.protocol === 'https:' ? 'wss' : 'ws';
    const ws = new WebSocket(proto + '://' + location.host + '/ws');
    ws.onmessage = (e) => {
      let m; try { m = JSON.parse(e.data); } catch (x) { return; }
      if (m.type === 'event') onEvent(m.data, false);
      else if (m.type === 'alert_test') onEvent(m.data, true);
    };
    ws.onclose = () => setTimeout(connect, 2500);
  }

  function mapType(ev){
    if (ev.type === 'gifts') return 'gift';
    if (ev.type === 'guard') return 'guard';
    if (ev.type === 'superchat') return 'superchat';
    if (ev.type === 'interact') return ev.msgType === 2 ? 'follow' : 'enter';
    return '';
  }

  function onEvent(ev, isTest){
    const t = mapType(ev || {});
    if (!t) return;
    const a = cfg || {};
    if (!a.enabled && !isTest) return; // 未启用时只响应测试，方便先配样式
    const conf = ((a.types || {})[t]) || {};
    if (!conf.on && !isTest) return;
    if (t === 'gift' && !isTest && conf.minAmount > 0 && !(Number(ev.value) >= conf.minAmount)) return;
    enqueue({ type: t, ev: ev, isTest: isTest });
  }

  function enqueue(item){
    const max = (cfg && cfg.maxQueue) || 10;
    queue.push(item);
    while (queue.length > max) queue.shift();
    if (!playing) next();
  }

  function next(){
    const item = queue.shift();
    if (!item) { playing = false; return; }
    playing = true;
    show(item);
    const dur = Math.max(2000, (cfg && cfg.durationMs) || 6000);
    setTimeout(next, dur);
  }

  function nodeHtml(item){
    const ev = item.ev || {};
    const ico = { gift: '🎁', guard: '🛡️', superchat: '💬', follow: '⭐', enter: '👋' }[item.type] || '🔔';
    let title = '', sub = '';
    if (item.type === 'gift') {
      title = esc(ev.uname || '观众') + ' 送出 ' + esc(ev.giftName || '礼物') + (ev.num ? ' ×' + ev.num : '');
      sub = ev.value ? ('¥' + fmtMoney(ev.value)) : '';
    } else if (item.type === 'guard') {
      title = esc(ev.uname || '观众') + ' 开通 ' + esc(ev.levelName || '舰长');
      sub = ev.value ? ('¥' + fmtMoney(ev.value)) : '';
    } else if (item.type === 'superchat') {
      title = esc(ev.uname || '观众') + ' SC ¥' + fmtMoney(ev.price);
      sub = ev.msg || '';
    } else if (item.type === 'follow') {
      title = esc(ev.uname || '观众') + ' 关注了直播间';
    } else if (item.type === 'enter') {
      title = '欢迎 ' + esc(ev.uname || '观众') + ' 进入直播间';
    }
    return '<div class="alert-item alert-' + item.type + '">' +
      '<span class="ai-ico">' + ico + '</span>' +
      '<span class="ai-body"><span class="ai-title">' + title + '</span>' + (sub ? '<span class="ai-sub">' + esc(sub) + '</span>' : '') + '</span>' +
      '</div>';
  }

  function show(item){
    const wrap = document.createElement('div');
    wrap.innerHTML = nodeHtml(item);
    const node = wrap.firstChild;
    stage.appendChild(node);
    // play sound (OBS captures this audio)
    try {
      const conf = ((cfg || {}).types || {})[item.type] || {};
      if (conf.sound) {
        const au = new Audio('/sounds/' + conf.sound);
        au.volume = (cfg.volume != null ? cfg.volume : 0.8);
        au.play().catch(function () {});
      }
    } catch (e) {}
    const dur = Math.max(2000, (cfg && cfg.durationMs) || 6000);
    setTimeout(() => { node.classList.add('alert-out'); setTimeout(() => node.remove(), 700); }, dur);
  }

  // demo mode (?demo=1): cycle sample alerts so users can preview styles without live events
  if (qs.get('demo') === '1') {
    const seq = [
      { type: 'gifts', uname: '测试观众', giftName: '小花花', num: 1, value: 0.1 },
      { type: 'guard', uname: '测试舰长', levelName: '舰长', num: 1, value: 138 },
      { type: 'superchat', uname: '测试SC', msg: '主播加油！', price: 30 },
      { type: 'interact', uname: '测试关注', msgType: 2 },
      { type: 'interact', uname: '测试观众', msgType: 1 }
    ];
    let i = 0;
    setInterval(() => { onEvent(seq[i % seq.length], true); i++; }, 4000);
  }

  loadCfg();
  connect();
  // 配置可能被修改（主题/开关），每分钟同步一次
  setInterval(loadCfg, 60000);
})();
