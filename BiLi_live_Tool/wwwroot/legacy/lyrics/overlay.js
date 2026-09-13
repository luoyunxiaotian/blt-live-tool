'use strict';
/*
 * Lyrics Overlay — OBS 浏览器源页（竖条 + 逐行滚动，像音乐播放器）
 * 数据来源：
 *   - 歌曲信息/播放进度：WS 'song_progress'（管理面板 BLTSong 每秒上报，服务端转发）
 *   - 初始/兜底：GET /api/song-request/playlist（每 5 秒轮询，WS 断了也能恢复）
 *   - 歌词：GET /api/lyrics（本地 data/lyrics/*.lrc 优先，其次网易云/QQ/酷狗）
 * 无歌词时显示占位；没有播放时整块淡出；连不上管理面板时底部显示状态条。
 * 诊断：地址后加 ?debug=1 显示实时状态。
 */
(function () {
  const $ = (s) => document.getElementById(s);
  const stage = $('ly-stage'), nameEl = $('ly-name'), artistEl = $('ly-artist'), reqEl = $('ly-req'),
    timeEl = $('ly-time'), track = $('ly-track'), viewport = $('ly-viewport'), barEl = $('ly-bar'), statusEl = $('ly-status');

  const DEBUG = /[?&]debug=1/.test(location.search);
  let song = null;        // {name, artist, requester, platform, songId}
  let position = 0, duration = 0, paused = false;
  let lrcLines = [];      // [{t, text}]
  let lrcKey = '';
  let lrcSource = '';     // '' / 'loading' / 'local' / 'netease' / 'qq' / 'kugou' / 'none'
  let curIdx = -1;
  let wsState = 'connecting';
  let msgCount = 0, lastMsgAt = 0, lastPollAt = 0, lastPollOk = null, lastPollErr = '';

  function esc(s){ return String(s == null ? '' : s).replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c])); }
  function fmtTime(s){ s = Math.max(0, Math.floor(s || 0)); const m = Math.floor(s / 60), sec = s % 60; return String(m).padStart(2, '0') + ':' + String(sec).padStart(2, '0'); }

  function parseLrc(text) {
    const out = [];
    String(text || '').split(/\r?\n/).forEach(line => {
      const m = line.match(/\[(\d{1,2}):(\d{1,2})(?:\.(\d{1,3}))?\]/g);
      if (!m) return;
      const txt = line.replace(/\[[^\]]*\]/g, '').trim();
      if (!txt) return;
      m.forEach(tag => {
        const mm = tag.match(/\[(\d{1,2}):(\d{1,2})(?:\.(\d{1,3}))?\]/);
        const t = Number(mm[1]) * 60 + Number(mm[2]) + (mm[3] ? Number('0.' + mm[3]) : 0);
        out.push({ t: t, text: txt });
      });
    });
    out.sort((a, b) => a.t - b.t);
    return out;
  }

  // 重建歌词列表（整首一次性渲染，靠 transform 滚动）
  function buildTrack() {
    curIdx = -1;
    if (!lrcLines.length) {
      track.style.transform = 'translateY(0)';
      track.innerHTML = lrcSource === 'loading'
        ? '<span class="lrc-none">♪ 歌词加载中…</span>'
        : (lrcSource === 'none' ? '<span class="lrc-none">♪ 暂无歌词（可把 .lrc 放到 data\\lyrics\\，或看 ?debug=1）</span>' : '');
      return;
    }
    track.innerHTML = lrcLines.map((l, i) => '<div class="lrc-line" data-i="' + i + '">' + esc(l.text) + '</div>').join('');
  }

  // 把第 idx 行滚到视窗正中
  function centerOn(idx, animate) {
    const line = track.children[idx];
    if (!line || !line.offsetHeight) return;
    if (!animate) track.style.transition = 'none';
    const y = viewport.clientHeight / 2 - (line.offsetTop + line.offsetHeight / 2);
    track.style.transform = 'translateY(' + Math.round(y) + 'px)';
    if (!animate) { void track.offsetHeight; track.style.transition = ''; }
  }

  function setCurrent(idx) {
    if (idx === curIdx) return;
    curIdx = idx;
    const kids = track.children;
    for (let i = 0; i < kids.length; i++) {
      const el = kids[i];
      if (!el.classList || !el.classList.contains('lrc-line')) continue;
      el.classList.toggle('cur', i === idx);
      el.classList.toggle('near', Math.abs(i - idx) === 1);
    }
    centerOn(idx, true);
  }

  function currentIndexFor(pos) {
    let idx = -1;
    for (let i = 0; i < lrcLines.length; i++) { if (lrcLines[i].t <= pos) idx = i; else break; }
    return idx;
  }

  async function loadLrc(force) {
    const key = song ? (song.name + '|' + song.platform + '|' + song.songId) : '';
    if (!force && key === lrcKey) return;
    lrcKey = key;
    lrcLines = [];
    if (!song || !song.name) { lrcSource = ''; buildTrack(); return; }
    lrcSource = 'loading';
    buildTrack();
    render();
    try {
      const url = '/api/lyrics?song=' + encodeURIComponent(song.name) + '&artist=' + encodeURIComponent(song.artist || '') +
        '&platform=' + encodeURIComponent(song.platform || '') + '&id=' + encodeURIComponent(song.songId || '');
      const ctrl = new AbortController();
      const timer = setTimeout(() => ctrl.abort(), 9000);
      let j = null;
      try {
        const r = await fetch(url, { signal: ctrl.signal });
        j = await r.json();
      } finally { clearTimeout(timer); }
      if (j && j.lrc) { lrcLines = parseLrc(j.lrc); lrcSource = j.source || 'local'; }
      else lrcSource = 'none';
    } catch (e) { lrcSource = 'none'; }
    buildTrack();
    render();
  }

  function render() {
    if (!song || !song.name) { stage.classList.remove('show'); return; }
    stage.classList.add('show');
    nameEl.textContent = song.name;
    artistEl.textContent = song.artist || '';
    reqEl.textContent = song.requester ? ('🎵 ' + song.requester + ' 点播') : '';
    timeEl.textContent = fmtTime(position) + ' / ' + fmtTime(duration);
    const pct = duration > 0 ? Math.min(100, (position / duration) * 100) : 0;
    barEl.style.width = pct.toFixed(1) + '%';
    if (lrcLines.length) setCurrent(currentIndexFor(position));
  }

  // 连接状态条：只在「连不上管理面板」时出现
  function renderStatus() {
    if (!statusEl) return;
    if (wsState === 'open') { statusEl.style.display = 'none'; return; }
    statusEl.style.display = 'block';
    statusEl.textContent = wsState === 'connecting'
      ? '♪ 正在连接管理面板…'
      : '♪ 未连接到管理面板（请确认「B站直播助手」正在运行）';
  }

  function renderDebug() {
    if (!DEBUG) return;
    let el = document.getElementById('ly-debug');
    if (!el) { el = document.createElement('div'); el.id = 'ly-debug'; document.body.appendChild(el); }
    const br = stage ? stage.getBoundingClientRect() : null;
    const curLine = (curIdx >= 0 && lrcLines[curIdx]) ? lrcLines[curIdx].text : '';
    el.textContent = JSON.stringify({
      ws: wsState,
      msgs: msgCount,
      lastMsg: lastMsgAt ? (Math.round((Date.now() - lastMsgAt) / 1000) + 's ago') : 'never',
      poll: lastPollAt ? (Math.round((Date.now() - lastPollAt) / 1000) + 's ago ok=' + lastPollOk + (lastPollErr ? ' err=' + lastPollErr : '')) : 'never',
      song: song && song.name, platform: song && song.platform, songId: song && song.songId,
      pos: Math.round(position), dur: Math.round(duration), paused: paused,
      lrcSource: lrcSource, lrcLines: lrcLines.length, curIdx: curIdx, curLine: curLine,
      stageShown: stage ? stage.classList.contains('show') : null,
      stageRect: br ? [Math.round(br.left), Math.round(br.top), Math.round(br.width), Math.round(br.height)] : null,
      viewport: [innerWidth, innerHeight]
    }, null, 1);
  }

  function setSong(s) {
    const changed = !song || !s || s.name !== song.name || s.platform !== song.platform || String(s.songId || '') !== String(song.songId || '');
    song = s || null;
    if (changed) { position = 0; duration = 0; loadLrc(true); }
    render();
  }

  async function fetchPlaylist() {
    lastPollAt = Date.now();
    try {
      const r = await fetch('/api/song-request/playlist');
      const j = await r.json();
      const pl = j.playlist || [];
      const idx = j.currentIndex != null ? j.currentIndex : -1;
      lastPollOk = true; lastPollErr = '';
      if (idx >= 0 && pl[idx]) setSong({ name: pl[idx].name, artist: pl[idx].artist || '', requester: pl[idx].requester || '', platform: pl[idx].platform || '', songId: pl[idx].id || '' });
      else setSong(null);
    } catch (e) { lastPollOk = false; lastPollErr = e.message || 'fetch failed'; }
    renderDebug();
  }

  function connect() {
    const proto = location.protocol === 'https:' ? 'wss' : 'ws';
    let ws;
    try { ws = new WebSocket(proto + '://' + location.host + '/ws'); }
    catch (e) { wsState = 'closed'; renderStatus(); setTimeout(connect, 2500); return; }
    wsState = 'connecting'; renderStatus(); renderDebug();
    ws.onopen = () => { wsState = 'open'; renderStatus(); renderDebug(); };
    ws.onmessage = (e) => {
      let m; try { m = JSON.parse(e.data); } catch (x) { return; }
      msgCount++; lastMsgAt = Date.now();
      if (m.type === 'song_progress' && m.data) {
        const d = m.data;
        setSong({ name: d.name || '', artist: d.artist || '', requester: d.requester || '', platform: d.platform || '', songId: d.songId || '' });
        position = Number(d.position) || 0;
        duration = Number(d.duration) || 0;
        paused = !!d.paused;
        render();
      } else if (m.type === 'song_request') {
        setTimeout(fetchPlaylist, 800);
      }
      renderDebug();
    };
    ws.onclose = () => { wsState = 'closed'; renderStatus(); renderDebug(); setTimeout(connect, 2500); };
    ws.onerror = () => { try { ws.close(); } catch (e) {} };
  }

  // 浏览器源尺寸变化时重新居中
  window.addEventListener('resize', function () { if (curIdx >= 0) centerOn(curIdx, false); });

  connect();
  fetchPlaylist();
  setInterval(fetchPlaylist, 5000);   // 兜底：WS 断了也能在 5 秒内跟上播放状态
  setInterval(render, 1000);          // 进度条/歌词平滑走秒
  setInterval(() => { renderStatus(); renderDebug(); }, 1000);
})();
