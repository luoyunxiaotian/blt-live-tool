'use strict';
/* B站直播助手 管理面板 SPA */

// ---------- 图标 ----------
const ICONS = {
  danmu: '<svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2"><path d="M4 5h16a1 1 0 0 1 1 1v9a1 1 0 0 1-1 1H9l-5 4V6a1 1 0 0 1 1-1z" stroke-linejoin="round"/></svg>',
  gifts: '<svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2"><rect x="3" y="8" width="18" height="4" rx="1"/><path d="M5 12v8h14v-8M12 8v12"/><path d="M12 8c-3-2-4-4-2-5s4 1 2 5zM12 8c3-2 4-4 2-5s-4 1-2 5z"/></svg>',
  blindbox: '<svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2"><path d="M12 3a9 9 0 1 0 9 9"/><path d="M12 12l6-4"/><circle cx="12" cy="12" r="1" fill="currentColor"/></svg>',
  guard: '<svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2"><path d="M12 3l7 3v5c0 5-3 8-7 10-4-2-7-5-7-10V6z" stroke-linejoin="round"/><path d="M9 12l2 2 4-4"/></svg>',
  superchat: '<svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2"><path d="M12 21a9 9 0 1 0-9-9"/><path d="M3 12l4 2v-4"/></svg>',
  tool: '<svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2"><path d="M14 6l4 4-8 8-4-4z"/><path d="M13 5l2-2 4 4-2 2"/></svg>',
  rt: '<svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2"><path d="M3 12h4l3-8 4 16 3-8h4"/></svg>',
  set: '<svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2"><circle cx="12" cy="12" r="3"/><path d="M19 12a7 7 0 1 1-14 0 7 7 0 0 1 14 0z"/><path d="M12 2v2M12 20v2M4 4l1.5 1.5M18.5 18.5L20 20M2 12h2M20 12h2M4 20l1.5-1.5M18.5 5.5L20 4"/></svg>'
};

const TYPES = ['danmu','gifts','blindbox','guard','superchat'];
const TYPE_META = {
  danmu:    { title:'弹幕记录', cols:[['time','时间'],['uname','用户名'],['uid','UID'],['msg','弹幕内容']] },
  gifts:    { title:'礼物记录', cols:[['time','时间'],['uname','用户名'],['uid','UID'],['giftName','礼物名称'],['num','数量'],['value','总价(元)'],['coinType','币种']] },
  blindbox: { title:'盲盒记录', cols:[['time','时间'],['uname','用户名'],['uid','UID'],['giftName','盲盒名称'],['num','数量'],['cost','成本'],['income','收入'],['profit','盈亏']] },
  guard:    { title:'舰长记录', cols:[['time','时间'],['uname','用户名'],['uid','UID'],['levelName','等级'],['num','数量'],['value','合计(元)']] },
  superchat:{ title:'醒目留言记录', cols:[['time','时间'],['uname','用户名'],['uid','UID'],['msg','留言内容'],['price','价格(元)'],['startTime','开始时间'],['endTime','结束时间']] }
};
const TYPE_NAMES = { danmu:'弹幕', gifts:'礼物', blindbox:'盲盒', guard:'舰长', superchat:'醒目留言' };

// ---------- 状态 ----------
const state = {
  config: null,
  status: null,
  recent: { danmu:[], gifts:[], blindbox:[], guard:[], superchat:[] },
  ws: null,
  queryState: {}
};

// ---------- 工具 ----------
const $ = (s, p) => (p || document).querySelector(s);
const $$ = (s, p) => Array.from((p || document).querySelectorAll(s));
function esc(s){ return String(s == null ? '' : s).replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c])); }
function fmtMoney(v){ const n = Number(v); return isNaN(n) ? '—' : n.toFixed(2); }
function toast(msg, ok, dur){
  const el = document.createElement('div');
  el.className = 't-item' + (ok === false ? ' err' : '');
  el.textContent = msg;
  $('#toast').appendChild(el);
  setTimeout(() => el.remove(), dur || 2600);
}

// ---------- UI 组件 helper（第一期） ----------
// 统一 Switch 开关 HTML
function switchHtml(id, checked, label, tip){
  return '<label class="switch"' + (tip ? ' data-tip="' + esc(tip) + '"' : '') + '><input type="checkbox" id="' + id + '"' + (checked ? ' checked' : '') + '><span class="track"></span>' +
    (label ? '<span class="sw-label">' + esc(label) + '</span>' : '') + '</label>';
}
// 保存按钮成功反馈：短暂变绿显示"已保存"
function flashSaved(btn){
  if (!btn) return;
  const orig = btn.textContent;
  btn.classList.add('saved');
  btn.textContent = '✓ 已保存';
  setTimeout(() => { btn.classList.remove('saved'); btn.textContent = orig; }, 1500);
}
// 连接引导横幅（renderSettings 顶部）：按连接状态给出下一步指引
function renderConnGuide(st){
  const connected = st && st.state === 'connected';
  const hasCookie = !!(state.config && state.config.cookie);
  const rid = (state.config && state.config.roomId) || '';
  const roomIdKnown = st && st.realRoomId ? st.realRoomId : rid;
  let html = '';
  if (connected){
    html = '<div class="banner ok" id="conn-guide"><span class="bn-icon">✓</span><div class="bn-body">' +
      '已连接房间 <b class="mono">' + esc(roomIdKnown || '-') + '</b>' + liveInfoBrief() +
      '，正在实时记录。可在左侧开启语音念弹幕、自动弹幕等功能。' +
      '</div></div>';
  } else if (!hasCookie){
    html = '<div class="banner warn" id="conn-guide"><span class="bn-icon">👋</span><div class="bn-body">' +
      '<b>开始使用只需 3 步：</b>① 下方「B站登录」扫码登录（或粘贴 Cookie）→ ② 填写房间号 → ③ 点击「连接」。<br>' +
      '<span class="muted">当前状态：未登录。未登录也能连接，但 B站会限流，弹幕/礼物可能记录不全。</span>' +
      '<div class="bn-actions"><button class="btn sm primary" onclick="scrollToCard(\'card-login\')">① 去登录</button>' +
      '<span class="muted" style="font-size:12px;align-self:center;">→ ② 填房间号 → ③ 连接</span></div>' +
      '</div></div>';
  } else if (!rid){
    html = '<div class="banner warn" id="conn-guide"><span class="bn-icon">📺</span><div class="bn-body">' +
      '已登录 B站 ✓，还差 2 步：<b>② 在下方填写直播间房间号 → ③ 点击「连接」</b>。' +
      '<div class="bn-actions"><button class="btn sm primary" onclick="scrollToCard(\'card-conn\')">② 去填房间号</button></div>' +
      '</div></div>';
  } else {
    html = '<div class="banner warn" id="conn-guide"><span class="bn-icon">🔌</span><div class="bn-body">' +
      '已登录 ✓ · 房间号 <b class="mono">' + esc(rid) + '</b> ✓ · 当前未连接，功能不会生效。' +
      '<div class="bn-actions"><button class="btn sm primary" onclick="quickConnect()">③ 立即连接</button></div>' +
      '</div></div>';
  }
  return html;
}
function scrollToCard(id){
  highlightPulse(id);
}
// v1.1.9: scroll to a card and pulse-highlight it, so guided jumps are easy to spot
function highlightPulse(id){
  const el = document.getElementById(id);
  if (!el) return;
  el.classList.remove('highlight-pulse');
  void el.offsetWidth; // restart animation
  el.classList.add('highlight-pulse');
  el.scrollIntoView({ behavior: 'smooth', block: 'start' });
  setTimeout(() => el.classList.remove('highlight-pulse'), 2600);
}
// 引导横幅里的"立即连接"：复用设置页连接逻辑
async function quickConnect(){
  const btn = document.getElementById('btn-connect');
  if (btn) btn.click();
  else toast('请在房间管理页操作', false);
}

// ---------- v1.1.9 易用性组件 ----------
const AUTHOR_UID = '10412378';
const AUTHOR_SPACE_URL = 'https://space.bilibili.com/' + AUTHOR_UID;

// Async button loading state: spinner + label swap, restore via btnDone()
function btnLoading(btn, text){
  if (!btn || btn._busy) return;
  btn._busy = true;
  btn._origHtml = btn.innerHTML;
  btn.disabled = true;
  btn.classList.add('loading');
  btn.innerHTML = '<span class="btn-spinner"></span> ' + esc(text || '处理中…');
}
function btnDone(btn){
  if (!btn || !btn._busy) return;
  btn._busy = false;
  btn.disabled = false;
  btn.classList.remove('loading');
  btn.innerHTML = btn._origHtml;
}

// Topbar "auto saved" indicator (fade in/out, non-intrusive)
let _autosaveTimer = null;
function autosaveHint(){
  let el = document.getElementById('autosaveHint');
  if (!el){
    el = document.createElement('span');
    el.id = 'autosaveHint';
    el.className = 'autosave-hint';
    el.innerHTML = '✓ 已自动保存';
    const actions = $('#topActions');
    if (actions) actions.appendChild(el);
    else return;
  }
  el.classList.add('show');
  clearTimeout(_autosaveTimer);
  _autosaveTimer = setTimeout(() => el.classList.remove('show'), 1600);
}

// Banner for feature pages when the live room is not connected yet
function connReadyBanner(){
  const st = state.status || {};
  if (st.state === 'connected') return '';
  return '<div class="banner warn" id="ready-banner"><span class="bn-icon">📺</span><div class="bn-body">' +
    '<b>此功能需要先连接直播间。</b>当前状态：' + esc(stateText(st)) + '，未连接时收不到弹幕/礼物事件，功能不会有反应。' +
    '<div class="bn-actions"><button class="btn sm primary" onclick="location.hash=\'#/settings\'">去连接直播间</button></div>' +
    '</div></div>';
}

// Collapsible box with localStorage memory (key: blt_box_state)
function toggleBox(btn, key){
  const box = btn && btn.closest('.box');
  if (!box) return;
  const collapsed = box.classList.toggle('collapsed');
  btn.textContent = collapsed ? '+' : '−';
  if (key) {
    try {
      const m = JSON.parse(localStorage.getItem('blt_box_state') || '{}');
      m[key] = collapsed;
      localStorage.setItem('blt_box_state', JSON.stringify(m));
    } catch (e) {}
  }
}
function boxCollapsed(key, def){
  try {
    const m = JSON.parse(localStorage.getItem('blt_box_state') || '{}');
    return m[key] != null ? !!m[key] : !!def;
  } catch (e) { return !!def; }
}
function boxToolBtn(key, def){
  const collapsed = key ? boxCollapsed(key, !!def) : false;
  return '<div class="box-tools"><button class="btn-box-tool" onclick="toggleBox(this,\'' + esc(key || '') + '\')">' + (collapsed ? '+' : '−') + '</button></div>';
}

// Copy author contact to clipboard (used by verify-lock card & help page)
function copyAuthorContact(){
  const text = 'B站UID ' + AUTHOR_UID + '（B站直播助手作者）';
  const done = () => toast('已复制：B站UID ' + AUTHOR_UID + '，去B站联系作者吧', true);
  if (navigator.clipboard && navigator.clipboard.writeText) {
    navigator.clipboard.writeText(text).then(done).catch(() => fallbackCopy(text, done));
  } else fallbackCopy(text, done);
}
function fallbackCopy(text, done){
  const ta = document.createElement('textarea');
  ta.value = text;
  ta.style.position = 'fixed'; ta.style.opacity = '0';
  document.body.appendChild(ta);
  ta.select();
  try { document.execCommand('copy'); done(); } catch (e) { toast('复制失败，作者B站UID：' + AUTHOR_UID, false); }
  ta.remove();
}
function openAuthorSpace(){
  if (window.electronAPI && window.electronAPI.shell && window.electronAPI.shell.openExternal) {
    window.electronAPI.shell.openExternal(AUTHOR_SPACE_URL);
  } else { try { window.open(AUTHOR_SPACE_URL); } catch (e) {} }
}
// Verification runs once at boot; a reload re-triggers it when logged in
function retryVerify(){
  toast('正在重新验证…');
  setTimeout(() => { try { location.reload(); } catch (e) {} }, 600);
}

// ---------- v1.1.9 验证锁定态：友好的说明卡片（替代"全灰无解释"） ----------
function renderVerifyLockCard(){
  if (!state.verifyLocked) return '';
  return '<div class="callout callout-danger" id="verify-lock-card">' +
    '<h4>🔒 账号待授权</h4>' +
    '<p style="margin:6px 0 4px 0;font-size:13px;line-height:1.6;">本工具为作者白名单授权制：你的B站账号 UID 需要<b>加入作者的白名单</b>后才能使用全部功能。' +
    '在授权通过前，其他功能临时锁定（已保存的配置不会丢失，授权后自动恢复）。</p>' +
    '<p style="margin:4px 0;font-size:13px;">获取授权：联系作者 <b>B站UID <span class="mono">' + AUTHOR_UID + '</span></b>，说明你的来意即可申请。</p>' +
    '<div class="row" style="gap:8px;margin-top:10px;">' +
    '<button class="btn sm primary" onclick="copyAuthorContact()">📋 复制作者B站ID</button>' +
    '<button class="btn sm" onclick="openAuthorSpace()">🔗 打开作者B站主页</button>' +
    '<button class="btn sm" onclick="retryVerify()">↻ 重试验证</button>' +
    '<a class="btn sm ghost" href="#/help" style="display:inline-flex;align-items:center;">📖 先看使用教程</a>' +
    '</div></div>';
}

// ---------- v1.1.9 首次启动向导（3 步图文） ----------
const ONB_KEY = 'blt_onboarded_v1';
const ONB_STEPS = [
  { emoji:'👋', title:'欢迎使用 B站直播助手', desc:'这是一款免费的B站直播辅助工具：语音念弹幕、自动欢迎/感谢、点歌、礼物记录、OBS 键鼠可视化等。开始只需 3 步：' },
  { emoji:'🔐', title:'① 登录B站账号', desc:'在「房间管理」页点击「打开 B站浏览器」，用B站 APP 扫码登录。登录后 Cookie 自动同步，弹幕/礼物记录更完整，自动弹幕等发送类功能也依赖登录。' },
  { emoji:'📺', title:'② 填写直播间房间号', desc:'房间号是直播间网址 live.bilibili.com/ 后面的数字（注意不是你的UID）。在「直播间连接」卡片的输入框填写，输入后自动保存。' },
  { emoji:'▶️', title:'③ 点击「连接」', desc:'连接成功后（状态灯变绿），语音念弹幕、自动弹幕、点歌、记录等功能即开始工作。左侧菜单可随时切换功能页面。' },
];
function maybeShowOnboarding(){
  if (state.verifyLocked) return; // locked users see the authorization card instead
  let done = false;
  try { done = !!localStorage.getItem(ONB_KEY); } catch (e) {}
  if (!done) showOnboarding();
}
function showOnboarding(){
  let mask = document.getElementById('onb-mask');
  if (!mask) {
    mask = document.createElement('div');
    mask.id = 'onb-mask';
    mask.className = 'onb-mask';
    document.body.appendChild(mask);
  }
  mask._step = 0;
  renderOnboardingStep();
}
function renderOnboardingStep(){
  const mask = document.getElementById('onb-mask');
  if (!mask) return;
  const i = mask._step || 0;
  const st = ONB_STEPS[i];
  const last = i === ONB_STEPS.length - 1;
  const items = ONB_STEPS.slice(1).map((s, k) =>
    '<div class="onb-step-item"><span class="onb-step-num">' + (k + 1) + '</span><div><b>' + s.title.replace(/^[①②③]\s*/, '') + '</b><div class="onb-desc">' + esc(s.desc.replace(/^[①②③]\s*/, '')) + '</div></div></div>'
  ).join('');
  const bodyHtml = i === 0
    ? items
    : '<div class="onb-step-item" style="border-color:var(--accent-line);background:var(--accent-soft);"><span class="onb-step-num">' + i + '</span><div><b>' + esc(st.title.replace(/^[①②③]\s*/, '')) + '</b><div class="onb-desc">' + esc(st.desc) + '</div></div></div>' +
      '<p class="muted" style="font-size:12px;margin:10px 2px 0 2px;">点击下方按钮直接跳到对应位置，界面会高亮提示。</p>';
  mask.innerHTML =
    '<div class="onb-card">' +
    '<div class="onb-head"><div class="onb-emoji">' + st.emoji + '</div><div class="onb-title">' + esc(st.title) + '</div></div>' +
    '<div class="onb-body">' + bodyHtml + '</div>' +
    '<div class="onb-foot">' +
      '<div class="onb-dots">' + ONB_STEPS.map((_, k) => '<span class="' + (k === i ? 'active' : '') + '"></span>').join('') + '</div>' +
      '<div class="onb-actions">' +
        (i === 0 ? '<button class="btn sm" onclick="finishOnboarding()">跳过</button>' : '<button class="btn sm" onclick="onbPrev()">← 上一步</button>') +
        (last ? '<button class="btn primary" onclick="finishOnboarding()">🎉 开始使用</button>'
              : (i === 0 ? '<button class="btn primary" onclick="onbNext()">下一步 →</button>'
                         : '<button class="btn primary" onclick="onbGoStep(' + (i + 1) + ')">去操作 →</button>')) +
      '</div>' +
    '</div></div>';
}
function onbNext(){
  const mask = document.getElementById('onb-mask');
  if (!mask) return;
  if (mask._step < ONB_STEPS.length - 1) { mask._step++; renderOnboardingStep(); }
}
function onbPrev(){
  const mask = document.getElementById('onb-mask');
  if (!mask) return;
  if (mask._step > 0) { mask._step--; renderOnboardingStep(); }
}
// Jump from a wizard step to the actual UI location (with highlight pulse)
function onbGoStep(stepIdx){
  finishOnboarding(false);
  try { localStorage.setItem(ONB_KEY, '1'); } catch (e) {}
  if (stepIdx === 1) { location.hash = '#/settings'; setTimeout(() => highlightPulse('card-login'), 380); }
  else if (stepIdx === 2) { location.hash = '#/settings'; setTimeout(() => { highlightPulse('card-conn'); const inp = $('#roomIdInline'); if (inp) inp.focus(); }, 380); }
  else if (stepIdx === 3) { location.hash = '#/settings'; setTimeout(() => highlightPulse('card-conn'), 380); }
}
function finishOnboarding(mark){
  const mask = document.getElementById('onb-mask');
  if (mask) mask.remove();
  if (mark !== false) { try { localStorage.setItem(ONB_KEY, '1'); } catch (e) {} toast('随时可从左侧「使用教程」回看新手引导'); }
}

// ---------- v1.1.9 帮助中心（#/help） ----------
function renderHelpPage(){
  $('#topTitle').textContent = '使用教程';
  $('#topSub').textContent = '快速上手 · 功能说明 · 常见问题';
  $('#topActions').innerHTML = '';
  const ver = window.__APP_VERSION || '';
  const stepItem = (n, title, desc, btnHtml) =>
    '<div class="onb-step-item"><span class="onb-step-num">' + n + '</span><div style="flex:1;"><b>' + title + '</b><div class="onb-desc">' + desc + '</div></div>' +
    (btnHtml ? '<div class="onb-go-col">' + btnHtml + '</div>' : '') + '</div>';
  const faq = (q, a) => '<details style="margin-bottom:8px;"><summary style="cursor:pointer;font-size:13.5px;color:var(--text-strong);">' + q + '</summary><div class="muted" style="padding:8px 2px 2px 2px;font-size:13px;line-height:1.65;">' + a + '</div></details>';
  $('#content').innerHTML =
    '<section class="content-header"><h1>使用教程 <small>3 分钟上手</small></h1>' +
    '<ol class="breadcrumb"><li><a href="#/settings">首页</a></li><li class="active">使用教程</li></ol></section>' +

    '<div class="box box-primary"><div class="box-header with-border"><h3 class="box-title">🚀 快速上手：只需 3 步</h3></div>' +
    '<div class="box-body">' +
    stepItem(1, '登录B站账号', '「房间管理」页 → B站登录 → 打开 B站浏览器扫码。登录后记录更完整，且自动弹幕等发送类功能可用。',
      '<button class="btn sm primary" onclick="location.hash=\'#/settings\';setTimeout(function(){highlightPulse(\'card-login\')},380)">去登录</button>') +
    stepItem(2, '填写直播间房间号', '房间号 = 直播间网址 live.bilibili.com/ 后面的数字（不是UID）。输入后自动保存。',
      '<button class="btn sm primary" onclick="location.hash=\'#/settings\';setTimeout(function(){highlightPulse(\'card-conn\')},380)">去填写</button>') +
    stepItem(3, '点击「连接」', '连接成功（状态灯变绿）后，所有功能开始工作。',
      '<button class="btn sm primary" onclick="location.hash=\'#/settings\';setTimeout(function(){highlightPulse(\'card-conn\')},380)">去连接</button>') +
    '<p class="muted" style="margin:8px 0 0 0;font-size:12px;">提示：连接后回本页无需重复操作；常用功能都在左侧菜单。</p>' +
    '</div></div>' +

    '<div class="box"><div class="box-header with-border"><h3 class="box-title">🧰 功能一览</h3></div>' +
    '<div class="box-body"><table class="table"><thead><tr><th>功能</th><th>干什么用</th><th>需要连接直播间？</th></tr></thead><tbody>' +
    '<tr><td>🔊 语音念弹幕</td><td>用语音朗读弹幕、礼物、醒目留言、欢迎进房</td><td><span class="label label-success">需要</span></td></tr>' +
    '<tr><td>自动弹幕</td><td>自动欢迎进房观众、感谢礼物、定时发弹幕活跃气氛</td><td><span class="label label-success">需要</span></td></tr>' +
    '<tr><td>🎵 点歌功能</td><td>观众弹幕发送「点歌 歌名」，自动搜索播放</td><td><span class="label label-success">需要</span></td></tr>' +
    '<tr><td>PK/连线</td><td>抓取对方直播间在线/粉丝/舰长数据</td><td><span class="label label-success">需要</span></td></tr>' +
    '<tr><td>实时展示</td><td>实时查看弹幕/礼物/舰长/醒目留言/盲盒事件流</td><td><span class="label label-success">需要</span></td></tr>' +
    '<tr><td>日志记录（5 类）</td><td>把弹幕/礼物/盲盒/舰长/醒目留言记录为本地文件</td><td><span class="label label-success">需要</span></td></tr>' +
    '<tr><td>礼物截图生成</td><td>生成可分享的礼物卡片图片（也可手动填写生成）</td><td><span class="label label-default">可选</span></td></tr>' +
    '<tr><td>🎮 键鼠手柄可视化</td><td>在 OBS 中显示键盘/鼠标/手柄操作，29 套皮肤</td><td><span class="label label-default">不需要</span></td></tr>' +
    '<tr><td>📜 文字挂件</td><td>OBS 画面显示公告/推广/粉丝群等文字，支持滚动/跳字/翻页/闪烁效果</td><td><span class="label label-default">不需要</span></td></tr>' +
    '<tr><td>✨ 提醒特效墙</td><td>礼物/上舰/SC/关注/进房 在 OBS 画面弹横幅和音效</td><td><span class="label label-success">需要</span></td></tr>' +
    '</tbody></table></div></div>' +

    '<div class="box"><div class="box-header with-border"><h3 class="box-title">❓ 常见问题</h3></div>' +
    '<div class="box-body">' +
    faq('房间号在哪里看？', '打开你的直播间网页，地址栏 <code class="mono">live.bilibili.com/2058233423</code> 中的数字就是房间号。注意：不是你的B站UID。') +
    faq('连接失败 / 提示超时怎么办？', '① 确认房间号正确；② 确认直播间已开播（未开播会提示）；③ 看连接条下方的红色错误文字；④ Cookie 失效时重新扫码登录。') +
    faq('为什么弹幕/礼物记录不全？', '未登录B站账号时，B站对匿名连接限流，事件会缺失。完成「B站登录」后即可获得完整弹幕流。') +
    faq('点了按钮没反应？', '大部分功能依赖「已连接直播间」。页面顶部出现黄色提示条时，先去连接直播间。另外某些操作（如点歌搜索）需要等待网络返回，按钮会显示加载动画。') +
    faq('记录的数据保存在哪里？', '全部保存在程序所在目录的 <code class="mono">data/</code> 文件夹（绝不写C盘AppData）。可在「房间管理 → 进阶功能 → 应用管理」里一键打开数据目录。') +
    faq('提示「账号待授权」是怎么回事？', '本工具为作者白名单授权制。联系作者（B站UID <b class="mono">' + AUTHOR_UID + '</b>）申请加入白名单，通过后重启应用即自动解锁。') +
    '<div class="row" style="margin-top:10px;"><button class="btn sm primary" onclick="location.hash=\'#/diagnostics\'">🔧 打开网络诊断</button><span class="muted" style="font-size:12px;">一键检测 B站接口/弹幕服务器/TTS/验证服务器，可复制结果发给作者快速排障</span></div>' +
    '</div></div>' +

    '<div class="box"><div class="box-header with-border"><h3 class="box-title">ℹ️ 关于</h3></div>' +
    '<div class="box-body">' +
    '<p style="margin-top:0;font-size:13.5px;">B站直播助手 <b>' + (ver ? 'v' + esc(ver) : '') + '</b> · 完全开源免费 · 数据全部保存在本地</p>' +
    '<p class="muted" style="font-size:13px;">作者：罗运小天-B站第二毒奶-（B站UID <span class="mono">' + AUTHOR_UID + '</span>）。使用问题或 BUG 反馈请联系作者；本工具完全开源免费，如有版权问题请联系作者。</p>' +
    '<div class="row" style="gap:8px;">' +
    '<button class="btn sm primary" onclick="copyAuthorContact()">📋 复制作者B站ID</button>' +
    '<button class="btn sm" onclick="openAuthorSpace()">🔗 作者B站主页</button>' +
    '</div></div></div>';
}

// ---------- v1.1.9 网络诊断页（#/diagnostics，锁定态也可访问） ----------
let _diagResult = null;
function renderDiagnosticsPage(){
  $('#topTitle').textContent = '网络诊断';
  $('#topSub').textContent = '哪个环节断了，一眼看出来';
  $('#topActions').innerHTML = '';
  $('#content').innerHTML =
    '<section class="content-header"><h1>网络诊断 <small>一键检测各环节连通性</small></h1>' +
    '<ol class="breadcrumb"><li><a href="#/settings">首页</a></li><li class="active">网络诊断</li></ol></section>' +
    '<div class="box"><div class="box-body">' +
    '<div class="row"><button class="btn primary" id="btn-diag-run">🔍 一键诊断</button>' +
    '<button class="btn" id="btn-diag-copy" disabled>📋 复制诊断结果</button>' +
    '<span class="muted" style="font-size:12px;">遇到「点了没反应」：先诊断，再复制结果发给作者，排障快得多</span></div>' +
    '<div id="diag-results" style="margin-top:14px;"><p class="muted">点击「一键诊断」开始检测（约 5 秒）。检测项：B站房间接口 / 弹幕服务器 / 授权验证服务器 / GitHub更新源 / 网易云点歌 / TTS语音服务 / 本机服务端。</p></div>' +
    '</div></div>';
  $('#btn-diag-run').addEventListener('click', runDiagnostics);
  $('#btn-diag-copy').addEventListener('click', copyDiagnosticsResult);
}
async function runDiagnostics(){
  const btn = $('#btn-diag-run');
  btnLoading(btn, '诊断中…');
  const box = $('#diag-results');
  if (box) box.innerHTML = '<p class="muted">⏳ 正在检测各环节（最长约 6 秒）…</p>';
  try {
    const r = await post('/api/diagnostics', {});
    _diagResult = r;
    const LABEL = { ok: ['success', '✓ 正常'], warn: ['warning', '⚠ 可用'], fail: ['danger', '✗ 失败'] };
    let html = '<table class="table"><thead><tr><th style="width:280px;">检测项</th><th style="width:90px;">状态</th><th style="width:90px;">耗时</th><th>说明</th></tr></thead><tbody>';
    (r.results || []).forEach(function(x){
      const m = LABEL[x.state] || LABEL.warn;
      html += '<tr><td>' + esc(x.name) + '</td><td><span class="label label-' + m[0] + '">' + m[1] + '</span></td>' +
        '<td class="num">' + (x.latency || 0) + 'ms</td><td style="white-space:normal;">' + esc(x.detail || '') + '</td></tr>';
    });
    html += '</tbody></table><p class="muted" style="font-size:12px;margin-bottom:0;">生成于 ' + new Date(r.generatedAt || Date.now()).toLocaleString() + '。✓=正常 ⚠=网络可达但有异常 ✗=失败。</p>';
    if (box) box.innerHTML = html;
    const cp = $('#btn-diag-copy'); if (cp) cp.disabled = false;
  } catch (e) {
    if (box) box.innerHTML = '<p style="color:var(--danger-bright);">✗ 诊断失败：' + esc((e && e.message) || e) + '（本机服务端可能未运行，请重启应用）</p>';
  } finally { btnDone(btn); }
}
function copyDiagnosticsResult(){
  if (!_diagResult) return;
  const LABEL = { ok: '正常', warn: '异常', fail: '失败' };
  const lines = ['【B站直播助手 网络诊断】', '版本 v' + (window.__APP_VERSION || '?') + ' · ' + new Date(_diagResult.generatedAt || Date.now()).toLocaleString()];
  (_diagResult.results || []).forEach(x => { lines.push('[' + (LABEL[x.state] || x.state) + '] ' + x.name + ' — ' + (x.latency || 0) + 'ms — ' + (x.detail || '')); });
  const text = lines.join('\n');
  const done = () => toast('诊断结果已复制，粘贴发给作者即可', true);
  if (navigator.clipboard && navigator.clipboard.writeText) navigator.clipboard.writeText(text).then(done).catch(() => fallbackCopy(text, done));
  else fallbackCopy(text, done);
}

// ---------- v1.1.9 多房间监控（PK 页扩展，上限 8 个，频次限制在服务端） ----------
let _monitorCache = null;
function monitorEntries(){ return (_monitorCache && _monitorCache.rooms) || []; }
function renderMonitorCard(){
  const rooms = monitorEntries();
  const iv = (_monitorCache && _monitorCache.intervalSec) || 30;
  const rows = rooms.length ? rooms.map(function(r){
    const st = r.state || {};
    const name = (st && (st.uname || st.title)) ? (st.uname || String(st.title).slice(0, 16)) : ('房间 ' + r.roomId);
    const fresh = st && st.lastOk ? '<div class="muted" style="font-size:11px;">更新于 ' + new Date(st.lastOk).toLocaleTimeString() + '</div>' : (st && st.lastErr ? '<div style="color:var(--danger-bright);font-size:11px;">' + esc(st.lastErr) + '</div>' : '');
    return '<tr>' +
      '<td style="white-space:normal;"><b>' + esc(name) + '</b><div class="muted mono" style="font-size:11px;">房间 ' + esc(r.roomId) + '</div>' + fresh + '</td>' +
      '<td class="num">👁 ' + (st.online != null ? st.online : '—') + '</td>' +
      '<td class="num">⭐ ' + (st.follower != null ? st.follower : '—') + '</td>' +
      '<td class="num">🛡 ' + (st.guard != null ? st.guard : '—') + '</td>' +
      '<td>' + switchHtml('mon-cb-' + r.roomId, !!r.enabled, '', r.enabled ? '轮询中，关闭后暂停刷新' : '已暂停，开启后恢复轮询') + '</td>' +
      '<td><button class="btn xs danger" onclick="monitorRemove(\'' + esc(r.roomId) + '\')">移除</button></td>' +
      '</tr>';
  }).join('') : '<tr><td colspan="6" class="muted">暂无监控房间。输入房间号添加（最多 8 个）；设置 PK 对手时会自动加入。</td></tr>';
  return '<div class="box"><div class="box-header with-border"><h3 class="box-title">📡 房间监控 <span class="title-hint">定时刷新对方直播间在线/粉丝/舰长（HTTP 轮询，无需连接对方弹幕，游客身份可查）</span></h3></div>' +
    '<div class="box-body">' +
    '<div class="row" style="margin-bottom:10px;">' +
    '<input id="mon-add-input" class="form-control mono" placeholder="对方房间号" style="width:170px;display:inline-block;" data-tip="要监控的直播间房间号（短号）">' +
    '<button class="btn sm primary" id="mon-add-btn">＋ 添加监控</button>' +
    '<label class="muted" style="display:flex;align-items:center;gap:4px;font-size:12px;">刷新间隔(秒)<input id="mon-interval" type="number" min="15" max="120" step="5" value="' + iv + '" style="width:64px;" data-tip="每个房间的轮询间隔，最小15秒（防风控）"></label>' +
    '<span class="muted" style="font-size:12px;">已启用 ' + rooms.filter(function(r){ return r.enabled; }).length + ' / ' + rooms.length + ' · 上限 8 个</span>' +
    '</div>' +
    '<div class="tbl-wrap"><table class="tbl"><thead><tr><th>主播 / 房间</th><th>在线</th><th>粉丝</th><th>舰长</th><th style="width:120px;">轮询</th><th style="width:70px;"></th></tr></thead><tbody>' + rows + '</tbody></table></div>' +
    '<p class="muted" style="margin-bottom:0;font-size:12px;">服务端自动错峰轮询（一次只查一个房间），开播与否都能查到在线数；数据也会推送到 OBS 浮层（规划中）。</p>' +
    '</div></div>';
}
function wireMonitorCard(){
  const add = $('#mon-add-btn');
  if (add) add.addEventListener('click', async function(){
    const inp = $('#mon-add-input');
    const rid = ((inp && inp.value) || '').trim();
    if (!/^\d+$/.test(rid)) { toast('请输入数字房间号', false); return; }
    btnLoading(add, '添加中…');
    try { const r = await post('/api/rooms/monitor', { action: 'add', roomId: rid }); _monitorCache = r; toast('已添加监控房间 ' + rid); await rerenderMonitorCard(); }
    catch (e) { toast(e.message, false); }
    finally { btnDone(add); }
  });
  const iv = $('#mon-interval');
  if (iv) iv.addEventListener('change', async function(){
    try { const r = await post('/api/rooms/monitor', { action: 'interval', intervalSec: Number(iv.value) || 30 }); toast('刷新间隔已保存（' + r.intervalSec + ' 秒）'); }
    catch (e) { toast(e.message, false); }
  });
  monitorEntries().forEach(function(r){
    const cb = document.getElementById('mon-cb-' + r.roomId);
    if (cb) cb.addEventListener('change', async function(){
      try { await post('/api/rooms/monitor', { action: 'toggle', roomId: r.roomId, enabled: cb.checked }); }
      catch (e) { toast(e.message, false); }
    });
  });
}
window.monitorRemove = async function(roomId){
  try { const r = await post('/api/rooms/monitor', { action: 'remove', roomId: roomId }); _monitorCache = r; toast('已移除 ' + roomId); await rerenderMonitorCard(); }
  catch (e) { toast(e.message, false); }
};
async function rerenderMonitorCard(){
  const el = document.getElementById('monitor-card-holder');
  if (!el) return;
  // don't clobber the card while the user is typing in it
  const ae = document.activeElement;
  if (ae && el.contains(ae) && ae.tagName === 'INPUT') return;
  el.innerHTML = renderMonitorCard(); wireMonitorCard();
}
async function refreshMonitor(){
  try { _monitorCache = await g('/api/rooms/monitor'); await rerenderMonitorCard(); } catch (e) {}
}
// ---------- v1.1.9 Alert 特效墙配置页（#/alert） ----------
const ALERT_THEMES = [
  { id: 'pink',    name: 'B站粉 · 简约横幅' },
  { id: 'cyber',   name: '霓虹赛博' },
  { id: 'bubble',  name: '头像气泡' },
  { id: 'minimal', name: '极简文字' }
];
const ALERT_TYPE_META = [
  { key: 'gift',      name: '🎁 礼物',     hasMin: true,  tip: '收到礼物时弹出横幅，可设最低金额过滤小礼物' },
  { key: 'guard',     name: '🛡 上舰',     hasMin: false, tip: '有人开通/续费舰长·提督·总督时弹出祝贺' },
  { key: 'superchat', name: '💬 醒目留言', hasMin: false, tip: '收到SC时弹出横幅' },
  { key: 'follow',    name: '⭐ 关注',     hasMin: false, tip: '有人关注直播间时弹出提示' },
  { key: 'enter',     name: '👋 进房',     hasMin: false, tip: '观众进入直播间时提示（人多容易刷屏，默认关闭）' }
];
function alertCfg(){ return (state.config && state.config.alert) || {}; }
function collectAlertCfg(){
  const a = JSON.parse(JSON.stringify(alertCfg()));
  a.enabled = !!($('#alert-enabled-cb') || {}).checked;
  a.panelSound = !!($('#alert-panel-cb') || {}).checked;
  a.theme = ($('#alert-theme') || {}).value || 'pink';
  a.durationMs = Number(($('#alert-duration') || {}).value) || 6000;
  a.volume = Number(($('#alert-volume') || {}).value);
  a.panelVolume = Number(($('#alert-panelvol') || {}).value);
  a.types = a.types || {};
  ALERT_TYPE_META.forEach(function(m){
    const t = a.types[m.key] = a.types[m.key] || {};
    t.on = !!(document.getElementById('alert-type-' + m.key) || {}).checked;
    if (m.hasMin) t.minAmount = Number((document.getElementById('alert-min-' + m.key) || {}).value) || 0;
    const sel = document.getElementById('alert-snd-' + m.key);
    if (sel) t.sound = sel.value;
  });
  return a;
}
function saveAlertCfg(){
  const a = collectAlertCfg();
  if (state.config) state.config.alert = a;
  post('/api/config', { alert: a }).then(() => autosaveHint()).catch(e => toast('❌ 保存失败: ' + (e && e.message || e), false));
}
function alertOverlayUrl(){
  return location.origin + '/alert/overlay.html';
}
async function renderAlertPage(){
  if (!state.config) state.config = await g('/api/config');
  if (!state.config.alert) state.config.alert = {};
  let sounds = [];
  try { sounds = (await g('/api/sounds')).sounds || []; } catch (e) {}
  const a = alertCfg();
  $('#topTitle').textContent = '✨ 提醒特效墙';
  $('#topSub').textContent = '礼物/舰长/SC/关注/进房 → OBS 画面横幅 + 音效';
  $('#topActions').innerHTML = '';

  const typeRows = ALERT_TYPE_META.map(function(m){
    const t = (a.types || {})[m.key] || {};
    let sndOpts = '<option value="">无音效</option>' + sounds.map(s => '<option value="' + esc(s) + '"' + (t.sound === s ? ' selected' : '') + '>' + esc(s) + '</option>').join('');
    return '<div class="switch-row">' +
      '<span class="sr-title" style="min-width:110px;">' + m.name + '</span>' +
      switchHtml('alert-type-' + m.key, t.on !== false, '', m.tip) +
      (m.hasMin ? '<label class="muted" style="display:flex;align-items:center;gap:4px;font-size:12px;">最低金额(元)<input type="number" id="alert-min-' + m.key + '" min="0" step="0.1" value="' + (t.minAmount || 0) + '" style="width:70px;" data-tip="礼物金额低于该值不弹横幅，0=不限"></label>' : '') +
      '<label class="muted" style="display:flex;align-items:center;gap:4px;font-size:12px;">音效<select id="alert-snd-' + m.key + '" class="input" style="width:150px;height:28px;">' + sndOpts + '</select></label>' +
      '<button class="btn xs" onclick="alertTest(\'' + m.key + '\')" data-tip="弹一条测试横幅（OBS 预览窗口和本页面都能看到）">测试</button>' +
      '</div>';
  }).join('');
  const themeOpts = ALERT_THEMES.map(t => '<option value="' + t.id + '"' + (a.theme === t.id ? ' selected' : '') + '>' + t.name + '</option>').join('');

  $('#content').innerHTML =
    '<section class="content-header"><h1>提醒特效墙 <small>OBS 浏览器源可视化提醒</small></h1>' +
    '<ol class="breadcrumb"><li><a href="#/settings">首页</a></li><li class="active">提醒特效墙</li></ol></section>' +
    connReadyBanner() +

    '<div class="box box-primary"><div class="box-header with-border"><h3 class="box-title">总开关 &amp; OBS 接入</h3>' + boxToolBtn('alert-main') + '</div>' +
    '<div class="box-body">' +
    '<div class="form-grid">' +
    '<div class="field"><label>启用提醒特效墙</label>' + switchHtml('alert-enabled-cb', a.enabled, '', '总开关。关闭后 OBS 浮层不再弹出任何横幅') + '<div class="hint">OBS 浮层是否弹横幅的总开关</div></div>' +
    '<div class="field"><label>样式主题</label><select id="alert-theme" class="form-control">' + themeOpts + '</select><div class="hint">切换后 OBS 浮层自动换样式（下次事件生效）</div></div>' +
    '<div class="field"><label>横幅停留时长</label><input id="alert-duration" type="number" min="3000" max="15000" step="500" value="' + (a.durationMs || 6000) + '" data-tip="每条横幅显示的毫秒数"><div class="hint">3000-15000 毫秒</div></div>' +
    '<div class="field"><label>横幅音效音量</label><input id="alert-volume" type="range" min="0" max="1" step="0.05" value="' + (a.volume != null ? a.volume : 0.8) + '" data-tip="OBS 浮层播放音效的音量"><div class="hint">OBS 会采集到这个声音</div></div>' +
    '</div>' +
    '<div class="row" style="margin-top:12px;">' +
    '<input id="alert-url" class="form-control mono" value="' + esc(alertOverlayUrl()) + '" readonly style="flex:1;min-width:280px;" data-tip="在 OBS 中添加「浏览器源」，粘贴此地址，建议宽 800 高 600">' +
    '<button class="btn sm" id="alert-copy-url">复制地址</button>' +
    '<button class="btn sm primary" id="alert-open-preview">打开预览</button>' +
    '</div>' +
    '<div class="hint" style="margin-top:6px;">OBS：来源 → 添加 → 浏览器源 → 粘贴上方地址 → 建议宽度 800 高度 600（背景透明）</div>' +
    '</div></div>' +

    '<div class="box"><div class="box-header with-border"><h3 class="box-title">提醒哪些事件 <span class="title-hint">每类可独立开关、过滤与配音效</span></h3>' + boxToolBtn('alert-types') + '</div>' +
    '<div class="box-body">' + typeRows +
    '<p class="muted" style="margin-bottom:0;font-size:12px;">同类事件 3 秒内自动合并，横幅排队播放不会叠成一团。</p>' +
    '</div></div>' +

    '<div class="box"><div class="box-header with-border"><h3 class="box-title">🔔 管理面板提示音 <span class="title-hint">不用 OBS，本程序窗口内也会"叮"一声</span></h3>' + boxToolBtn('alert-panel') + '</div>' +
    '<div class="box-body"><div class="form-grid">' +
    '<div class="field"><label>启用面板提示音</label>' + switchHtml('alert-panel-cb', a.panelSound, '', '事件发生时管理面板播放短音效（与语音念弹幕独立）') + '</div>' +
    '<div class="field"><label>面板音效音量</label><input id="alert-panelvol" type="range" min="0" max="1" step="0.05" value="' + (a.panelVolume != null ? a.panelVolume : 0.6) + '"></div>' +
    '</div></div></div>' +

    '<div class="box"><div class="box-header with-border"><h3 class="box-title">关于自定义音效</h3>' + boxToolBtn('alert-sounds') + '</div>' +
    '<div class="box-body"><p class="muted" style="margin-top:0;">把 <span class="mono">.wav / .ogg / .mp3</span> 文件放到程序的 <span class="mono">data\\sounds\\</span> 文件夹（没有就新建），刷新本页即可在音效下拉里选择，会优先使用同名自定义音效。</p></div></div>';

  // 绑定：所有控件改动即自动保存
  const bind = (sel, ev) => { const el = $(sel); if (el) el.addEventListener(ev, saveAlertCfg); };
  ['#alert-enabled-cb', '#alert-sound-cb', '#alert-panel-cb', '#alert-theme', '#alert-duration', '#alert-volume', '#alert-panelvol'].forEach(s => bind(s, 'change'));
  ALERT_TYPE_META.forEach(function(m){
    bind('#alert-type-' + m.key, 'change');
    const s = $('#alert-snd-' + m.key); if (s) s.addEventListener('change', saveAlertCfg);
    const mi = $('#alert-min-' + m.key); if (mi) mi.addEventListener('change', saveAlertCfg);
  });
  const cu = $('#alert-copy-url'); if (cu) cu.addEventListener('click', function(){
    const inp = $('#alert-url'); inp.select();
    if (navigator.clipboard && navigator.clipboard.writeText) navigator.clipboard.writeText(inp.value).then(() => toast('已复制 OBS 浏览器源地址', true)).catch(() => {});
    else { try { document.execCommand('copy'); toast('已复制 OBS 浏览器源地址', true); } catch (e) {} }
  });
  const op = $('#alert-open-preview'); if (op) op.addEventListener('click', () => { try { window.open(alertOverlayUrl() + '?demo=1'); } catch (e) {} });
}
window.alertTest = async function(type){
  try {
    await post('/api/alert/test', { type: type });
    toast('已发送测试横幅（OBS 预览中可见）', true);
    // 面板音效也响一声，方便确认
    alertPanelSound({ type: type === 'gift' ? 'gifts' : (type === 'guard' ? 'guard' : (type === 'superchat' ? 'superchat' : 'interact')), msgType: type === 'follow' ? 2 : 1 }, true);
  } catch (e) { toast('测试失败: ' + (e && e.message || e), false); }
};
// 面板音效：事件发生时"叮"一声（独立于 TTS 语音）；force=true 供测试按钮无视开关
function alertPanelSound(ev, force){
  const a = (state.config && state.config.alert) || {};
  if (!force && !a.panelSound) return;
  const map = { gifts: 'gift', guard: 'guard', superchat: 'superchat', interact: (ev.msgType === 2 ? 'follow' : 'enter') };
  const key = map[ev.type];
  if (!key) return;
  const t = ((a.types || {})[key]) || {};
  if (!t.on && !force) return;
  if (!t.sound) return;
  try {
    const au = new Audio('/sounds/' + t.sound);
    au.volume = a.panelVolume != null ? a.panelVolume : 0.6;
    au.play().catch(function(){});
  } catch (e) {}
}
// v1.1.9: 点歌播放进度 → WS 转发给 OBS 歌词浮层（song-player 每秒派发一次）
if (!window._songProgressBound) {
  window._songProgressBound = true;
  window.addEventListener('song:progress', function(e){
    try {
      if (state.ws && state.ws.readyState === 1) state.ws.send(JSON.stringify({ type: 'song_progress', data: e.detail }));
    } catch (x) {}
  });
}

// ---------- v1.1.9 文字挂件（#/widgets） ----------
const WIDGET_FONTS = ['微软雅黑', '黑体', '宋体', '楷体', '仿宋', '华文行楷', '等线', 'Arial'];
const WIDGET_EFFECTS = [
  { id: 'static',     name: '静态显示', tip: '文字常驻不动' },
  { id: 'marquee',    name: '滚动显示', tip: '跑马灯从右向左循环滚动。速度 = 滚动一圈的秒数' },
  { id: 'typewriter', name: '逐个跳字', tip: '打字机效果：逐字出现，播完停留 2 秒后重来。速度 = 每个字的间隔秒数（支持小数）' },
  { id: 'flipX',      name: '左右翻页', tip: '文本每行一页，向左滑动翻页轮播。速度 = 每页停留秒数' },
  { id: 'flipY',      name: '上下翻页', tip: '文本每行一页，向上滑动翻页轮播。速度 = 每页停留秒数' },
  { id: 'blink',      name: '闪烁呼吸', tip: '文字透明度呼吸闪烁。速度 = 一个闪烁周期的秒数' }
];
let _widgetsCache = null;

function widgetUrl(id){ return location.origin + '/widgets/overlay.html?id=' + encodeURIComponent(id); }

function widgetCardHtml(w, idx){
  const fontOpts = WIDGET_FONTS.map(f => '<option value="' + f + '"' + (w.font === f ? ' selected' : '') + '>' + f + '</option>').join('');
  const effOpts = WIDGET_EFFECTS.map(e => '<option value="' + e.id + '"' + (w.effect === e.id ? ' selected' : '') + ' title="' + e.tip + '">' + e.name + '</option>').join('');
  const alignOpts = [['left', '左对齐'], ['center', '居中对齐'], ['right', '右对齐']].map(a => '<option value="' + a[0] + '"' + ((w.align || 'center') === a[0] ? ' selected' : '') + '>' + a[1] + '</option>').join('');
  const olw = w.outlineWidth != null ? w.outlineWidth : 2;
  const pct = (v, d) => Math.round((v != null ? v : d) * 100);
  return '<div class="box" data-widx="' + idx + '">' +
    '<div class="box-header with-border"><h3 class="box-title">📜 <input id="wg-' + idx + '-name" value="' + esc(w.name || '') + '" placeholder="挂件名称" style="width:140px;background:transparent;border:none;color:var(--text-strong);font-weight:600;" data-tip="挂件名称（仅用于本页区分）"></h3>' +
    '<div class="box-tools">' +
    '<button class="btn xs" id="wg-' + idx + '-copy" data-tip="复制此挂件的 OBS 浏览器源地址">复制地址</button>' +
    '<button class="btn xs" id="wg-' + idx + '-preview" data-tip="在浏览器打开此挂件预览">预览</button>' +
    '<button class="btn xs danger" id="wg-' + idx + '-del" data-tip="删除此挂件">删除</button>' +
    '</div></div>' +
    '<div class="box-body">' +
    '<div class="field"><label>文字内容（翻页类效果按行分页）</label><textarea class="form-control" id="wg-' + idx + '-text" style="width:100%;height:72px;" data-tip="要显示的文字；多行文本在翻页效果中每行一页，滚动效果中会合并为一行">' + esc(w.text || '') + '</textarea></div>' +
    '<div class="form-grid" style="margin-top:10px;">' +
    '<div class="field"><label>显示效果</label><select id="wg-' + idx + '-effect" class="form-control">' + effOpts + '</select></div>' +
    '<div class="field"><label>间隔时间（秒，支持小数）</label><input id="wg-' + idx + '-speed" type="number" min="0.1" max="120" step="0.1" value="' + (w.speed != null ? w.speed : 10) + '" data-tip="含义随效果不同：滚动=一圈秒数；跳字=每个字的间隔；翻页=每页停留；闪烁=周期。切换效果时会自动填入推荐值"></div>' +
    '<div class="field"><label>对齐方向</label><select id="wg-' + idx + '-align" class="form-control">' + alignOpts + '</select><div class="hint">文字和底板在 OBS 来源里的水平位置</div></div>' +
    '<div class="field"><label>字号 (px)</label><input id="wg-' + idx + '-fontsize" type="number" min="12" max="120" value="' + (w.fontSize || 28) + '"></div>' +
    '<div class="field"><label>字体</label><select id="wg-' + idx + '-font" class="form-control">' + fontOpts + '</select></div>' +
    '<div class="field"><label>文字颜色</label><input id="wg-' + idx + '-color" type="color" value="' + esc(w.color || '#ffffff') + '" style="width:100%;height:34px;padding:2px;"></div>' +
    '<div class="field"><label>文字不透明度</label><input id="wg-' + idx + '-textopacity" type="range" min="0" max="1" step="0.05" value="' + (w.textOpacity != null ? w.textOpacity : 1) + '" style="width:100%;"><div class="hint" id="wg-' + idx + '-textopacity-v">' + pct(w.textOpacity, 1) + '%</div></div>' +
    '<div class="field"><label>文字描边</label><select id="wg-' + idx + '-outline" class="form-control">' +
      '<option value="none"' + (w.outline === 'none' ? ' selected' : '') + '>无描边</option>' +
      '<option value="black"' + (w.outline === 'black' ? ' selected' : '') + '>黑色描边</option>' +
      '<option value="white"' + (w.outline === 'white' ? ' selected' : '') + '>白色描边</option>' +
      '<option value="custom"' + (w.outline === 'custom' ? ' selected' : '') + '>自定义颜色</option>' +
    '</select></div>' +
    '<div class="field"><label>描边颜色（自定义时生效）</label><input id="wg-' + idx + '-outlinecolor" type="color" value="' + esc(w.outlineColor || '#000000') + '" style="width:100%;height:34px;padding:2px;"></div>' +
    '<div class="field"><label>描边不透明度</label><input id="wg-' + idx + '-outlineopacity" type="range" min="0" max="1" step="0.05" value="' + (w.outlineOpacity != null ? w.outlineOpacity : 1) + '" style="width:100%;"><div class="hint" id="wg-' + idx + '-outlineopacity-v">' + pct(w.outlineOpacity, 1) + '%</div></div>' +
    '<div class="field"><label>描边宽度 (px)</label><input id="wg-' + idx + '-outlinewidth" type="number" min="0" max="8" value="' + olw + '"></div>' +
    '<div class="field"><label>背景底板颜色</label><input id="wg-' + idx + '-bgcolor" type="color" value="' + esc(w.bgColor || '#fb7299') + '" style="width:100%;height:34px;padding:2px;"></div>' +
    '<div class="field"><label>底板不透明度</label><input id="wg-' + idx + '-bgopacity" type="range" min="0" max="1" step="0.05" value="' + (w.bgOpacity != null ? w.bgOpacity : 0.6) + '" style="width:100%;"><div class="hint" id="wg-' + idx + '-bgopacity-v">' + pct(w.bgOpacity, 0.6) + '%</div></div>' +
    '<div class="field"><label>底板圆角 (px)</label><input id="wg-' + idx + '-rounded" type="number" min="0" max="40" value="' + (w.rounded != null ? w.rounded : 8) + '"></div>' +
    '</div>' +
    '<div class="row" style="margin-top:10px;align-items:center;">' +
    '<input id="wg-' + idx + '-url" class="form-control mono" value="' + esc(widgetUrl(w.id)) + '" readonly style="flex:1;min-width:260px;" data-tip="在 OBS 中添加「浏览器源」粘贴此地址；建议高度 = 字号×3 左右">' +
    '</div>' +
    '</div></div>';
}

function collectWidgets(){
  return (_widgetsCache || []).map(function(w, idx){
    const v = (id) => { const el = document.getElementById('wg-' + idx + '-' + id); return el ? el.value : w[id]; };
    const n = (id, d) => { const x = Number(v(id)); return isNaN(x) ? d : x; };
    return Object.assign({}, w, {
      name: v('name') || '挂件',
      text: v('text'),
      effect: v('effect'),
      speed: n('speed', 10),
      align: v('align') || 'center',
      fontSize: n('fontsize', 28),
      font: v('font'),
      color: v('color'),
      textOpacity: n('textopacity', 1),
      outline: v('outline'),
      outlineColor: v('outlinecolor'),
      outlineOpacity: n('outlineopacity', 1),
      outlineWidth: n('outlinewidth', 2),
      bgColor: v('bgcolor'),
      bgOpacity: n('bgopacity', 0.6),
      rounded: n('rounded', 8)
    });
  });
}

let _widgetsSaveTimer = null;
function scheduleWidgetsSave(ms){
  clearTimeout(_widgetsSaveTimer);
  _widgetsSaveTimer = setTimeout(saveWidgets, ms || 700);
}
async function saveWidgets(){
  const list = collectWidgets();
  try {
    const r = await post('/api/widgets', { widgets: list });
    _widgetsCache = r.widgets || list;   // adopt server-normalized values silently
    autosaveHint();
  } catch (e) { toast('❌ 挂件保存失败: ' + (e && e.message || e), false); }
}
async function renderWidgetsPage(){
  try { _widgetsCache = (await g('/api/widgets')).widgets || []; } catch (e) { _widgetsCache = _widgetsCache || []; }
  $('#topTitle').textContent = '📜 文字挂件';
  $('#topSub').textContent = '在 OBS 画面显示公告 / 推广 / 粉丝群等内容';
  $('#topActions').innerHTML = '';
  const cards = (_widgetsCache || []).map((w, i) => widgetCardHtml(w, i)).join('');
  $('#content').innerHTML =
    '<section class="content-header"><h1>文字挂件 <small>OBS 浏览器源 · 公告 / 推广 / 粉丝群</small></h1>' +
    '<ol class="breadcrumb"><li><a href="#/settings">首页</a></li><li class="active">文字挂件</li></ol></section>' +
    '<div class="banner info"><span class="bn-icon">💡</span><div class="bn-body">' +
    '每个挂件一个 OBS 浏览器源地址：OBS → 来源 → 添加 → 浏览器源 → 粘贴挂件地址 → 在 OBS 里拖放定位。' +
    '修改内容/样式后<b>自动保存并实时生效</b>，无需在 OBS 里刷新。滚动效果建议来源宽度拉满（如 1920）；背景为透明，只有文字底板。' +
    '</div></div>' +
    '<div class="row" style="margin-bottom:14px;"><button class="btn primary" id="wg-add">＋ 添加挂件（' + _widgetsCache.length + '/10）</button></div>' +
    '<div id="widgets-list">' + cards + '</div>';
  $('#wg-add').addEventListener('click', async function(){
    if (_widgetsCache.length >= 10) { toast('最多 10 个挂件', false); return; }
    _widgetsCache.push({ name: '挂件 ' + (_widgetsCache.length + 1), text: '这里是挂件文字内容，点击编辑～', effect: 'static', speed: 10, align: 'center', fontSize: 28, font: '微软雅黑', color: '#ffffff', textOpacity: 1, outline: 'black', outlineColor: '#000000', outlineOpacity: 1, outlineWidth: 2, bgColor: '#fb7299', bgOpacity: 0.6, rounded: 8 });
    await saveWidgets();
    $('#widgets-list').innerHTML = _widgetsCache.map((w, i) => widgetCardHtml(w, i)).join('');
    wireWidgetCards();
    $('#wg-add').textContent = '＋ 添加挂件（' + _widgetsCache.length + '/10）';
  });
  wireWidgetCards();
}
function wireWidgetCards(){
  (_widgetsCache || []).forEach(function(w, idx){
    const del = document.getElementById('wg-' + idx + '-del');
    if (del) del.addEventListener('click', async function(){
      _widgetsCache.splice(idx, 1);
      await saveWidgets();
      $('#widgets-list').innerHTML = _widgetsCache.map((x, i) => widgetCardHtml(x, i)).join('');
      wireWidgetCards();
      const add = $('#wg-add'); if (add) add.textContent = '＋ 添加挂件（' + _widgetsCache.length + '/10）';
      toast('已删除挂件');
    });
    const cp = document.getElementById('wg-' + idx + '-copy');
    if (cp) cp.addEventListener('click', function(){
      const url = widgetUrl(w.id);
      if (navigator.clipboard && navigator.clipboard.writeText) navigator.clipboard.writeText(url).then(() => toast('已复制挂件地址', true)).catch(() => {});
      else { const inp = document.getElementById('wg-' + idx + '-url'); if (inp) { inp.select(); try { document.execCommand('copy'); toast('已复制挂件地址', true); } catch (e) {} } }
    });
    const pv = document.getElementById('wg-' + idx + '-preview');
    if (pv) pv.addEventListener('click', function(){ try { window.open(widgetUrl(w.id)); } catch (e) {} });
    // 切换效果时自动填入该效果的推荐间隔时间
    const effSel = document.getElementById('wg-' + idx + '-effect');
    const speedInp = document.getElementById('wg-' + idx + '-speed');
    if (effSel && speedInp) effSel.addEventListener('change', function(){
      const def = { static: 10, marquee: 15, typewriter: 0.3, flipX: 5, flipY: 5, blink: 2 }[effSel.value] || 10;
      speedInp.value = def;
      scheduleWidgetsSave(500);
    });
    // opacity labels
    const bindPct = function(f){
      const el = document.getElementById('wg-' + idx + '-' + f);
      const lab = document.getElementById('wg-' + idx + '-' + f + '-v');
      if (!el) return;
      const upd = function(){ if (lab) lab.textContent = Math.round(el.value * 100) + '%'; };
      el.addEventListener('input', upd);
      el.addEventListener('change', function(){ upd(); scheduleWidgetsSave(500); });
    };
    bindPct('textopacity');
    bindPct('outlineopacity');
    bindPct('bgopacity');
    // auto-save bindings
    ['name','text','effect','speed','fontsize','font','color','align','outline','outlinecolor','outlinewidth','bgcolor','rounded'].forEach(function(f){
      const el = document.getElementById('wg-' + idx + '-' + f);
      if (!el) return;
      el.addEventListener(f === 'text' || f === 'name' ? 'input' : 'change', function(){
        scheduleWidgetsSave(f === 'text' ? 900 : 500);
      });
    });
  });
}

// ---- R2: 更新推送通知（右上角非侵入式 toast，3s 静默检查）----
let updateToastEl = null;
function showUpdateToast(opts) {
  if (updateToastEl) { try { updateToastEl.remove(); } catch (e) {} updateToastEl = null; }
  const el = document.createElement('div');
  el.className = 't-item update';
  const tag = (opts && opts.tag) || '';
  const asset = (opts && opts.assetName) ? ' · ' + opts.assetName : '';
  const notes = (opts && opts.notes) ? ('<div style="margin-top:4px;color:var(--muted);font-size:12px;line-height:1.4;">' + esc(String(opts.notes).slice(0, 140)) + '</div>') : '';
  el.innerHTML =
    '<div style="font-weight:600;font-size:13px;">🆕 发现新版本 ' + esc(tag) + esc(asset) + '</div>' +
    notes +
    '<div class="t-actions">' +
      '<button class="primary" id="upd-install">立即下载并安装</button>' +
      '<button id="upd-page">查看发布页</button>' +
      '<button id="upd-dismiss">稍后</button>' +
    '</div>' +
    '<div class="t-progress" id="upd-progress" style="display:none;"><div class="bar"></div></div>';
  $('#toast').appendChild(el);
  updateToastEl = el;
  el.querySelector('#upd-install').onclick = () => triggerUpdateInstall();
  el.querySelector('#upd-page').onclick = () => { if (window.electronAPI && window.electronAPI.app.update.openReleasePage) window.electronAPI.app.update.openReleasePage(); };
  el.querySelector('#upd-dismiss').onclick = () => { try { el.remove(); } catch (e) {} updateToastEl = null; };
  // 不自动消失 —— 等用户操作
}
async function triggerUpdateInstall() {
  if (!window.electronAPI || !window.electronAPI.app.update.downloadInstall) return;
  // 替换按钮为进度条
  if (updateToastEl) {
    const btns = updateToastEl.querySelector('.t-actions'); if (btns) btns.style.display = 'none';
    const p = updateToastEl.querySelector('#upd-progress'); if (p) p.style.display = 'block';
  }
  const r = await window.electronAPI.app.update.downloadInstall();
  if (!r || !r.ok) {
    toast('下载失败：' + (r && r.error || '未知'), false);
    if (updateToastEl) { try { updateToastEl.remove(); } catch (e) {} updateToastEl = null; }
  } else {
    toast('下载完成，正在启动安装器…');
  }
}
function setupUpdaterListener() {
  if (!window.electronAPI || !window.electronAPI.app || !window.electronAPI.app.update) return;
  // 进度事件（来自主进程）
  try { window.electronAPI.app.update.onEvent((payload) => {
    if (!payload) return;
    if (payload.type === 'checked') {
      if (payload.status === 'available' && payload.state) {
        showUpdateToast({ tag: payload.state.lastTag, assetName: payload.state.asset && payload.state.asset.name, notes: payload.state.releaseNotes });
      } else if (payload.status === 'throttled') {
        // 静默：已节流不弹
      } else if (payload.status === 'uptodate') {
        // 已是最新版本，清除可能因旧缓存显示的更新提示
        if (updateToastEl) { try { updateToastEl.remove(); } catch (e) {} updateToastEl = null; }
      }
    } else if (payload.type === 'downloadProgress') {
      if (updateToastEl) {
        const bar = updateToastEl.querySelector('#upd-progress .bar');
        if (bar && payload.total) bar.style.width = Math.min(100, Math.round((payload.received / payload.total) * 100)) + '%';
      }
    } else if (payload.type === 'checkFailed') {
      // 静默：网络失败不打扰用户（仅写 log）
    }
  }); } catch (e) {}
  // 启动时拉一次缓存（避免错过 did-finish-load 之前的事件）
  try { window.electronAPI.app.update.getCached().then((st) => {
    if (st && st.lastNewer && st.lastTag) {
      // 仅当最近 12h 内发现的新版才显示（避免冷启动看到陈旧通知）
      if (st.lastCheck && (Date.now() - st.lastCheck) < 12 * 60 * 60 * 1000) {
        showUpdateToast({ tag: st.lastTag, assetName: st.asset && st.asset.name, notes: st.releaseNotes });
      }
    }
  }); } catch (e) {}
}

// 手动检查更新（设置页按钮）
async function manualCheckUpdate(force) {
  if (!window.electronAPI || !window.electronAPI.app.update) {
    if (typeof toast === 'function') toast('请在 Electron 模式下检查更新', false);
    return;
  }
  const btn = document.getElementById('btn-update-check');
  const statusEl = document.getElementById('update-status');
  const setStatus = (text, kind) => {
    if (statusEl) {
      statusEl.textContent = text;
      statusEl.style.color = kind === 'ok' ? 'var(--accent)' : (kind === 'err' ? 'var(--danger)' : 'var(--muted)');
    }
  };
  if (btn) { btn.disabled = true; btn.textContent = '⏳ 检查中...'; }
  setStatus('正在检查新版本…');
  try {
    const r = await window.electronAPI.app.update.check(force !== false);
    if (!r || !r.ok) {
      setStatus('检查失败：' + (r && r.error || '未知错误'), 'err');
      if (typeof toast === 'function') toast('检查更新失败：' + (r && r.error || '未知'), false);
      return;
    }
    if (r.status === 'available' && r.state && r.state.asset) {
      setStatus('发现新版本 ' + r.state.lastTag + '，已在上方弹出提示', 'ok');
    } else if (r.status === 'uptodate') {
      setStatus('已是最新版本（当前 ' + (r.state ? r.state.lastTag : '') + '）', 'ok');
      if (typeof toast === 'function') toast('已是最新版本');
    } else if (r.status === 'throttled') {
      setStatus('已节流（6h 内已检查过），稍后再试', 'ok');
    } else {
      setStatus('检查完成：' + r.status, 'ok');
    }
  } catch (e) {
    setStatus('检查失败：' + (e && e.message || e), 'err');
  } finally {
    if (btn) { btn.disabled = false; btn.textContent = '🆕 检查更新'; }
  }
}

// 管理面板从 file:// 加载，必须用绝对 http:// 地址访问 server（127.0.0.1:7360）
const SERVER_BASE = '';
function apiUrl(path) {
  let p = String(path || '').replace(/^\/+/, '');
  if (/^https?:\/\//i.test(p)) return p;
  return SERVER_BASE + '/' + p;
}
async function api(path, opts = {}) {
  const maxRetries = (opts && opts._retries != null) ? opts._retries : 20;
  const baseDelay = 300;
  let lastErr = null;
  const fullUrl = apiUrl(path);
  for (let attempt = 0; attempt < maxRetries; attempt++) {
    try {
      const r = await fetch(fullUrl, Object.assign({ headers: { 'Content-Type': 'application/json' } }, opts));
      let j; try { j = await r.json(); } catch (e) { j = {}; }
      if (!r.ok) throw new Error((j && j.error) || ('请求失败 ' + r.status));
      return j;
    } catch (e) {
      lastErr = e;
      const isServerNotReady = /Failed to fetch|network|ECONNREFUSED|load failed|fetch failed|Failed to load resource|Failed to load resource: net/i.test(String(e && e.message || ''));
      if (attempt >= maxRetries - 1) break;
      const delay = Math.min(baseDelay * Math.pow(2, attempt), 2000);
      await new Promise(r => setTimeout(r, delay));
    }
  }
  throw lastErr || new Error('请求失败');
}
const g = (p) => api(p);
const post = (p, b) => api(p, { method: 'POST', body: JSON.stringify(b || {}) });

// ---------- 文件选择器 ----------
function openModal(title, html){
  $('#modal-title').textContent = title;
  $('#modal-body').innerHTML = html;
  $('#modal-mask').classList.add('open');
}
function closeModal(){ $('#modal-mask').classList.remove('open'); }
async function pickFolder(dir){
  const data = await post('/api/list-dir', { dir: dir || (state.config && state.config.dataRoot) });
  const html =
    '<div><p class="muted">当前：<span class="mono">' + esc(data.dir || '') + '</span></p>' +
    '<div>' + (data.parent && data.parent !== data.dir ? '<button class="btn sm" onclick="browseUp()">⬆ 返回上级</button>' : '<button class="btn sm" disabled>⬆ 已是根目录</button>') + '</div>' +
    '<div class="fs-list" style="margin-top:10px;">' +
    data.items.map(it => '<div class="fs-row" data-dir="' + esc((data.dir + '/' + it).replace(/\\/g,'/')) + '">' +
      '<span>📁</span><span>' + esc(it) + '</span><span class="spacer"></span><button class="btn sm" data-use="1">选择此文件夹</button></div>').join('') +
    '</div>' +
    '<div class="row" style="margin-top:12px;justify-content:flex-end;"><button class="btn" onclick="closeModal()">取消</button><button class="btn primary" onclick="useThisDir()">使用当前目录</button></div></div>';
  window._fsDir = data.dir;
  window._fsParent = data.parent;
  window.chooseRow = chooseRow;
  window.browseUp = () => { closeModal(); pickFolder(window._fsParent); };
  window.useThisDir = () => { closeModal(); resolvePicked(window._fsDir); };
  openModal('选择文件夹', html);
  $$('.fs-row', $('#modal-body')).forEach(row => {
    const useBtn = row.querySelector('[data-use]');
    if (useBtn) useBtn.addEventListener('click', (e) => { e.stopPropagation(); chooseRow(row); });
    row.addEventListener('dblclick', () => chooseRow(row));
  });
}
function chooseRow(row){
  const dir = row.getAttribute('data-dir');
  closeModal();
  pickFolder(dir);
}
let pickedCb = null;
function resolvePicked(dir){ if (pickedCb){ pickedCb(dir); pickedCb = null; } }
function pickAndSet(inputId){
  const input = $('#' + inputId);
  pickedCb = (dir) => { input.value = dir; };
  pickFolder(input.value || (state.config && state.config.dataRoot));
}

// ---------- 侧栏 ----------
// 侧栏分组折叠：默认收起「日志记录」，点击组标题展开/收起，状态记 localStorage
const NAV_FOLD_KEY = 'blt_nav_fold';
const NAV_FOLD_DEFAULT = { control: false, realtime: false, logs: true, obs: false, settings: false };
function navFoldState(){
  try { return JSON.parse(localStorage.getItem(NAV_FOLD_KEY)) || {}; } catch (e) { return {}; }
}
function setGroupFold(group, folded, persist){
  const body = document.querySelector('#nav .nav-group-body[data-group="' + group + '"]');
  const head = document.querySelector('#nav .nav-group[data-nav-group="' + group + '"]');
  if (!body || !head) return;
  body.classList.toggle('folded', folded);
  head.classList.toggle('folded', folded);
  if (persist) {
    const st = navFoldState();
    st[group] = folded;
    try { localStorage.setItem(NAV_FOLD_KEY, JSON.stringify(st)); } catch (e) {}
  }
}
// 「日志记录」组标题小红点：有任一记录在进行时点亮（折叠时也能看到）
function updateLogsGroupDot(){
  const head = document.querySelector('#nav .nav-group[data-nav-group="logs"]');
  if (!head) return;
  const rec = (state.config && state.config.recording) || {};
  const anyOn = TYPES.some(t => rec[t] !== false);
  head.classList.toggle('has-rec', anyOn);
}
function buildNav(){
  const nav = $('#nav');
  const rec = (state.config && state.config.recording) || {};
  const logItems = TYPES.map(t => {
    const on = rec[t] !== false;
    return '<div class="nav-item nav-child" data-route="#/logs/' + t + '" data-tip="进入' + TYPE_META[t].title + '">' +
      '<span class="ico">' + ICONS[t] + '</span><span>' + TYPE_META[t].title + '</span>' +
      '<span class="badge-pill ' + (on ? 'on' : 'off') + ' nav-rec-badge" data-rec-type="' + t + '" data-tip="' + (on ? '记录中 · 点击停止记录' : '已停用 · 点击开始记录') + '">' + (on ? '记录中' : '停用') + '</span></div>';
  }).join('');
  let html =
    '<div class="nav-group" data-nav-group="control" data-tip="展开/收起"><span>直播控制</span></div>' +
    '<div class="nav-group-body" data-group="control">' +
    '<div class="nav-item" data-route="#/tts" data-tip="语音念弹幕：用语音引擎朗读弹幕、礼物等事件"><span class="ico">🔊</span><span>语音念弹幕</span></div>' +
    '<div class="nav-item" data-route="#/autodanmu" data-tip="自动弹幕：欢迎进房观众、感谢礼物、定时弹幕"><span class="ico">' + ICONS.danmu + '</span><span>自动弹幕</span></div>' +
    '<div class="nav-item" data-route="#/pk" data-tip="PK/连线：抓取对方直播间数据"><span class="ico">' + ICONS.guard + '</span><span>PK/连线</span></div>' +
    '<div class="nav-item" data-route="#/song-request" data-tip="点歌功能：观众弹幕点歌，自动搜索播放"><span class="ico">🎵</span><span>点歌功能</span></div>' +
    '</div>' +
    '<div class="nav-group" data-nav-group="realtime" data-tip="展开/收起"><span>实时与展示</span></div>' +
    '<div class="nav-group-body" data-group="realtime">' +
    '<div class="nav-item" data-route="#/tools/realtime" data-tip="实时展示：实时查看弹幕、礼物等事件"><span class="ico">' + ICONS.rt + '</span><span>实时展示</span></div>' +
    '<div class="nav-item" data-route="#/tools/gift" data-tip="礼物截图生成：生成可分享的礼物卡片图片"><span class="ico">' + ICONS.gifts + '</span><span>礼物截图生成</span></div>' +
    '</div>' +
    '<div class="nav-group" data-nav-group="logs" data-tip="展开/收起"><span>日志记录</span><span class="nav-group-rec-dot" data-tip="有记录正在进行"></span></div>' +
    '<div class="nav-group-body" data-group="logs">' +
    logItems +
    '</div>' +
    '<div class="nav-group" data-nav-group="obs" data-tip="展开/收起"><span>OBS 浮层</span></div>' +
    '<div class="nav-group-body" data-group="obs">' +
    '<div class="nav-item" data-route="#/alert" data-tip="提醒特效墙：礼物/舰长/SC/关注/进房 在直播画面弹横幅和音效"><span class="ico">✨</span><span>提醒特效墙</span></div>' +
    '<div class="nav-item" data-route="#/widgets" data-tip="文字挂件：在OBS画面显示公告/推广/粉丝群等文字，支持滚动/跳字/翻页效果"><span class="ico">📜</span><span>文字挂件</span></div>' +
    '<div class="nav-item" data-route="#/keyview" data-tip="键鼠手柄可视化：在OBS中显示键盘/鼠标/手柄操作"><span class="ico">🎮</span><span>键鼠手柄可视化</span></div>' +
    '</div>' +
    '<div class="nav-group" data-nav-group="settings" data-tip="展开/收起"><span>设置</span></div>' +
    '<div class="nav-group-body" data-group="settings">' +
    '<div class="nav-item" data-route="#/settings" data-tip="房间管理：连接直播间、登录、文件夹配置等"><span class="ico">' + ICONS.set + '</span><span>房间管理</span></div>' +
    '<div class="nav-item" data-route="#/help" data-tip="使用教程：3步快速上手、常见问题、联系作者"><span class="ico">📖</span><span>使用教程</span></div>' +
    '</div>';
  nav.innerHTML = html;
  $$('.nav-item[data-route]', nav).forEach(item => {
    item.addEventListener('click', () => { location.hash = item.getAttribute('data-route'); });
  });
  // 徽章点击 = 切换记录开关（不进页面）
  $$('.nav-rec-badge', nav).forEach(badge => {
    badge.addEventListener('click', (e) => { e.stopPropagation(); toggleRecordFromNav(badge); });
  });
  // 分组标题点击 = 折叠/展开（状态记 localStorage）
  $$('.nav-group[data-nav-group]', nav).forEach(head => {
    head.addEventListener('click', () => {
      const g = head.getAttribute('data-nav-group');
      setGroupFold(g, !head.classList.contains('folded'), true);
    });
  });
  // 应用折叠状态：有记忆用记忆，否则用默认（日志记录收起）
  const st = navFoldState();
  Object.keys(NAV_FOLD_DEFAULT).forEach(g => {
    setGroupFold(g, (g in st) ? !!st[g] : NAV_FOLD_DEFAULT[g], false);
  });
  // 当前页所在分组若被折叠则自动展开（折叠只是收纳，不碍事导航）
  const cur = nav.querySelector('.nav-item[data-route="' + (location.hash || '#/settings') + '"]');
  if (cur) {
    const body = cur.closest('.nav-group-body');
    if (body && body.classList.contains('folded')) setGroupFold(body.getAttribute('data-group'), false, true);
  }
  updateLogsGroupDot();
}
async function toggleRecordFromNav(badge) {
  const t = badge.getAttribute('data-rec-type');
  const on = !(state.config && state.config.recording && state.config.recording[t]);
  try {
    const r = await post('/api/recording/' + t, { on: on });
    if (!state.config.recording) state.config.recording = {};
    state.config.recording[t] = r.on;
    badge.classList.toggle('on', r.on);
    badge.classList.toggle('off', !r.on);
    badge.textContent = r.on ? '记录中' : '停用';
    badge.title = r.on ? '记录中 · 点击停止记录' : '已停用 · 点击开始记录';
    toast((r.on ? '已开始记录' : '已停止记录') + TYPE_NAMES[t]);
    updateLogsGroupDot();
    // 日志页顶部按钮同步
    if (window._logType === t) refreshRecordBtn(t);
  } catch (e) { toast(e.message, false); }
}
function setActiveNav(route){
  $$('.nav-item').forEach(i => i.classList.toggle('active', i.getAttribute('data-route') === route));
  // 目标分组若被折叠，导航过去时自动展开
  const item = document.querySelector('.nav-item[data-route="' + route + '"]');
  if (item) {
    const body = item.closest('.nav-group-body');
    if (body && body.classList.contains('folded')) setGroupFold(body.getAttribute('data-group'), false, true);
  }
}

// ---------- 路由 ----------
function router(){
  const hash = location.hash || '#/settings';
  // When verification is locked, allow only settings / bili-login / help / diagnostics (they explain how to get authorized & troubleshoot)
  if (state.verifyLocked && hash !== '#/settings' && hash !== '#/bili-login' && hash !== '#/help' && hash !== '#/diagnostics') {
    location.hash = '#/settings'; return;
  }
  const m = /^#\/logs\/([a-z]+)$/.exec(hash);
  if (m && TYPES.includes(m[1])) { renderLogView(m[1]); setActiveNav('#/logs/' + m[1]); return; }
  if (hash === '#/tools/gift') { renderGiftTool(); setActiveNav('#/tools/gift'); return; }
  if (hash === '#/tools/realtime') { renderRealtime(); setActiveNav('#/tools/realtime'); return; }
  if (hash === '#/autodanmu') { renderAutoDanmuPage(); setActiveNav('#/autodanmu'); return; }
  if (hash === '#/pk') { renderPkPage(); setActiveNav('#/pk'); return; }
  if (hash === '#/song-request') { renderSongRequestPage(); setActiveNav('#/song-request'); return; }
  if (hash === '#/tts') { renderTtsPage(); setActiveNav('#/tts'); return; }
  if (hash === '#/debug') { renderDebugPage(); setActiveNav(''); return; }
  if (hash === '#/keyview') { renderKeyViewPage(); setActiveNav('#/keyview'); return; }
  if (hash === '#/alert') { renderAlertPage(); setActiveNav('#/alert'); return; }
  if (hash === '#/widgets') { renderWidgetsPage(); setActiveNav('#/widgets'); return; }
  if (hash === '#/diagnostics') { renderDiagnosticsPage(); setActiveNav(''); return; }
  if (hash === '#/help') { renderHelpPage(); setActiveNav('#/help'); return; }
  if (hash === '#/bili-login' && window.electronAPI) { renderBiliLogin(); setActiveNav('#/bili-login'); return; }
  if (hash === '#/bili-live' && window.electronAPI) { renderBiliLive(); setActiveNav('#/bili-live'); return; }
  renderSettings(); setActiveNav('#/settings');
}

// ---------- 键鼠手柄可视化 ----------
// v1.1.9: localize layout option labels (values stay English for the overlay engine)
const KB_LAYOUT_NAMES = { full: '完整键盘', alpha: '仅字母区', compact: '紧凑', minimal: '极简' };
const GP_LAYOUT_NAMES = { xbox: 'Xbox 手柄', ps: 'PlayStation 手柄', nintendo: 'Switch 手柄' };
async function renderKeyViewPage(){
  const c = document.getElementById('content');
  if (!c) return;
  const kv = (window.electronAPI && window.electronAPI.keyview) ? window.electronAPI.keyview : null;
  let st = { running: false, port: 0, clients: 0 };
  let cfg = {};
  let themes = [];
  if (kv) {
    try { st = await kv.status(); } catch(e){}
    try { cfg = await kv.getConfig(); } catch(e){}
    try { const tm = await kv.getThemes(); themes = (tm && tm.themes) || []; } catch(e){}
  }
  if (themes.length === 0) {
    themes = [
      { id: 'glass', name: '毛玻璃极简' }, { id: 'real', name: '写实键盘鼠标' },
      { id: 'kb-stream', name: '流式按键序列' }, { id: 'pad-real', name: '写实手柄' },
      { id: 'kb-heatmap', name: 'KPS 热力图' }, { id: 'mouse-trail', name: '鼠标轨迹' },
      { id: 'neon', name: '霓虹赛博朋克' }, { id: 'mech', name: '机械轴体' },
      { id: 'pixel', name: '像素复古' }, { id: 'sticky', name: '手写便签' },
      { id: 'pad-mech', name: '手柄机甲' }, { id: 'dashboard', name: '统一仪表盘' },
      { id: 'ds', name: '饥荒手绘' }, { id: 'macaron', name: '马卡龙甜点' },
      { id: 'dmc', name: '鬼泣暗黑' }, { id: 'matrix', name: '黑客矩阵' },
      { id: 'vaporwave', name: '蒸汽波' }, { id: 'sakura', name: '日式樱花' },
      { id: 'gold', name: '王者金' }, { id: 'crt', name: '复古终端' },
      { id: 'ink', name: '水墨丹青' }, { id: 'aqua', name: '深海之光' },
      { id: 'hexgrid', name: '蜂巢六边形' }, { id: 'orb', name: '魔法气泡' },
      { id: 'arcade', name: '街机厅' }, { id: 'holo', name: '全息故障' },
      { id: 'gameboy', name: '掌机液晶' }, { id: 'candy', name: '果冻软糖' },
      { id: 'steampunk', name: '蒸汽朋克' },
    ];
  }
  const themeOpts = themes.map(function(t){ return '<option value="' + t.id + '"' + (cfg['overlay.theme'] === t.id ? ' selected' : '') + '>' + esc(t.name) + '</option>'; }).join('');
  const overlayUrl = st.running ? 'http://127.0.0.1:' + st.port + '/overlay' : '（未启动）';

  c.innerHTML =
    '<div class="card" style="margin-bottom:16px;">' +
      '<div class="box-header with-border"><h3 class="box-title">键鼠手柄可视化</h3></div>' +
      '<p class="muted" style="margin-top:0;">在直播时于 OBS 中显示键盘、鼠标、手柄操作的可视化叠加层。点击「启动」后，在 OBS 中添加浏览器源，URL 填下方的 Overlay 地址。</p>' +
      '<div class="row" style="margin-top:12px;align-items:center;gap:12px;">' +
        '<button class="btn ' + (st.running ? 'danger' : 'success') + '" id="kv-toggle">' + (st.running ? '⏹ 停止' : '▶ 启动') + '</button>' +
        '<span class="muted" id="kv-status">' + (st.running ? '运行中 · 端口 ' + st.port + ' · 客户端 ' + st.clients : '未运行') + '</span>' +
      '</div>' +
    '</div>' +

    '<div class="card" style="margin-bottom:16px;">' +
      '<div class="box-header with-border"><h3 class="box-title">OBS 接入</h3></div>' +
      '<div class="field" style="margin-top:8px;">' +
        '<label>Overlay URL（在 OBS 浏览器源中填此地址，建议宽 800 高 600）</label>' +
        '<div class="row" style="gap:8px;align-items:center;">' +
          '<input type="text" id="kv-url" value="' + esc(overlayUrl) + '" readonly style="flex:1;font-family:ui-monospace,monospace;">' +
          '<button class="btn sm" id="kv-copy-url">复制</button>' +
          '<button class="btn sm" id="kv-open-overlay" ' + (st.running ? '' : 'disabled') + '>打开预览</button>' +
        '</div>' +
      '</div>' +
    '</div>' +

    '<div class="card" style="margin-bottom:16px;">' +
      '<div class="box-header with-border"><h3 class="box-title">皮肤与显示</h3></div>' +
      '<div class="row" style="margin-top:12px;gap:16px;flex-wrap:wrap;">' +
        '<div class="field" style="flex:1;min-width:200px;"><label>皮肤</label><select id="kv-theme" class="form-control">' + themeOpts + '</select></div>' +
        '<div class="field" style="flex:0 0 120px;"><label>透明度</label><input type="range" id="kv-opacity" min="0.2" max="1" step="0.05" value="' + (cfg['overlay.opacity'] || 1) + '" style="width:100%;"></div>' +
        '<div class="field" style="flex:0 0 120px;"><label>缩放</label><input type="range" id="kv-scale" min="0.5" max="2" step="0.1" value="' + (cfg['overlay.scale'] || 1) + '" style="width:100%;"></div>' +
        '<div class="field" style="flex:0 0 140px;"><label>键盘布局</label><select id="kv-kb-layout" class="form-control">' +
          ['full','alpha','compact','minimal'].map(function(l){ return '<option value="' + l + '"' + (cfg['display.kbLayout'] === l ? ' selected' : '') + '>' + esc(KB_LAYOUT_NAMES[l] || l) + '</option>'; }).join('') +
        '</select></div>' +
        '<div class="field" style="flex:0 0 120px;"><label>手柄布局</label><select id="kv-gp-layout" class="form-control">' +
          ['xbox','ps','nintendo'].map(function(l){ return '<option value="' + l + '"' + (cfg['display.gpLayout'] === l ? ' selected' : '') + '>' + esc(GP_LAYOUT_NAMES[l] || l) + '</option>'; }).join('') +
        '</select></div>' +
      '</div>' +
    '</div>' +

    '<div class="card">' +
      '<div class="box-header with-border"><h3 class="box-title">设备开关</h3></div>' +
      '<div class="row" style="margin-top:12px;gap:20px;flex-wrap:wrap;">' +
        '<div>' + switchHtml('kv-kb-enabled', cfg['device.keyboard.enabled'] !== false, '键盘') + '</div>' +
        '<div>' + switchHtml('kv-ms-enabled', cfg['device.mouse.enabled'] !== false, '鼠标') + '</div>' +
        '<div>' + switchHtml('kv-gp-enabled', cfg['device.gamepad.enabled'] !== false, '手柄') + '</div>' +
      '</div>' +
      '<p class="muted" style="margin-top:12px;font-size:12px;">关闭某设备后，该设备的事件不会被捕获和显示。</p>' +
    '</div>';

  if (!kv) return;

  var btn = document.getElementById('kv-toggle');
  if (btn) btn.addEventListener('click', async function(){
    if (btn.textContent.indexOf('启动') >= 0) {
      btn.disabled = true; btn.textContent = '启动中…';
      var r = await kv.start();
      btn.disabled = false;
      if (r.ok) { toast('✅ 已启动，端口 ' + r.port); renderKeyViewPage(); }
      else { toast('启动失败: ' + (r.error || ''), false); btn.textContent = '▶ 启动'; btn.className = 'btn success'; }
    } else {
      btn.disabled = true; btn.textContent = '停止中…';
      await kv.stop();
      btn.disabled = false;
      toast('已停止');
      renderKeyViewPage();
    }
  });

  var copyBtn = document.getElementById('kv-copy-url');
  if (copyBtn) copyBtn.addEventListener('click', function(){
    var inp = document.getElementById('kv-url');
    if (inp && inp.value) { inp.select(); document.execCommand('copy'); toast('已复制到剪贴板'); }
  });

  var openBtn = document.getElementById('kv-open-overlay');
  if (openBtn) openBtn.addEventListener('click', function(){ kv.openOverlay(); });

  var themeSel = document.getElementById('kv-theme');
  if (themeSel) themeSel.addEventListener('change', function(){ kv.setConfig('overlay.theme', this.value); });

  var opEl = document.getElementById('kv-opacity');
  if (opEl) opEl.addEventListener('input', function(){ kv.setConfig('overlay.opacity', parseFloat(this.value)); });

  var scEl = document.getElementById('kv-scale');
  if (scEl) scEl.addEventListener('input', function(){ kv.setConfig('overlay.scale', parseFloat(this.value)); });

  var kbLayoutSel = document.getElementById('kv-kb-layout');
  if (kbLayoutSel) kbLayoutSel.addEventListener('change', function(){ kv.setConfig('display.kbLayout', this.value); });

  var gpLayoutSel = document.getElementById('kv-gp-layout');
  if (gpLayoutSel) gpLayoutSel.addEventListener('change', function(){ kv.setConfig('display.gpLayout', this.value); });

  var kbCb = document.getElementById('kv-kb-enabled');
  if (kbCb) kbCb.addEventListener('change', function(){ kv.setConfig('device.keyboard.enabled', this.checked); });

  var msCb = document.getElementById('kv-ms-enabled');
  if (msCb) msCb.addEventListener('change', function(){ kv.setConfig('device.mouse.enabled', this.checked); });

  var gpCb = document.getElementById('kv-gp-enabled');
  if (gpCb) gpCb.addEventListener('change', function(){ kv.setConfig('device.gamepad.enabled', this.checked); });
}

// ---------- 房间管理(设置) ----------
// ---------- Electron 桌面应用：服务端控制面板 ----------
async function renderServerCtrlPanel() {
  const el = document.getElementById('serverCtrlPanel');
  if (!el) return;
  if (!window.electronAPI) {
    // Web 模式：管理面板本身就是 server 提供，说明 server 在运行（v1.1.9 清理旧 bat 文案）
    el.innerHTML = '<p class="muted" style="margin:8px 0;">当前是 <b>Web 模式</b>（浏览器打开管理面板），服务端已在本机运行。</p>' +
      '<div class="row" style="margin-top:6px;">' +
      '<button class="btn sm" onclick="openDataFolder()">📂 打开数据目录</button>' +
      '<button class="btn sm" onclick="openConfigFolder()">⚙️ 打开配置目录</button>' +
      '</div>';
    return;
  }
  // Electron 模式：用 fetch 探测 server（管理面板从 http:// 加载，若能显示则 server 在跑）
  let status = { status: 'running', port: 7360 };
  try {
    const j = await g('/api/status');
    status = { status: 'running', port: 7360, pid: 0, external: false, online: j && j.popularity };
    const st = await window.electronAPI.server.status();
    if (st) {
      status.pid = st.pid || 0;
      status.recentLogs = st.recentLogs || [];
    }
  } catch (e) {
    status = { status: 'error', port: 7360 };
  }
  const ver = await window.electronAPI.app.getVersion().catch(() => ({}));
  const autoLaunch = await window.electronAPI.app.getAutoLaunch().catch(() => ({on:false}));

  const statusClass = {
    stopped: 'idle', starting: 'pending', running: 'live', crashed: 'error'
  }[status.status] || 'idle';
  const statusText = {
    stopped: '⏹ 已停止', starting: '⏳ 启动中...',
    running: status.external ? '✅ 运行中（外部）' : '✅ 运行中',
    crashed: '❌ 已崩溃'
  }[status.status] || status.status;

  el.innerHTML =
    '<div class="row" style="margin:6px 0;gap:10px;align-items:center;flex-wrap:wrap;">' +
      '<span class="dot ' + statusClass + '"></span>' +
      '<b>' + esc(statusText) + '</b>' +
      '<span class="muted">端口: ' + (status.port || 7360) + (status.pid ? ' (pid ' + status.pid + ')' : '') + '</span>' +
    '</div>' +
    '<div class="row" style="margin:6px 0;gap:6px;flex-wrap:wrap;">' +
      '<button class="btn sm primary" onclick="srvStart()" data-tip="启动内置服务端进程，负责弹幕连接和API接口">▶ 启动</button>' +
      '<button class="btn sm" onclick="srvStop()" data-tip="停止服务端进程">⏸ 停止</button>' +
      '<button class="btn sm" onclick="srvRestart()" data-tip="重启服务端进程">↻ 重启</button>' +
      '<button class="btn sm" onclick="srvReadLog()" data-tip="查看服务端最近50行运行日志，用于排查问题">📋 日志</button>' +
      '<span style="flex:1"></span>' +
      '<button class="btn sm" onclick="openDataFolder()" data-tip="打开数据目录（记录文件）">📂 数据</button>' +
      '<button class="btn sm" onclick="openConfigFolder()" data-tip="打开配置目录（config.json）">⚙️ 配置</button>' +
      '<button class="btn sm primary" onclick="manualCheckUpdate(true)" id="btn-update-check" data-tip="检查是否有新版本可下载安装">🆕 检查更新</button>' +
    '</div>' +
    '<div style="margin-top:8px;font-size:12px;">' +
      '<label class="row" style="gap:6px;cursor:pointer;">' +
        '<input type="checkbox" id="autoLaunchCb"' + (autoLaunch.on ? ' checked' : '') + ' onchange="toggleAutoLaunch(this.checked)" data-tip="开机时自动启动本程序"> 开机自启' +
      '</label>' +
    '</div>' +
    '<details style="margin-top:8px;">' +
      '<summary class="muted" style="cursor:pointer;font-size:12px;">最近日志（最多 50 行）</summary>' +
      '<pre id="serverLog" class="mono" style="background:rgba(0,0,0,.3);padding:8px;border-radius:4px;max-height:200px;overflow:auto;font-size:11px;margin:6px 0 0 0;">' + esc((status.recentLogs||[]).join('\n')) + '</pre>' +
    '</details>' +
    '<details style="margin-top:6px;">' +
      '<summary class="muted" style="cursor:pointer;font-size:12px;">应用信息</summary>' +
      '<div class="mono" style="font-size:11px;padding:4px 0;">本程序版本: <b>' + esc(ver.app || window.__APP_VERSION || '-') + '</b></div>' +
      '<div class="mono" style="font-size:11px;padding:4px 0;">Electron: ' + esc(ver.electron||'-') + ' | Node: ' + esc(ver.node||'-') + ' | Chrome: ' + esc((ver.chrome||'').slice(0,20)) + '</div>' +
      '<div id="update-status" class="muted" style="font-size:11px;padding:4px 0;">点上方「🆕 检查更新」手动检查新版本</div>' +
    '</details>';
    // 自动刷新：每 2 秒局部更新状态（不重绘整个面板，避免 details 折叠被重置）
    if (!renderServerCtrlPanel._timer) {
      renderServerCtrlPanel._timer = setInterval(() => {
        const el = document.getElementById('serverCtrlPanel');
        if (!el) { clearInterval(renderServerCtrlPanel._timer); renderServerCtrlPanel._timer = null; return; }
        g('/api/status').then(() => {
          updateServerStatusLine({ status: 'running', port: 7360 });
        }).catch(() => {
          updateServerStatusLine({ status: 'error', port: 7360 });
        });
      }, 2000);
    }
}

// 局部更新服务端控制面板的状态行（不重绘 details/日志，保持展开状态）
function updateServerStatusLine(status) {
  if (!status) return;
  const dot = document.querySelector('#serverCtrlPanel .dot');
  const txt = document.querySelector('#serverCtrlPanel b');
  const port = document.querySelector('#serverCtrlPanel .muted');
  if (!dot || !txt) return;
  const statusClass = {
    stopped: 'idle', starting: 'pending', running: 'live', crashed: 'error'
  }[status.status] || 'idle';
  const statusText = {
    stopped: '⏹ 已停止', starting: '⏳ 启动中...',
    running: status.external ? '✅ 运行中（外部）' : '✅ 运行中',
    crashed: '❌ 已崩溃'
  }[status.status] || status.status;
  dot.className = 'dot ' + statusClass;
  txt.textContent = statusText;
  if (port) port.textContent = '端口: ' + (status.port || 7360) + (status.pid ? ' (pid ' + status.pid + ')' : '');
}

// 手动刷新整个管理面板数据
async function refreshPanel() {
  try {
    state.config = await g('/api/config');
    await refreshStatus();
    await renderServerCtrlPanel(true);
    biliUpdateStatusInline();
    await renderSettings();
    toast('已刷新');
  } catch (e) {
    toast('刷新失败: ' + (e && e.message || e), false);
  }
}
async function srvStart() {
  const r = await window.electronAPI.server.start();
  if (r && r.ok) toast('服务已启动' + (r.pid ? ' (pid ' + r.pid + ')' : ''));
  else toast('启动失败: ' + (r && r.error || '未知'), false);
  await renderServerCtrlPanel();
}
async function srvStop() {
  const r = await window.electronAPI.server.stop();
  if (r && r.ok) toast('服务已停止');
  else toast('停止失败: ' + (r && r.error || '未知'), false);
  await renderServerCtrlPanel();
}
async function srvRestart() {
  const r = await window.electronAPI.server.restart();
  if (r && r.ok) toast('服务已重启' + (r.pid ? ' (pid ' + r.pid + ')' : ''));
  else toast('重启失败: ' + (r && r.error || '未知'), false);
  await renderServerCtrlPanel();
}

async function srvReadLog() {
  if (!window.electronAPI) return;
  const r = await window.electronAPI.server.readLog(100);
  const text = (r && r.lines && r.lines.length) ? r.lines.join('\n') : '(暂无日志)';
  // open a modal with the log
  const mask = document.getElementById('modal-mask');
  const body = document.getElementById('modal-body');
  const title = document.getElementById('modal-title');
  if (!mask || !body) { toast(text.slice(0, 200), false); return; }
  title.textContent = '服务端日志（最近 100 行）';
  body.innerHTML = '<pre class="mono" style="background:rgba(0,0,0,.4);padding:10px;border-radius:4px;max-height:60vh;overflow:auto;font-size:11px;white-space:pre-wrap;word-break:break-all;">' + esc(text) + '</pre>';
  mask.classList.add('open');
}
async function openDataFolder() {
  if (window.electronAPI) {
    const r = await window.electronAPI.app.openDataFolder();
    if (!r || !r.ok) toast('打开失败: ' + (r && r.error || '未知'), false);
  } else {
    // Web mode fallback
    const c = state.config || {};
    const paths = c.folders || {};
    if (paths.danmu) window.open('file:///' + paths.danmu.replace(/\\/g,'/').replace(/^([A-Z]:\?)/, '$1'));
  }
}
async function openConfigFolder() {
  if (window.electronAPI) {
    const r = await window.electronAPI.app.openConfigFolder();
    if (!r || !r.ok) toast('打开失败: ' + (r && r.error || '未知'), false);
  }
}
async function toggleAutoLaunch(on) {
  if (!window.electronAPI) return;
  const r = await window.electronAPI.app.setAutoLaunch(on);
  if (r && r.ok) toast(on ? '已开启开机自启' : '已关闭开机自启');
  else toast('设置失败: ' + (r && r.error || '未知'), false);
}

// Subscribe to server status / log (called once)
function setupElectronListeners() {
  if (!window.electronAPI) return;
  // B站内置浏览器是独立 WebContentsView：它持有键盘焦点时，面板里的输入框
  // 「点得进去（DOM 有焦点）但按键全被它吃掉」。任何一次面板内 mousedown 都
  // 显式把键盘焦点交还面板（节流，避免 IPC 刷屏）。
  if (window.electronAPI.app && window.electronAPI.app.focusPanel) {
    document.addEventListener('mousedown', function (e) {
      const now = Date.now();
      if (window._lastPanelFocusAt && now - window._lastPanelFocusAt < 400) return;
      window._lastPanelFocusAt = now;
      try { window.electronAPI.app.focusPanel(); } catch (e) {}
      try {
        if (typeof diag === 'function') diag('focus handback target=' + ((e.target && (e.target.id || e.target.tagName)) || '?') + ' hasFocus=' + document.hasFocus());
      } catch (e) {}
    }, true);
  }
  // Electron 模式：给 body 加 class 触发布局
  // 默认 electron-mode-no-browser（占满），打开 B站浏览器后切换为 electron-mode（35vw 折叠）
  document.body.classList.add('electron-mode-no-browser');
  // 订阅 B站浏览器状态
  if (window.electronAPI.bili && window.electronAPI.bili.onState) {
    window.electronAPI.bili.onState((st) => {
      updateBodyClassForBrowser(biliBrowserVisible(st));
    });
  }
  setTimeout(() => {
    // 初次拉一次状态决定 class
    if (window.electronAPI.bili && window.electronAPI.bili.state) {
      window.electronAPI.bili.state().then((st) => {
        updateBodyClassForBrowser(biliBrowserVisible(st));
      }).catch(() => {});
    }
  }, 200);
  if (window.electronAPI.server.onLog) {
    window.electronAPI.server.onLog((line) => {
      const pre = document.getElementById('serverLog');
      if (pre) {
        pre.textContent += (pre.textContent ? '\n' : '') + line;
        const lines = pre.textContent.split('\n');
        if (lines.length > 200) pre.textContent = lines.slice(-200).join('\n');
        pre.scrollTop = pre.scrollHeight;
      }
    });
  }
  if (window.electronAPI.server.onStatus) {
    window.electronAPI.server.onStatus(() => {
      const el = document.getElementById('serverCtrlPanel');
      if (el) renderServerCtrlPanel();
    });
  }
  if (window.electronAPI.bili && window.electronAPI.bili.onState) {
    window.electronAPI.bili.onState(() => { biliUpdateStatus(); });
  }
  // 音乐平台 Cookie 捕获事件（应用内登录成功后自动刷新）
  if (window.electronAPI.music && window.electronAPI.music.onCookieCaptured) {
    window.electronAPI.music.onCookieCaptured((payload) => {
      if (payload && payload.platform) {
        const platformNames = { qq: 'QQ音乐', netease: '网易云音乐', kugou: '酷狗音乐' };
        toast('✅ ' + (platformNames[payload.platform] || payload.platform) + ' 登录成功，Cookie 已自动保存', true);
        if (currentRoute() === '#/song-request') renderSongRequestPage();
      }
    });
  }
}

// 根据 B站浏览器是否可见来切换 body class
// 是否显示 B站内置浏览器：优先用主进程给的 visible（与原生视图是否挂载一致），
// 老版本没有 visible 字段时退回按 url 判断
function biliBrowserVisible(st) {
  if (!st) return false;
  if (st.visible !== undefined) return !!st.visible;
  return !!st.url;
}

function updateBodyClassForBrowser(browserVisible) {
  if (browserVisible) {
    document.body.classList.remove('electron-mode-no-browser');
    document.body.classList.add('electron-mode');
    // v1.1.9: first time per session, explain why the panel narrows (B站 page shown on the right)
    try {
      if (!sessionStorage.getItem('blt_browser_tip')) {
        sessionStorage.setItem('blt_browser_tip', '1');
        toast('💡 B站页面已显示在右侧，主面板暂时收窄；点「🖥 隐藏 B站浏览器」可恢复全宽', true, 6000);
      }
    } catch (e) {}
  } else {
    document.body.classList.remove('electron-mode');
    document.body.classList.add('electron-mode-no-browser');
  }
}

// ---------- B站内置浏览器（Electron 模式）----------
function renderBiliLogin() {
  if (!window.electronAPI) { location.hash = '#/settings'; return; }
  $('#topTitle').textContent = 'B站登录';
  $('#topSub').textContent = '扫码登录后自动同步 Cookie 到服务端';
  $('#topActions').innerHTML = '<button class="btn sm primary" onclick="biliOpenBrowser()">🔐 重新打开B站浏览器</button>' +
    '<button class="btn sm success" onclick="biliDetectLogin()">✅ 已完成登录，检测并同步</button>' +
    '<button class="btn sm" onclick="biliRefresh()">↻ 刷新</button>' +
    '<button class="btn sm" onclick="biliBack()">←</button>' +
    '<button class="btn sm" onclick="biliForward()">→</button>';
  $('#content').innerHTML =
    '<div class="card"><div class="card-title">B站登录</div>' +
    '<p class="muted" style="margin-top:0;">右侧的浏览器会加载 B 站登录页，请用 B站 APP 扫描二维码完成登录。登录成功后 Cookie 会自动同步到服务端配置。</p>' +
    '<div id="biliStatus" class="row" style="margin:10px 0;align-items:center;">检测中...</div>' +
    '<div class="row" style="gap:8px;flex-wrap:wrap;">' +
    '<button class="btn sm primary" onclick="biliDetectLogin()">✅ 已完成登录，检测并同步</button>' +
    '<button class="btn sm" onclick="biliShowCookie()">👁 查看当前 Cookie</button>' +
    '<button class="btn sm danger" onclick="biliClearCookies()">🗑 清除登录态</button>' +
    '</div>' +
    '<pre id="biliCookie" class="mono" style="background:rgba(0,0,0,.3);padding:8px;border-radius:4px;max-height:180px;overflow:auto;font-size:11px;margin:8px 0 0 0;white-space:pre-wrap;word-break:break-all;display:none;"></pre>' +
    '</div>';
  biliUpdateStatus();
}

function renderBiliLive() {
  if (!window.electronAPI) { location.hash = '#/settings'; return; }
  $('#topTitle').textContent = '直播间';
  $('#topSub').textContent = '查看 B 站直播画面，监控房间状态';
  $('#topActions').innerHTML = '<button class="btn sm primary" onclick="biliNavLive()">📺 打开直播间</button>' +
    '<button class="btn sm" onclick="biliNavLogin()">🔐 切到登录页</button>' +
    '<button class="btn sm" onclick="biliRefresh()">↻ 刷新</button>' +
    '<button class="btn sm" onclick="biliBack()">←</button>' +
    '<button class="btn sm" onclick="biliForward()">→</button>';
  $('#content').innerHTML =
    '<div class="card"><div class="card-title">直播间</div>' +
    '<p class="muted" style="margin-top:0;">右侧浏览器会加载 B 站直播间页面（<code>live.bilibili.com/{roomId}</code>）。</p>' +
    '<div class="row" style="gap:8px;flex-wrap:wrap;align-items:center;">' +
    '<label style="display:flex;align-items:center;gap:6px;"><span>房间号：</span><input id="biliRoomInput" class="mono" style="width:160px;" placeholder="如 30576429"></label>' +
    '<button class="btn sm primary" onclick="biliOpenRoomFromInput()">打开</button>' +
    '<button class="btn sm" onclick="biliOpenCurrentRoom()">用配置房间</button>' +
    '</div>' +
    '<div id="biliStatus" class="row" style="margin:10px 0;align-items:center;">检测中...</div>' +
    '</div>';
  biliUpdateStatus();
}

async function biliUpdateStatus() {
  if (!window.electronAPI) return;
  const el = document.getElementById('biliStatus');
  if (!el) return;
  let sessInfo = '';
  try {
    const cfg = await g('/api/config');
    if (cfg.cookie && /SESSDATA=([^;]+)/.test(cfg.cookie)) {
      const sess = cfg.cookie.match(/SESSDATA=([^;]+)/)[1];
      const uid = (cfg.cookie.match(/DedeUserID=(\d+)/) || [])[1];
      sessInfo = '<span class="tag" style="background:var(--accent);color:#000;padding:2px 8px;border-radius:3px;font-size:11px;">✅ 已登录</span> SESSDATA=***' + sess.slice(-4) + (uid ? ' (uid ' + uid + ')' : '');
    } else {
      sessInfo = '<span class="tag" style="background:#888;color:#fff;padding:2px 8px;border-radius:3px;font-size:11px;">未登录</span>';
    }
  } catch (e) {
    sessInfo = '<span class="tag" style="background:#888;color:#fff;padding:2px 8px;border-radius:3px;font-size:11px;">检测中...</span>';
  }
  let url = '';
  try { const st = await window.electronAPI.bili.state(); url = st.url || ''; } catch(e) {}
  el.innerHTML = sessInfo + ' <span class="muted">| 当前页面：</span><code class="mono" style="font-size:11px;">' + esc(url || '-') + '</code>';
}

async function biliNavLogin() { await window.electronAPI.bili.showLogin(); updateBodyClassForBrowser(true); biliUpdateStatus(); }
async function biliOpenBrowser() {
  // 打开当前 B站页面（不重新 loadURL，避免已登录会话被刷新触发重新登录）
  await window.electronAPI.bili.openCurrent();
  updateBodyClassForBrowser(true);  // 立刻折叠管理面板，避免按钮被遮
  biliUpdateStatus();
  biliUpdateStatusInline();
  toast('已打开 B站浏览器');
}

async function biliNavLive() {
  const roomId = (document.getElementById('biliRoomInput') || {}).value || (state.config && state.config.roomId) || '';
  await window.electronAPI.bili.showLive(roomId || undefined);
  updateBodyClassForBrowser(true);
  biliUpdateStatus();
}
async function biliRefresh() { await window.electronAPI.bili.refresh(); }
async function biliBack() { await window.electronAPI.bili.back(); }
async function biliForward() { await window.electronAPI.bili.forward(); }
async function biliOpenRoomFromInput() {
  const v = (document.getElementById('biliRoomInput') || {}).value || '';
  if (!v) { toast('请输入房间号', false); return; }
  await window.electronAPI.bili.showLive(v);
  updateBodyClassForBrowser(true);
  biliUpdateStatus();
}
async function biliOpenCurrentRoom() {
  const cfg = await g('/api/config');
  await window.electronAPI.bili.showLive(cfg.roomId || undefined);
  updateBodyClassForBrowser(true);
  biliUpdateStatus();
}
// 在房间管理页直接更新 B站状态
async function biliUpdateStatusInline() {
  if (!window.electronAPI) return;
  const el = document.getElementById('biliStatusInline');
  if (!el) return;
  // 启动轮询（每 3 秒刷新，直到元素消失）
  if (!biliUpdateStatusInline._timer) {
    biliUpdateStatusInline._timer = setInterval(() => {
      const e = document.getElementById('biliStatusInline');
      if (!e) { clearInterval(biliUpdateStatusInline._timer); biliUpdateStatusInline._timer = null; return; }
      biliUpdateStatusInlineCore();
    }, 3000);
  }
  const final = await biliUpdateStatusInlineCore();
  el.innerHTML = final;
}

async function biliUpdateStatusInlineCore() {
  let sessInfo = '';
  try {
    const cfg = await g('/api/config');
    if (cfg.cookie && /SESSDATA=([^;]+)/.test(cfg.cookie)) {
      const sess = cfg.cookie.match(/SESSDATA=([^;]+)/)[1];
      const uid = (cfg.cookie.match(/DedeUserID=(\d+)/) || [])[1];
      sessInfo = '✅ 已登录 ' + (uid ? '(uid ' + uid + ') ' : '') + 'SESSDATA=***' + sess.slice(-4);
    } else {
      sessInfo = '⚠ 未登录：请打开 B站浏览器扫码登录';
    }
  } catch (e) { sessInfo = '检测中…'; }
  let url = '';
  try { const st = await window.electronAPI.bili.state(); url = st.url || ''; } catch (e) {}
  return '<div>' + sessInfo + '</div>' + (url ? '<div class="muted" style="font-size:11px;margin-top:3px;">当前页面：' + esc(url) + '</div>' : '');
}

// 隐藏 B站浏览器
async function biliHideBrowser() {
  if (!window.electronAPI) return;
  await window.electronAPI.bili.setVisible(false);
  updateBodyClassForBrowser(false);  // 立刻展开管理面板
  toast('B站浏览器已隐藏');
  biliUpdateStatusInline();
}

// 手动检测登录：抓取 cookie → 上报 → 自动关闭浏览器
async function biliDetectLogin() {
  if (!window.electronAPI) { toast('需 Electron 模式', false); return; }
  const btn = document.getElementById('btn-bili-detect');
  btnLoading(btn, '检测同步中…');
  try {
    const r = await window.electronAPI.bili.captureCookie();
    if (r && r.cookie && /SESSDATA=/.test(r.cookie)) {
      await post('/api/cookie', { cookie: r.cookie });
      // 关闭浏览器
      await window.electronAPI.bili.setVisible(false);
      toast('✅ 登录成功，Cookie 已同步，已关闭 B站页面');
      biliUpdateStatus();
    } else {
      toast('⚠ 未检测到登录 Cookie，请先在 B站页面完成扫码登录', false);
    }
  } finally { btnDone(btn); }
}
async function biliForceCapture() {
  const r = await window.electronAPI.bili.captureCookie();
  if (r && r.cookie) {
    await post('/api/cookie', { cookie: r.cookie });
    toast('已抓取并同步 Cookie');
    biliUpdateStatus();
  } else {
    toast('当前未检测到 B 站 Cookie（未登录）', false);
  }
}
async function biliShowCookie() {
  const r = await window.electronAPI.bili.captureCookie();
  const pre = document.getElementById('biliCookie');
  if (!pre) return;
  if (r && r.cookie) {
    pre.textContent = r.cookie.replace(/SESSDATA=[^;]+/, 'SESSDATA=***').replace(/bili_jct=[^;]+/, 'bili_jct=***').replace(/DedeUserID=(\d+)/, 'DedeUserID=***');
    pre.style.display = 'block';
  } else {
    pre.textContent = '(当前未检测到 B 站 Cookie)';
    pre.style.display = 'block';
  }
}
async function biliClearCookies() {
  if (!confirm('确定要清除已保存的登录 Cookie 吗？')) return;
  await window.electronAPI.bili.clearCookies();
  try {
    const cfg = await g('/api/config');
    await post('/api/connect', { roomId: cfg.roomId, cookie: '' });
  } catch (e) {}
  toast('已清除登录态');
  biliUpdateStatus();
}

// ---- 连接状态轮询跟踪 ----
let _connectPollTimer = null;
let _connectPollDeadline = 0;
let _connectPollReqId = 0;

function stopConnectPoll() {
  if (_connectPollTimer) { clearInterval(_connectPollTimer); _connectPollTimer = null; }
}
function isConnectPolling() { return !!_connectPollTimer; }

// ---------- 语音念弹幕（TTS）----------
function getTtsVoices() {
  if (window.BLTTTS && window.BLTTTS.getVoices) { try { return window.BLTTTS.getVoices(); } catch(e){} }
  return [];
}
// 音色按 locale 分组（Edge 动态清单与内置回退列表通用）
function voiceGroup(locale) {
  const lc = String(locale || '');
  if (lc.indexOf('zh-CN') === 0) return '普通话';
  if (lc.indexOf('zh-HK') === 0 || lc.indexOf('zh-Hant-HK') === 0) return '粤语';
  if (lc.indexOf('zh-TW') === 0) return '台湾国语';
  if (lc.indexOf('en-') === 0) return '英语';
  if (lc.indexOf('ja-') === 0) return '日语';
  if (lc.indexOf('ko-') === 0) return '韩语';
  return lc ? '其它语言' : '在线音色(Edge)';
}
// ---- v1.1.10 语音引擎（edge 在线 / moss 本地 MOSS-TTS-Nano / sys 系统） ----
const TTS_ENGINE_META = [
  { id: 'edge', name: '在线语音 Edge', hint: '音质自然 · 12+ 中文音色 · 需联网' },
  { id: 'moss', name: '本地语音 MOSS', hint: '6 个中文音色 + 音色克隆 · 完全离线 · 模型按需下载（728MB，一次性）' },
  { id: 'sys',  name: '系统语音',      hint: '系统自带兜底 · 无需联网 · 音质一般' }
];
function currentTtsEngine() {
  const e = ((state.config && state.config.tts && state.config.tts.engine) || 'edge');
  if (e === 'kokoro') return 'moss';
  return (e === 'moss' || e === 'sys') ? e : 'edge';
}
function getMossVoicesSafe() {
  if (window.BLTTTS && window.BLTTTS.getMossVoices) { try { return window.BLTTTS.getMossVoices(); } catch (e) {} }
  return [];
}
function getMossModelStatusSafe() {
  if (window.BLTTTS && window.BLTTTS.getMossModelStatus) { try { return window.BLTTTS.getMossModelStatus(); } catch (e) {} }
  return null;
}
// MOSS 音色下拉：收藏置顶 → 内置音色 / 自定义（克隆）分组；kw 过滤
function buildMossVoiceOptions(t, kw, curSel) {
  const favs = (t && Array.isArray(t.favoriteVoices)) ? t.favoriteVoices : [];
  const k = String(kw || '').trim().toLowerCase();
  const all = getMossVoicesSafe();
  const match = (v) => !k || String(v.id || '').toLowerCase().indexOf(k) >= 0 || String(v.name || '').toLowerCase().indexOf(k) >= 0;
  const opt = (v) => '<option value="' + esc(v.id) + '"' + (v.id === curSel ? ' selected' : '') + '>' + esc(v.name) + '</option>';
  const hit = all.filter(match);
  let out = '';
  const favItems = hit.filter(v => favs.indexOf(v.id) >= 0);
  if (favs.length && favItems.length) out += '<optgroup label="⭐ 常用音色">' + favItems.map(opt).join('') + '</optgroup>';
  const builtin = hit.filter(v => !v.custom);
  const custom = hit.filter(v => v.custom);
  if (builtin.length) out += '<optgroup label="内置音色">' + builtin.map(opt).join('') + '</optgroup>';
  if (custom.length) out += '<optgroup label="自定义音色（克隆）">' + custom.map(opt).join('') + '</optgroup>';
  if (!out) out = '<option value="' + esc(curSel || 'Junhao') + '">' + esc(curSel || '加载中…（本地服务启动中）') + '</option>';
  return out;
}
// MOSS 模型状态行 + 下载 UI（按需下载 728MB，支持断点续传）
function renderMossModelRow() {
  const el = document.getElementById('moss-model-row');
  if (!el) return;
  const st = getMossModelStatusSafe();
  if (!st) { el.innerHTML = '<span class="muted" style="font-size:12px;">本地语音服务启动中…（首次约几秒）</span>'; return; }
  if (st.error && !st.downloading) {
    el.innerHTML = '<div style="color:var(--danger-bright);font-size:12px;">模型下载失败：' + esc(st.error) + '</div>' +
      '<button class="btn sm primary" id="moss-dl-btn" style="margin-top:6px;">重试下载</button>';
  } else if (st.downloading) {
    const pct = Math.max(0, Math.min(100, Math.round((st.doneBytes || 0) * 100 / Math.max(1, st.totalBytes || 1))));
    el.innerHTML = '<div style="font-size:12px;">正在下载模型 <b>' + pct + '%</b> · ' + esc(st.currentFile || '') +
      '（' + Math.round((st.doneBytes || 0) / 1048576) + ' / ' + Math.round((st.totalBytes || 0) / 1048576) + ' MB）</div>' +
      '<div class="progress sm" style="margin-top:6px;max-width:420px;"><div class="progress-bar" style="width:' + pct + '%;"></div></div>' +
      '<div class="muted" style="font-size:11px;margin-top:4px;">下载期间可继续使用在线引擎；中断后点「继续下载」可断点续传。</div>';
  } else if (st.ready) {
    el.innerHTML = '<span style="color:var(--success-bright);font-size:12px;">✓ 模型已就绪 · ' + getMossVoicesSafe().length + ' 个音色可用（完全离线）</span>';
  } else {
    el.innerHTML = '<div style="font-size:12px;">⚠ 本地模型未下载（约 728MB，一次性，之后完全离线）</div>' +
      '<button class="btn sm primary" id="moss-dl-btn" style="margin-top:6px;" data-tip="从国内镜像下载模型（支持断点续传），完成后本地引擎即可离线使用">⬇ 下载模型</button>';
  }
  const b = document.getElementById('moss-dl-btn');
  if (b) b.addEventListener('click', function () {
    if (window.BLTTTS) window.BLTTTS.startMossModelDownload();
    toast('已开始下载模型（约 728MB），可继续使用在线引擎', true, 4000);
    setTimeout(renderMossModelRow, 800);
  });
}
// 刷新本地服务开关状态（主进程进程状态）
function refreshMossServiceUi() {
  const dot = document.getElementById('moss-svc-dot');
  const txt = document.getElementById('moss-svc-text');
  if (!dot || !txt) return;
  if (!window.BLTTTS || !window.BLTTTS.mossServiceStatus) { txt.textContent = '（当前环境不支持进程控制）'; return; }
  window.BLTTTS.mossServiceStatus(function (st) {
    if (st && st.unsupported) { dot.className = 'dot idle'; txt.textContent = '（浏览器模式不支持进程控制，请在桌面客户端里管理）'; return; }
    const running = !!(st && st.running);
    dot.className = 'dot ' + (running ? 'ok' : 'idle');
    txt.textContent = running ? '服务运行中（首次合成后常驻约 1.6GB 内存）' : '服务已停止';
  });
}
function updateMossStatusLine() {
  const st = document.getElementById('tts-engine-status');
  if (!st) return;
  const eng = currentTtsEngine();
  if (eng === 'edge') { st.textContent = '在线引擎（需联网）'; return; }
  if (eng === 'sys') { st.textContent = '系统语音（兜底）'; return; }
  const ms = getMossModelStatusSafe();
  if (!ms) { st.textContent = '本地语音服务启动中…'; return; }
  if (ms.downloading) st.textContent = '正在下载模型 ' + Math.round((ms.doneBytes || 0) * 100 / Math.max(1, ms.totalBytes || 1)) + '%…';
  else if (ms.ready) st.textContent = '本地引擎已就绪 · ' + getMossVoicesSafe().length + ' 个音色';
  else st.textContent = '模型未下载（728MB）';
  renderMossModelRow();
}
// 构建音色下拉 options：收藏置顶 → 按语言分组 → 系统音色；kw 非空时按名称/标签过滤
function buildVoiceOptions(t, voices, kw, curSel) {
  const favs = (t && Array.isArray(t.favoriteVoices)) ? t.favoriteVoices : [];
  const k = String(kw || '').trim().toLowerCase();
  const match = (v) => !k || String(v.name || '').toLowerCase().indexOf(k) >= 0 || String(v.label || '').toLowerCase().indexOf(k) >= 0;
  const opt = (v) => '<option value="' + esc(v.name) + '"' + (v.name === curSel ? ' selected' : '') + '>' + esc(v.label || v.name) + '</option>';
  const edge = voices.filter(v => v.edge);
  const sys = voices.filter(v => v.sys && match(v));
  let out = '';
  const favItems = edge.filter(v => favs.indexOf(v.name) >= 0 && match(v));
  if (favs.length && favItems.length) out += '<optgroup label="⭐ 常用音色">' + favItems.map(opt).join('') + '</optgroup>';
  const groups = {};
  for (const v of edge) { if (!match(v)) continue; const g = voiceGroup(v.locale); (groups[g] = groups[g] || []).push(v); }
  const order = ['普通话', '粤语', '台湾国语', '英语', '日语', '韩语'];
  for (const g of order) if (groups[g]) out += '<optgroup label="' + g + '(Edge)">' + groups[g].map(opt).join('') + '</optgroup>';
  for (const g of Object.keys(groups)) if (order.indexOf(g) < 0) out += '<optgroup label="' + esc(g) + '(Edge)">' + groups[g].map(opt).join('') + '</optgroup>';
  if (sys.length) out += '<optgroup label="系统音色">' + sys.map(opt).join('') + '</optgroup>';
  if (!out) out = '<option value="' + esc(curSel || '') + '">' + esc(curSel || '默认') + '</option>';
  return out;
}
// 重建音色下拉（保留当前选中值 + 更新收藏按钮状态）；按当前引擎切换音色清单
function rebuildVoiceSelect() {
  const sel = $('#tts-voice');
  if (!sel) return;
  const cur = sel.value;
  const kw = ($('#tts-voice-search') || {}).value || '';
  const t = (state.config && state.config.tts) || {};
  if (currentTtsEngine() === 'moss') {
    sel.innerHTML = buildMossVoiceOptions(t, kw, cur || t.mossVoice);
  } else {
    sel.innerHTML = buildVoiceOptions(t, getTtsVoices(), kw, cur);
  }
  let found = false;
  for (const o of sel.options) { if (o.value === cur) { found = true; break; } }
  if (found) sel.value = cur;
  const fav = $('#tts-voice-fav');
  if (fav) fav.textContent = ((t.favoriteVoices || []).indexOf(sel.value) >= 0) ? '★ 已收藏' : '☆ 收藏';
}
// 音色清单签名（Edge/MOSS 动态清单到达后触发一次重建）
let _voiceSig = '';
function refreshVoiceSelectIfChanged() {
  const sel = $('#tts-voice');
  if (!sel) return;
  const eng = currentTtsEngine();
  const vs = eng === 'moss' ? getMossVoicesSafe() : getTtsVoices();
  const sig = eng + '|' + vs.length + '|' + vs.map(v => v.id || v.name).join(',');
  // 引擎状态行实时更新（本地引擎/模型状态）
  updateMossStatusLine();
  if (sig === _voiceSig) return;
  _voiceSig = sig;
  rebuildVoiceSelect();
}
// 语气下拉 options：跟随全局 + 各预设
function buildToneOptions(t, selected) {
  const presets = (t && t.tonePresets) || {};
  let out = '<option value=""' + (!selected ? ' selected' : '') + '>跟随全局</option>';
  for (const k of Object.keys(presets)) {
    const p = presets[k] || {};
    out += '<option value="' + esc(k) + '"' + (selected === k ? ' selected' : '') + '>' + esc(p.label || k) + '</option>';
  }
  return out;
}
function ttsTypeRow(title, key, tc, c) {
  const on = tc && tc.enabled !== false;
  const sayUid = tc && tc.sayUid;
  const minMedal = tc ? (tc.minMedal || 0) : 0;
  const minHonor = tc ? (tc.minHonor || 0) : 0;
  const vol = tc ? (tc.volume != null ? tc.volume : 1) : 1;
  const toneVal = (c.tts && c.tts.tone && c.tts.tone.byType && c.tts.tone.byType[key] != null) ? c.tts.tone.byType[key] : '';
  return '<div class="switch-row">' +
    '<span class="sr-title">' + title + '</span>' +
    '<label class="switch sm" data-tip="开启后该类型事件将被朗读。关闭则跳过"><input type="checkbox" data-tts-toggle="' + key + '"' + (on ? ' checked' : '') + '><span class="track"></span></label>' +
    '<label class="muted" style="display:flex;align-items:center;gap:4px;font-size:12px;cursor:pointer;" data-tip="朗读时先念用户昵称再念内容。UID永远不会被念出"><input type="checkbox" data-tts="' + key + '.sayUid"' + (sayUid ? ' checked' : '') + '>念昵称</label>' +
    '<label class="muted" style="display:flex;align-items:center;gap:4px;font-size:12px;" data-tip="粉丝灯牌等级不低于该值才朗读，0=不限制。过滤白嫖观众">灯牌≥<input type="number" data-tts="' + key + '.minMedal" value="' + minMedal + '" style="width:52px;" min="0"></label>' +
    '<label class="muted" style="display:flex;align-items:center;gap:4px;font-size:12px;" data-tip="荣耀等级（大航海等级）不低于该值才朗读，0=不限制">荣耀≥<input type="number" data-tts="' + key + '.minHonor" value="' + minHonor + '" style="width:52px;" min="0"></label>' +
    '<label class="muted" style="display:flex;align-items:center;gap:4px;font-size:12px;" data-tip="该类型独立音量，与全局音量相乘得到实际朗读音量">音量<input type="range" data-tts="' + key + '.volume" value="' + vol + '" min="0" max="1" step="0.05" style="width:80px;"></label>' +
    '<label class="muted" style="display:flex;align-items:center;gap:4px;font-size:12px;" data-tip="该事件类型专属朗读语气，默认跟随全局语气">语气<select data-tts-tone="' + key + '" style="width:auto;font-size:12px;padding:2px 4px;">' + buildToneOptions(c.tts, toneVal) + '</select></label>' +
    '</div>';
}

async function renderTtsPage() {
  if (!state.config) state.config = await g('/api/config');
  const c = state.config;
  const t = c.tts || {};
  const voices = getTtsVoices();
  const eng = currentTtsEngine();
  // 记录音色签名：动态清单（/voices）到达后靠 tts:status 事件触发刷新（按当前引擎取清单）
  const sigList = eng === 'moss' ? getMossVoicesSafe() : voices;
  _voiceSig = eng + '|' + sigList.length + '|' + sigList.map(v => v.id || v.name).join(',');
  const voiceOpts = eng === 'moss' ? buildMossVoiceOptions(t, '', t.mossVoice) : buildVoiceOptions(t, voices, '', t.voice);
  // 语气预设下拉（含自定义项）；本地引擎时标注绑定的情感音色
  const tonePresets = t.tonePresets || {};
  let toneOpts = '';
  const curTone = (t.tone && t.tone.global) || 'normal';
  for (const k of Object.keys(tonePresets)) {
    const p = tonePresets[k] || {};
    const voiceTag = (eng === 'moss' && p.voice) ? (' · ' + p.voice) : '';
    toneOpts += '<option value="' + esc(k) + '"' + (curTone === k ? ' selected' : '') + '>' + esc((p.label || k) + voiceTag) + '</option>';
  }
  if (!tonePresets.custom) toneOpts += '<option value="custom"' + (curTone === 'custom' ? ' selected' : '') + '>自定义</option>';
  const customP = tonePresets.custom || { rate: 1, pitch: 0, voice: '', prefix: '' };
  // 自定义语气的音色下拉（本地引擎时可选情感音色）
  const customVoiceOpts = '<option value="">跟随全局音色</option>' +
    getMossVoicesSafe().map(v => '<option value="' + esc(v.id) + '"' + (customP.voice === v.id ? ' selected' : '') + '>' + esc(v.name) + '</option>').join('');

  $('#topTitle').textContent = '语音念弹幕';
  $('#topSub').textContent = '用浏览器语音朗读弹幕/礼物等事件（Web Speech API）';
  $('#topActions').innerHTML =
    '<button class="btn sm" onclick="ttsPreview()" data-tip="用当前设置朗读一条测试语音">🔊 试听</button>' +
    '<button class="btn sm" onclick="ttsSkip()" data-tip="跳过当前正在朗读的语音，立即念下一条">⏭ 跳过当前</button>' +
    '<button class="btn sm" onclick="ttsRestart()" data-tip="清空待朗读队列并重启语音模块">🔄 重启语音模块</button>';

  let html = '';
  html += '<section class="content-header"><h1>语音念弹幕 <small>Web Speech API 朗读</small></h1>' +
    '<ol class="breadcrumb"><li><a href="#/settings">首页</a></li><li class="active">语音念弹幕</li></ol></section>';

  html += connReadyBanner();

  // 语音引擎选择卡（v1.1.10）
  html += '<div class="box box-primary">' +
    '<div class="box-header with-border"><h3 class="box-title">🔈 语音引擎 <span class="title-hint">三种引擎按需切换，失败自动降级</span></h3>' + boxToolBtn('tts-engine') + '</div>' +
    '<div class="box-body"><div class="form-grid">' +
    TTS_ENGINE_META.map(function (m) {
      return '<label class="field" style="cursor:pointer;display:flex;gap:8px;align-items:flex-start;margin:0;">' +
        '<input type="radio" name="tts-engine" value="' + m.id + '"' + (eng === m.id ? ' checked' : '') + ' style="margin-top:3px;">' +
        '<span><b style="color:var(--text-strong);">' + m.name + '</b><div class="hint" style="margin-top:2px;">' + m.hint + '</div></span></label>';
    }).join('') +
    '</div>' +
    '<div class="row" style="margin-top:10px;gap:8px;align-items:center;">' +
    '<span id="tts-engine-status" class="muted" style="font-size:12px;">' + (eng === 'moss' ? '本地引擎状态检测中…' : (eng === 'edge' ? '在线引擎（需联网）' : '系统语音（兜底）')) + '</span>' +
    '<button class="btn sm" id="tts-engine-try" data-tip="用当前引擎和音色朗读一条试听">🔊 试听引擎</button>' +
    '</div>' +
    (eng === 'moss' ? '<div id="moss-model-row" style="margin-top:10px;"></div>' +
      '<div class="row" id="moss-service-row" style="margin-top:10px;gap:8px;align-items:center;flex-wrap:wrap;border-top:1px solid var(--line);padding-top:10px;">' +
      '<span class="dot idle" id="moss-svc-dot"></span>' +
      '<span id="moss-svc-text" class="muted" style="font-size:12px;">服务状态检测中…</span>' +
      '<button class="btn sm" id="moss-svc-start" data-tip="启动本地语音服务：进程常驻约 30MB，首次合成时加载模型（约 7 秒），之后常驻约 1.6GB 内存">▶ 启动服务</button>' +
      '<button class="btn sm" id="moss-svc-stop" data-tip="停止本地语音服务，立即释放内存；模型文件保留，可随时再启动">⏹ 停止服务</button>' +
      '<button class="btn sm" id="moss-svc-restart" data-tip="重启本地语音服务（卡死或异常时使用）">↻ 重启服务</button>' +
      '<span class="muted" style="font-size:11px;">不用时可停止释放内存；切到其他引擎会自动停止</span>' +
      '</div>' : '') +
    '</div></div>';

  html += '<div class="box box-primary">' +
    '<div class="box-header with-border"><h3 class="box-title">总开关 &amp; 语音</h3>' +
    '<div class="box-tools"><button class="btn-box-tool" onclick="var b=this.closest(\'.box\');b.classList.toggle(\'collapsed\');this.textContent=b.classList.contains(\'collapsed\')?\'+\':\'−\'">−</button></div></div>' +
    '<div class="box-body"><div class="form-grid">' +
    '<div class="field"><label>启用语音念弹幕</label>' + switchHtml('tts-enabled-cb', t.enabled !== false, '', '总开关。关闭后所有类型的弹幕/礼物/留言都不再朗读') + '<div class="hint">总开关，关闭后所有事件都不再朗读</div></div>' +
    '<div class="field"><label>音色</label>' +
    '<div style="display:flex;gap:6px;align-items:center;flex-wrap:wrap;">' +
    '<input id="tts-voice-search" class="form-control" placeholder="搜索音色..." style="flex:1;min-width:130px;" data-tip="输入关键词过滤音色（支持中文名/英文名/地区）">' +
    '<select id="tts-voice" style="flex:2;min-width:170px;" data-tip="选择朗读使用的语音引擎。在线音色(Edge)效果更好但需联网；系统音色离线可用">' + voiceOpts + '</select>' +
    '<button class="btn sm" id="tts-voice-fav" data-tip="收藏/取消收藏当前选中音色，收藏后置顶到「常用音色」分组">☆ 收藏</button>' +
    '<button class="btn sm" id="tts-voice-try" data-tip="用当前选中的音色朗读一条试听例句">🔊</button>' +
    '</div>' +
    '<div class="hint">' + (eng === 'moss'
      ? 'MOSS 内置 <b>6 个中文音色</b>（含京味/说书/机车/深夜电台风格）+ <b>自定义音色（音色克隆，3-10 秒参考音频）</b>，完全离线；⭐收藏的音色置顶显示'
      : '在线音色(Edge)首次打开时从语音服务动态加载全量音色（普通话/粤语/台湾/英语/日韩等 400+），加载失败自动回退内置列表；⭐收藏的音色置顶显示') + '</div>' +
    (eng === 'moss' ? '<div class="row" style="margin-top:6px;gap:6px;flex-wrap:wrap;"><input type="file" id="moss-voice-file" accept="audio/*" style="display:none;"><button class="btn sm" id="moss-voice-add" data-tip="选择一段 3-10 秒的清晰人声，克隆为自定义音色（本地处理，不上传）">＋ 添加自定义音色（克隆）</button><button class="btn sm" id="moss-voice-del" data-tip="删除当前选中的自定义音色">🗑 删除自定义音色</button></div>' : '') +
    '</div>' +
    '<div class="field"><label>语速</label><input id="tts-rate" type="range" min="0.5" max="2" step="0.1" value="' + (t.rate||1) + '" data-tip="朗读语速倍率，1.0为正常速度，0.5最慢，2.0最快"><span id="tts-rate-v" class="muted">' + (t.rate||1) + '</span><div class="hint">0.5-2，1 为正常语速；语气非「正常」时在此基础上叠加</div></div>' +
    '<div class="field"><label>音调</label><input id="tts-pitch" type="range" min="0.5" max="2" step="0.1" value="' + (t.pitch||1) + '" data-tip="朗读音调倍率，1.0为正常音调"><span id="tts-pitch-v" class="muted">' + (t.pitch||1) + '</span><div class="hint">0.5-2，1 为正常音调；语气非「正常」时在此基础上叠加</div></div>' +
    '<div class="field"><label>全局音量</label><input id="tts-volume" type="range" min="0" max="1" step="0.05" value="' + (t.volume||1) + '" data-tip="所有类型共用的基础音量，与各类型独立音量相乘得到最终音量"><span id="tts-volume-v" class="muted">' + (t.volume||1) + '</span><div class="hint">0-1，与各类型独立音量相乘</div></div>' +
    '<div class="field"><label>增益 %</label><input id="tts-gain" type="number" min="0" max="50" value="' + (t.gain||0) + '" data-tip="额外放大音量的百分比，0%为不增益。过大可能爆音"><div class="hint">在全局音量基础上额外加成（0-50）</div></div>' +
    '<div class="field"><label>队列上限</label><input id="tts-queue" type="number" min="1" max="50" value="' + (t.queueMax||8) + '" data-tip="待朗读队列最多缓存条数，超出时丢弃最早的消息。防止积压"><div class="hint">待念条数达到上限时忽略新事件，避免积压</div></div>' +
    '<div class="field"><label>语音间隔（毫秒）</label><input id="tts-gap" type="number" min="0" max="5000" step="100" value="' + (t.gap||0) + '" data-tip="两条语音之间的最小间隔毫秒数，避免连续朗读太紧凑"><div class="hint">两条语音之间的等待间隔；0=念完马上接下一条；最多5000ms</div></div>' +
    '</div></div></div>';

  // 语气卡片：语气 = 情感音色 + 语速 + 前缀词（本地引擎）/ 语速+音调（在线引擎），可按事件绑定
  html += '<div class="box">' +
    '<div class="box-header with-border"><h3 class="box-title">🎭 语气 <span class="title-hint">' + (eng === 'moss' ? '情感音色 + 语速 + 前缀词' : '语速 + 音调的预设组合') + '，可按事件绑定</span></h3>' +
    '<div class="box-tools"><button class="btn-box-tool" onclick="var b=this.closest(\'.box\');b.classList.toggle(\'collapsed\');this.textContent=b.classList.contains(\'collapsed\')?\'+\':\'−\'">−</button></div></div>' +
    '<div class="box-body"><div class="form-grid">' +
    '<div class="field"><label>全局语气</label>' +
    '<div style="display:flex;gap:6px;align-items:center;flex-wrap:wrap;">' +
    '<select id="tts-tone-global" style="flex:1;min-width:140px;" data-tip="全局朗读语气。各事件类型可在「念哪些事件」里单独绑定语气，未绑定则跟随这里">' + toneOpts + '</select>' +
    '<button class="btn sm" id="tts-tone-try" data-tip="用当前选中的语气朗读固定例句「感谢关注，礼物多多」">🔊 试听语气</button>' +
    '</div>' +
    '<div id="tts-tone-custom-row" style="display:' + (curTone === 'custom' ? '' : 'none') + ';margin-top:6px;" class="muted">' +
    '语速 <input id="tts-tone-custom-rate" type="number" min="0.5" max="2" step="0.05" value="' + (customP.rate != null ? customP.rate : 1) + '" style="width:70px;"> 倍 · ' +
    '音调 <input id="tts-tone-custom-pitch" type="number" min="-50" max="50" step="1" value="' + (customP.pitch != null ? customP.pitch : 0) + '" style="width:70px;"> Hz · ' +
    '音色 <select id="tts-tone-custom-voice" style="width:auto;font-size:12px;padding:2px 4px;" data-tip="本地引擎时可为此语气绑定专属情感音色（如兴奋用偏高亮的音色）">' + customVoiceOpts + '</select> · ' +
    '前缀词 <input id="tts-tone-custom-prefix" value="' + esc(customP.prefix || '') + '" placeholder="如 哇！" style="width:80px;" data-tip="朗读前自动加上的词，增强情绪（如「哇！」「哈哈」）">' +
    '</div>' +
    '<div class="hint">' + (eng === 'moss'
      ? '本地引擎下，语气 = 专属情感音色 + 语速 + 前缀词；兴奋/激动适合 SC 与舰长提醒，温柔适合欢迎新观众'
      : '兴奋/激动适合 SC 与舰长提醒；切到「本地语音 MOSS」后可解锁情感音色与前缀词') + '</div></div>' +
    '</div></div></div>';

  html += '<div class="box">' +
    '<div class="box-header with-border"><h3 class="box-title">念哪些事件 <span class="title-hint">每类可独立开关、过滤与调音量</span></h3>' +
    '<div class="box-tools"><button class="btn-box-tool" onclick="var b=this.closest(\'.box\');b.classList.toggle(\'collapsed\');this.textContent=b.classList.contains(\'collapsed\')?\'+\':\'−\'">−</button></div></div>' +
    '<div class="box-body">' +
    ttsTypeRow('弹幕', 'danmu', t.danmu, c) +
    ttsTypeRow('礼物', 'gift', t.gift, c) +
    ttsTypeRow('醒目留言', 'superchat', t.superchat, c) +
    ttsTypeRow('欢迎词', 'welcome', t.welcome, c) +
    '<div class="form-group" style="margin-top:10px;"><label>欢迎语池</label><div class="hint" style="margin:0 0 6px 0;">每行一条随机用，<code>{uname}</code> = 昵称占位</div><textarea class="form-control" id="tts-welcome-texts" style="width:100%;height:110px;">' + esc(((t.welcome && t.welcome.texts) || []).join('\n')) + '</textarea></div>' +
    '</div></div>';

  html += '<div class="box">' +
    '<div class="box-header with-border"><h3 class="box-title">队列 / 过滤</h3>' +
    '<div class="box-tools"><button class="btn-box-tool" onclick="var b=this.closest(\'.box\');b.classList.toggle(\'collapsed\');this.textContent=b.classList.contains(\'collapsed\')?\'+\':\'−\'">−</button></div></div>' +
    '<div class="box-body"><div class="form-grid">' +
    '<div class="field"><label>舰长弹幕优先</label>' + switchHtml('tts-guardpri-cb', !!t.guardPriority, '', '舰长发的弹幕插队到队列前方优先朗读') + '<div class="hint">舰长的弹幕插队优先朗读</div></div>' +
    '<div class="field"><label>黑名单 UID</label><textarea id="tts-blacklist" placeholder="123456,789012" data-tip="这些UID的用户发的弹幕不朗读，逗号分隔">' + esc(t.blacklistUids || '') + '</textarea><div class="hint">逗号分隔，命中即不念</div></div>' +
    '<div class="field"><label>屏蔽词</label><textarea id="tts-banned" placeholder="广告,主播名字" data-tip="包含这些关键词的弹幕不朗读，逗号分隔。可屏蔽广告、敏感词">' + esc(t.bannedWords || '') + '</textarea><div class="hint">弹幕含任一词则不念，逗号分隔</div></div>' +
    '<div class="field"><label>当前语音队列</label><span id="tts-queue-st" class="muted">0 条待念</span> <button class="btn btn-default sm" onclick="ttsRestart()" data-tip="清空当前待朗读队列并重启语音模块">清空并重启</button><div id="tts-queue-list" style="margin-top:6px;"></div></div>' +
    '</div></div></div>';

  html += '';

  $('#content').innerHTML = html;

  // 绑定
  // 主开关：切换立即生效（内存配置 + TTS 模块 + 事件门控）并持久化
  $('#tts-enabled-cb').addEventListener('change', function(){
    const cfg = state.config; if (!cfg.tts) cfg.tts = {};
    cfg.tts.enabled = this.checked;
    state.ttsEnabled = cfg.tts.enabled;
    if (window.BLTTTS) window.BLTTTS.setConfig(cfg.tts);
    saveTts(true);
  });
  $('#tts-guardpri-cb').addEventListener('change', () => saveTts(true));
  $('#tts-rate').addEventListener('input', () => $('#tts-rate-v').textContent = $('#tts-rate').value);
  $('#tts-pitch').addEventListener('input', () => $('#tts-pitch-v').textContent = $('#tts-pitch').value);
  $('#tts-volume').addEventListener('input', () => $('#tts-volume-v').textContent = $('#tts-volume').value);
  // 所有控件 change → 立即保存并生效（滑杆松手、下拉切换、勾选即应用）；v1.1.9 起无手动保存按钮
  // 所有控件 change → 立即保存并生效（滑杆松手、下拉切换、勾选即应用）
  const ttsLive = (el) => { if (el) el.addEventListener('change', () => saveTts(true)); };
  ['tts-voice','tts-rate','tts-pitch','tts-volume','tts-gain','tts-queue','tts-gap'].forEach(id => ttsLive($('#' + id)));
  $$('#content [data-tts]').forEach(el => { el.addEventListener('change', () => saveTts(true)); });
  $$('#content [data-tts-tone]').forEach(el => { el.addEventListener('change', () => saveTts(true)); });
  // 语音引擎切换（v1.1.10）：立即重绘（不等保存，避免竞态）+ 异步持久化
  $$('#content input[name="tts-engine"]').forEach(function (radio) {
    radio.addEventListener('change', function () {
      if (!this.checked) return;
      const cfg = state.config; if (!cfg.tts) cfg.tts = {};
      cfg.tts.engine = this.value;
      if (this.value === 'moss' && window.BLTTTS) {
        window.BLTTTS.activateMoss();
        toast('本地引擎启动中；若模型未下载，请在下方点击「下载模型」', true, 4000);
      } else if (this.value !== 'moss' && window.BLTTTS) {
        // 切到其他引擎：自动停止本地服务，释放内存
        window.BLTTTS.mossServiceStatus(function (st) {
          if (st && st.running) {
            window.BLTTTS.mossServiceStop(function () { toast('已停止本地语音服务，释放约 1.6GB 内存', true); });
          }
        });
      }
      if (window.BLTTTS) window.BLTTTS.setConfig(cfg.tts);
      renderTtsPage();          // 同步重绘：音色下拉立即切到新引擎的清单
      saveTts(true);            // 异步持久化（序号防旧响应覆盖）
    });
  });
  const engTry = $('#tts-engine-try');
  if (engTry) engTry.addEventListener('click', function () {
    if (window.BLTTTS) window.BLTTTS.preview();
  });
  // MOSS：模型状态轮询 + 服务开关 + 自定义音色（克隆）管理
  if (currentTtsEngine() === 'moss') {
    if (window.BLTTTS) { window.BLTTTS.fetchMossModelStatus(updateMossStatusLine); refreshMossServiceUi(); }
    updateMossStatusLine();
    const svcStart = $('#moss-svc-start');
    if (svcStart) svcStart.addEventListener('click', function () {
      btnLoading(svcStart, '启动中…');
      if (window.BLTTTS) window.BLTTTS.mossServiceStart(function () { btnDone(svcStart); refreshMossServiceUi(); toast('本地语音服务已启动', true); });
      else btnDone(svcStart);
    });
    const svcStop = $('#moss-svc-stop');
    if (svcStop) svcStop.addEventListener('click', function () {
      if (window.BLTTTS) window.BLTTTS.mossServiceStop(function () { refreshMossServiceUi(); toast('本地语音服务已停止，内存已释放', true); });
    });
    const svcRestart = $('#moss-svc-restart');
    if (svcRestart) svcRestart.addEventListener('click', function () {
      btnLoading(svcRestart, '重启中…');
      if (window.BLTTTS) window.BLTTTS.mossServiceRestart(function () { btnDone(svcRestart); refreshMossServiceUi(); toast('本地语音服务已重启', true); });
      else btnDone(svcRestart);
    });
    if (!renderTtsPage._mossTimer) {
      renderTtsPage._mossTimer = setInterval(function () {
        if (!document.getElementById('moss-model-row')) return;
        if (window.BLTTTS) window.BLTTTS.fetchMossModelStatus(updateMossStatusLine);
      }, 2500);
    }
    const fileInput = $('#moss-voice-file');
    const addBtn = $('#moss-voice-add');
    if (addBtn && fileInput) addBtn.addEventListener('click', function () { fileInput.click(); });
    if (fileInput) fileInput.addEventListener('change', function () {
      const f = this.files && this.files[0];
      if (!f) return;
      const path = f.path || '';
      const name = (f.name || '自定义音色').replace(/\.[^.]+$/, '').slice(0, 20) || '自定义音色';
      if (!path) { toast('无法读取文件路径，请重试', false); return; }
      toast('正在处理参考音频…', true);
      if (window.BLTTTS) window.BLTTTS.addMossCustomVoice(name, path, function (ok, err) {
        if (ok) { toast('✅ 已添加自定义音色「' + name + '」', true); rebuildVoiceSelect(); }
        else toast('添加失败：' + (err || '未知错误'), false);
      });
      this.value = '';
    });
    const delBtn = $('#moss-voice-del');
    if (delBtn) delBtn.addEventListener('click', function () {
      const sel = $('#tts-voice');
      const cur = sel ? sel.value : '';
      const v = getMossVoicesSafe().find(function (x) { return x.id === cur; });
      if (!v || !v.custom) { toast('请先在音色下拉中选中一个「自定义音色」', false); return; }
      if (window.BLTTTS) window.BLTTTS.removeMossCustomVoice(cur, function (ok, err) {
        if (ok) { toast('已删除自定义音色「' + cur + '」', true); rebuildVoiceSelect(); }
        else toast('删除失败：' + (err || '未知错误'), false);
      });
    });
  }
  // 音色搜索 / 收藏 / 试听
  const voiceSearch = $('#tts-voice-search');
  if (voiceSearch) voiceSearch.addEventListener('input', () => rebuildVoiceSelect());
  const favBtn = $('#tts-voice-fav');
  if (favBtn) favBtn.addEventListener('click', () => ttsFavToggle());
  const tryBtn = $('#tts-voice-try');
  if (tryBtn) tryBtn.addEventListener('click', () => { const sel = $('#tts-voice'); if (sel && window.BLTTTS) window.BLTTTS.previewVoice(sel.value); });
  // 语气：全局切换 / 自定义参数 / 试听
  const toneSel = $('#tts-tone-global');
  if (toneSel) toneSel.addEventListener('change', function () {
    const row = $('#tts-tone-custom-row');
    if (row) row.style.display = this.value === 'custom' ? '' : 'none';
    if (this.value === 'excited' || this.value === 'fast') toast('弹幕高峰期建议搭配调大「语音间隔」，避免播报拖节奏', false);
    saveTts(true);
  });
  ['tts-tone-custom-rate','tts-tone-custom-pitch'].forEach(id => ttsLive($('#' + id)));
  const toneVoiceSel = $('#tts-tone-custom-voice');
  if (toneVoiceSel) toneVoiceSel.addEventListener('change', () => saveTts(true));
  const tonePrefix = $('#tts-tone-custom-prefix');
  if (tonePrefix) tonePrefix.addEventListener('change', () => saveTts(true));
  const toneTry = $('#tts-tone-try');
  if (toneTry) toneTry.addEventListener('click', () => ttsTonePreview());
  // 类型开关（Switch）
  $$('#content [data-tts-toggle]').forEach(el => {
    el.addEventListener('change', () => ttsTypeToggle(el.getAttribute('data-tts-toggle')));
  });
  // 队列实时显示：监听 tts:status 事件 + 兜底轮询
  // refreshVoiceSelectIfChanged 在非 TTS 页（无 #tts-voice）时自动跳过
  renderTtsQueue();
  if (!window._ttsStatusBound) {
    window._ttsStatusBound = true;
    window.addEventListener('tts:status', () => { renderTtsQueue(); refreshVoiceSelectIfChanged(); });
    setInterval(() => renderTtsQueue(), 800);
  }
}
// 实时刷新语音队列显示（当前状态 + 队列内容）
const TTS_TYPE_NAMES = { danmu:'弹幕', gift:'礼物', superchat:'醒目留言', welcome:'欢迎' };
function renderTtsQueue(){
  const st = $('#tts-queue-st');
  if (!st) return;
  let info = null;
  if (window.BLTTTS && window.BLTTTS.getStatus) { try { info = window.BLTTTS.getStatus(); } catch(e){} }
  else if (window.BLTTTS) { try { info = { enabled: true, speaking: window.BLTTTS.isSpeaking(), queueLength: window.BLTTTS.queueLength(), queue: [] }; } catch(e){} }
  if (!info) return;
  const list = $('#tts-queue-list');
  const statusTxt = (!info.enabled) ? '<span style="color:#e67e22;">语音已关闭</span>' :
    (info.speaking ? '<span style="color:#2ecc71;">🔊 正在播放…</span>' : (info.queueLength ? '<span style="color:#3498db;">排队中…</span>' : '空闲'));
  st.innerHTML = info.queueLength + ' 条待念 · ' + statusTxt;
  if (list) {
    if (!info.queue.length) { list.innerHTML = '<div class="muted" style="font-size:12px;">（队列为空）</div>'; }
    else {
      list.innerHTML = info.queue.map((q, i) => '<div style="font-size:12px;padding:3px 0;border-bottom:1px dashed var(--line);">' +
        '<span class="badge ' + (q.type === 'danmu' ? 'danmu' : '') + '" style="font-size:10px;margin-right:6px;">' + esc(TTS_TYPE_NAMES[q.type] || q.type) + '</span>' +
        '<span class="muted">' + esc(String(q.text).slice(0, 40)) + (String(q.text).length > 40 ? '…' : '') + '</span>' +
        (q.priority === 2 ? ' <span style="color:#f1c40f;font-size:10px;">舰长优先</span>' : '') + '</div>').join('');
    }
  }
}

// 切换某类型开关
function ttsTypeToggle(key) {
  const cfg = state.config;
  if (!cfg.tts) cfg.tts = { enabled: false, voice:'zh-CN-XiaoxiaoNeural', rate:1, pitch:1, volume:1, gain:0, queueMax:8, guardPriority:true, blacklistUids:'', bannedWords:'', danmu:{enabled:false,sayUid:false,minMedal:0,minHonor:0,volume:1}, gift:{enabled:false,sayUid:false,minMedal:0,minHonor:0,volume:1}, superchat:{enabled:false,sayUid:false,minMedal:0,minHonor:0,volume:1}, welcome:{enabled:false,sayUid:false,minMedal:0,minHonor:0,volume:1} };
  if (!cfg.tts[key]) cfg.tts[key] = { enabled: false, sayUid: false, minMedal: 0, minHonor: 0, volume: 1 };
  cfg.tts[key].enabled = !cfg.tts[key].enabled;
  // 即时应用到 TTS 引擎（无需等保存）
  if (window.BLTTTS) window.BLTTTS.setConfig(cfg.tts);
  renderTtsPage();
  saveTts(true);   // 自动保存持久化
}
let _ttsSaveSeq = 0;
async function saveTts(silent) {
  const mySeq = ++_ttsSaveSeq;
  const c = state.config;
  if (!c.tts) c.tts = {};
  c.tts.enabled = !!(($('#tts-enabled-cb')||{}).checked);
  // 音色按当前引擎分别保存；下拉框里的值与引擎不匹配时（切换瞬间的旧清单）不覆盖另一引擎的音色
  const engNow = ($$('#content input[name="tts-engine"]').find(function (r) { return r.checked; }) || {}).value || c.tts.engine || 'edge';
  c.tts.engine = (engNow === 'moss' || engNow === 'sys') ? engNow : 'edge';
  const voiceSelEl = $('#tts-voice');
  if (voiceSelEl && voiceSelEl.value) {
    const isMossVoice = !!(getMossVoicesSafe().some(v => v.id === voiceSelEl.value));
    if (c.tts.engine === 'moss' && isMossVoice) c.tts.mossVoice = voiceSelEl.value;
    else if (c.tts.engine !== 'moss' && !isMossVoice) c.tts.voice = voiceSelEl.value;
  }
  c.tts.rate = Number(($('#tts-rate')||{}).value) || 1;
  c.tts.pitch = Number(($('#tts-pitch')||{}).value) || 1;
  c.tts.volume = Number(($('#tts-volume')||{}).value) || 1;
  c.tts.gain = Number(($('#tts-gain')||{}).value) || 0;
  c.tts.queueMax = Number(($('#tts-queue')||{}).value) || 8;
  c.tts.gap = Math.max(0, Math.min(5000, Number(($('#tts-gap')||{}).value) || 0));
  c.tts.guardPriority = !!(($('#tts-guardpri-cb')||{}).checked);
  c.tts.blacklistUids = (($('#tts-blacklist')||{}).value || '').trim();
  c.tts.bannedWords = (($('#tts-banned')||{}).value || '').trim();
  // 语气：全局 + 每事件绑定 + 自定义预设
  const toneSelEl = $('#tts-tone-global');
  if (toneSelEl) {
    if (!c.tts.tone || typeof c.tts.tone !== 'object') c.tts.tone = { global: 'normal', byType: {} };
    c.tts.tone.global = toneSelEl.value || 'normal';
    if (!c.tts.tone.byType || typeof c.tts.tone.byType !== 'object') c.tts.tone.byType = {};
    for (const k of ['danmu','gift','superchat','welcome']) {
      const el = $('[data-tts-tone="' + k + '"]');
      if (el) c.tts.tone.byType[k] = el.value || '';
    }
    const cr = $('#tts-tone-custom-rate'), cp = $('#tts-tone-custom-pitch');
    if (cr && cp) {
      if (!c.tts.tonePresets || typeof c.tts.tonePresets !== 'object') c.tts.tonePresets = {};
      const cv = $('#tts-tone-custom-voice');
      const cpx = $('#tts-tone-custom-prefix');
      c.tts.tonePresets.custom = {
        label: '自定义',
        rate: Math.max(0.5, Math.min(2, Number(cr.value) || 1)),
        pitch: Math.max(-50, Math.min(50, Number(cp.value) || 0)),
        voice: cv ? (cv.value || '') : '',
        prefix: cpx ? String(cpx.value || '').slice(0, 12) : ''
      };
    }
  }
  // 各类型 checkbox/number/range
  for (const k of ['danmu','gift','superchat','welcome']) {
    if (!c.tts[k]) c.tts[k] = { enabled:true, sayUid:false, minMedal:0, minHonor:0, volume:1 };
    const el = $('[data-tts="' + k + '.sayUid"]');
    c.tts[k].sayUid = el ? el.checked : false;
    const mm = $('[data-tts="' + k + '.minMedal"]');
    c.tts[k].minMedal = mm ? Number(mm.value)||0 : 0;
    const mh = $('[data-tts="' + k + '.minHonor"]');
    c.tts[k].minHonor = mh ? Number(mh.value)||0 : 0;
    const v = $('[data-tts="' + k + '.volume"]');
    c.tts[k].volume = v ? Number(v.value)||1 : 1;
    if (k === 'welcome') {
      const txt = ($('#tts-welcome-texts')||{}).value || '';
      c.tts.welcome.texts = txt.split('\n').map(s=>s.trim()).filter(Boolean);
    }
  }
  // 更新 TTS 模块
  if (window.BLTTTS) window.BLTTTS.setConfig(c.tts);
  state.ttsEnabled = c.tts.enabled;
  try {
    const saved = await post('/api/config', c);
    // 只有最新一次保存的响应才应用，避免乱序响应把引擎/音色覆盖回旧值
    if (mySeq === _ttsSaveSeq) {
      state.config = saved;
      if (window.BLTTTS && saved.tts) window.BLTTTS.setConfig(saved.tts);
    }
    if (!silent) { flashSaved($('#tts-save')); toast('✅ 语音设置已保存'); }
    else autosaveHint();
  } catch (e) {
    toast('❌ 保存失败: ' + (e && e.message || e), false);
  }
}

function ttsPreview() {
  if (window.BLTTTS) window.BLTTTS.preview();
  else toast('语音模块未加载', false);
}
function ttsSkip() {
  if (window.BLTTTS) window.BLTTTS.skip();
}
function ttsRestart() {
  if (window.BLTTTS) { window.BLTTTS.restart(); toast('语音模块已重启'); }
}
// 收藏/取消收藏当前选中音色（持久化到 cfg.tts.favoriteVoices）
async function ttsFavToggle() {
  const sel = $('#tts-voice');
  if (!sel || !sel.value) return;
  const c = state.config;
  if (!c.tts) c.tts = {};
  if (!Array.isArray(c.tts.favoriteVoices)) c.tts.favoriteVoices = [];
  const name = sel.value;
  const i = c.tts.favoriteVoices.indexOf(name);
  if (i >= 0) { c.tts.favoriteVoices.splice(i, 1); toast('已取消收藏'); }
  else { c.tts.favoriteVoices.push(name); if (c.tts.favoriteVoices.length > 20) c.tts.favoriteVoices.shift(); toast('已加入常用音色', true); }
  rebuildVoiceSelect();
  await saveTts(true);
}
// 试听当前选中的语气（自定义时先把输入值写入内存配置）
function ttsTonePreview() {
  const sel = $('#tts-tone-global');
  const name = sel ? (sel.value || 'normal') : 'normal';
  if (name === 'custom') {
    const c = state.config;
    if (!c.tts) c.tts = {};
    if (!c.tts.tonePresets || typeof c.tts.tonePresets !== 'object') c.tts.tonePresets = {};
    const cv = $('#tts-tone-custom-voice');
    const cpx = $('#tts-tone-custom-prefix');
    c.tts.tonePresets.custom = {
      label: '自定义',
      rate: Number(($('#tts-tone-custom-rate')||{}).value) || 1,
      pitch: Number(($('#tts-tone-custom-pitch')||{}).value) || 0,
      voice: cv ? (cv.value || '') : '',
      prefix: cpx ? String(cpx.value || '').slice(0, 12) : ''
    };
    if (window.BLTTTS) window.BLTTTS.setConfig(c.tts);
  }
  if (window.BLTTTS) window.BLTTTS.previewTone(name);
}

async function renderSettings(){
  if (!state.config) state.config = await g('/api/config');
  await refreshStatus();
  const c = state.config;
  const st = state.status || {};
  $('#topTitle').textContent = '房间管理';
  $('#topSub').textContent = '配置直播间并控制连接与记录';
  $('#topActions').innerHTML = '<button class="btn sm" onclick="refreshPanel()" style="margin-left:8px;">🔄 刷新</button>';

  const folderRows = TYPES.map(t =>
    '<div class="field"><label>' + TYPE_NAMES[t] + '记录文件夹</label>' +
    '<div class="row"><input id="folder-' + t + '" value="' + esc((c.folders||{})[t]||'') + '" class="mono" style="flex:1">' +
    '<button class="btn sm" onclick="pickAndSet(\'folder-' + t + '\')">浏览</button>' +
    '<button class="btn sm" onclick="openFolder(\'' + t + '\')">打开</button></div></div>').join('');

  const bmRows = Object.keys(c.blindBox || {}).map(k =>
    '<div class="row bb-row" style="margin-bottom:6px;"><input class="bb-key mono" placeholder="礼物名" value="' + esc(k) + '"><input class="bb-val mono" placeholder="单个收入(元)" value="' + esc(c.blindBox[k]) + '" style="width:120px"><button class="btn sm danger" onclick="this.parentNode.remove()">✕</button></div>').join('');

  // v1.1.9: group titles + progressive disclosure (advanced boxes collapsed by default)
  const grpCore = '<div class="settings-group-title">连接直播间 <span class="sg-hint">必做 · 全部功能的前提</span></div>';
  const grpAdv  = '<div class="settings-group-title">进阶功能 <span class="sg-hint">可选 · 按需展开</span></div>';
  const grpApp  = '<div class="settings-group-title">应用管理</div>';
  const advBox = (key) => boxCollapsed(key, true) ? ' collapsed' : '';
  const coreBox = (key) => boxCollapsed(key, false) ? ' collapsed' : '';

  $('#content').innerHTML =
    '<section class="content-header"><h1>房间管理 <small>配置直播间并控制连接与记录</small></h1>' +
    '<ol class="breadcrumb"><li class="active">首页</li></ol></section>' +
    renderVerifyLockCard() +
    renderConnGuide(st) +
    grpCore +

    '<div class="box box-primary' + coreBox('conn') + '" id="card-conn">' +
    '<div class="box-header with-border"><h3 class="box-title">🔗 直播间连接 <span class="title-hint">第 2 步 · 填房间号并连接，全部功能的前提</span></h3>' +
    boxToolBtn('conn') + '</div>' +
    '<div class="box-body">' +
    '<div id="connBar" class="conn-bar">' +
      '<span class="dot ' + dotClass(st.state) + '" id="connDot"></span>' +
      '<span id="connStateText" class="conn-state">' + stateText(st) + '</span>' +
      '<input id="roomIdInline" value="' + esc(c.roomId) + '" placeholder="输入房间号，回车直接连接" class="form-control mono" style="flex:1;min-width:160px;max-width:240px;display:inline-block;" data-tip="B站直播间号（短号），不是UID。在B站直播间URL中可以看到">' +
      '<button class="btn primary" id="btn-connect" data-tip="连接到B站弹幕服务器，开始接收实时弹幕、礼物等事件">▶ 连接</button>' +
      '<button class="btn danger" id="btn-disconnect" data-tip="断开与弹幕服务器的连接，停止接收所有事件">⏹ 断开</button>' +
      '<span class="muted" id="connLiveInfo" style="margin-left:auto;">' + liveInfoDetail() + '</span>' +
    '</div>' +
    '<div id="connLiveTop3" class="muted" style="margin:-4px 0 4px 0;font-size:12px;min-height:18px;">' + liveInfoTop3Html() + '</div>' +
    '<div id="connMsg" class="muted" style="margin:0 0 4px 0;font-size:12px;min-height:18px;"></div>' +
    '<div id="connSteps" class="conn-steps"></div>' +
    '<div class="form-grid" style="margin-top:10px;">' +
    '<div class="field"><label>房间号（自动保存）</label><div id="connRoomEcho" style="min-height:34px;display:flex;align-items:center;">' + (c.roomId ? '<span class="mono">' + esc(c.roomId) + '</span>' : '<span class="muted">在上方连接条输入直播间房间号</span>') + '</div>' +
    '<div class="hint">直播间网址 live.bilibili.com/ 后面的数字（短号，不是UID）；输入后自动保存</div></div>' +
    '<div class="field"><label>主播UID（自动获取）</label><input id="uid" value="' + esc(c.uid || st.uid || '') + '" disabled data-tip="连接成功后自动从房间号获取，无需手动填写"></div>' +
    '<div class="field"><label>启动自动连接</label>' + switchHtml('autoConnectCb', !!c.autoConnect, '程序启动时自动连接上次的房间', '程序启动时自动连接上次成功连接的房间，省去手动点连接') + '</div>' +
    '</div></div></div>' +

    (window.electronAPI ? '<div class="box box-success' + coreBox('login') + '" id="card-login">' +
    '<div class="box-header with-border"><h3 class="box-title">🔐 B站登录 <span class="title-hint">第 1 步 · 扫码登录后 Cookie 自动同步，弹幕更完整</span></h3>' +
    boxToolBtn('login') + '</div>' +
    '<div class="box-body">' +
      '<div class="row" style="gap:8px;flex-wrap:wrap;">' +
        '<button class="btn sm" id="btn-bili-hide" onclick="biliHideBrowser()" data-tip="隐藏内置浏览器窗口，登录态保留，后台仍保持连接">🖥 隐藏 B站浏览器</button>' +
        '<button class="btn sm primary" id="btn-bili-open" onclick="biliOpenBrowser()" data-tip="在内置浏览器中打开B站首页，扫码或输入账号登录">🔐 打开 B站浏览器</button>' +
        '<button class="btn sm success" id="btn-bili-detect" onclick="biliDetectLogin()" data-tip="从内置浏览器读取当前登录Cookie并保存到配置，用于弹幕鉴权">✅ 已完成登录，检测并同步 Cookie</button>' +
        '<button class="btn sm danger" id="btn-bili-clear" onclick="biliClearCookies()" data-tip="清除已保存的B站Cookie，退出登录状态">🗑 清除登录态</button>' +
      '</div>' +
      '<div id="biliStatusInline" class="muted" style="margin-top:8px;font-size:12px;">检测中...</div>' +
      '<details style="margin-top:10px;">' +
        '<summary class="muted" style="cursor:pointer;font-size:12px;">查看当前 Cookie（已脱敏）</summary>' +
        '<pre id="biliCookieInline" class="mono" style="background:rgba(0,0,0,.3);padding:8px;border-radius:4px;max-height:160px;overflow:auto;font-size:11px;margin:6px 0 0 0;white-space:pre-wrap;word-break:break-all;display:none;"></pre>' +
      '</details>' +
    '</div></div>' : '') +

    grpAdv +

    '<div class="box box-info' + advBox('cookieimport') + '">' +
    '<div class="box-header with-border"><h3 class="box-title">📋 手动导入 Cookie <span class="title-hint">备用方式 · 没有扫码条件时使用</span><span class="label label-default" style="margin-left:6px;">高级</span></h3>' +
    boxToolBtn('cookieimport', true) + '</div>' +
    '<div class="box-body">' +
    '<p class="muted" style="margin-top:0;">推荐用上方「B站登录」扫码。以下方式适合无法扫码的场景：按 <b>F12 → 网络(Network) → 筛选 Fetch/XHR → 点一个 URL 以 <span class="mono">web-room</span> 或 <span class="mono">web-interface</span> 开头的请求 → 看「请求标头」里的 <span class="mono">Cookie</span> 那行</b>，整段复制粘贴到下面，会自动解析并保存。</p>' +
    '<textarea class="form-control" id="cookie-paste" placeholder="粘贴整段 Cookie，例如：enable_web_push=DISABLE; ...; SESSDATA=xxx; bili_jct=xxx" style="width:100%;height:72px;font-family:ui-monospace,monospace;font-size:12px;" oninput="parseCookieInput()" data-tip="粘贴从浏览器复制的完整Cookie字符串，需包含SESSDATA和bili_jct"></textarea>' +
    '<div class="row" style="margin-top:10px;"><button class="btn primary" id="btn-apply-cookie" onclick="applyPastedCookie()" data-tip="自动解析粘贴的Cookie，提取SESSDATA等关键字段并保存">解析并保存Cookie</button>' +
    '<button class="btn btn-default" onclick="parseCookieInput()" data-tip="仅解析并展示结果，不保存到配置，用于确认Cookie是否有效">解析预览</button></div>' +
    '<div id="cookie-parse-result" class="muted" style="margin-top:8px;font-size:13px;"></div>' +
    '<div class="field" style="margin-top:12px;"><label>当前 Cookie（登录后自动维护，一般无需手动改）</label><textarea id="cookie" placeholder="粘贴 SESSDATA=...">' + esc(c.cookie || '') + '</textarea>' +
    '<div class="hint" style="color:' + (c.cookie ? 'var(--muted)' : 'var(--warning-bright)') + ';">' + (c.cookie ? '已配置 Cookie，将获得完整弹幕流。' : '⚠ 未配置：B站会限流匿名连接，可能只记录到很少的弹幕/礼物。建议先扫码登录。') + '</div></div>' +
    '</div></div>' +

    '<div class="box' + advBox('like') + '">' +
    '<div class="box-header with-border"><h3 class="box-title">👍 自动点赞 <span class="title-hint">给当前直播间自动点赞到每日上限</span></h3>' +
    boxToolBtn('like', true) + '</div>' +
    '<div class="box-body">' +
    '<p class="muted" style="margin-top:0;">自动点赞当前直播间到B站每日上限（约1000次）。需要登录Cookie，每次间隔300ms。</p>' +
    '<div class="row" style="gap:8px;flex-wrap:wrap;">' +
      '<button class="btn primary" id="likeStartBtn" onclick="startLike()" data-tip="自动为当前直播间点赞，模拟用户点击点赞按钮">👍 自动点赞</button>' +
      '<button class="btn danger" id="likeStopBtn" onclick="stopLike()" style="display:none;" data-tip="停止自动点赞">⏹ 停止</button>' +
    '</div>' +
    '<div id="likeStatus" class="muted" style="margin-top:8px;font-size:13px;"></div>' +
    '</div></div>' +

    '<div class="box' + advBox('folders') + '">' +
    '<div class="box-header with-border"><h3 class="box-title">📁 记录文件夹 <span class="title-hint">弹幕/礼物等记录文件的保存位置</span></h3>' +
    boxToolBtn('folders', true) + '</div>' +
    '<div class="box-body"><div class="form-grid">' + folderRows + '</div></div></div>' +

    '<div class="box' + advBox('blindbox') + '">' +
    '<div class="box-header with-border"><h3 class="box-title">🎁 盲盒收入映射 <span class="title-hint">用于计算盲盒盈亏，不影响记录</span></h3>' +
    boxToolBtn('blindbox', true) + '</div>' +
    '<div class="box-body"><p class="muted" style="margin-top:0;">礼物名 → 单个盲盒开出收入(元)，用于计算盈亏。留空表示收入=成本。</p>' +
    '<div id="bb-rows">' + (bmRows || '<div class="muted">尚未配置</div>') + '</div>' +
    '<div class="row" style="margin-top:10px;gap:8px;flex-wrap:wrap;"><button class="btn btn-default sm" onclick="addBb()" data-tip="新增一行盲盒映射">＋ 添加礼物</button><button class="btn sm primary" id="btn-detect-bb" onclick="detectBlindBoxes()" data-tip="自动从已记录的盲盒数据中检测新出现的礼物名，辅助补全映射">🔍 检测盲盒更新</button></div>' +
    '<div id="blindboxDetectResult" style="margin-top:10px;"></div>' +
    '</div></div>' +

    grpApp +

    '<div class="box box-primary" id="card-skin">' +
    '<div class="box-header with-border"><h3 class="box-title">🎨 界面皮肤 <span class="title-hint">即时生效 · 全部页面换装 · 自动保存</span></h3></div>' +
    '<div class="box-body">' +
    '<div class="form-grid">' +
    '<div class="field"><label>选择皮肤（默认 + 8 款风格）</label>' +
    '<select id="skin-select" onchange="changeSkin(this.value)">' +
    SKINS.map(function (s) {
      return '<option value="' + s.id + '"' + ((state.config.skin || 'default') === s.id ? ' selected' : '') + '>' + s.name + '</option>';
    }).join('') +
    '</select><div class="hint" id="skin-hint">' + esc(skinDesc(state.config.skin || 'default')) + '</div></div>' +
    '</div></div></div>' +

    '<div class="box' + advBox('server') + '">' +
    '<div class="box-header with-border"><h3 class="box-title">⚙️ 桌面应用 / 服务端控制' + (window.electronAPI ? ' <span class="label label-success">Electron</span>' : ' <span class="label label-default">Web</span>') + '<span class="label label-default" style="margin-left:6px;">高级</span></h3>' +
    boxToolBtn('server', true) + '</div>' +
    '<div class="box-body"><div id="serverCtrlPanel">加载中...</div></div></div>' +

    '<div class="box' + advBox('dev') + '">' +
    '<div class="box-header with-border"><h3 class="box-title">🛠️ 开发者选项</h3>' +
    boxToolBtn('dev', true) + '</div>' +
    '<div class="box-body">' +
    '<div class="row" style="gap:8px;">' +
    '<button class="btn btn-default sm" onclick="location.hash=\'#/debug\'">🧪 模拟测试（开发调试用）</button>' +
    '<button class="btn btn-default sm" onclick="location.hash=\'#/diagnostics\'">🔧 网络诊断</button>' +
    '<span class="muted" style="font-size:12px;">模拟 B 站事件验证链路；网络诊断用于排查连通性问题</span>' +
    '</div></div></div>';

  wireSettings();
  // 服务端控制面板（此时 #content 已渲染，serverCtrlPanel 元素存在）
  await renderServerCtrlPanel();
  // B站浏览器状态实时刷新（如果存在）
  if (window.electronAPI) {
    setTimeout(biliUpdateStatusInline, 300);
    setTimeout(biliUpdateStatusInline, 1500);
  }
}

function dotClass(s){
  if (s === 'connected') return 'ok';
  if (s === 'connecting' || s === 'getting_danmu' || s === 'resolving' || s === 'reconnecting' || s === 'checking_live' || s === 'not_live') return 'warn';
  if (s === 'error') return 'err';
  return 'idle';
}
function stateText(st){
  const map = { idle:'未连接', resolving:'解析房间…', checking_live:'检查开播状态…', getting_danmu:'获取弹幕服务器…', connected:'已连接', connecting:'连接中…', reconnecting:'重连中…', error:'连接出错', disconnected:'已断开', not_live:'房间未开播（开播后自动连接）' };
  return map[st.state] || st.state;
}

// v1.1.9: room-id echo under the connection bar (single source: #roomIdInline)
function updateRoomEcho(){
  const rid = $('#roomIdInline');
  const echo = document.getElementById('connRoomEcho');
  if (!rid || !echo) return;
  const v = rid.value.trim();
  echo.innerHTML = v ? '<span class="mono">' + esc(v) + '</span>&nbsp;<span class="muted" style="font-size:11px;">自动保存</span>'
    : '<span class="muted">在上方连接条输入直播间房间号</span>';
}

// v1.1.9: connection pipeline steps (解析房间 → 检查开播 → 连接服务器 → 已连接)
let _connStepIdx = 0; // last known progress step, used to mark where an error happened
function renderConnSteps(st){
  const el = document.getElementById('connSteps');
  if (!el) return;
  const labels = ['解析房间', '检查开播', '连接服务器', '已连接'];
  const s = (st && st.state) || 'idle';
  const order = { resolving:0, connecting:0, checking_live:1, not_live:1, getting_danmu:2, reconnecting:2 };
  let idx = -1, fail = false;
  if (s === 'connected') { idx = 3; _connStepIdx = 3; }
  else if (s === 'error') { idx = _connStepIdx; fail = true; }
  else if (s in order) { idx = order[s]; _connStepIdx = idx; }
  el.innerHTML = labels.map((lb, i) => {
    let cls = '';
    if (idx >= 0) {
      if (i < idx) cls = 'done';
      else if (i === idx) cls = fail ? 'fail' : 'active';
      if (i === 3 && s === 'connected') cls = 'done';
    }
    const mark = cls === 'done' ? '✓' : (cls === 'fail' ? '✗' : String(i + 1));
    return '<span class="conn-step ' + cls + '"><span class="cs-dot">' + mark + '</span>' + lb + '</span>' +
      (i < labels.length - 1 ? '<span class="conn-step-arrow">→</span>' : '');
  }).join('');
  // dim the steps when idle so they read as "what will happen after you connect"
  el.style.opacity = (idx >= 0) ? '1' : '.55';
}

let _settingsSaveTimer = null;
function wireSettings(){
  const c = state.config;
  const st = state.status || {};
  // ---- 连接/断开：持续轮询直到 connected/error/超时 ----
  const btnConnect = $('#btn-connect');
  const btnDisconnect = $('#btn-disconnect');
  if (state.verifyLocked && btnConnect) btnConnect.disabled = true;
  const roomIdInline = $('#roomIdInline');
  // 预填：优先"上一次连接"的房间（localStorage），无则用 config.roomId
  let prefill = c.roomId || '';
  try { prefill = (localStorage.getItem('bili_last_room') || c.roomId || ''); } catch (e) {}
  if (roomIdInline && !roomIdInline.value) roomIdInline.value = prefill;
  updateRoomEcho();
  // ---- v1.1.9 统一自动保存：改动后防抖保存，失焦/回车立即保存 ----
  const scheduleSave = (ms) => { clearTimeout(_settingsSaveTimer); _settingsSaveTimer = setTimeout(() => saveSettings(true), ms || 800); };
  roomIdInline && roomIdInline.addEventListener('input', () => { updateRoomEcho(); scheduleSave(); });
  roomIdInline && roomIdInline.addEventListener('blur', () => saveSettings(true));
  // Enter in room input = save + connect immediately
  roomIdInline && roomIdInline.addEventListener('keydown', (e) => {
    if (e.key === 'Enter') { e.preventDefault(); saveSettings(true); btnConnect && btnConnect.click(); }
  });
  const cookieTa = $('#cookie');
  cookieTa && cookieTa.addEventListener('input', () => scheduleSave(1200));
  cookieTa && cookieTa.addEventListener('blur', () => saveSettings(true));
  $$('input[id^="folder-"]', $('#content')).forEach(inp => {
    inp.addEventListener('input', () => scheduleSave());
    inp.addEventListener('blur', () => saveSettings(true));
  });
  // blindbox rows are added dynamically → delegate on #content (bind only once)
  const contentEl = $('#content');
  if (contentEl && !contentEl._bbAutoSave) {
    contentEl._bbAutoSave = true;
    contentEl.addEventListener('input', (e) => { if (e.target.closest && e.target.closest('.bb-row')) scheduleSave(); });
    contentEl.addEventListener('change', (e) => { if (e.target.closest && e.target.closest('.bb-row')) saveSettings(true); });
  }
  const setConnUiState = (connState) => {
    if (!btnConnect) return;
    if (connState === 'connecting') {
      btnConnect.disabled = true;
      btnConnect.textContent = '连接中…';
      btnDisconnect && (btnDisconnect.disabled = true);
    } else {
      btnConnect.disabled = !!state.verifyLocked;
      btnConnect.textContent = '▶ 连接';
      btnDisconnect && (btnDisconnect.disabled = false);
    }
  };
  // 同步顶部状态条
  const updateConnBar = (st) => {
    const dot = $('#connDot');
    const txt = $('#connStateText');
    const pop = $('#connLiveInfo');
    const msg = $('#connMsg');
    if (dot) dot.className = 'dot ' + dotClass(st.state);
    if (txt) txt.textContent = stateText(st);
    if (pop) pop.innerHTML = liveInfoDetail();
    if (msg) {
      if (st.error) msg.innerHTML = '<span style="color:var(--danger)">错误: ' + esc(st.error) + '</span>';
      else if (st.state === 'connected') msg.innerHTML = '<span style="color:var(--success)">✓ 已连接到 ' + esc(st.realRoomId || c.roomId) + '</span>';
      else msg.textContent = '';
    }
    // 引导横幅跟随连接状态刷新
    const guide = document.getElementById('conn-guide');
    if (guide) guide.outerHTML = renderConnGuide(st);
    // v1.1.9: connection step indicator follows state
    renderConnSteps(st);
  };
  updateConnBar(st);
  setConnUiState(isConnectPolling() ? 'connecting' : (st.state === 'connected' ? 'connected' : 'idle'));

  btnConnect && btnConnect.addEventListener('click', async () => {
    if (state.verifyLocked) { toast('请先登录授权账号', false); return; }
    const rid = ((roomIdInline && roomIdInline.value) || '').trim();
    if (!rid) { toast('请输入房间号', false); roomIdInline && roomIdInline.focus(); return; }
    setConnUiState('connecting');
    const msg = $('#connMsg');
    if (msg) msg.innerHTML = '<span style="color:var(--warn)">⏳ 正在连接房间 ' + esc(rid) + '…</span>';
    // 取消旧的轮询
    stopConnectPoll();
    const myReqId = ++_connectPollReqId;
    // 调用 connect
    try { await post('/api/connect', { roomId: rid, cookie: (($('#cookie') || {}).value || '') }); }
    catch (e) { if (msg) msg.innerHTML = '<span style="color:var(--danger)">连接请求失败: ' + esc(e.message) + '</span>'; setConnUiState('idle'); return; }
    // 持续轮询直到 connected / error / 30 秒超时
    const startTime = Date.now();
    const TIMEOUT_MS = 30000;
    const POLL_MS = 500;
    const tick = async () => {
      if (myReqId !== _connectPollReqId) return; // 被新的连接请求取消
      let st2;
      try { st2 = await g('/api/status'); } catch (e) {}
      if (st2) {
        updateConnBar(st2);
        state.status = st2;
        if (st2.state === 'connected') {
          stopConnectPoll();
          setConnUiState('connected');
          const m = $('#connMsg');
          if (m) m.innerHTML = '<span style="color:var(--accent)">✓ 连接成功 房间 ' + esc(st2.realRoomId || rid) + '</span>';
          // 记录"上一次连接"的房间，供下次预填
          try { localStorage.setItem('bili_last_room', rid); } catch (e) {}
          toast('连接成功');
          return;
        }
        if (st2.state === 'error') {
          stopConnectPoll();
          setConnUiState('idle');
          const m = $('#connMsg');
          if (m) m.innerHTML = '<span style="color:var(--danger)">✗ 连接失败: ' + esc(st2.error || '未知错误') + '</span>';
          toast('连接失败', false);
          return;
        }
      }
      // 进度提示（用中文状态文案，避免露出英文原值）
      const elapsed = ((Date.now() - startTime) / 1000).toFixed(1);
      const m2 = $('#connMsg');
      if (m2 && st2) m2.innerHTML = '<span style="color:var(--warn)">⏳ 正在连接（' + esc(stateText(st2)) + '）… (' + elapsed + 's / 30s)</span>';
      // Room not live: the server keeps watching and auto-connects later; polling to timeout would mislead
      if (st2 && st2.state === 'not_live') {
        stopConnectPoll();
        setConnUiState('idle');
        const m3 = $('#connMsg');
        if (m3) m3.innerHTML = '<span style="color:var(--warning-bright)">📺 房间未开播。开播后会自动连接，无需手动操作。</span>';
        toast('房间未开播，开播后自动连接');
        return;
      }
      if (Date.now() - startTime > TIMEOUT_MS) {
        stopConnectPoll();
        setConnUiState('idle');
        const m = $('#connMsg');
        if (m) m.innerHTML = '<span style="color:var(--danger)">✗ 连接超时（30 秒未连上），请检查房间号或 Cookie</span>';
        toast('连接超时', false);
        return;
      }
      _connectPollTimer = setTimeout(tick, POLL_MS);
    };
    tick();
  });
  btnDisconnect && btnDisconnect.addEventListener('click', async () => {
    stopConnectPoll();
    _connectPollReqId++; // 取消任何正在轮询的连接
    setConnUiState('idle');
    try { await post('/api/disconnect'); } catch (e) {}
    await refreshStatus();
    const m = $('#connMsg');
    if (m) m.innerHTML = '<span class="muted">已断开</span>';
  });
  $('#autoConnectCb').addEventListener('change', function(){
    state.config.autoConnect = this.checked;
    delete state.config.__dirty;
    saveSettings(true);
    toast(this.checked ? '已开启：下次启动自动连接上次的房间' : '已关闭自动连接');
  });
  $$('.toggle-record', $('#content')).forEach(btn => {
    btn.addEventListener('click', async () => {
      const t = btn.getAttribute('data-type');
      const on = !(state.config.recording && state.config.recording[t]);
      const r = await post('/api/recording/' + t, { on: on });
      state.config.recording[t] = r.on;
      btn.textContent = r.on ? '✅ 开启' : '⛔ 关闭';
      btn.classList.toggle('primary', r.on);
    });
  });
}
function setPkTarget() {
  const roomId = ($('#pk-target') || {}).value.trim();
  if (!roomId) { toast('请先填写对方房间号', false); return; }
  post('/api/pk-target', { roomId: roomId }).then(() => {
    toast('已设置PK对手房间 ' + roomId);
    refreshPkInfo();
  }).catch(e => toast(e.message, false));
}
async function refreshPkInfo() {
  try {
    const pk = await g('/api/pk-status');
    renderPkInfo(pk);
  } catch (e) {}
}
function renderPkInfo(pk) {
  const el = $('#pk-info');
  if (!el) return;
  const o = pk && pk.opponent;
  if (!pk || !pk.active || !o) { el.innerHTML = '<span class="muted">当前未处于 PK/连线（或未抓到对方房间）。</span>'; return; }
  el.innerHTML =
    '<div class="row" style="gap:14px;flex-wrap:wrap;">' +
    '<span>房间 <span class="mono">' + esc(o.roomId || '-') + '</span></span>' +
    '<span>👁 在线 <b>' + ((o.online || o.popularity) || 0) + '</b></span>' +
    '<span>⭐ 粉丝 <b>' + (o.follower || 0) + '</b></span>' +
    '<span>🛡 舰长 <b>' + (o.guard || 0) + '</b></span>' +
    '<span>💠 对方PK分/礼物 <b>' + (pk.votes || pk.score || 0) + '</b></span>' +
    '</div>' + (o.title ? '<div class="muted" style="margin-top:6px;">' + esc(o.title) + '</div>' : '');
}
let pkPollTimer = null;
function startPkPoll() {
  if (pkPollTimer) clearInterval(pkPollTimer);
  refreshPkInfo();
  pkPollTimer = setInterval(refreshPkInfo, 5000);
}
// ---------- 一键导入登录Cookie（复制整段自动解析） ----------
// 解析 cookie（兼容多种粘贴格式）并返回标准 "k=v; k=v" 串
function parseCookieStr(raw) {
  if (raw == null) raw = '';
  raw = String(raw).trim();
  if (!raw) return '';
  const entries = {};
  const set = (k, v) => {
    if (k == null) return; k = String(k).trim(); if (!k) return;
    if (v == null) return; v = String(v).trim();
    if (v === '' || v === 'undefined' || v === 'null' || v === 'NaN') return;
    entries[k] = v;
  };
  // JSON 对象 / 数组
  if (/^[[{]/.test(raw)) {
    try {
      const j = JSON.parse(raw);
      const pairs = [];
      if (Array.isArray(j)) { for (const it of j) { if (!it || typeof it !== 'object') continue; if ((it.name != null || it.key != null) && it.value != null) pairs.push([it.name != null ? it.name : it.key, it.value]); else for (const k of Object.keys(it)) pairs.push([k, it[k]]); } }
      else { for (const k of Object.keys(j)) pairs.push([k, j[k]]); }
      for (const [k, v] of pairs) set(k, v);
      return Object.keys(entries).map(k => k + '=' + entries[k]).join('; ');
    } catch (e) { /* 不是 JSON 则继续按字符串处理 */ }
  }
  // 剥离 Cookie: 前缀
  raw = raw.replace(/^cookie\s*:\s*/i, '').trim();
  // 整段 URL-encoded
  if (raw.indexOf('=') === -1 && /%3[dD]/i.test(raw)) { try { raw = decodeURIComponent(raw); } catch (e) {} }
  // 换行分隔
  if (raw.indexOf('\n') !== -1 || raw.indexOf('\r') !== -1) {
    for (let line of raw.split(/\r?\n/)) {
      line = line.trim(); if (!line) continue;
      if (/^\[.*\]$/.test(line)) continue;
      const eq = line.indexOf('=');
      if (eq > 0) set(line.slice(0, eq).trim(), line.slice(eq + 1).trim());
      else { const m = line.match(/^(\S+)\s+(\S.*)$/); if (m) set(m[1], m[2]); }
    }
  } else {
    for (let piece of raw.split(';')) {
      piece = piece.trim(); if (!piece) continue;
      const eq = piece.indexOf('='); if (eq <= 0) continue;
      set(piece.slice(0, eq).trim(), piece.slice(eq + 1).trim());
    }
  }
  return Object.keys(entries).map(k => k + '=' + entries[k]).join('; ');
}

// 识别 cookie 格式并返回分析信息（供提示用）
function analyzeCookie(raw) {
  const parsed = parseCookieStr(raw);
  const has = (k) => new RegExp('(?:^|;\\s*)' + k + '=').test(parsed + '; '); // 简单判断
  const hasSess = has('SESSDATA'), hasJct = has('bili_jct'), hasUid = has('DedeUserID');
  let format = '标准分号';
  if (/^[[{]/.test(String(raw||'').trim())) format = 'JSON';
  else if (/\r?\n/.test(String(raw||'').trim())) format = '换行(编辑器导出)';
  else if (/%3[dD]/.test(String(raw||'').trim()) && String(raw||'').indexOf('=') === -1) format = 'URL编码整串';
  return { parsed, count: parsed ? parsed.split('; ').length : 0, format, hasSess, hasJct, hasUid };
}
function maskCookie(ck) {
  return String(ck || '').replace(/SESSDATA=[^;]+/g, 'SESSDATA=***').replace(/bili_jct=[^;]+/g, 'bili_jct=***').replace(/DedeUserID=([^;]{0,3})[^;]*/g, 'DedeUserID=$1***');
}
function parseCookieInput() {
  const pasteEl = $('#cookie-paste');
  const paste = (pasteEl && pasteEl.value) || '';
  const a = analyzeCookie(paste);
  const out = $('#cookie-parse-result');
  if (!a.parsed) {
    if (out) out.innerHTML = '<span style="color:var(--danger)">未识别到有效 Cookies。可粘贴整段 Cookie（含 <code>SESSDATA=</code> 等），支持标准分号 / <code>Cookie:</code> 前缀 / URL编码整串 / 换行(编辑器导出) / JSON 对象或数组。</span>';
    return;
  }
  const cookieInput = $('#cookie');
  if (cookieInput) cookieInput.value = a.parsed;
  if (out) {
    let miss = [];
    if (!a.hasSess) miss.push('SESSDATA');
    if (!a.hasJct) miss.push('bili_jct');
    if (!a.hasUid) miss.push('DedeUserID');
    let tip = '';
    if (a.hasSess && a.hasJct && a.hasUid) tip = '<br><span style="color:var(--accent)">✅ 识别为完整登录 Cookie。</span>';
    else if (a.hasSess) tip = '<br><span style="color:var(--warn);">⚠ 缺少：' + miss.join('、') + '——若只有 SESSDATA，服务端连接可能被判为匿名而被限流，建议补全。仅缺 DedeUserID 时服务端会自动用 nav 推导。</span>';
    else tip = '<br><span style="color:var(--danger)">未识别到 SESSDATA，可能不是登录 Cookie。</span>';
    out.innerHTML = '<span style="color:var(--accent)">✅ 已解析（' + a.format + '，' + a.count + ' 条）并填入上方 Cookie 框。</span>' + tip +
      '<br><span class="mono" style="color:var(--muted);font-size:11px;">' + esc(maskCookie(a.parsed).slice(0, 90)) + '…</span>';
  }
}
async function applyPastedCookie() {
  const pasteEl = $('#cookie-paste');
  const parsed = parseCookieStr((pasteEl && pasteEl.value) || '');
  if (!parsed) { toast('未识别到有效 Cookie', false); return; }
  const cookieInput = $('#cookie');
  if (cookieInput) cookieInput.value = parsed;
  const btn = document.getElementById('btn-apply-cookie');
  btnLoading(btn, '解析保存中…');
  try {
    await post('/api/cookie', { cookie: parsed });
    const ridEl = $('#roomIdInline');
    const rid = ((ridEl && ridEl.value) || '').trim() || (state.config.roomId || '');
    await post('/api/connect', { roomId: rid, cookie: parsed });
    toast('Cookie 已保存，已用登录态连接');
    await refreshStatus();
    await renderSettings();
  } catch (e) { toast('保存失败: ' + e.message, false); }
  finally { btnDone(btn); }
}
function openFolder(type){ post('/api/open-folder/' + type).then(() => toast('已打开文件夹')).catch(e => toast(e.message, false)); }
function addBb(){
  const d = document.createElement('div');
  d.className = 'row bb-row';
  d.style.marginBottom = '6px';
  d.innerHTML = '<input class="bb-key mono" placeholder="礼物名" style="flex:1"><input class="bb-val mono" placeholder="单个收入(元)" style="width:120px"><button class="btn sm danger" onclick="this.parentNode.remove()">✕</button>';
  $('#bb-rows').appendChild(d);
}
// silent=true → show subtle "auto saved" hint instead of toast (v1.1.9 unified auto-save)
async function saveSettings(silent){
  // While verification-locked the in-memory config holds forced-off values; never persist those
  if (state.verifyLocked) { return; }
  const c = state.config;
  const ridEl = $('#roomIdInline');
  if (ridEl) c.roomId = ridEl.value.trim();
  const ckEl = $('#cookie');
  if (ckEl) c.cookie = ckEl.value;
  if (!c.folders) c.folders = {};
  for (const t of TYPES) { const f = $('#folder-' + t); if (f) c.folders[t] = f.value.trim(); }
  // 盲盒映射
  const map = {};
  $$('.bb-row', $('#content')).forEach(r => {
    const k = $('.bb-key', r).value.trim(); const v = $('.bb-val', r).value.trim();
    if (k) map[k] = (v === '' ? 0 : Number(v));
  });
  c.blindBox = map;
  delete c.__dirty;
  try {
    const saved = await post('/api/config', c);
    state.config = saved;
    if (!silent) { flashSaved($('#btn-save')); toast('配置已保存'); }
    else autosaveHint();
  } catch (e) {
    toast('❌ 保存失败: ' + (e && e.message || e), false);
  }
}


// ---------- 自动弹幕 设置页 ----------
async function renderAutoDanmuPage(){
  if (!state.config) state.config = await g('/api/config');
  const c = state.config;
  const w = (c.autoDanmu && c.autoDanmu.welcome) || {};
  const th = (c.autoDanmu && c.autoDanmu.thank) || {};
  const tm = (c.autoDanmu && c.autoDanmu.timer) || {};
  const fl = (c.autoDanmu && c.autoDanmu.follow) || {};
  $('#topTitle').textContent = '自动弹幕';
  $('#topSub').textContent = '欢迎进房 / 感谢礼物 / 定时弹幕';
  $('#topActions').innerHTML = '';
  const texts = (w.texts && w.texts.join('\n')) || '';
  const guardTexts = (w.guardTexts && w.guardTexts.join('\n')) || '';
  $('#content').innerHTML =
    '<section class="content-header"><h1>自动弹幕 <small>欢迎/感谢/定时</small></h1>' +
    '<ol class="breadcrumb"><li><a href="#/settings">首页</a></li><li class="active">自动弹幕</li></ol></section>' +
    connReadyBanner() +
    '<div class="box box-primary">' +
    '<div class="box-header with-border"><h3 class="box-title">欢迎进房观众 <span class="title-hint">观众进入直播间时自动发欢迎弹幕</span></h3>' +
    '<div class="box-tools"><button class="btn-box-tool" onclick="var b=this.closest(\'.box\');b.classList.toggle(\'collapsed\');this.textContent=b.classList.contains(\'collapsed\')?\'+\':\'−\'">−</button></div></div>' +
    '<div class="box-body">' +
    '<p class="muted" style="margin-top:0;">同 UID 60 秒内只欢迎一次，显示观众昵称。需<u>登录态 Cookie</u> 才能发送弹幕。</p>' +
    '<div class="form-grid">' +
    '<div class="field"><label>启用欢迎</label>' + switchHtml('ad-welcome-cb', w.enabled !== false, '', '有观众进入直播间时自动发送一条欢迎弹幕') + '</div>' +
    '<div class="field"><label>荣耀等级限制</label><input id="ad-honor" type="number" value="' + esc(w.honorLevelMin != null ? w.honorLevelMin : 0) + '" data-tip="仅欢迎荣耀等级大于该值的观众，0=欢迎所有人"><div class="hint">仅欢迎荣耀等级大于该值的观众；0 = 不限</div></div>' +
    '<div class="field"><label>粉丝灯牌等级限制</label><input id="ad-medal" type="number" value="' + esc(w.fanMedalMin != null ? w.fanMedalMin : 0) + '" data-tip="仅欢迎灯牌等级大于该值的观众，0=不限制"><div class="hint">仅欢迎灯牌等级大于该值的观众；0 = 不限</div></div>' +
    '<div class="field"><label>欢迎间隔限制（秒）</label><input id="ad-rate" type="number" step="0.1" min="0" value="' + esc(w.rate != null ? w.rate : 1) + '" data-tip="两次欢迎之间的最小间隔秒数，0=不限制。防止短时间内刷屏"><div class="hint">0 = 不限制；1 = 至少间隔1秒；0.5 = 至少间隔0.5秒</div></div>' +
    '</div>' +
    '<div class="form-group" style="margin-top:10px;"><label>欢迎语池</label><div class="hint" style="margin:0 0 6px 0;">每行一句，随机使用</div><textarea class="form-control" id="ad-welcome-texts" style="width:100%;height:88px;">' + esc(texts) + '</textarea></div>' +
    '<div class="form-group" style="margin-top:10px;"><label>舰长欢迎语池</label><div class="hint" style="margin:0 0 6px 0;">每行一句，舰长/提督/总督进房时随机使用</div><textarea class="form-control" id="ad-guard-texts" style="width:100%;height:80px;">' + esc(guardTexts) + '</textarea></div>' +
    '</div></div>' +
    '<div class="box box-success">' +
    '<div class="box-header with-border"><h3 class="box-title">感谢礼物 <span class="title-hint">收到礼物后自动发感谢弹幕</span></h3>' +
    '<div class="box-tools"><button class="btn-box-tool" onclick="var b=this.closest(\'.box\');b.classList.toggle(\'collapsed\');this.textContent=b.classList.contains(\'collapsed\')?\'+\':\'−\'">−</button></div></div>' +
    '<div class="box-body">' +
    '<p class="muted" style="margin-top:0;">收到礼物延迟等待；期间同 UID 送同名礼物会刷新等待并合并数量，避免连点刷屏。</p>' +
    '<div class="form-grid">' +
    '<div class="field"><label>启用感谢</label>' + switchHtml('ad-thank-cb', th.enabled !== false, '', '收到礼物时自动发送一条感谢弹幕') + '</div>' +
    '<div class="field"><label>合并等待（毫秒）</label><input id="ad-wait" type="number" value="' + esc(th.waitMs || 3000) + '" data-tip="等待期内同一用户连续送的多个礼物合并为一条感谢弹幕，单位毫秒"><div class="hint">等待期内连点的礼物合并为一条感谢</div></div>' +
    '<div class="field" style="grid-column:1/-1;"><label>感谢语池</label><div class="hint" style="margin:0 0 6px 0;">每行一条随机用；可用占位符 {uname} 昵称、{giftSummary} 礼物汇总（如"小花花 5个"）、{giftName} 单礼物名、{num} 数量</div><textarea class="form-control" id="ad-thank-texts" style="width:100%;height:110px;">' + esc(((th.texts && th.texts.join('\n')) || th.text || '')) + '</textarea></div>' +
    '</div></div>' +
    '<div class="box box-info">' +
    '<div class="box-header with-border"><h3 class="box-title">感谢关注 <span class="title-hint">有人关注直播间时自动发感谢弹幕</span></h3>' +
    boxToolBtn('ad-follow') + '</div>' +
    '<div class="box-body">' +
    '<p class="muted" style="margin-top:0;">同一位观众<b>本场只感谢一次</b>：感谢过的 UID 会记录在内存里（取关再关注也不重复），<b>关闭程序自动清空</b>。已记录 <span id="ad-follow-count">0</span> 位。</p>' +
    '<div class="form-grid">' +
    '<div class="field"><label>启用感谢关注</label>' + switchHtml('ad-follow-cb', !!fl.enabled, '', '有观众关注直播间时自动发送一条感谢弹幕') + '</div>' +
    '<div class="field"><label>感谢间隔（秒）</label><input id="ad-follow-rate" type="number" step="0.5" min="0" value="' + esc(fl.rate != null ? fl.rate : 1) + '" data-tip="两次感谢之间的最小间隔秒数，0=不限制。防止短时间多人关注刷屏"><div class="hint">0 = 不限制；防止多人同时关注刷屏</div></div>' +
    '</div>' +
    '<div class="form-group" style="margin-top:10px;"><label>感谢语池</label><div class="hint" style="margin:0 0 6px 0;">每行一句随机用，<code>{uname}</code> = 昵称占位</div><textarea class="form-control" id="ad-follow-texts" style="width:100%;height:88px;">' + esc(((fl.texts && fl.texts.join('\n')) || '感谢 {uname} 的关注～')) + '</textarea></div>' +
    '</div></div>' +
    '<div class="box box-warning">' +
    '<div class="box-header with-border"><h3 class="box-title">定时弹幕 <span class="title-hint">按间隔自动发弹幕活跃气氛</span></h3>' +
    '<div class="box-tools"><button class="btn-box-tool" onclick="var b=this.closest(\'.box\');b.classList.toggle(\'collapsed\');this.textContent=b.classList.contains(\'collapsed\')?\'+\':\'−\'">−</button></div></div>' +
    '<div class="box-body">' +
    '<p class="muted" style="margin-top:0;">每 N 秒从弹幕池随机发送一条。需<u>登录态 Cookie</u> 且已连接房间。</p>' +
    '<div class="form-grid">' +
    '<div class="field"><label>启用定时弹幕</label>' + switchHtml('ad-timer-cb', !!tm.enabled, '', '按设定间隔自动从弹幕池中随机选一条发送到直播间') + '</div>' +
    '<div class="field"><label>发送间隔（秒）</label><input id="ad-interval" type="number" min="5" value="' + esc(tm.intervalSec != null ? tm.intervalSec : 60) + '" data-tip="两条定时弹幕之间的间隔秒数，最小5秒"><div class="hint">默认 60 秒一条</div></div>' +
    '</div>' +
    '<div class="form-group" style="margin-top:10px;"><label>弹幕池</label><div class="hint" style="margin:0 0 6px 0;">每行一条，随机发送</div><textarea class="form-control" id="ad-timer-texts" style="width:100%;height:110px;">' + esc((tm.texts || []).join('\n')) + '</textarea></div>' +
    '</div></div>';
  // v1.1.9: no manual save button — changes auto-save (already wired below)
  ['ad-welcome-cb','ad-honor','ad-medal','ad-rate','ad-thank-cb','ad-wait','ad-timer-cb','ad-interval','ad-follow-cb','ad-follow-rate'].forEach(function(id){
    var el = $('#' + id);
    if (el) el.addEventListener('change', function() { saveAutoDanmu(true); });
  });
  // 感谢语池 textarea：停止输入 1.2s 自动保存
  ['ad-follow-texts'].forEach(function(id){
    var el = $('#' + id);
    if (el) el.addEventListener('input', function(){
      clearTimeout(saveAutoDanmu._flTimer);
      saveAutoDanmu._flTimer = setTimeout(function(){ saveAutoDanmu(true); }, 1200);
    });
  });
  // 感谢关注计数轮询（元素存在时每 5 秒刷新）
  if (!renderAutoDanmuPage._fsTimer) {
    renderAutoDanmuPage._fsTimer = setInterval(function(){
      const el = document.getElementById('ad-follow-count');
      if (!el) return;
      g('/api/autodanmu/follow-stats').then(function(s){ el.textContent = s.count || 0; }).catch(function(){});
    }, 5000);
  }
  g('/api/autodanmu/follow-stats').then(function(s){ const el = document.getElementById('ad-follow-count'); if (el) el.textContent = s.count || 0; }).catch(function(){});
}
async function saveAutoDanmu(silent){
  const c = state.config;
  if (!c.autoDanmu) c.autoDanmu = {};
  const on = (id) => !!(($('#' + id) || {}).checked);
  const toLines = (id) => (($('#' + id) || {}).value || '').split('\n').map(s => s.trim()).filter(Boolean);
  const wLines = toLines('ad-welcome-texts');
  const gLines = toLines('ad-guard-texts');
  c.autoDanmu.welcome = {
    enabled: on('ad-welcome-cb'),
    honorLevelMin: Number(($('#ad-honor')||{}).value) || 0,
    fanMedalMin: Number(($('#ad-medal')||{}).value) || 0,
    rate: Number(($('#ad-rate')||{}).value) || 0,
    texts: wLines.length ? wLines : ['欢迎 {uname} 来到直播间～'],
    guardTexts: gLines.length ? gLines : ['欢迎 {uname} 舰长光临直播间！'],
    text: wLines[0] || '欢迎 {uname} 来到直播间～'
  };
  const tLines = toLines('ad-thank-texts');
  c.autoDanmu.thank = {
    enabled: on('ad-thank-cb'),
    waitMs: Number(($('#ad-wait')||{}).value) || 3000,
    texts: tLines.length ? tLines : ['感谢 {uname} 送的 {giftName} x{num}～'],
    text: tLines[0] || '感谢 {uname} 送的 {giftName} x{num}～'
  };
  const tmLines = toLines('ad-timer-texts');
  c.autoDanmu.timer = {
    enabled: on('ad-timer-cb'),
    intervalSec: Math.max(1, Number(($('#ad-interval')||{}).value) || 60),
    texts: tmLines.length ? tmLines : ['欢迎来到直播间，喜欢主播的点点关注～']
  };
  // v1.1.9 感谢关注
  const fLines = toLines('ad-follow-texts');
  c.autoDanmu.follow = {
    enabled: on('ad-follow-cb'),
    rate: Math.max(0, Number(($('#ad-follow-rate')||{}).value) || 0),
    texts: fLines.length ? fLines : ['感谢 {uname} 的关注～']
  };
  try {
    const saved = await post('/api/config', c);
    state.config = saved;
    if (!silent) { flashSaved($('#ad-save')); toast('✅ 自动弹幕设置已保存'); }
    else autosaveHint();
  } catch (e) {
    toast('❌ 保存失败: ' + (e && e.message || e), false);
  }
}

// ---------- 模拟测试页（调试用：不花钱验证完整链路）----------
function dbgSw(label, on) {
  return '<div class="field"><label>' + esc(label) + '</label><div class="btn sm" style="cursor:default;">' + (on ? '✅ 开启' : '⛔ 关闭') + '</div></div>';
}
function dbgGiftName() {
  const el = $('#dbg-giftname');
  return (el && el.value || '').trim() || '小花花';
}
async function sendDebugEvent(type, extra) {
  const unameEl = $('#dbg-uname');
  const uname = (unameEl && unameEl.value || '').trim();
  const payload = Object.assign({ type: type, uname: uname }, extra || {});
  try {
    await post('/api/debug/event', payload);
    toast('✅ 已注入「' + type + '」事件');
  } catch (e) {
    toast('❌ 注入失败: ' + (e && e.message || e), false);
  }
}
async function loadDebugStats() {
  const box = $('#dbg-stats');
  if (!box) return;
  try {
    const d = await g('/api/debug/stats');
    const rows = d.stats || [];
    const conn = $('#dbg-conn');
    if (conn) conn.textContent = (d.connected ? '✅ 已连接' : '⛔ 未连接') + (d.roomId ? (' · 房间 ' + d.roomId) : '');
    box.innerHTML = rows.length
      ? rows.map(function (r) {
          return '<div class="fs-row"><span>' + esc(r.type) + '</span><span class="spacer"></span><span>× ' + r.count + '</span><span style="opacity:.55;margin-left:14px;">' + esc(String(r.sample || '').slice(0, 18)) + '</span></div>';
        }).join('')
      : '<p class="muted" style="margin:0;">尚未收到任何事件。请先连接直播间，再回来看这张表。</p>';
  } catch (e) {
    box.innerHTML = '<p class="muted" style="margin:0;">读取失败：' + esc((e && e.message) || e) + '</p>';
  }
}
async function renderDebugPage(){
  if (!state.config) state.config = await g('/api/config');
  const c = state.config || {};
  const tts = c.tts || {};
  const ad = c.autoDanmu || {};
  const w = ad.welcome || {};
  const th = ad.thank || {};
  $('#topTitle').textContent = '模拟测试';
  $('#topSub').textContent = '模拟 B 站事件，走真实处理链路，不花钱也能测';
  $('#topActions').innerHTML = '<button class="btn" id="dbg-refresh">刷新诊断</button>';
  const nums = [1, 10, 20, 100, 111, 101, 1000];
  $('#content').innerHTML =
    '<div class="card"><div class="card-title">开关状态自检</div>' +
    '<p class="muted" style="margin-top:0;">任一开关未开，对应功能就不会有反应。动手测试前先在这里确认一遍。</p>' +
    '<div class="form-grid">' +
    dbgSw('语音念弹幕（总开关）', tts.enabled) +
    dbgSw('自动弹幕 · 欢迎', w.enabled) +
    dbgSw('自动弹幕 · 感谢礼物', th.enabled) +
    '<div class="field"><label>欢迎过滤：荣耀等级需 &gt;</label><input value="' + esc(w.honorLevelMin != null ? w.honorLevelMin : 0) + '" disabled></div>' +
    '<div class="field"><label>欢迎过滤：灯牌等级需 &gt;</label><input value="' + esc(w.fanMedalMin != null ? w.fanMedalMin : 0) + '" disabled></div>' +
    '</div>' +
    '<p class="muted" style="margin-bottom:0;">⚠️ 上面两个过滤值若 <b>大于 0</b>，普通新观众（0 级）会被直接跳过、不欢迎。想让所有人都被欢迎，请到「自动弹幕」页把这两项改成 <b>0</b> 并保存。</p>' +
    '</div>' +
    '<div class="card"><div class="card-title">模拟送礼（验证数量念法 + 感谢弹幕）</div>' +
    '<p class="muted" style="margin-top:0;">点击后应立即听到语音播报；若已开启「感谢礼物」，约 ' + esc(String(th.waitMs || 3000)) + ' 毫秒后直播间会收到感谢弹幕（需已连接房间且 Cookie 有效）。</p>' +
    '<div class="form-grid">' +
    '<div class="field"><label>送礼人昵称</label><input id="dbg-uname" value="测试观众" data-tip="模拟送礼事件的发送者昵称"></div>' +
    '<div class="field"><label>礼物名称</label><input id="dbg-giftname" value="小花花" data-tip="模拟送礼的礼物名"></div>' +
    '</div>' +
    '<div class="row" style="margin-top:12px;flex-wrap:wrap;gap:8px;">' +
    nums.map(function (n) { return '<button class="btn" data-gift="' + n + '" data-tip="模拟发送' + n + '个礼物事件，用于测试感谢弹幕等功能">送出 ' + n + ' 个</button>'; }).join('') +
    '</div>' +
    '<div class="row" style="margin-top:10px;gap:8px;align-items:center;">' +
    '<input id="dbg-num" type="number" value="7" style="width:120px;">' +
    '<button class="btn primary" id="dbg-gift-custom" data-tip="按输入框中的数量模拟送礼事件">送出自定义数量</button>' +
    '</div>' +
    '</div>' +
    '<div class="card"><div class="card-title">模拟其他事件</div>' +
    '<div class="row" style="flex-wrap:wrap;gap:8px;">' +
    '<button class="btn" id="dbg-interact" data-tip="模拟一个观众进房事件，用于测试欢迎弹幕">👋 有人进直播间</button>' +
    '<button class="btn" id="dbg-follow" data-tip="模拟一个关注事件（固定UID），用于测试感谢关注：开两次第二下应被去重跳过">⭐ 有人关注</button>' +
    '<button class="btn" id="dbg-guard" data-tip="模拟一个舰长进房事件">👑 舰长进场</button>' +
    '<button class="btn" id="dbg-danmu" data-tip="模拟一条弹幕事件，用于测试语音念弹幕">💬 发一条弹幕</button>' +
    '<button class="btn" id="dbg-sc" data-tip="模拟一条醒目留言(SC)事件">💰 醒目留言</button>' +
    '</div>' +
    '<p class="muted" style="margin-bottom:0;">「有人进直播间」默认每次用不同 UID，以规避 60 秒欢迎冷却；要测冷却本身，请用下面的固定 UID。「有人关注」用固定 UID——开启「感谢关注」后连点两次，第二次应被去重跳过（本场只感谢一次）。</p>' +
    '<div class="row" style="margin-top:10px;gap:8px;align-items:center;">' +
    '<input id="dbg-uid" placeholder="固定 UID（可选，留空=随机）" style="width:220px;" data-tip="使用指定UID发送模拟事件，留空则随机生成">' +
    '<button class="btn" id="dbg-interact-fixed" data-tip="用上方输入的固定UID模拟进房事件">用固定 UID 进直播间</button>' +
    '</div>' +
    '</div>' +
    '<div class="card"><div class="card-title">事件到达诊断</div>' +
    '<p class="muted" style="margin-top:0;">这张表统计<b>真实从 B 站收到</b>的事件。若「interact」始终为 0，说明 B 站没有推送进房事件，那是数据源问题，不是程序逻辑问题。</p>' +
    '<div class="field"><label>连接状态</label><div id="dbg-conn" class="btn sm" style="cursor:default;">读取中…</div></div>' +
    '<div id="dbg-stats" style="margin-top:10px;"></div>' +
    '</div>';

  $$('#content [data-gift]').forEach(function (btn) {
    btn.addEventListener('click', function () { sendDebugEvent('gifts', { giftName: dbgGiftName(), num: Number(btn.getAttribute('data-gift')) }); });
  });
  const bind = function (id, type, extra) {
    const el = $('#' + id);
    if (el) el.addEventListener('click', function () { sendDebugEvent(type, extra); });
  };
  bind('dbg-interact', 'interact');
  bind('dbg-follow', 'follow');
  bind('dbg-guard', 'guard');
  bind('dbg-danmu', 'danmu', { msg: '这是一条测试弹幕' });
  bind('dbg-sc', 'superchat', { msg: '这是一条测试醒目留言', price: 30 });
  const custom = $('#dbg-gift-custom');
  if (custom) custom.addEventListener('click', function () { sendDebugEvent('gifts', { giftName: dbgGiftName(), num: Number(($('#dbg-num') || {}).value) || 1 }); });
  const fixed = $('#dbg-interact-fixed');
  if (fixed) fixed.addEventListener('click', function () {
    const uid = (($('#dbg-uid') || {}).value || '').trim();
    sendDebugEvent('interact', uid ? { uid: uid } : {});
  });
  const refresh = $('#dbg-refresh');
  if (refresh) refresh.addEventListener('click', loadDebugStats);
  loadDebugStats();
}

// ---------- PK / 连线 设置页 ----------
async function renderPkPage(){
  if (!state.config) state.config = await g('/api/config');
  const c = state.config;
  const pk = c.pk || {};
  $('#topTitle').textContent = 'PK / 连线 对方信息';
  $('#topSub').textContent = '对方房间 在线/粉丝/舰长/PK分';
  $('#topActions').innerHTML = '';
  $('#content').innerHTML =
    '<section class="content-header"><h1>PK / 连线 <small>对方房间信息</small></h1>' +
    '<ol class="breadcrumb"><li><a href="#/settings">首页</a></li><li class="active">PK/连线</li></ol></section>' +
    connReadyBanner() +
    '<div class="box box-primary">' +
    '<div class="box-header with-border"><h3 class="box-title">对方直播间信息 <span class="title-hint">连线时实时显示对方在线/粉丝/舰长/PK分</span></h3></div>' +
    '<div class="box-body">' +
    '<div class="form-grid">' +
    '<div class="field"><label>启用抓取</label>' + switchHtml('pk-enabled-cb', pk.enabled !== false, '', 'PK/连线期间自动抓取对方直播间的在线人数、舰长数等数据') + '<div class="hint">PK/连线期间自动抓取对方房间数据</div></div>' +
    '<div class="field"><label>刷新间隔（毫秒）</label><input id="pk-refresh" type="number" value="' + esc(pk.refreshMs || 8000) + '" data-tip="抓取对方房间数据的间隔毫秒数，默认8000毫秒"><div class="hint">默认 8000 毫秒</div></div>' +
    '<div class="field"><label>悬浮球显示对方信息</label>' + switchHtml('pk-ball-cb', pk.showOnFloating !== false, '', '在直播悬浮球上展示对方房间数据，方便对比') + '<div class="hint">在直播悬浮球上展示对方数据</div></div>' +
    '</div>' +
    '<div class="row" style="margin-top:12px;"><span class="muted">对方房间号（自动抓取失败时可手动填）：</span><input id="pk-target" class="form-control mono" placeholder="对方房间号" style="width:160px;display:inline-block;" data-tip="手动输入对方直播间号，点击设为PK对手立即抓取一次"><button class="btn btn-default sm" onclick="setPkTarget()" data-tip="将该房间号设为PK对手并立即抓取数据">设为PK对手</button></div>' +
    '<div id="pk-info" class="muted" style="margin-top:12px;font-size:13px;">获取中…</div>' +
    '</div></div>' +
    '<div id="monitor-card-holder">' + renderMonitorCard() + '</div>';
  // v1.1.9: no manual save button — changes auto-save (wired below)
  ['pk-enabled-cb','pk-refresh','pk-ball-cb'].forEach(function(id){
    var el = $('#' + id);
    if (el) el.addEventListener('change', function() { savePkPage(true); });
  });
  startPkPoll();
  // v1.1.9: room monitor card (data arrives via WS push + 20s fallback poll)
  wireMonitorCard();
  refreshMonitor();
  if (!renderPkPage._monTimer) {
    renderPkPage._monTimer = setInterval(function(){
      if (currentRoute() !== '#/pk') return;
      refreshMonitor();
    }, 20000);
  }
}
async function savePkPage(silent){
  const c = state.config;
  const on = (id) => !!(($('#' + id) || {}).checked);
  c.pk = { enabled: on('pk-enabled-cb'), refreshMs: Number(($('#pk-refresh')||{}).value) || 8000, showOnFloating: on('pk-ball-cb') };
  try {
    const saved = await post('/api/config', c);
    state.config = saved;
    if (!silent) { flashSaved($('#pk-save')); toast('PK设置已保存'); }
    else autosaveHint();
  } catch (e) { toast('❌ 保存失败: ' + (e && e.message || e), false); }
}

// ---------- 日志视图 ----------
async function renderLogView(type){
  const meta = TYPE_META[type];
  $('#topTitle').textContent = meta.title;
  $('#topSub').textContent = '查看 / 筛选 ' + meta.title;
  const st = state.status || {};
  $('#topActions').innerHTML =
    '<button class="btn sm button-record" data-type="' + type + '" data-tip="开始或停止记录该类型事件到文件。开始后实时写入，停止后保存">记录开关</button>' +
    '<button class="btn sm" onclick="openFolder(\'' + type + '\')" data-tip="打开该类型记录文件的保存目录">📁 打开文件夹</button>' +
    '<button class="btn sm" onclick="exportHtml(\'' + type + '\')" data-tip="将记录导出为HTML文件查看，或刷新当前HTML视图">⬇ 导出/刷新HTML</button>';

  // 未连接提示：日志依赖连接
  const connBanner = (st.state !== 'connected')
    ? '<div class="banner warn"><span class="bn-icon">📺</span><div class="bn-body">当前未连接直播间，不会有新记录产生。' +
      '<div class="bn-actions"><button class="btn sm primary" onclick="location.hash=\'#/settings\'">去连接直播间</button></div></div></div>'
    : '';

  $('#content').innerHTML =
    connBanner +
    '<div class="card"><div class="card-title">筛选<span class="title-hint">全部条件可组合使用，留空即不限制</span></div>' +
    '<div class="row">' +
    '<input id="f-uid" placeholder="用户UID" style="width:140px;height:34px;border-radius:4px;border:1px solid var(--line);background:rgba(255,255,255,.07);color:var(--text);padding:0 10px;" data-tip="只显示该UID用户的记录，留空=不筛选">' +
    '<input id="f-q" placeholder="搜索 用户名/内容" style="width:220px;height:34px;border-radius:4px;border:1px solid var(--line);background:rgba(255,255,255,.07);color:var(--text);padding:0 10px;" data-tip="按用户名或内容关键词搜索记录">' +
    '<input id="f-start" type="datetime-local" placeholder="开始时间" style="height:34px;border-radius:4px;border:1px solid var(--line);background:rgba(255,255,255,.07);color:var(--text);padding:0 10px;" data-tip="筛选指定时间范围内的记录">' +
    '<input id="f-end" type="datetime-local" placeholder="结束时间" style="height:34px;border-radius:4px;border:1px solid var(--line);background:rgba(255,255,255,.07);color:var(--text);padding:0 10px;" data-tip="筛选指定时间范围内的记录">' +
    '<button class="btn primary" onclick="doQuery()">查询</button>' +
    '<button class="btn" onclick="resetQuery()">重置</button>' +
    '</div></div>' +
    '<div id="log-body"></div>';

  refreshRecordBtn(type);
  const br = $('#topActions .button-record');
  if (br) br.addEventListener('click', () => toggleRecord(type));
  doQuery(type);
}

function refreshRecordBtn(type){
  const on = state.config && state.config.recording && state.config.recording[type];
  const btn = $('#topActions .button-record');
  if (btn){ btn.textContent = on ? '⏸ 停止记录' : '▶ 开始记录'; btn.classList.toggle('primary', !!on); }
  // 侧栏徽章同步
  const badge = document.querySelector('.nav-rec-badge[data-rec-type="' + type + '"]');
  if (badge){
    badge.classList.toggle('on', !!on);
    badge.classList.toggle('off', !on);
    badge.textContent = on ? '记录中' : '停用';
    badge.title = on ? '记录中 · 点击停止记录' : '已停用 · 点击开始记录';
  }
}
async function toggleRecord(type){
  const on = !(state.config.recording && state.config.recording[type]);
  const r = await post('/api/recording/' + type, { on: on });
  if (!state.config.recording) state.config.recording = {};
  state.config.recording[type] = r.on;
  refreshRecordBtn(type);
  toast((r.on ? '已开始' : '已停止') + TYPE_NAMES[type] + '记录');
}
async function exportHtml(type){ await post('/api/export/' + type); toast('HTML 已更新'); }

async function doQuery(type){
  type = type || (window._logType || 'danmu');
  window._logType = type;
  const q = new URLSearchParams();
  const uid = ($('#f-uid') || {}).value;
  const kq = ($('#f-q') || {}).value;
  const start = ($('#f-start') || {}).value;
  const end = ($('#f-end') || {}).value;
  q.set('page', (state.queryState[type] && state.queryState[type].page) || 1);
  q.set('size', 50);
  if (uid) q.set('uid', uid);
  if (kq) q.set('q', kq);
  if (start) q.set('start', start.replace('T',' '));
  if (end) q.set('end', end.replace('T',' '));
  const res = await g('/api/logs/' + type + '?' + q.toString());
  renderLogTable(type, res);
  window._logTotal = res.total;
  window._logPage = res.page;
}
function resetQuery(){
  $('#f-uid').value=''; $('#f-q').value=''; $('#f-start').value=''; $('#f-end').value='';
  state.queryState[window._logType] = { page: 1 };
  doQuery(window._logType);
}
function gotoPage(p){
  state.queryState[window._logType] = { page: p };
  // 保留筛选
  doQuery(window._logType);
}

function renderLogTable(type, res){
  const meta = TYPE_META[type];
  const rows = res.rows || [];
  const thead = meta.cols.map(c => '<th>' + c[1] + '</th>').join('');
  const recOn = state.config && state.config.recording && state.config.recording[type];
  const connOk = state.status && state.status.state === 'connected';
  let emptyCell;
  if (!connOk){
    emptyCell = '<div class="empty"><span class="empty-icon">📺</span>未连接直播间，连接并开启记录后此处将显示数据' +
      '<div class="empty-action"><button class="btn sm primary" onclick="location.hash=\'#/settings\'">去连接直播间</button></div></div>';
  } else if (!recOn){
    emptyCell = '<div class="empty"><span class="empty-icon">⏸</span>已连接，但' + TYPE_NAMES[type] + '记录未开启' +
      '<div class="empty-action"><button class="btn sm primary" onclick="toggleRecord(window._logType)">▶ 开始记录</button></div></div>';
  } else {
    emptyCell = '<div class="empty"><span class="empty-icon">📭</span>记录已开启，暂无数据。等待观众互动后此处将显示</div>';
  }
  const tbody = rows.length
    ? rows.map(r => '<tr>' + meta.cols.map(c => '<td' + (c[0].indexOf('元')>=0 ? ' class="num"' : '') + '>' + esc(cell(r, c[0])) + '</td>').join('') + '</tr>').join('')
    : '<tr><td colspan="' + meta.cols.length + '">' + emptyCell + '</td></tr>';
  const totalPages = Math.max(1, Math.ceil(res.total / res.size));
  $('#log-body').innerHTML =
    '<div class="tbl-wrap"><table class="tbl"><thead><tr>' + thead + '</tr></thead><tbody>' + tbody + '</tbody></table></div>' +
    '<div class="pager"><button class="btn sm" onclick="gotoPage(' + (res.page - 1) + ')" ' + (res.page <= 1 ? 'disabled' : '') + '>上一页</button>' +
    '<span class="info">第 ' + res.page + ' / ' + totalPages + ' 页 · 共 ' + res.total + ' 条</span>' +
    '<button class="btn sm" onclick="gotoPage(' + (res.page + 1) + ')" ' + (res.page >= totalPages ? 'disabled' : '') + '>下一页</button></div>';
}
function cell(r, k){
  const v = r[k];
  if (v === undefined || v === null) return '';
  if (k === 'value' || k === 'cost' || k === 'income' || k === 'profit' || k === 'price') return fmtMoney(v);
  return v;
}

// ---------- 礼物截图生成 ----------
function renderGiftTool(){
  $('#topTitle').textContent = '礼物截图生成';
  $('#topSub').textContent = '生成可分享的礼物卡片图片';
  $('#topActions').innerHTML = '';
  $('#content').innerHTML =
    '<div class="card" style="max-width:760px;"><div class="card-title">礼物截图生成</div>' +
    '<div class="form-grid" style="grid-template-columns:repeat(auto-fill,minmax(200px,1fr));">' +
    '<div class="field"><label>用户名</label><input id="g-uname" value="" data-tip="送礼观众的昵称，显示在截图上"></div>' +
    '<div class="field"><label>用户UID</label><input id="g-uid" value="" data-tip="送礼观众的UID"></div>' +
    '<div class="field"><label>礼物名称</label><input id="g-name" value="" data-tip="礼物名称，显示在截图上"></div>' +
    '<div class="field"><label>数量</label><input id="g-num" type="number" value="1" min="1" data-tip="礼物数量"></div>' +
    '<div class="field"><label>单价(金瓜子)</label><input id="g-price" type="number" value="1000" data-tip="单个礼物的金瓜子价格，用于计算总价"><div class="hint">1 元 = 1000 金瓜子（B站站内换算）</div></div>' +
    '<div class="field"><label>备注/时间</label><input id="g-note" value="" data-tip="显示在截图上的附加文字，通常为时间"></div>' +
    '</div>' +
    '<div class="row" style="margin-top:16px;"><button class="btn primary" onclick="genGift()" data-tip="根据填写的信息生成礼物截图预览">生成预览</button>' +
    '<button class="btn" onclick="downloadGift()" data-tip="将生成的截图保存为PNG图片">⬇ 下载PNG</button>' +
    '<button class="btn" onclick="fillFromRecent()" data-tip="自动从最近收到的礼物事件中填充表单，省去手动输入">从最新礼物填入</button></div></div>' +
    '<div class="card" id="gift-out"><div class="card-title">预览</div><div id="gift-preview-wrap"></div></div>';
  // 默认从最近礼物填入（无最近数据则使用默认值）
  fillFromRecent(true);
  // 无论有无最近数据，都显示一个默认预览
  genGift();
}

function layoutGift(){
  const uname = $('#g-uname').value || '匿名观众';
  const giftName = $('#g-name').value || '辣条';
  const num = +$('#g-num').value || 1;
  const price = +$('#g-price').value || 0;
  const note = $('#g-note').value || '';
  const value = num * price / 1000;
  const now = new Date();
  const timeStr = (note || now.toLocaleString('zh-CN', { hour12:false }));
  return { uname, giftName, num, value, timeStr };
}

function drawGiftCard(canvas, d){
  const W = 720, H = 380;
  canvas.width = W; canvas.height = H;
  const ctx = canvas.getContext('2d');
  const grad = ctx.createLinearGradient(0, 0, W, H);
  grad.addColorStop(0, '#3b2a5e'); grad.addColorStop(1, '#1c2b4e');
  ctx.fillStyle = grad; ctx.fillRect(0, 0, W, H);
  // 装饰圆
  ctx.globalAlpha = .10; ctx.fillStyle = '#fff';
  ctx.beginPath(); ctx.arc(W-70, 40, 120, 0, Math.PI*2); ctx.fill();
  ctx.globalAlpha = 1;
  // 礼物图标(方形)
  ctx.fillStyle = 'rgba(255,255,255,.14)';
  ctx.strokeStyle = 'rgba(255,255,255,.3)';
  ctx.lineWidth = 2;
  roundRect(ctx, 40, 60, 96, 96, 18); ctx.fill(); ctx.stroke();
  ctx.fillStyle = '#ffe08a'; ctx.font = 'bold 52px sans-serif'; ctx.textAlign = 'center'; ctx.textBaseline = 'middle';
  ctx.fillText('🎁', 88, 108);
  // 礼物名称
  ctx.fillStyle = '#fff'; ctx.font = 'bold 32px "PingFang SC", sans-serif'; ctx.textAlign = 'left';
  ctx.fillText(d.giftName, 160, 90);
  ctx.fillStyle = 'rgba(255,255,255,.7)'; ctx.font = '20px sans-serif';
  ctx.fillText(d.uname + ' 送出', 160, 130);
  // 数量 & 金额
  ctx.fillStyle = '#ffe08a'; ctx.font = 'bold 52px sans-serif';
  ctx.fillText('×' + d.num, 160, 200);
  ctx.fillStyle = '#fff'; ctx.font = 'bold 30px sans-serif';
  ctx.fillText('价值 ¥' + d.value.toFixed(2), 160, 268);
  // 底部
  ctx.fillStyle = 'rgba(255,255,255,.55)'; ctx.font = '18px sans-serif';
  ctx.fillText('@B站直播间 · ' + d.timeStr, 40, 330);
  // 角标
  ctx.fillStyle = '#63e2b7'; ctx.beginPath(); ctx.arc(W-52, H-52, 20, 0, Math.PI*2); ctx.fill();
  ctx.fillStyle = '#000'; ctx.font = 'bold 18px sans-serif'; ctx.textAlign = 'center';
  ctx.fillText('B', W-52, H-50-2);
}
function roundRect(ctx, x, y, w, h, r){ ctx.beginPath(); ctx.moveTo(x+r, y); ctx.arcTo(x+w, y, x+w, y+h, r); ctx.arcTo(x+w, y+h, x, y+h, r); ctx.arcTo(x, y+h, x, y, r); ctx.arcTo(x, y, x+w, y, r); ctx.closePath(); }

function genGift(){
  const d = layoutGift();
  const wrap = $('#gift-preview-wrap');
  wrap.innerHTML = '<canvas id="gift-canvas"></canvas>';
  drawGiftCard($('#gift-canvas'), d);
}
function downloadGift(){
  const d = layoutGift();
  if (!$('#gift-canvas')) genGift();
  const c = $('#gift-canvas');
  drawGiftCard(c, d);
  const a = document.createElement('a');
  a.download = (d.giftName + '-' + d.uname + '-礼物.png');
  a.href = c.toDataURL('image/png');
  a.click();
  toast('已下载礼物截图');
}
async function fillFromRecent(silent){
  try {
    const res = await g('/api/recent?type=gifts');
    const ev = (res.events || [])[res.events.length - 1];
    if (!ev) { if (!silent) toast('暂无礼物记录'); return; }
    $('#g-uname').value = ev.uname || '';
    $('#g-uid').value = ev.uid || '';
    $('#g-name').value = ev.giftName || '';
    $('#g-num').value = ev.num || 1;
    $('#g-price').value = ev.price || 0;
    $('#g-note').value = ev.time || '';
    genGift();
  } catch (e) {}
}

// ---------- 实时展示 ----------
function renderRealtime(){
  $('#topTitle').textContent = '实时展示';
  $('#topSub').textContent = '实时弹幕 / 礼物 / 舰长 / 醒目留言 / 盲盒';
  $('#topActions').innerHTML = '';
  $('#content').innerHTML =
    connReadyBanner() +
    '<div class="card"><div class="tabs" id="rt-tabs">' +
    TYPES.map((t, i) => '<div class="tab' + (i===0?' active':'') + '" data-tab="' + t + '">' + TYPE_NAMES[t] + '</div>').join('') +
    '</div><div class="row" style="justify-content:space-between;"><span class="muted" id="rt-status"></span><span class="muted" id="rt-count"></span></div>' +
    '<div class="rt-list" id="rt-list" style="margin-top:12px;"></div></div>';
  window._rtTab = 'danmu';
  $$('#rt-tabs .tab').forEach(tab => tab.addEventListener('click', () => {
    $$('#rt-tabs .tab').forEach(t => t.classList.remove('active'));
    tab.classList.add('active');
    window._rtTab = tab.getAttribute('data-tab');
    renderRtList();
  }));
  connectRealtime();
  rtStatusText();
}
function connectRealtime(){
  if (state.ws && state.ws.readyState === 1) return;
  // file:// 下 location.host 为空，需用绝对地址连 127.0.0.1:7360
  // file:// 下 location.host 为空，用绝对地址连 7360
  const host = location.host || '127.0.0.1:7360';
  const proto = location.protocol === 'https:' ? 'wss' : 'ws';
  const ws = new WebSocket(proto + '://' + host + '/ws');
  state.ws = ws;
  ws.onmessage = (e) => {
    let msg; try { msg = JSON.parse(e.data); } catch (x) { return; }
    if (msg.type === 'event'){
      const ev = msg.data;
      if (ev.type){
        if (!state.recent[ev.type]) state.recent[ev.type] = [];
        state.recent[ev.type].push(ev);
        if (state.recent[ev.type].length > 300) state.recent[ev.type].shift();
        if (ev.type === window._rtTab) renderRtList();
        // v1.1.9: 面板提示音（独立于 TTS）
        try { alertPanelSound(ev); } catch (e) {}
        // 语音念弹幕
        if (window.BLTTTS && state.ttsEnabled !== false) {
          try { window.BLTTTS.handleEvent(ev); } catch (e) {}
        }
      }
    } else if (msg.type === 'song_request'){
      if (window.BLTSong) { try { window.BLTSong.handleWsEvent(msg.data); } catch (e) {} }
    } else if (msg.type === 'monitor'){
      // v1.1.9: 房间监控数据推送
      _monitorCache = msg.data;
      rerenderMonitorCard();
    } else if (msg.type === 'alert_test'){
      // v1.1.9: 测试横幅 → 面板音效也响一声
      try { alertPanelSound(msg.data, true); } catch (e) {}
    } else if (msg.type === 'status'){
      state.status = msg.data;
      rtStatusText();
      updateSideStatus();
      syncConnBarUi(msg.data); // keep settings-page connection bar in sync (e.g. auto-connect when live starts)
    }
  };
  ws.onclose = () => { setTimeout(connectRealtime, 1500); };
  // 加载最近
  TYPES.forEach(t => { g('/api/recent?type=' + t).then(r => { state.recent[t] = r.events || []; if (t === window._rtTab) renderRtList(); }).catch(()=>{}); });
}
function rtStatusText(){
  const el = $('#rt-status');
  if (el){ const st = state.status || {}; el.innerHTML = '<span class="dot ' + dotClass(st.state) + '"></span> ' + stateText(st) + liveInfoBrief(); }
}
// ===== 直播间实时信息（观众数/大航海/top3）=====
var liveInfoData = { online: 0, guardTotal: 0, guardCount: [0,0,0], top3: [], onlineRank: [] };
var liveInfoTimer = null;
function liveInfoBrief() {
  var d = liveInfoData, gc = d.guardCount || [0,0,0];
  if (!d.guardTotal && !d.online) return '';
  var html = ' · 👁' + (d.online || 0) + ' · 🛡<b style="color:#e74c3c">' + gc[0] + '</b>/<b style="color:#9b59b6">' + gc[1] + '</b>/<b style="color:#3498db">' + gc[2] + '</b>';
  var rank = d.onlineRank;
  if (rank && rank.length) {
    html += ' · 🏆';
    for (var i = 0; i < 3; i++) {
      if (i > 0) html += ' ';
      var u = rank[i];
      if (u) { var n = esc(u.uname || ''); if (n.length > 5) n = n.slice(0,5) + '…'; html += '<b>' + n + '</b>(' + u.score + ')'; }
      else html += '<span class="muted">-</span>';
    }
  }
  return html;
}
function liveInfoDetail() {
  var d = liveInfoData, gc = d.guardCount || [0,0,0];
  if (!d.guardTotal && !d.online) return '<span class="muted">等待数据...</span>';
  var html = '👁 观众 <b>' + (d.online || 0) + '</b>';
  html += '  🛡 <b style="color:#e74c3c">' + gc[0] + '</b>/<b style="color:#9b59b6">' + gc[1] + '</b>/<b style="color:#3498db">' + gc[2] + '</b>';
  return html;
}
function liveInfoTop3Html() {
  var d = liveInfoData;
  var rank = d.onlineRank;
  if (rank && rank.length) {
    var html = '🏆 在线榜: ';
    for (var i = 0; i < 3; i++) {
      if (i > 0) html += ' | ';
      var u = rank[i];
      html += u ? ('<b>' + esc(u.uname) + '</b>(' + u.score + ')') : '<span class="muted">暂无</span>';
    }
    return html;
  }
  if (!d.top3 || !d.top3.length) return '';
  var html = '🏆 大航海: ';
  for (var i = 0; i < 3; i++) {
    if (i > 0) html += ' | ';
    var u = d.top3[i];
    html += u ? ('<b>' + esc(u.name) + '</b>(' + u.accompany + '天)') : '<span class="muted">暂无</span>';
  }
  return html;
}
function pollLiveInfo() {
  fetch(apiUrl('/api/room/live-info')).then(function(r){ return r.json(); }).then(function(d){
    liveInfoData = d;
    updateSideStatus(); rtStatusText();
    var el = document.getElementById('connLiveInfo');
    if (el) el.innerHTML = liveInfoDetail();
    var el2 = document.getElementById('connLiveTop3');
    if (el2) el2.innerHTML = liveInfoTop3Html();
  }).catch(function(){});
}
function startLiveInfoPoll() {
  if (liveInfoTimer) clearInterval(liveInfoTimer);
  pollLiveInfo();
  liveInfoTimer = setInterval(pollLiveInfo, 15000);
}
function stopLiveInfoPoll() {
  if (liveInfoTimer) { clearInterval(liveInfoTimer); liveInfoTimer = null; }
  liveInfoData = { online: 0, guardTotal: 0, guardCount: [0,0,0], top3: [], onlineRank: [] };
}
function updateSideStatus(){
  const st = state.status || {};
  const cls = dotClass(st.state);
  const txt = stateText(st);
  if (st.state === 'connected') { if (!liveInfoTimer) startLiveInfoPoll(); }
  else { if (liveInfoTimer) stopLiveInfoPoll(); }
  const el = $('#sideStatus');
  if (el){
    el.innerHTML = '<div><span class="dot ' + cls + '"></span>' + esc(txt) + '</div>' +
      (window.__APP_VERSION ? '<div class="side-version">v' + esc(window.__APP_VERSION) + '</div>' : '');
  }
  // 顶栏常驻连接状态
  const ts = $('#topStatus');
  if (ts){
    ts.className = 'top-status ' + (cls === 'ok' ? 'ok' : (cls === 'err' ? 'err' : ''));
    if (cls === 'warn') ts.classList.add('warn');
    ts.innerHTML = '<span class="dot ' + cls + '"></span>' + esc(txt) + liveInfoBrief();
  }
}
function renderRtList(){
  const list = state.recent[window._rtTab] || [];
  const total = state.recent[window._rtTab] ? state.recent[window._rtTab].length : 0;
  const el = $('#rt-list');
  if (el){
    $('#rt-count').textContent = '当前 ' + window._rtTab + ' 共 ' + total + ' 条';
    if (!list.length){
      const connOk = state.status && state.status.state === 'connected';
      el.innerHTML = connOk
        ? '<div class="empty"><span class="empty-icon">📭</span>已连接，暂无 ' + TYPE_NAMES[window._rtTab] + ' 事件，等待观众互动…</div>'
        : '<div class="empty"><span class="empty-icon">📺</span>未连接直播间，暂无 ' + TYPE_NAMES[window._rtTab] + ' 事件' +
          '<div class="empty-action"><button class="btn sm primary" onclick="location.hash=\'#/settings\'">去连接直播间</button></div></div>';
      return;
    }
    el.innerHTML = list.slice(-200).reverse().map(ev => rtItem(ev)).join('');
  }
}
function rtItem(ev){
  let body = '', sub = '';
  if (ev.type === 'danmu'){ body = ev.msg; }
  else if (ev.type === 'gifts'){ body = ev.giftName + ' ×' + ev.num + ' · ¥' + fmtMoney(ev.value); }
  else if (ev.type === 'blindbox'){ body = ev.giftName + ' ×' + ev.num + ' · 成本 ¥' + fmtMoney(ev.cost) + ' / 收入 ¥' + fmtMoney(ev.income) + ' / 盈亏 ' + fmtMoney(ev.profit); }
  else if (ev.type === 'guard'){ body = ev.levelName + ' ×' + ev.num + ' · ¥' + fmtMoney(ev.value); }
  else if (ev.type === 'superchat'){ body = ev.msg + ' · ¥' + fmtMoney(ev.price); }
  else body = JSON.stringify(ev);
  sub = '';
  return '<div class="rt-item"><span class="badge ' + ev.type + '">' + TYPE_NAMES[ev.type] + '</span>' +
    '<div class="rt-main"><div class="rt-head"><span class="uname">' + esc(ev.uname || '') + '</span><span class="time">' + esc(ev.time || '') + '</span>' + (ev.uid ? '<span class="muted">UID ' + esc(ev.uid) + '</span>' : '') + '</div>' +
    '<div class="rt-body">' + esc(body) + '</div>' + (sub ? '<div class="rt-sub">' + esc(sub) + '</div>' : '') + '</div></div>';
}

// ---------- 状态刷新 ----------
// Sync the settings-page connection bar (if rendered) from a status object
function syncConnBarUi(st){
  const dot = $('#connDot');
  if (!dot || !st) return;
  dot.className = 'dot ' + dotClass(st.state);
  const txt = $('#connStateText');
  if (txt) txt.textContent = stateText(st);
  const pop = $('#connLiveInfo');
  if (pop) pop.innerHTML = liveInfoDetail();
  renderConnSteps(st);
  updateRoomEcho();
}
async function refreshStatus(){
  try { state.status = await g('/api/status'); updateSideStatus(); } catch (e) {}
  // 同步顶部连接状态条（如果设置页已渲染）
  syncConnBarUi(state.status);
}

// ---------- 启动 ----------
function currentRoute(){ return location.hash || '#/settings'; }

// ─── 点歌功能页面 ───
const SONG_PLATFORM_NAMES = { qq: 'QQ音乐', netease: '网易云', kugou: '酷狗', bilibili: 'B站' };
const SONG_PLATFORM_COLORS = { qq: '#12b7f5', netease: '#d43c33', kugou: '#2f7df6', bilibili: '#fb7299' };

function songBadges(song) {
  const pName = SONG_PLATFORM_NAMES[song.platform] || (song.platform || '');
  const pColor = SONG_PLATFORM_COLORS[song.platform] || '#888';
  let html = '';
  if (pName) html += '<span class="sr-badge-platform" style="color:' + pColor + '">' + esc(pName) + '</span>';
  html += song.vip ? '<span class="sr-badge-vip">VIP</span>' : '<span class="sr-badge-free">免费</span>';
  return html;
}

async function renderSongRequestPage() {
  diag('song-page render start');
  if (!state.config) state.config = await g('/api/config');
  const c = state.config;
  const sr = c.songRequest || {};
  const bl = sr.blacklist || [];
  const cookieStatus = await g('/api/song-request/cookie-status').catch(() => ({}));
  const playlistData = await g('/api/song-request/playlist').catch(() => ({}));
  const recentData = await g('/api/song-request/recent').catch(() => ({}));
  const pl = playlistData.playlist || [];
  const curIdx = playlistData.currentIndex != null ? playlistData.currentIndex : -1;
  const recent = recentData.recent || [];

  $('#topTitle').textContent = '🎵 点歌功能';
  $('#topSub').textContent = '弹幕点歌 → 自动搜索 → 播放列表播放';
  $('#topActions').innerHTML = '';

  const blRows = bl.map((b, i) =>
    '<tr><td>' + esc(b.type === 'song' ? '歌曲' : '歌手') + '</td><td>' + esc(b.value) + '</td><td><button class="btn xs danger" onclick="removeBlacklist(' + i + ')">删除</button></td></tr>'
  ).join('');

  const plRows = pl.map((s, i) => {
    const cur = i === curIdx ? '▶ ' : '  ';
    return '<tr class="pl-row" draggable="true" data-index="' + i + '">' +
      '<td>' + cur + esc(s.name) + ' ' + songBadges(s) + '</td><td>' + esc(s.artist || '') + '</td><td>' + esc(s.requester || '') + '</td>' +
      '<td><button class="btn xs" onclick="songPlay(' + i + ')">播放</button> <button class="btn xs danger" onclick="songRemove(' + i + ')">删除</button></td></tr>';
  }).join('');

  const recentRows = recent.slice(0, 20).map(r =>
    '<tr><td>' + new Date(r.time).toLocaleTimeString() + '</td><td>' + esc(r.songName) + '</td><td>' + esc(r.uname) + '</td><td>' + esc(r.status) + '</td></tr>'
  ).join('');

  $('#content').innerHTML =
    connReadyBanner() +
    '<div class="card">' +
    '<h3>点歌功能设置</h3>' +    switchHtml('sr-enabled', sr.enabled, '启用点歌功能', '开启后观众可通过弹幕发送点歌指令来点歌。关闭则忽略所有点歌弹幕') +
    '</div>' +

    '<div class="card">' +
    '<div class="row" style="justify-content:space-between;align-items:center;margin-bottom:8px;flex-wrap:wrap;gap:8px">' +
    '<h3 style="margin:0">播放列表</h3>' +
    '<div class="row" style="align-items:center;gap:8px;flex:0 0 auto">' +
    '<label style="display:flex;align-items:center;gap:4px;cursor:pointer;font-size:13px;color:var(--muted)" data-tip="允许弹幕点歌时选择VIP/付费歌曲。关闭则只选免费可播放的歌曲"><input type="checkbox" id="sr-allowVip" ' + (sr.allowVip ? 'checked' : '') + '> 允许VIP</label>' +
    '<select id="sr-platform" class="input" style="width:auto" data-tip="选择弹幕点歌的搜索来源。不同平台的可播放歌曲范围不同">' +
    '<option value="qq"' + (sr.platform === 'qq' ? ' selected' : '') + '>QQ音乐</option>' +
    '<option value="netease"' + (sr.platform === 'netease' ? ' selected' : '') + '>网易云音乐</option>' +
    '<option value="kugou"' + (sr.platform === 'kugou' ? ' selected' : '') + '>酷狗音乐</option>' +
    '<option value="bilibili"' + (sr.platform === 'bilibili' ? ' selected' : '') + '>B站</option>' +
    '</select>' +
    '<input class="input" id="sr-keyword" value="' + esc(sr.keyword || '点歌') + '" placeholder="触发关键词" style="width:120px" data-tip="弹幕以此关键词开头才识别为点歌指令。如点歌 晴天。修改后需保存生效">' +
    '</div>' +
    '</div>' +
    // QQ 平台的付费歌曲没有 Cookie 拿不到播放地址（免费歌曲不受影响）
    (sr.platform === 'qq' && !((sr.cookies || {}).qq)
      ? '<div class="hint" style="color:var(--warning-bright,#e08e0b);margin:0 0 8px 0;">⚠ 未设置 QQ音乐 Cookie：QQ 平台的付费/热门正版歌拿不到播放地址（列表里带 VIP 标记的都需要 Cookie），免费歌曲可正常播放。需要放 VIP 歌时请在下方「Cookie 授权」里登录或粘贴。</div>'
      : '') +
    '<div class="row">' +
    '<button class="btn" onclick="songPrev()" data-tip="播放上一首歌">⏮ 上一首</button> ' +
    '<button class="btn" id="sr-pause" onclick="songPause()" data-tip="暂停或继续当前歌曲的播放">⏸ 暂停</button> ' +
    '<button class="btn" onclick="songSkip()" data-tip="跳过当前歌曲，播放下一首">⏭ 跳过</button> ' +
    '<button class="btn" onclick="songManualAdd()" data-tip="打开搜索框，手动搜索并添加歌曲到播放列表">+ 手动添加</button> ' +
    '<button class="btn danger" onclick="songClear()" data-tip="清空整个播放列表，不可恢复">清空列表</button>' +
    '</div>' +
    '<div class="row" id="sr-progress-row" style="display:none;align-items:center;gap:8px">' +
    '<span id="sr-cur-time" style="font-size:12px;color:var(--muted);flex:0 0 auto">00:00</span>' +
    '<input type="range" id="sr-progress" min="0" max="100" value="0" step="0.1" style="flex:1;min-width:120px" oninput="onSongSeekInput(this.value)" onchange="onSongSeek(this.value)" data-tip="当前歌曲播放进度，可拖动跳转到指定位置">' +
    '<span id="sr-total-time" style="font-size:12px;color:var(--muted);flex:0 0 auto">00:00</span>' +
    '</div>' +
    '<div class="row" id="sr-search-row" style="display:none">' +
    '<select class="input" id="sr-search-type" style="width:auto;flex:0 0 auto" data-tip="搜索时匹配的字段。智能搜索=综合排序，按歌名/歌手/歌词=精确匹配">' +
    '<option value="smart">智能搜索</option>' +
    '<option value="song">按歌名</option>' +
    '<option value="artist">按歌手</option>' +
    '<option value="lyric">按歌词</option>' +
    '</select> ' +
    '<input class="input" id="sr-search-kw" placeholder="输入关键词..." data-tip="输入歌曲名、歌手名或歌词片段进行搜索"> ' +
    '<button class="btn" id="sr-search-btn" onclick="songSearch()" data-tip="在选定平台上搜索歌曲，结果展示在下方列表中">搜索</button> ' +
    '<button class="btn" onclick="cancelSongSearch()" data-tip="关闭搜索框">取消</button>' +
    '</div>' +
    '<div id="sr-search-results"></div>' +
    '<table class="table"><thead><tr><th>歌名</th><th>歌手</th><th>点歌人</th><th>操作</th></tr></thead><tbody id="sr-playlist-body">' + plRows + '</tbody></table>' +
    '</div>' +

    '<div class="card">' +

    '<h3>Cookie 授权</h3>' +
    '<div class="sr-disclaimer">' +
    '<b>⚠️ 免责声明</b><br>' +
    '本点歌功能需要获取音乐平台 Cookie 用于搜索和播放歌曲。Cookie 仅用于本程序的点歌功能，不会上传至任何服务器或分享给第三方。<br><br>' +
    '获取方式：<br>' +
    '1. 读取本应用已保存的 Cookie（之前通过「应用内登录」获取过）<br>' +
    '2. 在应用内登录音乐平台网页版获取<br>' +
    '3. 手动粘贴 Cookie<br><br>' +
    '请选择平台后点击下方按钮获取 Cookie。' +
    '</div>' +
    (sr.cookieAgreed ?
      '<div class="row"><span style="color:var(--accent)">✅ 已同意免责声明</span></div>' :
      '<div class="row"><button class="btn primary" id="sr-agree-btn" data-tip="确认已阅读免责声明。点歌功能仅用于个人娱乐，请遵守各平台用户协议">我已阅读并同意免责声明</button></div>'
    ) +
    '<div class="row">' +
    '<button class="btn" id="sr-cookie-read"' + (sr.cookieAgreed ? '' : ' disabled') + ' data-tip="从配置中读取已保存的音乐平台Cookie，用于搜索VIP歌曲">📋 读取已存Cookie</button> ' +
    '<button class="btn" id="sr-cookie-login"' + (sr.cookieAgreed ? '' : ' disabled') + ' data-tip="在内置浏览器中打开音乐平台登录页，扫码登录后自动获取Cookie">🌐 应用内登录</button> ' +
    '<button class="btn" id="sr-cookie-paste"' + (sr.cookieAgreed ? '' : ' disabled') + ' data-tip="手动粘贴从浏览器复制的音乐平台Cookie">✏️ 手动粘贴</button> ' +
    '<button class="btn danger" id="sr-cookie-clear"' + (sr.cookieAgreed ? '' : ' disabled') + ' data-tip="清除所有音乐平台的已保存Cookie">🗑 清空Cookie</button>' +
    '</div>' +
    '<div class="row"><label>Cookie 状态：</label>' +
    '<span>QQ音乐: ' + (cookieStatus.qq ? '✅' : '❌') + '</span> ' +
    '<span>网易云: ' + (cookieStatus.netease ? '✅' : '❌') + '</span> ' +
    '<span>酷狗: ' + (cookieStatus.kugou ? '✅' : '❌') + '</span> ' +
    '<span>B站: ' + (cookieStatus.bilibili ? '✅' : '❌') + '</span>' +
    '</div>' +
    '<div class="row" id="sr-cookie-input-row" style="display:none">' +
    '<textarea class="input" id="sr-cookie-input" rows="3" placeholder="粘贴 Cookie..."></textarea>' +
    '<button class="btn primary" onclick="saveCookieManual()">保存Cookie</button>' +
    '</div>' +
    '</div>' +

    '<div class="box collapsed">' +
    '<div class="box-header"><span class="box-title">B站 UP 主名单</span>' +
    '<div class="box-tools"><button class="btn-box-tool" onclick="var b=this.closest(\'.box\');b.classList.toggle(\'collapsed\');this.textContent=b.classList.contains(\'collapsed\')?\'+\':\'−\'">+</button></div></div>' +
    '<div class="box-body">' +
    '<p class="muted" style="margin-bottom:8px">选择B站平台时，仅搜索名单内UP主发布的视频</p>' +
    '<div class="row" id="sr-uplist-row" style="flex-wrap:wrap;gap:6px"></div>' +
    '<div class="row" style="gap:8px">' +
    '<input class="input" id="sr-uplist-input" placeholder="输入UP主UID" style="width:200px" data-tip="输入B站UP主的UID（纯数字），可在UP主主页URL中找到">' +
    '<button class="btn primary" onclick="addBiliUp()" data-tip="将该UID加入UP主名单">添加</button>' +
    '</div>' +
    '</div>' +
    '</div>' +

    '<div class="two-col">' +
    '<div class="card">' +
    '<h3>权限控制</h3>' +
    '<div class="row"><label>最低粉丝勋章</label><input class="input" type="number" id="sr-fanMedal" value="' + (sr.fanMedalMin || 0) + '" min="0" data-tip="观众粉丝灯牌等级需不低于此值才能点歌，0=不限制"></div>' +
    '<div class="row"><label>最低大航海等级</label><input class="input" type="number" id="sr-honor" value="' + (sr.honorLevelMin || 0) + '" min="0" data-tip="观众大航海等级需不低于此值才能点歌，0=不限制"></div>' +
    '<div class="row">' + switchHtml('sr-guardOnly', sr.guardOnly, '仅舰长可点歌', '开启后只有舰长/提督/总督才能点歌') + '</div>' +
    '<div class="row"><label>全局冷却(秒)</label><input class="input" type="number" id="sr-cooldown" value="' + (Math.round((sr.cooldownMs || 10000) / 1000)) + '" min="0" data-tip="任意两次点歌之间的最小间隔秒数，防止点歌刷屏"></div>' +
    '<div class="row"><label>用户冷却(秒)</label><input class="input" type="number" id="sr-userCooldown" value="' + Math.round((sr.userCooldownMs || 60000) / 1000) + '" min="0" data-tip="同一用户两次点歌之间的最小间隔秒数"></div>' +
    '<div class="row"><label>每日上限(0=不限)</label><input class="input" type="number" id="sr-maxDaily" value="' + (sr.maxDaily || 0) + '" min="0" data-tip="每个用户每天最多可点歌次数，0=不限"></div>' +
    '</div>' +
    '<div class="card">' +
    '<h3>播放设置</h3>' +
    '<div class="row"><label>音量</label><input type="range" id="sr-volume" min="0" max="1" step="0.01" value="' + (sr.volume != null ? sr.volume : 0.8) + '" data-tip="歌曲播放音量"> <span id="sr-vol-text">' + Math.round((sr.volume != null ? sr.volume : 0.8) * 100) + '%</span></div>' +
    '<div class="row">' + switchHtml('sr-autoPlay', sr.autoPlay, '自动播放下一首', '当前歌曲播放结束后自动播放列表中的下一首') + '</div>' +
    '<div class="row">' + switchHtml('sr-dedup', sr.dedupEnabled, '去重(5分钟内不重复)', '5分钟内不重复添加同一首歌到播放列表') + '</div>' +
    '</div>' +
    '</div>' +

    '<div class="card">' +
    '<h3>黑名单管理</h3>' +
    '<div class="row">' +
    '<select class="input" id="sr-bl-type" data-tip="选择按歌曲名还是歌手名加入黑名单"><option value="song">歌曲</option><option value="artist">歌手</option></select> ' +
    '<input class="input" id="sr-bl-value" placeholder="歌曲名或歌手名" data-tip="输入要屏蔽的歌曲名或歌手名"> ' +
    '<button class="btn" onclick="addBlacklist()" data-tip="加入黑名单。黑名单中的歌曲/歌手不会被搜索结果采纳">+ 添加</button>' +
    '</div>' +
    '<table class="table"><thead><tr><th>类型</th><th>内容</th><th>操作</th></tr></thead><tbody>' + blRows + '</tbody></table>' +
    '</div>' +

    '<div class="card">' +

    '<h3>最近点歌</h3>' +
    '<table class="table"><thead><tr><th>时间</th><th>歌名</th><th>点歌人</th><th>状态</th></tr></thead><tbody>' + recentRows + '</tbody></table>' +
    '</div>' +

    '<div class="box"><div class="box-header with-border"><h3 class="box-title">🎤 歌词浮层（OBS） <span class="title-hint">在直播画面显示正在播放的歌曲和歌词</span></h3>' +
    boxToolBtn('lyrics') + '</div>' +
    '<div class="box-body">' +
    '<div class="row" style="align-items:center;">' +
    '<input id="lyrics-url" class="form-control mono" value="' + location.origin + '/lyrics/overlay.html" readonly style="flex:1;min-width:280px;" data-tip="在 OBS 中添加「浏览器源」，粘贴此地址；竖条歌词建议宽 360 高 720（竖向比例），底板会自动贴合歌词宽度，两侧透明">' +
    '<button class="btn sm" id="lyrics-copy-url">复制地址</button>' +
    '<button class="btn sm primary" id="lyrics-open-preview">打开预览</button>' +
    '</div>' +
    '<div class="hint" style="margin-top:6px;">自动显示歌名/歌手/点歌人/进度条；把 <span class="mono">.lrc</span> 歌词文件放到 <span class="mono">data\\lyrics\\</span>（支持 <span class="mono">歌名.lrc</span> / <span class="mono">歌手 - 歌名.lrc</span> 两种命名）可显示逐行歌词，网易云/QQ/酷狗 来源的歌会自动尝试在线歌词；若 OBS 里歌词不更新，右键浏览器源 → <b>刷新</b>（或勾选「场景激活时刷新」），B站来源的歌建议放本地 LRC。</div>' +
    '</div></div>';

  // 事件绑定（v1.1.9: 无手动保存按钮，改动自动保存）
  const agreeBtn = $('#sr-agree-btn');
  if (agreeBtn) agreeBtn.onclick = () => agreeCookieDisclaimer();
  const pasteBtn = $('#sr-cookie-paste');
  if (pasteBtn) pasteBtn.onclick = () => {
    const row = $('#sr-cookie-input-row');
    row.style.display = row.style.display === 'none' ? '' : 'none';
  };
  const readBtn = $('#sr-cookie-read');
  if (readBtn) readBtn.onclick = () => readLocalCookie();
  const loginBtn = $('#sr-cookie-login');
  if (loginBtn) loginBtn.onclick = () => openMusicLogin();
  const clearCookieBtn = $('#sr-cookie-clear');
  if (clearCookieBtn) clearCookieBtn.onclick = () => clearMusicCookie();
  // 歌词浮层
  const lyrCopy = $('#lyrics-copy-url');
  if (lyrCopy) lyrCopy.addEventListener('click', function(){
    const inp = $('#lyrics-url'); inp.select();
    if (navigator.clipboard && navigator.clipboard.writeText) navigator.clipboard.writeText(inp.value).then(() => toast('已复制歌词浮层地址', true)).catch(() => {});
    else { try { document.execCommand('copy'); toast('已复制歌词浮层地址', true); } catch (e) {} }
  });
  const lyrOpen = $('#lyrics-open-preview');
  if (lyrOpen) lyrOpen.addEventListener('click', function(){ try { window.open(location.origin + '/lyrics/overlay.html'); } catch (e) {} });
  const volSlider = $('#sr-volume');
  if (volSlider) {
    volSlider.oninput = () => { $('#sr-vol-text').textContent = Math.round(volSlider.value * 100) + '%'; };
    volSlider.onchange = () => saveSongRequest(null);
  }

  // 设置即时保存（change 事件触发自动保存）
  const autoSaveIds = ['sr-enabled', 'sr-allowVip', 'sr-platform', 'sr-keyword', 'sr-fanMedal', 'sr-honor', 'sr-guardOnly', 'sr-cooldown', 'sr-userCooldown', 'sr-maxDaily', 'sr-autoPlay', 'sr-dedup'];
  autoSaveIds.forEach(id => {
    const el = document.getElementById(id);
    if (el) el.addEventListener('change', () => {
      diag('change ' + id + '=' + (el.value !== undefined ? el.value : el.checked));
      saveSongRequest(null);
      // 下拉选完后主动失焦：避免原生下拉弹层残留焦点导致后续点击被吞
      if (el.tagName === 'SELECT') { try { el.blur(); } catch (e) {} }
    });
  });

  // 偶发问题埋点：手动添加输入框的点击/焦点/按键（用户反馈「切换来源后点不进去」）
  (function bindSearchDiag() {
    const kw = $('#sr-search-kw');
    if (!kw) return;
    const rectOf = (el) => { const r = el.getBoundingClientRect(); return [Math.round(r.left), Math.round(r.top), Math.round(r.width), Math.round(r.height)].join(','); };
    ['mousedown', 'click', 'focus', 'blur'].forEach(function (evt) {
      kw.addEventListener(evt, function (e) {
        diag('kw ' + evt + ' at=' + (e.clientX != null ? (Math.round(e.clientX) + ',' + Math.round(e.clientY)) : '-') + ' rect=' + rectOf(kw) + ' active=' + (document.activeElement && (document.activeElement.id || document.activeElement.tagName)) + ' disabled=' + kw.disabled + ' readOnly=' + kw.readOnly);
      });
    });
    let keyLoggedSinceFocus = false;
    kw.addEventListener('focus', function () { keyLoggedSinceFocus = false; });
    kw.addEventListener('mousedown', function () { keyLoggedSinceFocus = false; });
    kw.addEventListener('keydown', function (e) {
      if (keyLoggedSinceFocus) return;
      keyLoggedSinceFocus = true;
      diag('kw keydown ok (' + e.key + ')');
    });
    const row = $('#sr-search-row');
    if (row) row.addEventListener('mousedown', function (e) {
      if (e.target !== row) return;
      const top = document.elementFromPoint(e.clientX, e.clientY);
      diag('row mousedown at=' + Math.round(e.clientX) + ',' + Math.round(e.clientY) + ' kwRect=' + rectOf(kw) + ' topEl=' + ((top && (top.id || top.tagName)) || '?'));
      // 点在输入框旁边的空隙（输入框只有 34px 高，很容易点偏）→ 直接把焦点给输入框，
      // 而不是让 row 吞掉这次点击、把已聚焦的输入框弄失焦
      e.preventDefault();
      try { kw.focus(); } catch (x) {}
    });
    // 窗口级按键/焦点：判断按键是否根本没进页面（例如被原生 B站视图抢走焦点）
    if (!window._diagWinBound) {
      window._diagWinBound = true;
      window.addEventListener('keydown', function (e) {
        const t = e.target || {};
        if (t.id === 'sr-search-kw') return;   // 输入框自己的按键上面已记
        diag('window keydown ' + e.key + ' target=' + (t.id || t.tagName || '?'));
      }, true);
      window.addEventListener('blur', function () { diag('window blur'); });
      window.addEventListener('focus', function () { diag('window focus'); });
    }
  })();

  // 播放列表拖拽排序
  setupPlaylistDragDrop();

  // 加载B站UP名单
  fetch(apiUrl('/api/song-request/bilibili-uplist')).then(r => r.json()).then(d => renderBiliUpList(d.upList)).catch(() => {});

  // 监听播放器状态更新（先移除旧监听器避免重复绑定导致DOM频繁重建）
  window.removeEventListener('song:status', updatePlaylistUI);
  window.addEventListener('song:status', updatePlaylistUI);
  diag('song-page render done');
}

function saveSongRequest(btn) {
  const sr = {
    enabled: $('#sr-enabled').checked,
    platform: $('#sr-platform').value,
    keyword: $('#sr-keyword').value.trim() || '点歌',
    fanMedalMin: parseInt($('#sr-fanMedal').value) || 0,
    honorLevelMin: parseInt($('#sr-honor').value) || 0,
    guardOnly: $('#sr-guardOnly').checked,
    cooldownMs: (parseInt($('#sr-cooldown').value) || 0) * 1000,
    userCooldownMs: (parseInt($('#sr-userCooldown').value) || 0) * 1000,
    maxDaily: parseInt($('#sr-maxDaily').value) || 0,
    volume: parseFloat($('#sr-volume').value) || 0.8,
    autoPlay: $('#sr-autoPlay').checked,
    allowVip: $('#sr-allowVip').checked,
    dedupEnabled: $('#sr-dedup').checked
  };
  const full = Object.assign({}, state.config.songRequest || {}, sr);
  state.config.songRequest = full;
  fetch(apiUrl('/api/config'), { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ songRequest: full }) })
    .then(() => { if (window.BLTSong) window.BLTSong.setConfig(full); if (btn) { flashSaved(btn); toast('点歌设置已保存', true); } else autosaveHint(); })
    .catch(e => toast('保存失败: ' + e.message, false));
}

function addBlacklist() {
  const type = $('#sr-bl-type').value;
  const value = $('#sr-bl-value').value.trim();
  if (!value) return toast('请输入内容', false);
  fetch(apiUrl('/api/song-request/blacklist'), { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ type, value }) })
    .then(r => r.json()).then(d => { if (d.ok) { toast('已添加黑名单', true); renderSongRequestPage(); } })
    .catch(() => toast('添加失败', false));
}

function removeBlacklist(index) {
  fetch(apiUrl('/api/song-request/blacklist/delete'), { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ index }) })
    .then(r => r.json()).then(d => { if (d.ok) { toast('已删除', true); renderSongRequestPage(); } })
    .catch(() => toast('删除失败', false));
}

function addBiliUp() {
  const uid = ($('#sr-uplist-input').value || '').trim();
  if (!uid) return toast('请输入UID', false);
  fetch(apiUrl('/api/song-request/bilibili-uplist/add'), { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ uid }) })
    .then(r => r.json()).then(d => { if (d.ok) { toast('已添加UP: ' + uid, true); $('#sr-uplist-input').value = ''; renderBiliUpList(d.upList); } })
    .catch(() => toast('添加失败', false));
}

function removeBiliUp(uid) {
  fetch(apiUrl('/api/song-request/bilibili-uplist/delete'), { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ uid }) })
    .then(r => r.json()).then(d => { if (d.ok) { toast('已删除', true); renderBiliUpList(d.upList); } })
    .catch(() => toast('删除失败', false));
}

function renderBiliUpList(upList) {
  const row = $('#sr-uplist-row');
  if (!row) return;
  if (!upList || !upList.length) { row.innerHTML = '<span class="muted">名单为空</span>'; return; }
  row.innerHTML = upList.map(function(uid) {
    return '<span class="sr-up-tag" style="display:inline-flex;align-items:center;gap:4px;padding:4px 8px;background:var(--panel3);border:1px solid var(--line-strong);border-radius:4px;font-size:13px;color:var(--text)">' +
      esc(uid) + ' <a href="javascript:void(0)" onclick="removeBiliUp(\'' + esc(uid) + '\')" style="color:var(--danger-bright);text-decoration:none;font-weight:bold;margin-left:2px">×</a></span>';
  }).join('');
}

function songPlay(index) { if (window.BLTSong) window.BLTSong.play(index); }
function songPause() { if (window.BLTSong) window.BLTSong.pause(); }
function songSkip() { if (window.BLTSong) window.BLTSong.skip(); }
function songPrev() { if (window.BLTSong) window.BLTSong.prev(); }
function songRemove(index) { if (window.BLTSong) window.BLTSong.removeFromPlaylist(index); }
function songClear() {
  if (!confirm('确定清空播放列表？')) return;
  if (window.BLTSong) window.BLTSong.clearPlaylist();
  renderSongRequestPage();
}
function songManualAdd() {
  const row = $('#sr-search-row');
  if (row) row.style.display = '';
  const kw = $('#sr-search-kw');
  if (kw) { try { kw.scrollIntoView({ block: 'nearest' }); } catch (e) {} requestAnimationFrame(function () { try { kw.focus(); } catch (e) {} }); }
}
// 偶发问题现场埋点（写入 <数据目录>/client-log.txt，可用 /api/debug/client-log 查看）
function diag(msg) {
  try { fetch(apiUrl('/api/debug/client-log'), { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ msg: String(msg).slice(0, 400) }) }).catch(function () {}); } catch (e) {}
}
function cancelSongSearch() {
  const row = $('#sr-search-row');
  if (row) row.style.display = 'none';
  const results = $('#sr-search-results');
  if (results) results.innerHTML = '';
}
function onSongSeek(val) {
  if (window.BLTSong) window.BLTSong.seek(parseFloat(val));
}
function onSongSeekInput(val) {
  if (window.BLTSong) window.BLTSong.seekPreview(parseFloat(val));
}
async function songSearch() {
  const kw = $('#sr-search-kw').value.trim();
  if (!kw) return;
  if (!window.BLTSong) return;
  const btn = document.getElementById('sr-search-btn');
  const searchType = $('#sr-search-type') ? $('#sr-search-type').value : 'song';
  btnLoading(btn, '搜索中…');
  try {
    const d = await window.BLTSong.search(kw, searchType);
    if (d.error) { toast('搜索失败: ' + d.error, false); return; }
    const results = d.results || [];
    if (!results.length) { toast('未找到歌曲，可尝试切换搜索类型（智能搜索/按歌词）', false); return; }
    $('#sr-search-results').innerHTML = '<div class="sr-search-list">' + results.map((s, i) =>
      '<div class="sr-search-item"><span>' + esc(s.name) + ' - ' + esc(s.artist) + songBadges(s) + '</span> <button class="btn xs" onclick=\'songAddToPlaylist(' + JSON.stringify(s).replace(/'/g, "\\'") + ')\'>添加</button></div>'
    ).join('') + '</div>';
  } catch (e) {
    toast('搜索失败: ' + (e && e.message || e), false);
  } finally {
    btnDone(btn);
  }
}
function songAddToPlaylist(song) {
  if (window.BLTSong) window.BLTSong.addToPlaylist(song);
  $('#sr-search-results').innerHTML = '';
  $('#sr-search-row').style.display = 'none';
  toast('已添加《' + song.name + '》', true);
}
function saveCookieManual() {
  const platform = $('#sr-platform').value;
  const cookie = $('#sr-cookie-input').value.trim();
  if (!cookie) return toast('请输入Cookie', false);
  fetch(apiUrl('/api/song-request/cookie'), { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ platform, cookie }) })
    .then(r => r.json()).then(d => { if (d.ok) { toast('Cookie已保存', true); renderSongRequestPage(); } })
    .catch(() => toast('保存失败', false));
}
async function readLocalCookie() {
  const platform = $('#sr-platform').value;
  if (!window.electronAPI || !window.electronAPI.music) return toast('当前环境不支持读取Cookie，请使用手动粘贴', false);
  const btn = document.getElementById('sr-cookie-read');
  btnLoading(btn, '读取中…');
  try {
    const r = await window.electronAPI.music.captureCookie(platform);
    if (r && r.cookie) {
      await post('/api/song-request/cookie', { platform, cookie: r.cookie });
      toast('✅ 已读取并保存 ' + platform + ' Cookie', true);
      renderSongRequestPage();
    } else {
      toast('未找到已保存的 Cookie，请先使用「应用内登录」', false);
    }
  } catch (e) {
    toast('读取失败: ' + e.message, false);
  } finally { btnDone(btn); }
}

async function openMusicLogin() {
  if (!window.electronAPI || !window.electronAPI.music) return toast('当前环境不支持应用内登录，请使用手动粘贴', false);
  const html =
    '<p class="muted" style="margin-bottom:12px">请选择要登录的音乐平台：</p>' +
    '<div class="row" style="justify-content:center;gap:12px;flex-wrap:wrap">' +
    '<button class="btn" onclick="doOpenMusicLogin(\'qq\')">🎵 QQ音乐</button>' +
    '<button class="btn" onclick="doOpenMusicLogin(\'netease\')">🔴 网易云音乐</button>' +
    '<button class="btn" onclick="doOpenMusicLogin(\'kugou\')">🎤 酷狗音乐</button>' +
    '</div>' +
    '<div class="row" style="margin-top:12px;justify-content:flex-end;"><button class="btn" onclick="closeModal()">取消</button></div>';
  openModal('🌐 应用内登录', html);
}

async function doOpenMusicLogin(platform) {
  closeModal();
  const platformNames = { qq: 'QQ音乐', netease: '网易云音乐', kugou: '酷狗音乐' };
  toast('正在打开 ' + (platformNames[platform] || platform) + ' 登录页，请在弹出的窗口中登录...');
  try {
    const r = await window.electronAPI.music.showLogin(platform);
    if (r && r.ok) {
      toast('已打开登录窗口，登录成功后 Cookie 会自动同步', true);
    } else {
      toast('打开登录窗口失败: ' + (r && r.error || ''), false);
    }
  } catch (e) {
    toast('打开失败: ' + e.message, false);
  }
}

async function agreeCookieDisclaimer() {
  try {
    const sr = state.config.songRequest || {};
    sr.cookieAgreed = true;
    state.config.songRequest = sr;
    await post('/api/config', { songRequest: sr });
    toast('已同意免责声明', true);
    renderSongRequestPage();
  } catch (e) {
    toast('保存失败: ' + e.message, false);
  }
}

async function clearMusicCookie() {
  const platform = $('#sr-platform').value;
  const platformNames = { qq: 'QQ音乐', netease: '网易云音乐', kugou: '酷狗音乐' };
  if (!confirm('确定要清空' + (platformNames[platform] || platform) + '的Cookie吗？')) return;
  try {
    if (window.electronAPI && window.electronAPI.music) {
      await window.electronAPI.music.clearCookies(platform);
    }
    await post('/api/song-request/cookie', { platform, cookie: '' });
    toast('已清空 ' + (platformNames[platform] || platform) + ' Cookie', true);
    renderSongRequestPage();
  } catch (e) {
    toast('清空失败: ' + e.message, false);
  }
}

function setupPlaylistDragDrop() {
  const tbody = $('#sr-playlist-body');
  if (!tbody) return;
  let dragFrom = null;
  tbody.querySelectorAll('.pl-row').forEach(row => {
    row.addEventListener('dragstart', e => { dragFrom = parseInt(row.dataset.index); row.style.opacity = '0.5'; });
    row.addEventListener('dragend', () => { row.style.opacity = ''; });
    row.addEventListener('dragover', e => { e.preventDefault(); });
    row.addEventListener('drop', e => {
      e.preventDefault();
      const dragTo = parseInt(row.dataset.index);
      if (dragFrom !== null && dragFrom !== dragTo) {
        fetch(apiUrl('/api/song-request/playlist/reorder'), { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ from: dragFrom, to: dragTo }) })
          .then(() => renderSongRequestPage());
      }
      dragFrom = null;
    });
  });
}

function updatePlaylistUI(e) {
  const { playlist, currentIndex, paused } = e.detail;
  const tbody = $('#sr-playlist-body');
  if (!tbody) return;
  const existingRows = tbody.querySelectorAll('.pl-row');
  if (existingRows.length !== playlist.length) {
    tbody.innerHTML = playlist.map((s, i) => {
      const cur = i === currentIndex ? '▶ ' : '  ';
      return '<tr class="pl-row" draggable="true" data-index="' + i + '">' +
        '<td>' + cur + esc(s.name) + ' ' + songBadges(s) + '</td><td>' + esc(s.artist || '') + '</td><td>' + esc(s.requester || '') + '</td>' +
        '<td><button class="btn xs" onclick="songPlay(' + i + ')">播放</button> <button class="btn xs danger" onclick="songRemove(' + i + ')">删除</button></td></tr>';
    }).join('');
    setupPlaylistDragDrop();
    return;
  }
  existingRows.forEach((row, i) => {
    const td = row.querySelector('td:first-child');
    if (td) {
      const song = playlist[i];
      const cur = i === currentIndex ? '▶ ' : '  ';
      td.innerHTML = cur + esc(song ? song.name : '') + ' ' + songBadges(song || {});
    }
  });
}

function applyVerifyUnlock(){
  state.verifyLocked = false;
  document.body.classList.remove('verify-locked');
  if (state._cfgBackup){
    if (state._cfgBackup.tts) state.config.tts = state._cfgBackup.tts;
    if (state._cfgBackup.autoDanmu) state.config.autoDanmu = state._cfgBackup.autoDanmu;
    if (state._cfgBackup.pk) state.config.pk = state._cfgBackup.pk;
    if (state._cfgBackup.songRequest) state.config.songRequest = state._cfgBackup.songRequest;
    if (state._cfgBackup.recording) state.config.recording = state._cfgBackup.recording;
  }
  state.ttsEnabled = !!(state.config.tts && state.config.tts.enabled);
  if (window.BLTTTS && state.config.tts){ try{ window.BLTTTS.setConfig(state.config.tts); }catch(e){} }
  if (window.BLTSong && state.config.songRequest){ try{ window.BLTSong.init(state.config.songRequest); }catch(e){} }
  var btn = document.getElementById('btn-connect');
  if (btn) btn.disabled = false;
  buildNav();
  router();
  toast('✅ 账号验证通过', true);
  maybeShowOnboarding();
}

// ===== 界面皮肤（与 public/skins/*.css 一一对应；default=原版样式，无对应 css） =====
var SKINS = [
  { id: 'default',   name: '默认 · 经典（深色 B站粉）', desc: '程序自带外观，深色面板 + B站粉强调色' },
  { id: 'neon',      name: 'A 暗夜霓虹',   desc: '深紫氛围底 · 玻璃质感卡片 · 霓虹渐变发光' },
  { id: 'workbench', name: 'B 清爽工作台', desc: '浅灰白底 · 细边框扁平卡 · 蓝主色，紧凑克制' },
  { id: 'console',   name: 'C 专业控制台', desc: '石墨深底 · 琥珀强调 · 面板化高密度，监控台风' },
  { id: 'bili',      name: 'D B站应援',    desc: '白底大圆角 · 粉蓝双主色 · 胶囊按钮，活泼亲和' },
  { id: 'vibrancy',  name: 'E 灵动玻璃',   desc: 'macOS 质感 · 振动玻璃侧栏 · 系统蓝 + 绿色开关' },
  { id: 'brutal',    name: 'F 新粗野主义', desc: '奶油纸底 · 粗黑描边 + 硬投影 · 高饱和撞色' },
  { id: 'hud',       name: 'G 电竞 HUD',   desc: '网格暗底 · 切角面板 · 警示红，硬核电竞感' },
  { id: 'editorial', name: 'H 杂志编辑风', desc: '米白纸感 · 衬线标题 · 发丝分隔，安静文气' }
];
function applySkin(id){
  if (!SKINS.some(function (s) { return s.id === id; })) id = 'default';
  if (id === 'default') document.documentElement.removeAttribute('data-skin');
  else document.documentElement.setAttribute('data-skin', id);
  try { localStorage.setItem('blt_skin', id); } catch (e) {}
}
function skinDesc(id){
  const s = SKINS.find(function (x) { return x.id === id; });
  return s ? s.desc : '';
}
// 设置页下拉切换：立即应用 + 持久化到 config.json
function changeSkin(v){
  applySkin(v);
  const h = $('#skin-hint');
  if (h) h.textContent = skinDesc(v);
  post('/api/config', { skin: v })
    .then(function (saved) { if (saved && saved.skin !== undefined) state.config = saved; autosaveHint(); })
    .catch(function (e) { toast('❌ 皮肤保存失败: ' + (e && e.message || e), false); });
}

async function init(){
  window.addEventListener('hashchange', router);
  // 暴露全局函数供 onclick
  Object.assign(window, { openFolder, addBb, pickAndSet, doQuery, resetQuery, gotoPage, exportHtml, genGift, downloadGift, fillFromRecent, closeModal, parseCookieInput, applyPastedCookie, parseCookieStr, maskCookie, setPkTarget, saveAutoDanmu, savePkPage, ttsTypeToggle, ttsPreview, ttsSkip, ttsRestart, saveTts, renderTtsPage, manualCheckUpdate, scrollToCard, quickConnect, refreshPanel, renderSongRequestPage, saveSongRequest, addBlacklist, removeBlacklist, songPlay, songPause, songSkip, songPrev, songRemove, songClear, songManualAdd, songSearch, cancelSongSearch, onSongSeek, onSongSeekInput, songAddToPlaylist, saveCookieManual, readLocalCookie, openMusicLogin, doOpenMusicLogin, agreeCookieDisclaimer, clearMusicCookie,
    // v1.1.9 usability helpers used from inline onclick handlers
    toggleBox, copyAuthorContact, openAuthorSpace, retryVerify, highlightPulse,
    onbNext, onbPrev, onbGoStep, finishOnboarding, updateRoomEcho,
    // 皮肤
    changeSkin, applySkin });
  try { state.config = await g('/api/config'); } catch (e) { state.config = { recording:{} }; }
  // 皮肤：以 config 为权威值校正（首帧已用 localStorage 缓存，见 index.html）
  if (state.config && state.config.skin) applySkin(state.config.skin);
  state.ttsEnabled = !!(state.config.tts && state.config.tts.enabled);
  if (window.BLTTTS && state.config.tts) { try { window.BLTTTS.setConfig(state.config.tts); } catch (e) {} }
  if (window.BLTSong && state.config.songRequest) { try { window.BLTSong.init(state.config.songRequest); } catch (e) {} }
  // 版本号（侧栏底部显示）
  if (window.electronAPI && window.electronAPI.app && window.electronAPI.app.getVersion) {
    try { const v = await window.electronAPI.app.getVersion(); window.__APP_VERSION = v.app || ''; } catch (e) {}
  }
  // ---- 验证锁定：启动时默认锁定，等待 IPC 解锁（仅 Electron 有验证机制；Web 模式不锁） ----
  state.verifyLocked = false; /* MAUI host: verification lock not ported */
  if (state.verifyLocked) {
  state._cfgBackup = JSON.parse(JSON.stringify({
    tts: state.config.tts || {}, autoDanmu: state.config.autoDanmu || {},
    pk: state.config.pk || {}, songRequest: state.config.songRequest || {},
    recording: state.config.recording || {}
  }));
  if (!state.config.tts) state.config.tts = {};
  state.config.tts.enabled = false; state.ttsEnabled = false;
  if (window.BLTTTS) { try { window.BLTTTS.setConfig(state.config.tts); } catch(e){} }
  if (!state.config.autoDanmu) state.config.autoDanmu = {};
  if (!state.config.autoDanmu.welcome) state.config.autoDanmu.welcome = {};
  state.config.autoDanmu.welcome.enabled = false;
  if (!state.config.autoDanmu.thank) state.config.autoDanmu.thank = {};
  state.config.autoDanmu.thank.enabled = false;
  if (!state.config.autoDanmu.follow) state.config.autoDanmu.follow = {};
  state.config.autoDanmu.follow.enabled = false;
  if (!state.config.autoDanmu.timer) state.config.autoDanmu.timer = {};
  state.config.autoDanmu.timer.enabled = false;
  if (!state.config.pk) state.config.pk = {};
  state.config.pk.enabled = false;
  if (!state.config.songRequest) state.config.songRequest = {};
  state.config.songRequest.enabled = false;
  if (window.BLTSong) { try { window.BLTSong.init(state.config.songRequest); } catch(e){} }
  if (!state.config.recording) state.config.recording = {};
  TYPES.forEach(function(t){ state.config.recording[t] = false; });
  var _ls = document.createElement('style');
  _ls.textContent = '.verify-locked .nav-item:not([data-route="#/settings"]):not([data-route="#/help"]) { opacity:0.35; pointer-events:none; } .verify-locked #btn-connect { opacity:0.5; }';
  document.head.appendChild(_ls);
  document.body.classList.add('verify-locked');
  if (window.electronAPI && window.electronAPI.verify && window.electronAPI.verify.onStatus) {
    window.electronAPI.verify.onStatus(function(st){ if (st && !st.locked) applyVerifyUnlock(); });
  }
  } // end if (state.verifyLocked)

  buildNav();
  setupElectronListeners();
  setupUpdaterListener();
  connectRealtime();   // 启动即全局连接 WS，让语音念弹幕在任何页面都能收到事件
  await refreshStatus();
  router();
}

// ===== 自动点赞 =====
var likePollTimer = null;
function startLike(){
  var btn = document.getElementById('likeStartBtn');
  var stopBtn = document.getElementById('likeStopBtn');
  var statusEl = document.getElementById('likeStatus');
  if(!btn) return;
  btn.disabled = true; btn.textContent = '点赞中...';
  stopBtn.style.display = '';
  statusEl.textContent = '正在点赞...';
  fetch(apiUrl('/api/like/start'), { method: 'POST' }).then(function(r){ return r.json(); }).then(function(j){
    if(j.error){ btn.disabled = false; btn.textContent = '👍 自动点赞'; stopBtn.style.display = 'none'; statusEl.textContent = '❌ ' + j.error; return; }
    if(likePollTimer) clearInterval(likePollTimer);
    likePollTimer = setInterval(pollLikeStatus, 1000);
  }).catch(function(e){ btn.disabled = false; btn.textContent = '👍 自动点赞'; stopBtn.style.display = 'none'; statusEl.textContent = '❌ ' + e.message; });
}
function stopLike(){
  fetch(apiUrl('/api/like/stop'), { method: 'POST' });
  var statusEl = document.getElementById('likeStatus');
  if(statusEl) statusEl.textContent = '正在停止...';
}
function pollLikeStatus(){
  fetch(apiUrl('/api/like/status')).then(function(r){ return r.json(); }).then(function(j){
    var btn = document.getElementById('likeStartBtn');
    var stopBtn = document.getElementById('likeStopBtn');
    var statusEl = document.getElementById('likeStatus');
    if(!statusEl) return;
    if(j.running){
      statusEl.textContent = '已点赞 ' + j.liked + ' 次...';
    } else {
      if(likePollTimer) { clearInterval(likePollTimer); likePollTimer = null; }
      if(btn){ btn.disabled = false; btn.textContent = '👍 自动点赞'; }
      if(stopBtn){ stopBtn.style.display = 'none'; }
      if(j.error){ statusEl.textContent = '已点赞 ' + j.liked + ' 次，停止：' + j.error; }
      else { statusEl.textContent = '✅ 已点赞 ' + j.liked + ' 次（达上限）'; }
    }
  }).catch(function(){});
}

// ===== 盲盒检测 =====
var _lastBlindBoxData = null;
function detectBlindBoxes(){
  var result = document.getElementById('blindboxDetectResult');
  if(!result) return;
  var btn = document.getElementById('btn-detect-bb');
  btnLoading(btn, '检测中…');
  result.innerHTML = '<p class="muted">正在检测...</p>';
  fetch(apiUrl('/api/blindbox/detect'), { method: 'POST' }).then(function(r){ return r.json(); }).then(function(j){
    if(j.error){ result.innerHTML = '<p style="color:var(--danger);">❌ ' + esc(j.error) + '</p>'; return; }
    var boxes = j.boxes || [];
    if(!boxes.length){ result.innerHTML = '<p class="muted">未检测到盲盒礼物。</p>'; return; }
    var html = '<table class="table" style="width:100%;font-size:12px;"><thead><tr><th>盲盒名称</th><th>成本</th><th>期望收入</th><th>期望盈亏</th><th>奖池</th></tr></thead><tbody>';
    var saveData = {};
    boxes.forEach(function(b){
      var profit = Math.round((b.expectIncome - b.cost) * 100) / 100;
      var profitColor = profit >= 0 ? 'var(--success)' : 'var(--danger)';
      var giftsStr = b.gifts.map(function(g){ return esc(g.name) + '(' + (g.price/1000) + '元,' + esc(g.chance) + ')'; }).join(' / ');
      html += '<tr><td>' + esc(b.name) + '</td><td>' + b.cost + '元</td><td>' + b.expectIncome + '元</td><td style="color:' + profitColor + ';">' + profit + '元</td><td style="font-size:11px;color:var(--muted);">' + giftsStr + '</td></tr>';
      saveData[b.name] = { giftId: b.giftId, cost: b.cost, expectIncome: b.expectIncome, gifts: b.gifts };
    });
    html += '</tbody></table>';
    html += '<div class="row" style="margin-top:10px;gap:8px;"><button class="btn sm success" onclick="saveBlindBoxData()">💾 保存到内置数据库</button></div>';
    _lastBlindBoxData = saveData;
    result.innerHTML = html;
  }).catch(function(e){ result.innerHTML = '<p style="color:var(--danger);">❌ ' + esc(e.message) + '</p>'; }).finally(function(){ btnDone(btn); });
}
function saveBlindBoxData(){
  if(!_lastBlindBoxData) return;
  var result = document.getElementById('blindboxDetectResult');
  fetch(apiUrl('/api/blindbox/save'), { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ data: _lastBlindBoxData }) }).then(function(r){ return r.json(); }).then(function(j){
    if(j.ok){ result.innerHTML = '<p style="color:var(--success);">✅ 已保存到内置数据库，盲盒盈亏将自动使用期望收入计算。</p>'; }
    else { result.innerHTML = '<p style="color:var(--danger);">❌ ' + esc(j.error) + '</p>'; }
  }).catch(function(e){ result.innerHTML = '<p style="color:var(--danger);">❌ ' + esc(e.message) + '</p>'; });
}

// 全局 Tooltip（JS 动态定位，自动避免视口溢出）
(function(){
  var tipEl = null, hideTimer = null;
  function getTipEl(){
    if (!tipEl) { tipEl = document.createElement('div'); tipEl.className = 'blt-tooltip'; document.body.appendChild(tipEl); }
    return tipEl;
  }
  function showTip(target){
    var text = target.getAttribute('data-tip');
    if (!text) return;
    var el = getTipEl();
    el.textContent = text;
    el.style.display = 'block';
    el.style.visibility = 'hidden';
    el.classList.remove('show');
    var rect = target.getBoundingClientRect();
    var tw = el.offsetWidth, th = el.offsetHeight;
    var gap = 8, pad = 6;
    var top;
    if (rect.top > window.innerHeight * 0.5) {
      top = rect.top - th - gap;
      if (top < pad) top = rect.bottom + gap;
    } else {
      top = rect.bottom + gap;
      if (top + th > window.innerHeight - pad) top = rect.top - th - gap;
    }
    var left = rect.left + rect.width / 2 - tw / 2;
    if (left < pad) left = pad;
    if (left + tw > window.innerWidth - pad) left = window.innerWidth - tw - pad;
    if (top < pad) top = pad;
    el.style.top = Math.round(top) + 'px';
    el.style.left = Math.round(left) + 'px';
    el.style.visibility = 'visible';
    el.classList.add('show');
  }
  function hideTip(){
    var el = getTipEl();
    el.classList.remove('show');
    hideTimer = setTimeout(function(){ el.style.display = 'none'; }, 150);
  }
  document.addEventListener('mouseover', function(e){
    var target = e.target.closest && e.target.closest('[data-tip]');
    if (!target) return;
    if (hideTimer) { clearTimeout(hideTimer); hideTimer = null; }
    showTip(target);
  });
  document.addEventListener('mouseout', function(e){
    var target = e.target.closest && e.target.closest('[data-tip]');
    if (!target) return;
    var related = e.relatedTarget;
    if (related && related.closest && related.closest('[data-tip]') === target) return;
    hideTip();
  });
})();

init();
