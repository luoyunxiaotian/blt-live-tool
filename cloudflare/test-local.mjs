/**
 * 本地测试：不依赖 Cloudflare，用内存 KV 跑通 Worker 的全部逻辑。
 * 运行：node cloudflare/test-local.mjs
 */
import worker from './announce-worker.mjs';

const TOKEN = 'test-token-1234567890';

function fakeKV() {
  const m = new Map();
  return {
    async get(k, type) {
      if (!m.has(k)) return null;
      const v = m.get(k);
      return type === 'json' ? JSON.parse(v) : v;
    },
    async put(k, v, opts) { m.set(k, typeof v === 'string' ? v : JSON.stringify(v)); },
    async delete(k) { m.delete(k); },
    _size: () => m.size,
  };
}

const env = { ANN: fakeKV(), ADMIN_TOKEN: TOKEN };
const base = 'https://announce.test';
let pass = 0, fail = 0;

function check(name, cond, extra = '') {
  if (cond) { pass++; console.log('  ✓ ' + name); }
  else { fail++; console.log('  ✗ ' + name + (extra ? '  → ' + extra : '')); }
}

async function call(path, { method = 'GET', body, token, headers = {} } = {}) {
  const req = new Request(base + path, {
    method,
    headers: {
      'content-type': 'application/json',
      ...(token ? { authorization: 'Bearer ' + token } : {}),
      ...headers,
    },
    body: body ? JSON.stringify(body) : undefined,
  });
  const res = await worker.fetch(req, env);
  let json = null;
  const text = await res.text();
  try { json = JSON.parse(text); } catch { }
  return { status: res.status, json, text, headers: res.headers };
}

const now = Date.now();
const items = [
  { id: 'n1', type: 'normal', level: 'info', title: '普通公告', body: '角落显示' },
  { id: 's1', type: 'sticky', level: 'info', title: '常驻提示', body: '顶部横幅' },
  { id: 'a1', type: 'ack', level: 'critical', title: '必须确认', body: '强通知', ackMode: 'soft' },
  { id: 'u1', type: 'normal', title: '定向通知', body: '只给指定 UID', uids: ['10412378'] },
  { id: 'p1', type: 'normal', title: '定时公告', body: '未来才出现', publishAt: new Date(now + 3600e3).toISOString() },
  { id: 'v1', type: 'normal', title: '版本门槛', body: '只给 0.2.0+', minVersion: '0.2.0' },
  { id: 'e1', type: 'normal', title: '已过期', body: '不该出现', expireAt: new Date(now - 3600e3).toISOString() },
];

console.log('1) 空 KV 拉取');
{
  const r = await call('/announcements');
  check('返回 items 空数组', Array.isArray(r.json.items) && r.json.items.length === 0);
  check('默认轮询 300 秒', r.json.pollAfterSeconds === 300);
  check('带 serverTime', typeof r.json.serverTime === 'number');
}

console.log('2) 管理鉴权');
{
  const noTok = await call('/admin/api/state');
  check('无 token → 401', noTok.status === 401);
  const badTok = await call('/admin/api/state', { token: 'wrong' });
  check('错误 token → 401', badTok.status === 401);
  const ok = await call('/admin/api/state', { token: TOKEN });
  check('正确 token → 200', ok.status === 200 && ok.json.ok);
}

console.log('3) 发布 + 客户端过滤');
{
  const pub = await call('/admin/api/publish', { method: 'POST', token: TOKEN, body: { items, cfg: { pollAfterSeconds: 300 } } });
  check('发布成功（7 条）', pub.json.ok && pub.json.published === 7, JSON.stringify(pub.json));

  const mine = await call('/announcements?uid=10412378&v=0.1.3&ch=maui');
  const ids = mine.json.items.map((x) => x.id).sort();
  check('定向命中本人 → u1 出现', ids.includes('u1'), ids.join(','));
  check('定时公告未到点 → p1 不出现', !ids.includes('p1'));
  check('版本门槛 0.2.0 → v1 不出现（本机 0.1.3）', !ids.includes('v1'));
  check('已过期 → e1 不出现', !ids.includes('e1'));
  check('三类都在（n1/s1/a1）', ids.includes('n1') && ids.includes('s1') && ids.includes('a1'));

  const other = await call('/announcements?uid=99999999&v=0.1.3&ch=maui');
  const others = other.json.items.map((x) => x.id);
  check('他人看不到定向条目 u1', !others.includes('u1'));

  const highVer = await call('/announcements?uid=99999999&v=0.2.5&ch=maui');
  check('0.2.5 客户端能看到 v1', highVer.json.items.map((x) => x.id).includes('v1'));

  const wrongCh = await call('/announcements?uid=1&v=0.1.3&ch=electron');
  check('通道不匹配时全部过滤（本项目条目未限通道 → 仍可见）', Array.isArray(wrongCh.json.items));
}

console.log('4) ETag 与 304');
{
  const a = await call('/announcements?uid=10412378&v=0.1.3&ch=maui');
  const etag = a.headers.get('etag');
  check('响应带 ETag', !!etag);
  const b = await call('/announcements?uid=10412378&v=0.1.3&ch=maui', { headers: { 'if-none-match': etag } });
  check('命中 ETag → 304 无 body', b.status === 304 && b.text.length === 0);
}

console.log('5) 回执统计（含去重）');
{
  const r1 = await call('/receipt', { method: 'POST', body: { id: 'a1', uid: '10412378', kind: 'ack' } });
  const r2 = await call('/receipt', { method: 'POST', body: { id: 'a1', uid: '10412378', kind: 'ack' } });
  const r3 = await call('/receipt', { method: 'POST', body: { id: 'a1', uid: '88888888', kind: 'seen' } });
  check('首次回执 ok', r1.json.ok && !r1.json.dup);
  check('同用户重复回执去重', r2.json.ok && r2.json.dup === true);
  check('另一用户计数', r3.json.ok);
  const st = await call('/admin/api/state', { token: TOKEN });
  check('统计：ack=1 / seen=1', st.json.stats.a1.ack === 1 && st.json.stats.a1.seen === 1, JSON.stringify(st.json.stats.a1));
}

console.log('6) 紧急模式');
{
  const cfg = await call('/admin/api/cfg', { method: 'POST', token: TOKEN, body: { emergency: true } });
  check('写入紧急模式', cfg.json.ok && cfg.json.cfg.emergency === true);
  const r = await call('/announcements?uid=1&v=0.1.3&ch=maui');
  check('轮询变 60 秒', r.json.pollAfterSeconds === 60);
  const off = await call('/admin/api/cfg', { method: 'POST', token: TOKEN, body: { emergency: false } });
  const r2 = await call('/announcements');
  check('关闭后回到 300 秒', off.json.cfg.emergency === false && r2.json.pollAfterSeconds === 300);
  const disabled = await call('/admin/api/cfg', { method: 'POST', token: TOKEN, body: { enabled: false } });
  const r3 = await call('/announcements');
  check('总开关关闭 → 客户端收到空列表', disabled.json.cfg.enabled === false && r3.json.items.length === 0);
  await call('/admin/api/cfg', { method: 'POST', token: TOKEN, body: { enabled: true } });
}

console.log('7) 发布历史与回滚');
{
  const before = await call('/admin/api/state', { token: TOKEN });
  const histCount = before.json.history.length;
  check('发布产生了历史快照', histCount >= 1, 'hist=' + histCount);
  const pub2 = await call('/admin/api/publish', { method: 'POST', token: TOKEN, body: { items: [items[0]], cfg: { pollAfterSeconds: 300 } } });
  const after2 = await call('/admin/api/state', { token: TOKEN });
  check('第二次发布后线上只剩 1 条', after2.json.live.length === 1);
  const snap = after2.json.history[0].ts;
  const rb = await call('/admin/api/rollback', { method: 'POST', token: TOKEN, body: { ts: snap } });
  const after3 = await call('/admin/api/state', { token: TOKEN });
  check('回滚恢复上一版（7 条）', rb.json.ok && after3.json.live.length === 7, 'live=' + after3.json.live.length);
}

console.log('8) 控制台页面');
{
  const page = await call('/admin');
  check('GET /admin 返回 HTML', page.status === 200 && page.text.indexOf('<title>BLT 公告控制台</title>') >= 0);
  check('含三种类型选择', /name="type" value="normal"/.test(page.text) && /value="sticky"/.test(page.text) && /value="ack"/.test(page.text));
  check('含 UID 定向输入', /id="uids"/.test(page.text));
  check('含发布/草稿/删除按钮', /id="btnPublish"/.test(page.text) && /id="btnDraft"/.test(page.text) && /id="btnDelete"/.test(page.text));
  check('含回执统计表', /id="stats"/.test(page.text));
}

console.log('\n结果：' + pass + ' 通过 / ' + fail + ' 失败');
process.exit(fail === 0 ? 0 : 1);
