/**
 * BLT 公告推送 · 单文件 Cloudflare Worker（公开接口 + 控制台 Web UI + KV）
 *
 * 需要绑定：
 *   KV 命名空间  变量名 ANN
 *   环境变量     ADMIN_TOKEN（Secret，控制台登录用；建议 32 位以上随机串）
 *
 * 路由：
 *   GET  /announcements?uid=&v=&ch=   公开：客户端拉取（按 uid/版本/通道/时间过滤，带 ETag）
 *   POST /receipt                     公开：客户端已读/确认回执（只计数，存 uid 哈希）
 *   GET  /admin                       控制台页面（本文件内嵌）
 *   GET  /admin/api/state             管理：live/draft/cfg/历史/统计
 *   POST /admin/api/draft|publish|rollback|cfg
 *   GET  /admin/api/stats
 *
 * KV 键：
 *   live = 对外公告数组        cfg = {pollAfterSeconds, emergency, enabled}
 *   draft = 控制台草稿         hist:<ts> = 发布快照（保留 20 份）
 *   stat:<id>:ack|seen = 回执计数     statseen:<id>:<uidhash> = 去重
 */

const HIST_MAX = 20;
const MAX_ITEMS = 30;
const MAX_BODY = 2000;

const json = (data, status = 200, extra = {}) =>
  new Response(JSON.stringify(data), {
    status,
    headers: { 'content-type': 'application/json; charset=utf-8', 'cache-control': 'no-store', ...extra },
  });

const nowSec = () => Math.floor(Date.now() / 1000);

function parseVer(v) {
  return String(v || '')
    .replace(/^v/i, '')
    .split(/[^0-9]+/)
    .filter(Boolean)
    .slice(0, 3)
    .map((n) => parseInt(n, 10) || 0)
    .concat([0, 0, 0])
    .slice(0, 3);
}
function verCmp(a, b) {
  const x = parseVer(a), y = parseVer(b);
  for (let i = 0; i < 3; i++) {
    if (x[i] > y[i]) return 1;
    if (x[i] < y[i]) return -1;
  }
  return 0;
}
function ts(v) {
  if (!v) return 0;
  const t = Date.parse(v);
  return isNaN(t) ? 0 : Math.floor(t / 1000);
}

/** 过滤出"该给这个客户端看的"条目：通道 → 时间窗 → 版本区间 → uid 定向 */
export function selectItems(items, { uid, v, ch, now }) {
  const list = Array.isArray(items) ? items : [];
  return list.filter((it) => {
    if (!it || !it.id || !it.title) return false;
    if (it.channel && ch && it.channel !== ch) return false;
    const pub = ts(it.publishAt), exp = ts(it.expireAt);
    if (pub && pub > now) return false;
    if (exp && exp <= now) return false;
    if (it.minVersion && v && verCmp(v, it.minVersion) < 0) return false;
    if (it.maxVersion && v && verCmp(v, it.maxVersion) > 0) return false;
    const uids = Array.isArray(it.uids) ? it.uids.map(String) : [];
    if (uids.length > 0 && !uids.includes(String(uid || ''))) return false;
    return true;
  });
}

async function sha256Hex(text) {
  const buf = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(text));
  return [...new Uint8Array(buf)].map((b) => b.toString(16).padStart(2, '0')).join('');
}

async function readJson(req) {
  try { return await req.json(); } catch { return null; }
}

function authed(req, env) {
  const h = req.headers.get('authorization') || '';
  const token = env.ADMIN_TOKEN || '';
  return token.length > 0 && h === 'Bearer ' + token;
}

/** 粗暴但有效的失败限速（同一 Worker 实例内计数） */
const fails = new Map();
function tooMany(ip) {
  const rec = fails.get(ip);
  return rec && rec.n >= 10 && Date.now() - rec.t < 10 * 60 * 1000;
}
function noteFail(ip) {
  const rec = fails.get(ip) || { n: 0, t: Date.now() };
  rec.n++; rec.t = Date.now();
  fails.set(ip, rec);
}

export default {
  async fetch(req, env) {
    const url = new URL(req.url);
    const ip = req.headers.get('cf-connecting-ip') || 'local';
    const p = url.pathname.replace(/\/+$/, '') || '/';

    // ---------------- 公开：客户端拉取 ----------------
    if (p === '/announcements' && req.method === 'GET') {
      const cfg = (await env.ANN.get('cfg', 'json')) || { pollAfterSeconds: 300, emergency: false, enabled: true };
      if (cfg.enabled === false) {
        return json({ schema: 1, serverTime: nowSec(), pollAfterSeconds: 900, items: [] });
      }
      const live = (await env.ANN.get('live', 'json')) || [];
      const items = selectItems(live, {
        uid: url.searchParams.get('uid') || '',
        v: url.searchParams.get('v') || '',
        ch: url.searchParams.get('ch') || '',
        now: nowSec(),
      });
      const body = {
        schema: 1,
        serverTime: nowSec(),
        pollAfterSeconds: cfg.emergency ? 60 : (cfg.pollAfterSeconds || 300),
        items,
      };
      const text = JSON.stringify(body);
      const etag = '"' + (await sha256Hex(text)).slice(0, 32) + '"';
      if (req.headers.get('if-none-match') === etag) {
        return new Response(null, { status: 304, headers: { etag, 'cache-control': 'no-store' } });
      }
      return new Response(text, {
        headers: { 'content-type': 'application/json; charset=utf-8', etag, 'cache-control': 'no-store' },
      });
    }

    // ---------------- 公开：回执 ----------------
    if (p === '/receipt' && req.method === 'POST') {
      const b = (await readJson(req)) || {};
      const id = String(b.id || '').slice(0, 80);
      const kind = b.kind === 'ack' ? 'ack' : 'seen';
      const uid = String(b.uid || '');
      if (!id) return json({ ok: false, error: 'missing id' }, 400);
      const uh = uid ? (await sha256Hex('blt:' + uid)).slice(0, 24) : '';
      try {
        if (uh) {
          const seenKey = 'statseen:' + id + ':' + kind + ':' + uh;
          if (await env.ANN.get(seenKey)) return json({ ok: true, dup: true });
          await env.ANN.put(seenKey, '1', { expirationTtl: 60 * 60 * 24 * 180 });
        }
        const key = 'stat:' + id + ':' + kind;
        const cur = parseInt((await env.ANN.get(key)) || '0', 10) || 0;
        await env.ANN.put(key, String(cur + 1));
      } catch (e) {
        return json({ ok: false, error: String(e && e.message || e) }, 200);
      }
      return json({ ok: true });
    }

    // ---------------- 控制台页面 ----------------
    if (p === '/admin' && req.method === 'GET') {
      return new Response(CONSOLE_HTML, {
        headers: { 'content-type': 'text/html; charset=utf-8', 'cache-control': 'no-store' },
      });
    }

    // ---------------- 管理接口 ----------------
    if (p.startsWith('/admin/api/')) {
      if (tooMany(ip)) return json({ ok: false, error: '尝试次数过多，请 10 分钟后再试' }, 429);
      if (!authed(req, env)) {
        noteFail(ip);
        return json({ ok: false, error: '未授权：请检查管理密钥' }, 401);
      }
      const action = p.slice('/admin/api/'.length);

      if (action === 'state' && req.method === 'GET') {
        const live = (await env.ANN.get('live', 'json')) || [];
        const cfg = (await env.ANN.get('cfg', 'json')) || { pollAfterSeconds: 300, emergency: false, enabled: true };
        const draft = (await env.ANN.get('draft', 'json')) || null;
        const histRaw = (await env.ANN.get('hist:index', 'json')) || [];
        const stats = {};
        for (const it of live) {
          stats[it.id] = {
            ack: parseInt((await env.ANN.get('stat:' + it.id + ':ack')) || '0', 10) || 0,
            seen: parseInt((await env.ANN.get('stat:' + it.id + ':seen')) || '0', 10) || 0,
          };
        }
        // 历史明细：最近 10 个快照里逐条公告（控制台按日期折叠、逐条查看/编辑/复制/恢复）
        const detail = [];
        for (const h of histRaw.slice(0, 10)) {
          const snapItems = (await env.ANN.get('hist:' + h.ts, 'json')) || [];
          detail.push({
            ts: h.ts, at: h.at,
            items: snapItems.map((it) => ({
              id: it.id, type: it.type, level: it.level, title: it.title,
              publishAt: it.publishAt || '', expireAt: it.expireAt || '',
            })),
          });
        }
        return json({ ok: true, live, cfg, draft, history: histRaw, historyDetail: detail, stats, serverTime: nowSec() });
      }
      if (action === 'draft' && req.method === 'POST') {
        const b = (await readJson(req)) || {};
        await env.ANN.put('draft', JSON.stringify(b.draft || null));
        return json({ ok: true });
      }
      if (action === 'publish' && req.method === 'POST') {
        const b = (await readJson(req)) || {};
        const items = (Array.isArray(b.items) ? b.items : []).slice(0, MAX_ITEMS).map((it) => ({
          ...it,
          id: String(it.id || '').slice(0, 80),
          title: String(it.title || '').slice(0, 120),
          body: String(it.body || '').slice(0, MAX_BODY),
        })).filter((it) => it.id && it.title);
        const cfg = {
          pollAfterSeconds: Math.max(60, Math.min(1800, parseInt(b.cfg && b.cfg.pollAfterSeconds, 10) || 300)),
          emergency: !!(b.cfg && b.cfg.emergency),
          enabled: !(b.cfg && b.cfg.enabled === false),
        };
        const stamp = String(nowSec());
        const prev = (await env.ANN.get('live', 'json')) || [];
        await env.ANN.put('hist:' + stamp, JSON.stringify(prev));
        const idx = (await env.ANN.get('hist:index', 'json')) || [];
        idx.unshift({ ts: stamp, at: new Date().toISOString(), count: prev.length });
        const trimmed = idx.slice(0, HIST_MAX);
        await env.ANN.put('hist:index', JSON.stringify(trimmed));
        for (const drop of idx.slice(HIST_MAX)) { try { await env.ANN.delete('hist:' + drop.ts); } catch {} }
        await env.ANN.put('live', JSON.stringify(items));
        await env.ANN.put('cfg', JSON.stringify(cfg));
        return json({ ok: true, published: items.length, snapshot: stamp });
      }
      if (action === 'rollback' && req.method === 'POST') {
        const b = (await readJson(req)) || {};
        const snapshot = await env.ANN.get('hist:' + String(b.ts || ''), 'json');
        if (!snapshot) return json({ ok: false, error: '快照不存在' }, 404);
        const cur = (await env.ANN.get('live', 'json')) || [];
        const stamp = String(nowSec()) + 'r';
        await env.ANN.put('hist:' + stamp, JSON.stringify(cur));
        const idx = (await env.ANN.get('hist:index', 'json')) || [];
        idx.unshift({ ts: stamp, at: new Date().toISOString(), count: cur.length });
        await env.ANN.put('hist:index', JSON.stringify(idx.slice(0, HIST_MAX)));
        await env.ANN.put('live', JSON.stringify(snapshot));
        return json({ ok: true, restored: snapshot.length });
      }
      if (action === 'cfg' && req.method === 'POST') {
        const b = (await readJson(req)) || {};
        const cur = (await env.ANN.get('cfg', 'json')) || { pollAfterSeconds: 300, emergency: false, enabled: true };
        const next = {
          pollAfterSeconds: Math.max(60, Math.min(1800, parseInt(b.pollAfterSeconds, 10) || cur.pollAfterSeconds || 300)),
          emergency: b.emergency === undefined ? !!cur.emergency : !!b.emergency,
          enabled: b.enabled === undefined ? cur.enabled !== false : !!b.enabled,
        };
        await env.ANN.put('cfg', JSON.stringify(next));
        return json({ ok: true, cfg: next });
      }
      if (action === 'stats' && req.method === 'GET') {
        const live = (await env.ANN.get('live', 'json')) || [];
        const stats = {};
        for (const it of live) {
          stats[it.id] = {
            ack: parseInt((await env.ANN.get('stat:' + it.id + ':ack')) || '0', 10) || 0,
            seen: parseInt((await env.ANN.get('stat:' + it.id + ':seen')) || '0', 10) || 0,
          };
        }
        return json({ ok: true, stats });
      }
      return json({ ok: false, error: '未知接口 ' + action }, 404);
    }

    return json({ ok: false, error: 'not found' }, 404);
  },
};

// ============================ 控制台页面 ============================
const CONSOLE_HTML = `<!DOCTYPE html>
<html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>BLT 公告控制台</title>
<style>
:root{--bg:#0f131a;--panel:#171e28;--panel2:#1d2634;--line:#2b3846;--ink:#dfe6f0;--ink2:#9fb0c4;
--amber:#ffb224;--green:#3fd68f;--red:#f56e6e;--blue:#57a6ff}
*{box-sizing:border-box}
body{margin:0;background:var(--bg);color:var(--ink);font:14px/1.6 "Segoe UI","Microsoft YaHei",sans-serif}
header{display:flex;gap:10px;align-items:center;padding:12px 16px;background:var(--panel);border-bottom:1px solid var(--line);flex-wrap:wrap}
h1{font-size:15px;margin:0;letter-spacing:1px}
.wrap{display:grid;grid-template-columns:280px 1fr 360px;gap:12px;padding:12px}
@media (max-width:1100px){.wrap{grid-template-columns:1fr}}
.card{background:var(--panel);border:1px solid var(--line);border-radius:8px;padding:12px}
.card h2{font-size:12px;letter-spacing:1.4px;color:var(--ink2);margin:0 0 8px;text-transform:uppercase}
button{background:var(--panel2);color:var(--ink);border:1px solid var(--line);border-radius:6px;padding:7px 12px;cursor:pointer;font-size:13px}
button:hover{border-color:var(--amber);color:var(--amber)}
button.primary{background:var(--amber);border-color:var(--amber);color:#1a1300;font-weight:700}
button.danger{border-color:var(--red);color:var(--red)}
input,select,textarea{width:100%;background:var(--bg);color:var(--ink);border:1px solid var(--line);border-radius:6px;padding:7px 9px;font:13px/1.5 inherit}
textarea{min-height:90px;resize:vertical}
label{display:block;font-size:12px;color:var(--ink2);margin:10px 0 4px}
.row{display:flex;gap:8px;flex-wrap:wrap;align-items:center}
.item{border:1px solid var(--line);border-radius:6px;padding:8px 10px;margin-bottom:8px;background:var(--panel2);cursor:pointer}
.item.active{border-color:var(--amber)}
.badge{display:inline-block;font-size:11px;padding:1px 6px;border-radius:4px;margin-right:6px;background:#0d1117;border:1px solid var(--line)}
.b-normal{color:var(--blue);border-color:var(--blue)}
.b-sticky{color:var(--green);border-color:var(--green)}
.b-ack{color:var(--red);border-color:var(--red)}
.muted{color:var(--ink2);font-size:12px}
.mock{border:1px solid var(--line);border-radius:8px;background:#0b0f14;padding:8px;font-size:12px}
.mock .bar{background:var(--panel2);border-radius:4px;padding:4px 8px;margin-bottom:6px}
.mock .sticky{background:rgba(63,214,143,.12);border:1px solid var(--green);color:var(--green);border-radius:4px;padding:5px 8px;margin-bottom:6px}
.mock .body{color:var(--ink2);padding:14px 8px;text-align:center}
.mock .corner{width:190px;margin-left:auto;background:var(--panel);border:1px solid var(--line);border-left:3px solid var(--amber);border-radius:6px;padding:7px 9px}
.mock .modal{margin:12px auto;width:240px;background:var(--panel);border:1px solid var(--line);border-radius:8px;padding:10px;text-align:center}
.hint{font-size:12px;color:var(--ink2);margin-top:6px}
.radio{display:inline-flex;gap:6px;align-items:center;margin-right:12px;font-size:13px;color:var(--ink)}
.radio input{width:auto}
table{width:100%;border-collapse:collapse;font-size:12px}
td,th{border-bottom:1px solid var(--line);padding:5px 4px;text-align:left}
#login{max-width:420px;margin:80px auto}
.tpl{flex:1;min-width:88px}
.hist-group{border:1px solid var(--line);border-radius:6px;margin-bottom:8px;background:var(--panel2)}
.hist-group>summary{cursor:pointer;padding:7px 10px;font-size:12px;color:var(--ink2);list-style:none}
.hist-group>summary::-webkit-details-marker{display:none}
.hist-group>summary::before{content:'\u25b8 ';color:var(--ink2)}
.hist-group[open]>summary::before{content:'\u25be '}
.hist-item{display:flex;flex-wrap:wrap;align-items:center;gap:6px;padding:7px 10px;border-top:1px solid var(--line);font-size:12.5px}
.hist-item .t{flex:1 0 100%;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;margin-bottom:2px}
.hist-item button{padding:2px 8px;font-size:11.5px}
.itm-acts{display:flex;gap:6px;margin-top:6px}
.itm-acts button{padding:2px 8px;font-size:11.5px}
</style></head><body>
<header>
  <h1>BLT 公告控制台</h1>
  <span class="muted" id="status">未登录</span>
  <span style="flex:1"></span>
  <label class="radio" title="紧急模式：客户端轮询 5 分钟 → 60 秒"><input type="checkbox" id="emergency"> 紧急模式</label>
  <label class="radio" title="关闭后客户端不再显示任何公告"><input type="checkbox" id="enabled" checked> 公告开关</label>
  <button id="btnLogout">退出</button>
</header>

<div id="login" class="card">
  <h2>控制台登录</h2>
  <label>管理密钥（部署时设置的 ADMIN_TOKEN）</label>
  <input type="password" id="token" placeholder="粘贴管理密钥">
  <div class="row" style="margin-top:12px"><button class="primary" id="btnLogin">登录</button></div>
  <div class="hint" id="loginMsg">密钥只保存在本浏览器，不会上传到别处。</div>
</div>

<div class="wrap" id="app" style="display:none">
  <div class="card">
    <h2>快速新建</h2>
    <div class="row" id="tpl">
      <button class="tpl" data-tpl="normal">＋ 普通通知</button>
      <button class="tpl" data-tpl="sticky">＋ 持续通知</button>
      <button class="tpl" data-tpl="ack">＋ 强通知</button>
    </div>
    <div class="hint">点模板即按该类型新建：普通=右下角卡片，持续=顶部常驻横幅，强通知=必须点确认。</div>
    <h2 style="margin-top:14px">线上公告</h2>
    <div id="list"></div>
    <div class="row" style="margin-top:8px"><button id="btnNew">＋ 空白新建</button><button id="btnReload">刷新</button></div>
    <div class="hint" id="listHint"></div>
    <h2 style="margin-top:14px">发布历史（按日期折叠）</h2>
    <div id="hist"></div>
  </div>

  <div class="card">
    <h2>编辑</h2>
    <label>类型</label>
    <div class="row">
      <label class="radio"><input type="radio" name="type" value="normal" checked> 普通通知（角落卡片）</label>
      <label class="radio"><input type="radio" name="type" value="sticky"> 持续通知（常驻横幅）</label>
      <label class="radio"><input type="radio" name="type" value="ack"> 强通知（必须确认）</label>
    </div>
    <div class="row">
      <div style="flex:1"><label>级别</label>
        <select id="level"><option value="info">info 普通</option><option value="warn">warn 提醒</option><option value="critical">critical 紧急（置顶）</option></select>
      </div>
      <div style="flex:1"><label>强通知强度</label>
        <select id="ackMode"><option value="soft">soft 可稍后（推荐）</option><option value="hard">hard 不确认不能用</option></select>
      </div>
    </div>
    <label>标题</label><input id="title" maxlength="120" placeholder="今晚 23:00 服务器维护">
    <label>正文（纯文本，最多 2000 字）</label><textarea id="body" maxlength="2000" placeholder="预计 30 分钟，期间连接会中断，请提前保存记录。"></textarea>
    <div class="row">
      <div style="flex:1"><label>按钮① 文字</label><input id="act1" placeholder="立即更新"></div>
      <div style="flex:1"><label>按钮① 动作</label>
        <select id="act1kind"><option value="">无</option><option value="update">调应用内更新</option><option value="url">打开链接</option></select>
      </div>
    </div>
    <label>按钮① 链接（动作选“打开链接”时填 https）</label><input id="act1url" placeholder="https://...">
    <div class="row">
      <div style="flex:1"><label>发布方式</label>
        <select id="pubmode"><option value="now">立即发布</option><option value="later">定时发布</option></select>
      </div>
      <div style="flex:1"><label>定时发布时间（本地）</label><input type="datetime-local" id="pubat"></div>
    </div>
    <label>过期时间（本地，留空=不过期；到点自动下架）</label><input type="datetime-local" id="expire">
    <div class="row">
      <div style="flex:1"><label>通道</label><select id="channel"><option value="">全部</option><option value="maui">maui</option><option value="electron">electron</option></select></div>
      <div style="flex:1"><label>最低版本（可选）</label><input id="minV" placeholder="0.1.3"></div>
      <div style="flex:1"><label>最高版本（可选）</label><input id="maxV" placeholder=""></div>
    </div>
    <label>指定 UID（每行一个，留空=所有用户）</label><textarea id="uids" style="min-height:60px" placeholder="10412378"></textarea>
    <label class="radio" style="margin-top:8px"><input type="checkbox" id="repeat"> 到期前重复显示（已读也继续显示，适合“维护中”）</label>
    <div class="row" style="margin-top:12px">
      <button class="primary" id="btnPublish">发布</button>
      <button id="btnDraft">保存草稿</button>
      <button class="danger" id="btnDelete">从线上删除此条</button>
    </div>
    <div class="hint" id="editMsg"></div>
  </div>

  <div class="card">
    <h2>效果预览（应用内的固定位置）</h2>
    <div class="mock">
      <div class="bar">顶栏 · 房间 / WS / 在线 / 端口</div>
      <div class="sticky" id="pvSticky">持续通知：一直显示在这里（顶部横幅）</div>
      <div class="body">页面内容…<div class="corner" id="pvNormal">普通通知：出现在右下角<br><span class="muted">标题 + 正文 + 按钮</span></div></div>
      <div class="modal" id="pvAck">强通知：全屏模态<br><span class="muted">必须点「我已阅读并确认」</span><br><button class="primary" style="margin-top:6px">我已阅读并确认</button></div>
    </div>
    <h2 style="margin-top:14px">回执统计</h2>
    <table><thead><tr><th>公告</th><th>已读</th><th>已确认</th></tr></thead><tbody id="stats"></tbody></table>
    <div class="hint">回执由客户端在显示/确认时上报（只存 uid 哈希与计数）。</div>
  </div>
</div>

<script>
var TOKEN = localStorage.getItem('blt_admin_token') || '';
var state = { live: [], cfg: {}, history: [], stats: {} };
var editing = null;

function esc(s){ return String(s==null?'':s).replace(/[&<>"']/g, function(c){ return {'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]; }); }
function $(id){ return document.getElementById(id); }
function showErr(el, msg, ok){ el.textContent = msg; el.style.color = ok ? 'var(--green)' : 'var(--red)'; }

async function api(path, method, body){
  var r = await fetch('/admin/api/' + path, {
    method: method || 'GET',
    headers: { 'Authorization': 'Bearer ' + TOKEN, 'Content-Type': 'application/json' },
    body: body ? JSON.stringify(body) : undefined
  });
  var j = null; try { j = await r.json(); } catch(e){}
  if (!r.ok) throw new Error((j && j.error) || ('HTTP ' + r.status));
  return j;
}

function localToIso(v){ return v ? new Date(v).toISOString() : ''; }
function isoToLocal(s){ if(!s) return ''; var d=new Date(s); if(isNaN(d)) return ''; var p=function(n){return (n<10?'0':'')+n}; return d.getFullYear()+'-'+p(d.getMonth()+1)+'-'+p(d.getDate())+'T'+p(d.getHours())+':'+p(d.getMinutes()); }

function typeBadge(t){
  var m = { normal: ['普','b-normal'], sticky: ['持','b-sticky'], ack: ['强','b-ack'] };
  var v = m[t] || m.normal;
  return '<span class="badge ' + v[1] + '">' + v[0] + '</span>';
}
function statusOf(it){
  var now = Date.now();
  if (it.publishAt && Date.parse(it.publishAt) > now) return '<span class="muted">已排期 ' + isoToLocal(it.publishAt).replace('T',' ') + '</span>';
  if (it.expireAt && Date.parse(it.expireAt) <= now) return '<span class="muted">已过期</span>';
  return '<span style="color:var(--green)">生效中</span>';
}

function liveStatus(it){
  var now = Date.now();
  if (it.publishAt && Date.parse(it.publishAt) > now) return '已排期';
  if (it.expireAt && Date.parse(it.expireAt) <= now) return '已过期';
  return '生效中';
}

function render(){
  // ---- 线上公告：点标题编辑；行内小按钮可 编辑/复制/提前结束/删除 ----
  var l = $('list'); l.innerHTML = '';
  state.live.forEach(function(it, i){
    var d = document.createElement('div');
    d.className = 'item' + (editing === i ? ' active' : '');
    d.innerHTML = typeBadge(it.type) + '<b>' + esc(it.title) + '</b><div class="muted">' + statusOf(it) +
      (it.level === 'critical' ? ' · <span style="color:var(--red)">紧急</span>' : '') +
      ((it.uids && it.uids.length) ? ' · 定向 ' + it.uids.length + ' 个 UID' : '') + '</div>' +
      '<div class="itm-acts">' +
        '<button data-a="edit">编辑</button>' +
        '<button data-a="copy">复制</button>' +
        '<button data-a="end" title="把过期时间设为现在，客户端下一次轮询后即不再显示">提前结束</button>' +
        '<button data-a="del" class="danger">删除</button>' +
      '</div>';
    d.querySelector('b').onclick = function(){ loadItem(i); };
    d.querySelector('[data-a=edit]').onclick = function(ev){ ev.stopPropagation(); loadItem(i); };
    d.querySelector('[data-a=copy]').onclick = function(ev){ ev.stopPropagation(); duplicateItem(i); };
    d.querySelector('[data-a=end]').onclick = function(ev){ ev.stopPropagation(); endItem(i); };
    d.querySelector('[data-a=del]').onclick = function(ev){ ev.stopPropagation(); deleteItem(i); };
    l.appendChild(d);
  });
  if (!state.live.length) l.innerHTML = '<div class="muted">线上暂无公告</div>';

  // ---- 发布历史：按日期折叠，逐条列出并可单独操作 ----
  var h = $('hist'); h.innerHTML = '';
  var detail = state.historyDetail || [];
  if (!detail.length) h.innerHTML = '<div class="muted">暂无历史</div>';
  detail.forEach(function(g){
    var gd = document.createElement('details');
    gd.className = 'hist-group';
    gd.open = detail.indexOf(g) === 0;
    var when = g.at ? String(g.at).replace('T', ' ').slice(0, 16) : g.ts;
    gd.innerHTML = '<summary>' + esc(when) + ' · ' + g.items.length + ' 条</summary>';
    g.items.forEach(function(e){
      var liveIdx = -1;
      for (var k = 0; k < state.live.length; k++) if (state.live[k].id === e.id) liveIdx = k;
      var row = document.createElement('div');
      row.className = 'hist-item';
      row.innerHTML = typeBadge(e.type) +
        '<span class="t" title="' + esc(e.title) + '">' + esc(e.title) + '</span>' +
        '<span class="muted">' + (liveIdx >= 0 ? liveStatus(state.live[liveIdx]) : '已下架') + '</span>' +
        '<button data-a="load">编辑</button>' +
        '<button data-a="copy">复制</button>' +
        (liveIdx >= 0 ? '<button data-a="end">结束</button><button data-a="del" class="danger">删除</button>' : '') +
        '<button data-a="rb" title="把线上内容整体回滚到这一次发布">恢复</button>';
      row.querySelector('[data-a=load]').onclick = function(){ loadFromHistory(e, liveIdx); };
      row.querySelector('[data-a=copy]').onclick = function(){ copyFromEntry(e); };
      if (liveIdx >= 0) {
        row.querySelector('[data-a=end]').onclick = function(){ endItem(liveIdx); };
        row.querySelector('[data-a=del]').onclick = function(){ deleteItem(liveIdx); };
      }
      row.querySelector('[data-a=rb]').onclick = function(){ rollback(g.ts); };
      gd.appendChild(row);
    });
    h.appendChild(gd);
  });

  // ---- 回执统计 ----
  var st = $('stats'); st.innerHTML = '';
  state.live.forEach(function(it){
    var v = state.stats[it.id] || { ack: 0, seen: 0 };
    var tr = document.createElement('tr');
    tr.innerHTML = '<td>' + esc(it.title).slice(0, 18) + '</td><td>' + v.seen + '</td><td>' + v.ack + '</td>';
    st.appendChild(tr);
  });

  $('emergency').checked = !!(state.cfg && state.cfg.emergency);
  $('enabled').checked = !(state.cfg && state.cfg.enabled === false);
  var t = editing != null && state.live[editing] ? state.live[editing].type : 'normal';
  preview(t);
}

// ---- 逐条操作 ----

function newId(){ return 'a' + Date.now().toString(36) + Math.floor(Math.random() * 1000).toString(36); }

function newFromTemplate(type){
  resetForm();
  var r = document.querySelector('input[name=type][value="' + type + '"]');
  if (r) { r.checked = true; preview(type); }
  $('title').focus();
  showErr($('editMsg'), type === 'ack'
    ? '强通知：客户端全屏弹出、必须点确认（soft 可稍后，hard 不确认不能用）'
    : (type === 'sticky' ? '持续通知：常驻顶部横幅，直到过期或被下架' : '普通通知：右下角卡片，用户可点「知道了」关闭'), true);
}

function duplicateItem(i){
  var src = state.live[i]; if (!src) return;
  resetForm();
  var copy = JSON.parse(JSON.stringify(src));
  copy.id = newId();
  copy.title = (src.title + '（副本）').slice(0, 120);
  state.live = state.live.concat([copy]);
  editing = state.live.length - 1;
  loadItem(editing);
  showErr($('editMsg'), '已复制为一条新公告（尚未发布）：改好后点发布', true);
}

function findHistoryEntry(id){
  var found = null;
  (state.historyDetail || []).forEach(function(g){
    g.items.forEach(function(x){ if (x.id === id && !found) found = x; });
  });
  return found;
}

function copyFromEntry(e){
  resetForm();
  $('title').value = ((e.title || '') + '（副本）').slice(0, 120);
  var r = document.querySelector('input[name=type][value="' + (e.type || 'normal') + '"]');
  if (r) r.checked = true;
  $('level').value = e.level || 'info';
  if (e.expireAt) $('expire').value = isoToLocal(e.expireAt);
  preview(e.type || 'normal');
  showErr($('editMsg'), '已按该条新建副本（未发布，请补正文后点发布）', true);
}

function loadFromHistory(e, liveIdx){
  if (liveIdx >= 0) { loadItem(liveIdx); return; }
  resetForm();
  $('title').value = e.title || '';
  var r = document.querySelector('input[name=type][value="' + (e.type || 'normal') + '"]');
  if (r) r.checked = true;
  $('level').value = e.level || 'info';
  if (e.expireAt) $('expire').value = isoToLocal(e.expireAt);
  preview(e.type || 'normal');
  showErr($('editMsg'), '该条已不在线上：这里是历史内容；发布会以同 id 重新上线，或先点「复制」换新 id', true);
}

async function publishItems(items, msg){
  try {
    await api('publish', 'POST', { items: items, cfg: state.cfg });
    showErr($('editMsg'), msg, true);
    editing = null;
    await reload();
  } catch (e) { showErr($('editMsg'), e.message, false); }
}

function endItem(i){
  var items = state.live.slice();
  if (!items[i]) return;
  if (!confirm('提前结束「' + items[i].title + '」？客户端下一次轮询后就会停止显示。')) return;
  items[i] = JSON.parse(JSON.stringify(items[i]));
  items[i].expireAt = new Date().toISOString();
  publishItems(items, '已提前结束该条公告');
}

function deleteItem(i){
  var items = state.live.slice();
  if (!items[i]) return;
  if (!confirm('从线上删除「' + items[i].title + '」？（内容仍保留在发布历史里，可随时恢复）')) return;
  items.splice(i, 1);
  publishItems(items, '已删除该条公告');
}

function preview(type){
  $('pvSticky').style.display = type === 'sticky' ? '' : 'none';
  $('pvNormal').style.display = type === 'normal' ? '' : 'none';
  $('pvAck').style.display = type === 'ack' ? '' : 'none';
}
document.querySelectorAll('input[name=type]').forEach(function(r){ r.onchange = function(){ preview(r.value); }; });

function resetForm(){
  editing = null;
  $('title').value = ''; $('body').value = '';
  $('level').value = 'info'; $('ackMode').value = 'soft';
  document.querySelector('input[name=type][value=normal]').checked = true;
  $('act1').value = ''; $('act1kind').value = ''; $('act1url').value = '';
  $('pubmode').value = 'now'; $('pubat').value = ''; $('expire').value = '';
  $('channel').value = ''; $('minV').value = ''; $('maxV').value = ''; $('uids').value = '';
  $('repeat').checked = false;
  showErr($('editMsg'), '', true);
  preview('normal'); render();
}
function loadItem(i){
  var it = state.live[i]; if (!it) return;
  editing = i;
  document.querySelector('input[name=type][value="' + (it.type || 'normal') + '"]').checked = true;
  $('level').value = it.level || 'info'; $('ackMode').value = it.ackMode || 'soft';
  $('title').value = it.title || ''; $('body').value = it.body || '';
  var a = (it.actions && it.actions[0]) || {};
  $('act1').value = a.label || ''; $('act1kind').value = a.kind || (a.url ? 'url' : ''); $('act1url').value = a.url || '';
  $('pubmode').value = it.publishAt && Date.parse(it.publishAt) > Date.now() ? 'later' : 'now';
  $('pubat').value = isoToLocal(it.publishAt); $('expire').value = isoToLocal(it.expireAt);
  $('channel').value = it.channel || ''; $('minV').value = it.minVersion || ''; $('maxV').value = it.maxVersion || '';
  $('uids').value = (it.uids || []).join('\\n'); $('repeat').checked = !!it.repeatUntilExpire;
  preview(it.type || 'normal'); render();
}

function collect(){
  var type = document.querySelector('input[name=type]:checked').value;
  var label = $('act1').value.trim(), kind = $('act1kind').value, url = $('act1url').value.trim();
  var actions = [];
  if (kind === 'update') actions.push({ label: label || '立即更新', kind: 'update' });
  else if (kind === 'url' && url) actions.push({ label: label || '查看详情', url: url });
  var uids = $('uids').value.split(/[\\s,，]+/).map(function(x){return x.trim();}).filter(Boolean);
  var it = {
    id: editing != null && state.live[editing] ? state.live[editing].id : ('a' + Date.now().toString(36)),
    type: type,
    level: $('level').value,
    title: $('title').value.trim(),
    body: $('body').value,
    publishAt: $('pubmode').value === 'later' ? localToIso($('pubat').value) : '',
    expireAt: localToIso($('expire').value),
    channel: $('channel').value,
    minVersion: $('minV').value.trim(),
    maxVersion: $('maxV').value.trim(),
    repeatUntilExpire: $('repeat').checked,
    actions: actions
  };
  if (type === 'ack') it.ackMode = $('ackMode').value;
  if (uids.length) it.uids = uids;
  return it;
}

async function publish(){
  var it = collect();
  if (!it.title) { showErr($('editMsg'), '标题必填', false); return; }
  var items = state.live.slice();
  if (editing != null) items[editing] = it; else items.push(it);
  try {
    await api('publish', 'POST', { items: items, cfg: { pollAfterSeconds: state.cfg.pollAfterSeconds || 300, emergency: $('emergency').checked, enabled: $('enabled').checked } });
    showErr($('editMsg'), '已发布，客户端 5 分钟内（紧急模式 1 分钟）生效', true);
    await reload(); editing = null;
  } catch (e) { showErr($('editMsg'), e.message, false); }
}
async function removeItem(){
  if (editing == null) { showErr($('editMsg'), '请先在左侧选中一条', false); return; }
  if (!confirm('确定把这条公告从线上删除？（内容保留在历史里，可恢复）')) return;
  var items = state.live.slice(); items.splice(editing, 1);
  try { await api('publish', 'POST', { items: items, cfg: state.cfg }); showErr($('editMsg'), '已删除', true); editing = null; await reload(); }
  catch (e) { showErr($('editMsg'), e.message, false); }
}
async function saveDraft(){
  try { await api('draft', 'POST', { draft: collect() }); showErr($('editMsg'), '草稿已保存', true); }
  catch (e) { showErr($('editMsg'), e.message, false); }
}
async function rollback(ts){
  if (!confirm('恢复到这个版本？当前线上内容会被替换（当前内容也会存为历史）。')) return;
  try { await api('rollback', 'POST', { ts: ts }); showErr($('editMsg'), '已恢复', true); await reload(); }
  catch (e) { showErr($('editMsg'), e.message, false); }
}
async function saveCfg(){
  try { await api('cfg', 'POST', { emergency: $('emergency').checked, enabled: $('enabled').checked }); state.cfg.emergency = $('emergency').checked; state.cfg.enabled = $('enabled').checked; }
  catch (e) { }
}
async function reload(){
  var s = await api('state');
  state.live = s.live || []; state.cfg = s.cfg || {}; state.history = s.history || [];
  state.historyDetail = s.historyDetail || []; state.stats = s.stats || {};
  $('status').textContent = '已连接 · 线上 ' + state.live.length + ' 条 · 历史 ' + (state.historyDetail || []).length + ' 版' + (state.cfg.emergency ? ' · 紧急模式' : '');
  $('listHint').textContent = '点击列表中的公告可编辑；发布后立即生效。';
  render();
}

function enter(){
  $('login').style.display = 'none';
  $('app').style.display = '';
  reload().catch(function(e){ $('status').textContent = '连接失败：' + e.message; });
}
$('btnLogin').onclick = function(){
  var candidate = $('token').value.replace(/\s+/g, '');   // 粘贴时常带空格/换行
  if (!candidate) { showErr($('loginMsg'), '请输入管理密钥', false); return; }
  TOKEN = candidate;
  showErr($('loginMsg'), '正在校验…', true);
  api('state').then(function(){
    localStorage.setItem('blt_admin_token', candidate);     // 只在成功后记住
    enter();
  }).catch(function(e){
    TOKEN = '';
    localStorage.removeItem('blt_admin_token');
    $('token').value = '';
    var hint = (e && /401|未授权/.test(e.message || ''))
      ? '密钥不正确：请确认只粘贴一次、前后没有多余字符（常见误操作是粘了两遍）'
      : (e && e.message) || '登录失败';
    showErr($('loginMsg'), hint, false);
    $('token').focus();
  });
};
$('btnLogout').onclick = function(){ localStorage.removeItem('blt_admin_token'); location.reload(); };
$('btnNew').onclick = resetForm;
Array.prototype.forEach.call(document.querySelectorAll('#tpl .tpl'), function(b){
  b.onclick = function(){ newFromTemplate(b.getAttribute('data-tpl')); };
});
$('btnReload').onclick = function(){ reload().catch(function(e){ showErr($('editMsg'), e.message, false); }); };
$('btnPublish').onclick = publish;
$('btnDelete').onclick = removeItem;
$('btnDraft').onclick = saveDraft;
$('emergency').onchange = saveCfg;
$('enabled').onchange = saveCfg;
if (TOKEN) { $('token').value = TOKEN; api('state').then(enter).catch(function(){ TOKEN = ''; localStorage.removeItem('blt_admin_token'); }); }
</script></body></html>`;
