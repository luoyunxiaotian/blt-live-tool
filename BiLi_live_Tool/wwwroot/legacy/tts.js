(function () {
  const w = window;
  // 本地 TTS 服务（Edge 8020 / MOSS 8021）统一走管理服务端同源代理：
  // Electron 下页面是 file://，浏览器下是 127.0.0.1:7360，直连这两个端口都会被 CORS 拦截
  const API_BASE = (w.location && w.location.protocol === 'file:') ? 'http://127.0.0.1:7360' : '';
  const EDGE_BASE = API_BASE + '/api/tts/edge';
  const MOSS_BASE = API_BASE + '/api/tts/moss';
  const TYPE_KEY = { danmu: 'danmu', gifts: 'gift', gifts_merged: 'gift', superchat: 'superchat', interact: 'welcome', guard: 'welcome' };

  // ---- 引擎选择：moss(本地/离线/6中文音色+克隆) | edge(在线) | sys(系统语音兜底) ----
  function engineOf() {
    const e = String(getCfg().engine || 'edge');
    if (e === 'kokoro') return 'moss';   // 旧版 Kokoro 配置迁移到 MOSS
    return (e === 'moss' || e === 'sys') ? e : 'edge';
  }
  // 本地引擎按需拉起（主进程 spawn）
  let mossVoices = null;        // [{id, name, desc, custom}]
  let mossModelStatus = null;   // /models/status 最近一次结果
  let _mossRetry = 0;
  let _mossEnsured = false;
  let _mossUserStopped = false;   // 用户手动停止后，不再自动拉起（直到手动启动）
  function ensureMossServer() {
    if (_mossUserStopped || _mossEnsured) return;
    _mossEnsured = true;
    try {
      if (w.electronAPI && w.electronAPI.tts && w.electronAPI.tts.mossEnsure) {
        w.electronAPI.tts.mossEnsure().catch(function () {});
      }
    } catch (e) {}
  }
  // 本地服务生命周期（主进程管理进程；模型文件不删除）
  function mossServiceStart(cb) {
    _mossUserStopped = false;
    _mossEnsured = true;
    try {
      if (w.electronAPI && w.electronAPI.tts && w.electronAPI.tts.mossEnsure) {
        w.electronAPI.tts.mossEnsure().then(function (r) {
          mossVoices = null; fetchMossVoices(); fetchMossModelStatus();
          if (cb) cb(r);
        }).catch(function () { if (cb) cb(null); });
      } else if (cb) cb(null);
    } catch (e) { if (cb) cb(null); }
  }
  function mossServiceStop(cb) {
    _mossUserStopped = true;
    try {
      if (w.electronAPI && w.electronAPI.tts && w.electronAPI.tts.mossStop) {
        w.electronAPI.tts.mossStop().then(function (r) { if (cb) cb(r); }).catch(function () { if (cb) cb(null); });
      } else if (cb) cb(null);
    } catch (e) { if (cb) cb(null); }
  }
  function mossServiceRestart(cb) {
    _mossUserStopped = false;
    _mossEnsured = true;
    try {
      if (w.electronAPI && w.electronAPI.tts && w.electronAPI.tts.mossRestart) {
        w.electronAPI.tts.mossRestart().then(function (r) {
          mossVoices = null; fetchMossVoices(); fetchMossModelStatus();
          if (cb) cb(r);
        }).catch(function () { if (cb) cb(null); });
      } else if (cb) cb(null);
    } catch (e) { if (cb) cb(null); }
  }
  function mossServiceStatus(cb) {
    try {
      if (w.electronAPI && w.electronAPI.tts && w.electronAPI.tts.mossStatus) {
        w.electronAPI.tts.mossStatus().then(function (r) { if (cb) cb(r); }).catch(function () { if (cb) cb(null); });
      } else if (cb) cb({ unsupported: true });
    } catch (e) { if (cb) cb({ unsupported: true }); }
  }
  function fetchMossVoices() {
    try {
      const xhr = new w.XMLHttpRequest();
      xhr.open('GET', MOSS_BASE + '/voices', true);
      xhr.timeout = 4000;
      xhr.onload = function () {
        if (xhr.status === 200) {
          try {
            const j = JSON.parse(xhr.responseText);
            const list = [];
            (j.builtin || []).forEach(v => list.push({ id: v.id, name: v.name, desc: v.desc || '', custom: false }));
            (j.custom || []).forEach(v => list.push({ id: v.id, name: v.name, desc: '自定义音色（克隆）', custom: true }));
            if (list.length) { mossVoices = list; _mossRetry = 0; emit(); return; }
          } catch (e) {}
        }
        retryMossVoices();
      };
      xhr.onerror = function () { retryMossVoices(); };
      xhr.ontimeout = function () { retryMossVoices(); };
      xhr.send();
    } catch (e) { retryMossVoices(); }
  }
  // 服务启动/模型加载慢：5 秒一次最多 24 次（2 分钟）
  function retryMossVoices() {
    if (mossVoices || _mossRetry >= 24) return;
    _mossRetry++;
    setTimeout(fetchMossVoices, 5000);
  }
  function getMossVoices() { return mossVoices ? mossVoices.slice() : []; }
  function mossReady() { return !!(mossVoices && mossVoices.length); }
  // 模型状态：{ready, downloading, percent, currentFile, error}
  function fetchMossModelStatus(cb) {
    try {
      const xhr = new w.XMLHttpRequest();
      xhr.open('GET', MOSS_BASE + '/models/status', true);
      xhr.timeout = 4000;
      xhr.onload = function () {
        if (xhr.status !== 200) { if (typeof cb === 'function') cb(null); return; }
        try { mossModelStatus = JSON.parse(xhr.responseText); } catch (e) { mossModelStatus = null; }
        if (typeof cb === 'function') cb(mossModelStatus);
        emit();
      };
      xhr.onerror = function () { if (typeof cb === 'function') cb(null); };
      xhr.ontimeout = function () { if (typeof cb === 'function') cb(null); };
      xhr.send();
    } catch (e) { if (typeof cb === 'function') cb(null); }
  }
  function startMossModelDownload() {
    try {
      const xhr = new w.XMLHttpRequest();
      xhr.open('POST', MOSS_BASE + '/models/download', true);
      xhr.setRequestHeader('Content-Type', 'application/json');
      xhr.timeout = 10000;
      xhr.onload = function () { fetchMossModelStatus(); };
      xhr.onerror = function () {};
      xhr.send('{}');
    } catch (e) {}
  }
  function addMossCustomVoice(name, path, cb) {
    try {
      const xhr = new w.XMLHttpRequest();
      xhr.open('POST', MOSS_BASE + '/voices/add', true);
      xhr.setRequestHeader('Content-Type', 'application/json');
      xhr.timeout = 15000;
      xhr.onload = function () {
        let ok = false, err = '';
        try { const j = JSON.parse(xhr.responseText); ok = !!j.ok; err = j.error || ''; } catch (e) {}
        if (ok) { mossVoices = null; fetchMossVoices(); }
        if (typeof cb === 'function') cb(ok, err);
      };
      xhr.onerror = function () { if (typeof cb === 'function') cb(false, '服务未响应'); };
      xhr.send(JSON.stringify({ name: name, path: path }));
    } catch (e) { if (typeof cb === 'function') cb(false, String(e)); }
  }
  function removeMossCustomVoice(id, cb) {
    try {
      const xhr = new w.XMLHttpRequest();
      xhr.open('POST', MOSS_BASE + '/voices/remove', true);
      xhr.setRequestHeader('Content-Type', 'application/json');
      xhr.timeout = 10000;
      xhr.onload = function () {
        mossVoices = null; fetchMossVoices();
        if (typeof cb === 'function') cb(true, '');
      };
      xhr.onerror = function () { if (typeof cb === 'function') cb(false, '服务未响应'); };
      xhr.send(JSON.stringify({ id: id }));
    } catch (e) { if (typeof cb === 'function') cb(false, String(e)); }
  }
  function activateMoss() { ensureMossServer(); if (!mossVoices) fetchMossVoices(); fetchMossModelStatus(); }

  // 数字转中文读法：111 -> 一百一十一；100 -> 一百；10001 -> 一万零一
  // 两个关键点：
  //  1) 延迟补零——遇到 0 只置 pending 标记，等下一个非零数字出现时才补，
  //     避免「100 -> 一百零」的末尾多零与「101 -> 一百零零一」的重复补零。
  //  2) 段间跳位检测——本段数值 < 1000 说明千位为空，拼接更高段时补一个「零」，
  //     修复「10001 -> 一万一」这类缺零。
  function numToCn(n){
    n = Math.floor(Number(n)); if (isNaN(n) || n <= 0) return '';
    const digits = ['零','一','二','三','四','五','六','七','八','九'];
    const units = ['','十','百','千'];
    const bigUnits = ['','万','亿'];
    if (n < 10) return digits[n];
    let out = '', bigIdx = 0, needZero = false, rest = n;
    while (rest > 0 && bigIdx < bigUnits.length) {
      const seg = rest % 10000;
      rest = Math.floor(rest / 10000);
      let segStr = '', zeroPending = false;
      const sd = String(seg);
      for (let i = 0; i < sd.length; i++) {
        const d = Number(sd[i]);
        const pos = sd.length - 1 - i;
        if (d === 0) { if (segStr) zeroPending = true; }
        else { if (zeroPending) { segStr += '零'; zeroPending = false; } segStr += digits[d] + units[pos]; }
      }
      if (segStr) out = segStr + bigUnits[bigIdx] + (needZero && out ? '零' : '') + out;
      if (seg > 0) needZero = (seg < 1000);
      bigIdx++;
    }
    out = out.replace(/^零+/, '').replace(/零+/g, '零');
    if (n >= 10 && n < 20) out = out.replace(/^一十/, '十');
    return out || digits[n];
  }
  let CFG = null;
  let queue = [];
  let tokenSeq = 0;
  let currentToken = 0;
  let speaking = false;
  let currentAudio = null;   // 正在播放的 Edge Audio
  let currentXhr = null;     // 在途 edge-tts 请求

  function defaults() {
    return {
      enabled: true, voice: 'zh-CN-XiaoxiaoNeural', rate: 1, pitch: 1, volume: 1, gain: 0, queueMax: 8,
      guardPriority: true, blacklistUids: '', bannedWords: '',
      danmu: { enabled: true, sayUid: false, minMedal: 0, minHonor: 0, volume: 1 },
      gift: { enabled: true, sayUid: false, minMedal: 0, minHonor: 0, volume: 1 },
      superchat: { enabled: true, sayUid: false, minMedal: 0, minHonor: 0, volume: 1 },
      welcome: { enabled: true, sayUid: false, minMedal: 0, minHonor: 0, volume: 1 }
    };
  }
  function getCfg() { if (!CFG) CFG = defaults(); return CFG; }
  function getConfig() { return getCfg(); }
  function setConfig(cfg) {
    if (cfg) CFG = cfg;
    // 切到本地引擎时按需拉起服务并拉取音色清单
    try { if (engineOf() === 'moss') activateMoss(); } catch (e) {}
    emit();
  }


  // ---- 状态广播 ----
  function emit() {
    try {
      w.dispatchEvent(new CustomEvent('tts:status', { detail: {
        enabled: getCfg().enabled,
        speaking: speaking,
        queueLength: queue.length,
        queue: queue.slice(0, 20).map(q => ({ text: q.text, type: q.type, priority: q.priority })),
        current: speaking && queue[0] ? null : null
      } }));
    } catch (e) {}
  }

  // ---- 音色列表 ----
  let sysVoicesCache = [];
  function loadSysVoices() {
    try {
      const vs = w.speechSynthesis && w.speechSynthesis.getVoices();
      if (vs && vs.length) sysVoicesCache = Array.prototype.slice.call(vs);
      emit();
    } catch (e) {}
  }
  loadSysVoices();
  if (w.speechSynthesis && w.speechSynthesis.onvoiceschanged !== undefined) w.speechSynthesis.onvoiceschanged = loadSysVoices;
  const EDGE_VOICES = [
    { name: 'zh-CN-XiaoxiaoNeural', label: '小晓(女·普通话)', locale: 'zh-CN', gender: 'Female' },
    { name: 'zh-CN-XiaoyiNeural', label: '晓伊(女·普通话)', locale: 'zh-CN', gender: 'Female' },
    { name: 'zh-CN-YunxiNeural', label: '云希(男·普通话)', locale: 'zh-CN', gender: 'Male' },
    { name: 'zh-CN-YunyangNeural', label: '云扬(男·普通话)', locale: 'zh-CN', gender: 'Male' },
    { name: 'zh-CN-YunjianNeural', label: '云健(男·普通话)', locale: 'zh-CN', gender: 'Male' },
    { name: 'zh-CN-YunxiaNeural', label: '云夏(男·普通话)', locale: 'zh-CN', gender: 'Male' },
    { name: 'zh-CN-liaoning-XiaobeiNeural', label: '小北(女·辽宁)', locale: 'zh-CN', gender: 'Female' },
    { name: 'zh-CN-shaanxi-XiaoniNeural', label: '小妮(女·陕西)', locale: 'zh-CN', gender: 'Female' },
    { name: 'zh-HK-HiuGaaiNeural', label: '曉佳(女·粤语)', locale: 'zh-HK', gender: 'Female' },
    { name: 'zh-HK-WanLungNeural', label: '雲龍(男·粤语)', locale: 'zh-HK', gender: 'Male' },
    { name: 'zh-TW-HsiaoChenNeural', label: '曉臻(女·台湾)', locale: 'zh-TW', gender: 'Female' },
    { name: 'zh-TW-YunJheNeural', label: '雲哲(男·台湾)', locale: 'zh-TW', gender: 'Male' }
  ];
  // 常用中文音色显示名映射（远程 /voices 清单命中时用中文名，未命中走兜底格式）
  const VOICE_ZH = {
    'zh-CN-XiaoxiaoNeural': '小晓(女·普通话)', 'zh-CN-XiaoyiNeural': '晓伊(女·普通话)',
    'zh-CN-YunxiNeural': '云希(男·普通话)', 'zh-CN-YunyangNeural': '云扬(男·普通话)',
    'zh-CN-YunjianNeural': '云健(男·普通话)', 'zh-CN-YunxiaNeural': '云夏(男·普通话)',
    'zh-CN-liaoning-XiaobeiNeural': '小北(女·辽宁)', 'zh-CN-shaanxi-XiaoniNeural': '小妮(女·陕西)',
    'zh-CN-henan-YundengNeural': '云登(男·河南)',
    'zh-HK-HiuGaaiNeural': '曉佳(女·粤语)', 'zh-HK-HiuMaanNeural': '曉曼(女·粤语)', 'zh-HK-WanLungNeural': '雲龍(男·粤语)',
    'zh-TW-HsiaoChenNeural': '曉臻(女·台湾)', 'zh-TW-HsiaoYuNeural': '曉雨(女·台湾)', 'zh-TW-YunJheNeural': '雲哲(男·台湾)'
  };
  // ---- 音色动态加载：优先 GET http://127.0.0.1:8020/voices（全量 400+），失败回退内置 12 个 ----
  let edgeVoicesRemote = null;
  let _voicesRetry = 0;
  function fetchEdgeVoices() {
    try {
      const xhr = new w.XMLHttpRequest();
      xhr.open('GET', EDGE_BASE + '/voices', true);
      xhr.timeout = 3000;
      xhr.onload = function () {
        if (xhr.status === 200) {
          try {
            const list = JSON.parse(xhr.responseText);
            if (Array.isArray(list) && list.length) { edgeVoicesRemote = list; _voicesRetry = 0; emit(); return; }
          } catch (e) {}
        }
        retryFetchVoices();
      };
      xhr.onerror = function () { retryFetchVoices(); };
      xhr.ontimeout = function () { retryFetchVoices(); };
      xhr.send();
    } catch (e) { retryFetchVoices(); }
  }
  // exe 启动慢时重试（5s 间隔，最多 12 次 = 1 分钟），拿到清单后停止
  function retryFetchVoices() {
    if (edgeVoicesRemote || _voicesRetry >= 12) return;
    _voicesRetry++;
    setTimeout(fetchEdgeVoices, 5000);
  }
  function buildEdgeVoices() {
    if (edgeVoicesRemote) {
      const out = [];
      for (const v of edgeVoicesRemote) {
        const name = v.ShortName || v.shortName || v.Name;
        if (!name) continue;
        const locale = v.Locale || v.locale || '';
        const gender = v.Gender || v.gender || '';
        const label = VOICE_ZH[name] || (name + '(' + (gender === 'Female' ? '女' : '男') + '·' + locale + ')');
        out.push({ name: name, label: label, edge: true, locale: locale, gender: gender });
      }
      if (out.length) return out;
    }
    return EDGE_VOICES.map(v => ({ name: v.name, label: v.label, edge: true, locale: v.locale, gender: v.gender }));
  }
  function getVoices() {
    const edge = buildEdgeVoices();
    const sys = sysVoicesCache.map(v => ({ name: v.name, label: v.name, sys: true }));
    return edge.concat(sys);
  }
  fetchEdgeVoices();
  // 本地引擎：若配置选择 moss，启动时即拉起服务并拉音色/模型状态
  try { if (engineOf() === 'moss') activateMoss(); } catch (e) {}

  function shouldSpeak(ev, type) {
    if (!ev) return false;
    const cfg = getCfg(); if (!cfg.enabled) return false;
    const k = TYPE_KEY[type] || type; const tc = cfg[k]; if (!tc || !tc.enabled) return false;
    if (cfg.blacklistUids) {
      const uids = String(cfg.blacklistUids).split(/[,，\s]+/).map(s => s.trim()).filter(Boolean);
      if (uids.length && uids.indexOf(String(ev.uid)) !== -1) return false;
    }
    if (cfg.bannedWords && ev.msg) {
      const words = String(cfg.bannedWords).split(/[,，]+/).map(s => s.trim()).filter(Boolean);
      for (const wd of words) { if (ev.msg.indexOf(wd) !== -1) return false; }
    }
    const medal = Number(ev.medalLevel) || 0; if (tc.minMedal > 0 && medal < tc.minMedal) return false;
    const honor = Number(ev.honorLevel) || 0; if (tc.minHonor > 0 && honor < tc.minHonor) return false;
    return true;
  }

  function buildText(ev, type) {
    if (!ev) return '';
    const cfg = getCfg(); const k = TYPE_KEY[type] || type; const tc = cfg[k] || {};
    // 【念ID】开关 = 念昵称(uname)；UID 数字无论如何都不念（隐私）
    const uname = (tc.sayUid && ev.uname && String(ev.uname).trim()) ? String(ev.uname).trim() : '';
    if (type === 'danmu') return uname ? (uname + '说：' + (ev.msg || '')) : ('说：' + (ev.msg || ''));
    if (type === 'gifts') {
      const gname = ev.giftName || '礼物';
      const numTxt = (Number(ev.num) > 1) ? numToCn(Number(ev.num)) + '个' : '';
      return uname ? (uname + '送出' + gname + numTxt) : ('送出' + gname + numTxt);
    }
    if (type === 'gifts_merged') {
      // 礼物汇总播报（服务端按用户聚合后的一条）：「大水桶送出小花花五个、牛哇牛哇七个、比心」
      // 数量用中文念法（numToCn），1 个省略数量
      const parts = (Array.isArray(ev.gifts) ? ev.gifts : []).map(g => {
        const n = Math.max(1, Number(g.num) || 1);
        return String(g.giftName || '礼物') + (n > 1 ? numToCn(n) + '个' : '');
      });
      if (!parts.length) return '';
      const body = parts.join('、');
      return uname ? (uname + '送出' + body) : ('送出' + body);
    }
    if (type === 'superchat') return '醒目留言 ' + (uname ? (uname + '：') : '') + (ev.msg || '');
    if (type === 'interact' || type === 'guard') {
      if (!uname) return '';
      const pool = (tc.texts && tc.texts.length) ? tc.texts : ['欢迎 {uname} 进入直播间', '{uname} 来啦，欢迎欢迎', '欢迎 {uname} 的到来'];
      const tpl = pool[Math.floor(Math.random() * pool.length)];
      return String(tpl).split('{uname}').join(uname);
    }
    return uname ? (uname + (ev.msg || '')) : (ev.msg || '');
  }
  function enqueue(ev, type) {
    const cfg = getCfg();
    // 主开关在事件分发处已拦截，这里再兜底
    if (!cfg.enabled) return false;
    if (!shouldSpeak(ev, type)) return false;
    const text = buildText(ev, type); if (!text) return false;
    if (queue.length >= (cfg.queueMax || 8)) return false;
    const isGuard = ev.isGuard || ev.guardLevel >= 1 || type === 'guard';
    const item = { text: text, type: type, priority: (cfg.guardPriority && isGuard) ? 2 : 1, ev: ev, token: ++tokenSeq };
    if (item.priority === 2) {
      let idx = 0; while (idx < queue.length && queue[idx].priority === 2) idx++;
      queue.splice(idx, 0, item);
    } else { queue.push(item); }
    emit();
    pump(); return true;
  }

  function pump() {
    if (speaking) return;
    const item = queue.shift(); if (!item) { emit(); return; }
    speak(item);
  }
  // 语音间隔：播放完一条后等待 gap 毫秒再播下一条（0=无缝衔接，最多5000ms）
  function pumpAfterGap() {
    const cfg = getCfg();
    const gap = Math.max(0, Math.min(5000, Number(cfg.gap) || 0));
    if (gap > 0) {
      const tok = currentToken;
      setTimeout(function () { if (tok === currentToken) pump(); }, gap);
    } else { pump(); }
  }

  // 停止当前一切播放（Edge Audio + 在途请求 + speechSynthesis + 计数失效）
  function stopCurrent() {
    currentToken++;
    speaking = false;
    if (currentXhr) { try { currentXhr.abort(); } catch (e) {} currentXhr = null; }
    if (currentAudio) {
      try { currentAudio.pause(); currentAudio.src = ''; } catch (e) {}
      currentAudio = null;
    }
    try { if (w.speechSynthesis) w.speechSynthesis.cancel(); } catch (e) {}
    emit();
  }

  function activeStop() { stopCurrent(); }

  function playEdge(text, voice, rate, pitch, vol, done) {
    try {
      const xhr = new w.XMLHttpRequest();
      xhr.open('POST', EDGE_BASE + '/', true);
      xhr.timeout = 8000;
      xhr.responseType = 'arraybuffer';
      currentXhr = xhr;
      const finish = (ok) => { if (currentXhr === xhr) currentXhr = null; done(ok); };
      xhr.onload = function () {
        if (xhr.status === 200 && xhr.response && xhr.response.byteLength > 100) {
          try {
            const blob = new w.Blob([xhr.response], { type: 'audio/mpeg' });
            const url = w.URL.createObjectURL(blob);
            const a = new w.Audio(); a.src = url; a.volume = Math.max(0, Math.min(1, vol));
            currentAudio = a;
            const token = currentToken;
            a.onended = function () {
              if (currentAudio === a) currentAudio = null;
              w.URL.revokeObjectURL(url);
              finish(true);
            };
            a.onerror = function () {
              if (currentAudio === a) currentAudio = null;
              w.URL.revokeObjectURL(url);
              finish(false);
            };
            const p = a.play();
            if (p && p.catch) p.catch(() => { /* 自动播放被拦：回退系统语音 */ finish(false); });
            return;
          } catch (e2) {}
        }
        finish(false);
      };
      xhr.onerror = function () { finish(false); };
      xhr.ontimeout = function () { finish(false); };
      const rateStr = rate > 1 ? ('+' + (Math.round((rate - 1) * 100)) + '%') : (rate < 1 ? ('-' + (Math.round((1 - rate) * 100)) + '%') : '+0%');
      const pitchHz = Math.round((pitch - 1) * 50);
      const pitchStr = pitchHz >= 0 ? ('+' + pitchHz + 'Hz') : (pitchHz + 'Hz');
      const volPct = (vol > 1) ? ('+' + Math.round((vol - 1) * 100) + '%') : '+0%';
      xhr.send(JSON.stringify({ text: text, voice: voice, rate: rateStr, pitch: pitchStr, volume: volPct }));
      return;
    } catch (e) { done(false); }
  }

  function playSpeech(text, voiceName, rate, pitch, vol, done) {
    if (!w.speechSynthesis) { done(false); return; }
    try {
      const u = new w.SpeechSynthesisUtterance(text);
      u.lang = 'zh-CN';
      const sys = sysVoicesCache.find(x => x.name === voiceName) || sysVoicesCache.find(x => /zh|Chinese/i.test(x.lang || x.name));
      if (sys) u.voice = sys;
      u.rate = Math.max(0.5, Math.min(2, Number(rate) || 1));
      u.pitch = Math.max(0.5, Math.min(2, Number(pitch) || 1));
      u.volume = Math.max(0, Math.min(1, vol));
      u.onend = function () { done(true); };
      u.onerror = function () { done(false); };
      w.speechSynthesis.speak(u);
      return;
    } catch (e) { done(false); }
  }

  // 本地 MOSS 合成：POST / -> WAV(48kHz)。支持内置音色(voice)与克隆音色(voiceId)；速度用播放倍速实现
  function playMoss(text, voice, voiceId, speed, vol, done) {
    try {
      const xhr = new w.XMLHttpRequest();
      xhr.open('POST', MOSS_BASE + '/', true);
      xhr.timeout = 90000;
      xhr.responseType = 'arraybuffer';
      currentXhr = xhr;
      const finish = (ok) => { if (currentXhr === xhr) currentXhr = null; done(ok); };
      xhr.onload = function () {
        if (xhr.status === 200 && xhr.response && xhr.response.byteLength > 100) {
          try {
            const blob = new w.Blob([xhr.response], { type: 'audio/wav' });
            const url = w.URL.createObjectURL(blob);
            const a = new w.Audio(); a.src = url; a.volume = Math.max(0, Math.min(1, vol));
            a.playbackRate = Math.max(0.5, Math.min(2, speed || 1));
            currentAudio = a;
            a.onended = function () {
              if (currentAudio === a) currentAudio = null;
              w.URL.revokeObjectURL(url);
              finish(true);
            };
            a.onerror = function () {
              if (currentAudio === a) currentAudio = null;
              w.URL.revokeObjectURL(url);
              finish(false);
            };
            const p = a.play();
            if (p && p.catch) p.catch(() => finish(false));
            return;
          } catch (e2) {}
        }
        finish(false);
      };
      xhr.onerror = function () { finish(false); };
      xhr.ontimeout = function () { finish(false); };
      const payload = { text: text, speed: Math.max(0.5, Math.min(2, speed || 1)) };
      if (voiceId) payload.voiceId = voiceId; else payload.voice = voice || 'Junhao';
      xhr.send(JSON.stringify(payload));
      return;
    } catch (e) { done(false); }
  }

  // 计算最终音量：全局音量 × 类型音量 × (1 + 增益%)
  function calcVolume(type) {
    const cfg = getCfg();
    const k = TYPE_KEY[type] || type; const tc = cfg[k] || {};
    let vol = Number(cfg.volume) || 1;
    if (tc.volume != null) vol = vol * (Number(tc.volume) || 1);
    const gain = Number(cfg.gain) || 0;
    if (gain > 0) vol = vol * (1 + gain / 100);
    return Math.max(0, Math.min(1.2, vol));
  }

  // ---- 语气预设 v2：语气 = 情感音色 + 语速 + 前缀词（本地引擎）/ rate+pitch（在线引擎） ----
  // 解析顺序：toneOverride（试听用）> 事件类型绑定 byType > 全局 global > normal
  // 预设缺失/非法时回退 {rate:1, pitchHz:0}（等价「正常」，向后兼容旧配置）
  function resolveTone(type, toneOverride) {
    const cfg = getCfg();
    let name = toneOverride || '';
    if (!name && cfg.tone) {
      const k = TYPE_KEY[type] || type;
      name = (cfg.tone.byType && cfg.tone.byType[k]) || cfg.tone.global || 'normal';
    }
    if (!name) name = 'normal';
    const p = (cfg.tonePresets || {})[name];
    if (!p) return { rate: 1, pitchHz: 0, voice: '', prefix: '' };
    return {
      rate: Number(p.rate) || 1,
      pitchHz: Number(p.pitch) || 0,
      voice: p.voice ? String(p.voice) : '',     // 情感音色（本地引擎用，空=跟随全局音色）
      prefix: p.prefix ? String(p.prefix) : ''   // 前缀词（如「哇！」），增强情绪
    };
  }

  // edge 连续失败计数（偶发失败重试；连续失败才回退系统语音）
  let edgeFailStreak = 0;
  let mossFailStreak = 0;

  // 在线(Edge)合成分支：失败重试最多 3 次，连续失败回退系统语音
  function speakViaEdge(item, tok, vol, voiceOverride, finalRate, finalPitchMul, text) {
    const cfg = getCfg();
    const voice = voiceOverride || cfg.voice;
    const attempt = (n) => {
      playEdge(text, voice, finalRate, finalPitchMul, vol, function (ok) {
        if (tok !== currentToken) return;            // 已被 skip/restart 打断
        if (ok) { edgeFailStreak = 0; speaking = false; emit(); pumpAfterGap(); }
        else if (n < 2) { setTimeout(() => { if (tok === currentToken) attempt(n + 1); }, 300 + n * 400); }
        else { edgeFailStreak++; speaking = false; if (edgeFailStreak >= 2) playSpeechSpeak(item, tok, vol, voice, finalRate, finalPitchMul); else { emit(); pump(); } }
      });
    };
    attempt(0);
  }

  function speak(item, voiceOverride) {
    const cfg = getCfg();
    const tok = ++tokenSeq; currentToken = tok; speaking = true;
    try { w.__ttsSpeakCount = (w.__ttsSpeakCount || 0) + 1; } catch (e) {}
    const vol = calcVolume(item.type);
    // 语气合成：全局滑条是「语气=正常时的基准微调」——rate 相乘、pitch(Hz) 相加；
    // 「正常」语气(rate=1,pitch=0)时结果与旧版纯滑条完全一致
    const tone = resolveTone(item.type, item.toneOverride);
    const finalRate = Math.max(0.5, Math.min(2.5, (Number(cfg.rate) || 1) * tone.rate));
    const globalPitchHz = ((Number(cfg.pitch) || 1) - 1) * 50;
    const finalPitchMul = Math.max(0.5, Math.min(2, 1 + Math.max(-50, Math.min(50, globalPitchHz + tone.pitchHz)) / 50));
    // 语气前缀词（如「哇！」）增强情绪，仅本地引擎/在线引擎合成时拼接
    const text = (tone.prefix ? tone.prefix : '') + item.text;
    const engine = item.engineOverride || engineOf();

    if (engine === 'moss') {
      const mv = voiceOverride || (tone.voice && tone.voice !== 'inherit' ? tone.voice : (cfg.mossVoice || 'Junhao'));
      // 自定义（克隆）音色：id 不在内置列表里 -> 用 voiceId 传
      const isCustom = !!(mossVoices && mossVoices.some(v => v.id === mv && v.custom));
      const attempt = (n) => {
        playMoss(text, isCustom ? '' : mv, isCustom ? mv : '', finalRate, vol, function (ok) {
          if (tok !== currentToken) return;
          if (ok) { mossFailStreak = 0; speaking = false; emit(); pumpAfterGap(); }
          else if (n < 1) { setTimeout(() => { if (tok === currentToken) attempt(n + 1); }, 500); }
          else {
            mossFailStreak++; speaking = false;
            // 本地引擎连续失败 → 回退在线引擎（再失败则由 edge 分支回退系统语音）
            if (mossFailStreak >= 2) { speakViaEdge(item, tok, vol, voiceOverride, finalRate, finalPitchMul, text); }
            else { emit(); pump(); }
          }
        });
      };
      attempt(0);
    } else if (engine === 'sys') {
      playSpeechSpeak(item, tok, vol, voiceOverride || cfg.voice, finalRate, finalPitchMul);
    } else {
      speakViaEdge(item, tok, vol, voiceOverride, finalRate, finalPitchMul, text);
    }
    emit();
  }
  function playSpeechSpeak(item, tok, vol, voice, rate, pitch) {
    const cfg = getCfg();
    playSpeech(item.text, voice || cfg.voice, rate || (Number(cfg.rate) || 1), pitch || (Number(cfg.pitch) || 1), vol, function (ok) {
      if (tok !== currentToken) return;
      speaking = false;
      emit(); pumpAfterGap();
    });
  }

  function handleEvent(ev) {
    if (!ev || !ev.type) return false;
    if (ev.type === 'gifts') return false;   // 原始礼物事件不播报，由 gifts_merged 汇总后播一条
    const cfg = getCfg();
    if (!cfg.enabled) return false;   // 主开关
    return enqueue(ev, ev.type);
  }
  function preview(text) {
    if (!text) text = '你好，欢迎来到直播间，这是一段语音试听。';
    stopCurrent(); queue = [];
    speak({ text: text, type: 'danmu', priority: 3, ev: {} });
  }
  // 指定语气预设试听（固定例句，忽略事件类型绑定的语气）
  function previewTone(name) {
    stopCurrent(); queue = [];
    speak({ text: '感谢关注，礼物多多', type: 'danmu', priority: 3, ev: {}, toneOverride: name || 'normal' });
  }
  // 指定音色试听（合成固定例句，选音色不用靠猜）——按音色名自动判定引擎
  function previewVoice(name) {
    if (!name) return;
    stopCurrent(); queue = [];
    const isMoss = !!(mossVoices && mossVoices.some(v => v.id === name));
    const engineOverride = isMoss ? 'moss' : (/^zh-|Neural/i.test(String(name)) ? 'edge' : 'sys');
    if (engineOverride === 'moss') activateMoss();
    speak({ text: '你好，这是当前音色的试听效果。', type: 'danmu', priority: 3, ev: {}, engineOverride: engineOverride }, name);
  }
  function skip() {
    stopCurrent();      // 真正打断当前语音
    pump();             // 念下一条
  }
  function restart() {
    stopCurrent(); queue = [];
    loadSysVoices();
    return true;
  }
  function cancelAll() { stopCurrent(); queue = []; }
  function queueLength() { return queue.length; }
  function isSpeaking() { return speaking; }
  function getStatus() {
    return { enabled: getCfg().enabled, speaking: speaking, queueLength: queue.length, queue: queue.slice(0, 20).map(q => ({ text: q.text, type: q.type, priority: q.priority })) };
  }

  w.BLTTTS = {
    handleEvent, preview, previewTone, previewVoice, skip, restart, cancel: cancelAll, getVoices, getConfig, setConfig,
    queueLength, isSpeaking, getStatus,
    getMossVoices, mossReady, engineOf, activateMoss,
    getMossModelStatus: function () { return mossModelStatus; },
    fetchMossModelStatus, startMossModelDownload, addMossCustomVoice, removeMossCustomVoice,
    mossServiceStart, mossServiceStop, mossServiceRestart, mossServiceStatus,
    speakCount: function () { return w.__ttsSpeakCount || 0; }
  };
})();
