'use strict';
/*
 * Lyrics Overlay — OBS 浏览器源页（竖条 + 逐行滚动，像音乐播放器）
 * 数据来源：
 *   - 本程序内点歌：WS 'song_progress'（高频进度推送，包含点歌人与平台）
 *   - 外部媒体感知（Now Playing）：WS 'system_media' / 'system_media_progress'（网易云、QQ音乐、酷狗、Spotify 等）
 *   - 初始与断网兜底：GET /api/song-request/playlist + GET /api/media/current（每 5 秒轮询）
 *   - 歌词检索：GET /api/lyrics（本地 data/lyrics/*.lrc 优先，其次精确 ID 取词与各大在线曲库检索）
 * 优先级逻辑：
 *   - 当直播小帮手内部点歌正在播放时，优先展示内部点歌歌曲与点播人；
 *   - 当内部点歌停止或列表空闲时，自动无缝呈现外部音乐软件播放曲目及歌词；
 *   - 外部音乐软件切歌时自动重新拉取对应 LRC 歌词；
 *   - 当所有播放器均未播放时，整块卡片平滑淡出。
 * 诊断：地址后加 ?debug=1 显示实时状态。
 */
(function () {
  const $ = (s) => document.getElementById(s);
  const stage = $('ly-stage'), nameEl = $('ly-name'), artistEl = $('ly-artist'), reqEl = $('ly-req'),
    timeEl = $('ly-time'), track = $('ly-track'), viewport = $('ly-viewport'), barEl = $('ly-bar'), statusEl = $('ly-status');

  const DEBUG = /[?&]debug=1/.test(location.search);

  // 当前展示的歌曲状态
  let song = null;        // { name, artist, requester, sourceApp, album, platform, songId, isExternal }
  let position = 0, duration = 0, paused = false;
  let lrcLines = [];      // [{t, text}]
  let lrcKey = '';
  let lastLyricSong = ''; // 上一次成功取到歌词的歌曲 key
  let lrcSource = '';     // '' / 'loading' / 'local' / 'netease' / 'qq' / 'kugou' / 'none'
  let curIdx = -1;

  // 网络与优先级状态
  let wsState = 'connecting';
  let msgCount = 0, lastMsgAt = 0, lastPollAt = 0, lastPollOk = null, lastPollErr = '';
  let lastInternalProgressAt = 0;
  const PROGRESS_STALE_MS = 6000; // 超过 6 秒未收到内部点歌进度帧，则认为内部播放已停止

  // 缓存两路数据源的最新状态
  let internalTrack = null;
  let externalTrack = null;

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
        : (lrcSource === 'none' ? '<span class="lrc-none">♪ 暂无歌词（伴奏/纯音乐或未匹配到曲库歌词）</span>' : '');
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
    const key = song ? (song.name + '|' + (song.artist || '') + '|' + (song.platform || '') + '|' + (song.songId || '')) : '';
    if (!force && key === lrcKey) return;
    const sameSong = (key === lastLyricSong);
    lrcKey = key;
    if (!sameSong) lrcLines = [];   // 只有换歌才清空：同一首重取失败时保留已有歌词，避免浮层闪烁
    if (!song || !song.name) { lrcSource = ''; buildTrack(); return; }
    lrcSource = 'loading';
    buildTrack();
    render();
    try {
      const url = '/api/lyrics?song=' + encodeURIComponent(song.name) +
        '&artist=' + encodeURIComponent(song.artist || '') +
        '&platform=' + encodeURIComponent(song.platform || '') +
        '&id=' + encodeURIComponent(song.songId || '');
      const ctrl = new AbortController();
      const timer = setTimeout(() => ctrl.abort(), 9000);
      let j = null;
      try {
        const r = await fetch(url, { signal: ctrl.signal });
        j = await r.json();
      } finally { clearTimeout(timer); }
      if (j && j.lrc) {
        lrcLines = parseLrc(j.lrc);
        lrcSource = j.source || 'local';
        lastLyricSong = key;
      } else if (lrcLines.length === 0) {
        lrcSource = 'none';   // 已有歌词时不要退回「暂无歌词」
      }
    } catch (e) {
      if (lrcLines.length === 0) lrcSource = 'none';
    }
    buildTrack();
    render();
  }

  function render() {
    if (!song || !song.name) {
      stage.classList.remove('show');
      return;
    }
    stage.classList.add('show');
    nameEl.textContent = song.name;
    artistEl.textContent = song.artist || '';

    // 徽标展示：内部点歌显示点歌人，外部歌曲显示播放来源软件或专辑名
    if (song.requester && song.requester.trim().length > 0) {
      reqEl.textContent = '🎵 ' + song.requester + ' 点播';
    } else if (song.sourceApp && song.sourceApp.trim().length > 0) {
      reqEl.textContent = '🎧 ' + song.sourceApp;
    } else if (song.album && song.album.trim().length > 0 && song.album !== song.name) {
      reqEl.textContent = '💿 ' + song.album;
    } else {
      reqEl.textContent = '';
    }

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
      ? '♪ 正在连接直播小帮手…'
      : '♪ 未连接到直播小帮手（请确认主程序正在运行）';
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
      internalStale: !lastInternalProgressAt || (Date.now() - lastInternalProgressAt > PROGRESS_STALE_MS),
      source: song ? (song.isExternal ? 'external (' + song.sourceApp + ')' : 'internal') : 'none',
      song: song && song.name, artist: song && song.artist, platform: song && song.platform, songId: song && song.songId,
      pos: Math.round(position), dur: Math.round(duration), paused: paused,
      lrcSource: lrcSource, lrcLines: lrcLines.length, curIdx: curIdx, curLine: curLine,
      stageShown: stage ? stage.classList.contains('show') : null,
      stageRect: br ? [Math.round(br.left), Math.round(br.top), Math.round(br.width), Math.round(br.height)] : null,
      viewport: [innerWidth, innerHeight]
    }, null, 1);
  }

  // 统一应用选中的歌曲
  function applySelectedSong(target, pos, dur, isPaused) {
    const prevKey = song ? (song.name + '|' + (song.artist || '') + '|' + (song.platform || '') + '|' + (song.songId || '')) : '';
    const newKey = target ? (target.name + '|' + (target.artist || '') + '|' + (target.platform || '') + '|' + (target.songId || '')) : '';
    const changed = prevKey !== newKey;

    song = target ? { ...target } : null;
    position = Number(pos) || 0;
    if (dur && dur > 0) duration = Number(dur);
    paused = !!isPaused;

    if (changed) {
      loadLrc(true);
    }
    render();
  }

  // 优先级仲裁：内部点歌优先，否则展示外部媒体
  function evaluateCurrentTrack() {
    const internalActive = internalTrack && internalTrack.name &&
      lastInternalProgressAt && (Date.now() - lastInternalProgressAt <= PROGRESS_STALE_MS);

    if (internalActive) {
      applySelectedSong(internalTrack, internalTrack.position, internalTrack.duration, internalTrack.paused);
      return;
    }

    // 内部播放已结束或闲置，检查外部播放器 (Now Playing)
    if (externalTrack && externalTrack.hasSong && externalTrack.status !== 'None') {
      applySelectedSong(externalTrack, externalTrack.position, externalTrack.duration, externalTrack.status !== 'Playing');
      return;
    }

    // 均无歌曲
    applySelectedSong(null, 0, 0, true);
  }

  // 轮询兜底与初次拉取
  async function pollState() {
    lastPollAt = Date.now();
    try {
      // 1. 检查内部点歌列表
      let internalFound = false;
      try {
        const r1 = await fetch('/api/song-request/playlist', { cache: 'no-store' });
        if (r1.ok) {
          const j1 = await r1.json();
          const pl = j1.playlist || [];
          const idx = Number.isInteger(j1.playingIndex) ? j1.playingIndex : -1;
          if (idx >= 0 && pl[idx]) {
            internalFound = true;
            // 只有当长时间未收到实时 WS 进度帧时才以轮询为准
            if (!lastInternalProgressAt || Date.now() - lastInternalProgressAt > PROGRESS_STALE_MS) {
              internalTrack = {
                name: pl[idx].name,
                artist: pl[idx].artist || '',
                requester: pl[idx].requester || '',
                platform: pl[idx].platform || '',
                songId: pl[idx].id || '',
                position: 0,
                duration: 0,
                paused: false,
                isExternal: false
              };
            }
          }
        }
      } catch (e1) {}

      if (!internalFound && (!lastInternalProgressAt || Date.now() - lastInternalProgressAt > PROGRESS_STALE_MS)) {
        internalTrack = null;
      }

      // 2. 检查外部媒体 (Now Playing)
      try {
        const r2 = await fetch('/api/media/current', { cache: 'no-store' });
        if (r2.ok) {
          const j2 = await r2.json();
          if (j2 && j2.hasSong && j2.title && j2.status !== 'None') {
            externalTrack = {
              hasSong: true,
              status: j2.status || 'Playing',
              name: j2.title,
              artist: j2.artist || '',
              album: j2.album || '',
              sourceApp: j2.sourceApp || '外部媒体',
              requester: j2.requester || '',
              platform: j2.platform || '',
              songId: j2.songId || '',
              position: Number(j2.positionSec) || 0,
              duration: Number(j2.durationSec) || 0,
              isExternal: true
            };
          } else {
            externalTrack = null;
          }
        }
      } catch (e2) {}

      lastPollOk = true;
      lastPollErr = '';
      evaluateCurrentTrack();
    } catch (e) {
      lastPollOk = false;
      lastPollErr = e.message || 'poll failed';
    }
    renderDebug();
  }

  function connect() {
    const proto = location.protocol === 'https:' ? 'wss' : 'ws';
    let ws;
    try { ws = new WebSocket(proto + '://' + location.host + '/ws'); }
    catch (e) { wsState = 'closed'; renderStatus(); setTimeout(connect, 2500); return; }

    wsState = 'connecting';
    renderStatus();
    renderDebug();

    ws.onopen = () => {
      wsState = 'open';
      renderStatus();
      renderDebug();
      pollState();
    };

    ws.onmessage = (e) => {
      let m;
      try { m = JSON.parse(e.data); } catch (x) { return; }
      msgCount++;
      lastMsgAt = Date.now();

      // 1. 内部点歌实时进度帧
      if (m.type === 'song_progress' && m.data) {
        const d = m.data;
        if (d.name && d.name.trim().length > 0 && !d.stopped) {
          lastInternalProgressAt = Date.now();
          internalTrack = {
            name: d.name || '',
            artist: d.artist || '',
            requester: d.requester || '',
            platform: d.platform || '',
            songId: d.songId || '',
            position: Number(d.position) || 0,
            duration: Number(d.duration) || 0,
            paused: !!d.paused,
            isExternal: false
          };
          applySelectedSong(internalTrack, internalTrack.position, internalTrack.duration, internalTrack.paused);
        } else {
          internalTrack = null;
          lastInternalProgressAt = 0;
          evaluateCurrentTrack();
        }
      }
      // 2. 外部媒体感知曲目或状态切换 (Now Playing)
      else if (m.type === 'system_media' && m.data) {
        const d = m.data;
        if (d.hasSong && d.title && d.status !== 'None') {
          externalTrack = {
            hasSong: true,
            status: d.status || 'Playing',
            name: d.title,
            artist: d.artist || '',
            album: d.album || '',
            sourceApp: d.sourceApp || '外部媒体',
            requester: d.requester || '',
            platform: d.platform || '',
            songId: d.songId || '',
            position: Number(d.positionSec) || 0,
            duration: Number(d.durationSec) || 0,
            isExternal: true
          };
        } else {
          externalTrack = null;
        }
        evaluateCurrentTrack();
      }
      // 3. 外部媒体感知进度更新帧 (Now Playing 每秒广播)
      else if (m.type === 'system_media_progress' && m.data) {
        if (externalTrack) {
          externalTrack.position = Number(m.data.position) || 0;
          if (Number(m.data.duration) > 0) externalTrack.duration = Number(m.data.duration);
        }
        // 若当前浮层正显示外部媒体，实时更新进度与滚动歌词
        if (song && song.isExternal) {
          position = Number(m.data.position) || 0;
          if (Number(m.data.duration) > 0) duration = Number(m.data.duration);
          render();
        }
      }
      // 4. 点歌列表变动
      else if (m.type === 'song_request') {
        setTimeout(pollState, 600);
      }

      renderDebug();
    };

    ws.onclose = () => {
      wsState = 'closed';
      renderStatus();
      renderDebug();
      setTimeout(connect, 2500);
    };

    ws.onerror = () => {
      try { ws.close(); } catch (e) {}
    };
  }

  // 浏览器源尺寸变化时重新居中歌词
  window.addEventListener('resize', function () {
    if (curIdx >= 0) centerOn(curIdx, false);
  });

  // 启动
  connect();
  pollState();

  // 5 秒轮询保活/兜底
  setInterval(pollState, 5000);

  // 1 秒本地平滑步进：播放状态下本地走秒，在接收到下一包 WS 时精准校准
  setInterval(function () {
    if (song && !paused) {
      position += 1;
      if (duration > 0 && position > duration) position = duration;
      render();
    }
  }, 1000);

  setInterval(() => { renderStatus(); renderDebug(); }, 1000);
})();
