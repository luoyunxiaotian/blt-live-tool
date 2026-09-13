'use strict';
/*
 * SongPlayer — 点歌播放器模块
 * 维护 HTML5 Audio 播放，自动获取播放URL，自动播放下一首
 * 通过 window.BLTSong 全局对象暴露接口
 */
(function () {
  const w = window;
  let audio = null;
  let playlist = [];
  let currentIndex = -1;
  let paused = false;
  let volume = 0.8;
  let autoPlay = true;
  let cfg = {};

  let _playLock = false;
  let _failCount = 0;
  let _errorTimer = 0;
  let _seeking = false;
  let _seekTimer = 0;
  let _waitNotified = false;

  const PLATFORM_NAMES = { qq: 'QQ音乐', netease: '网易云', kugou: '酷狗', bilibili: 'B站' };

  function init(config) {
    cfg = config || {};
    volume = cfg.volume != null ? cfg.volume : 0.8;
    autoPlay = cfg.autoPlay != null ? cfg.autoPlay : true;
    if (!audio) {
      audio = new Audio();
      audio.volume = volume;
      audio.addEventListener('ended', onEnded);
      audio.addEventListener('error', onError);
      audio.addEventListener('timeupdate', onTimeUpdate);
      audio.addEventListener('loadedmetadata', onLoadedMeta);
      audio.addEventListener('seeked', onSeeked);
      audio.addEventListener('waiting', onWaiting);
      audio.addEventListener('playing', onPlaying);
    }
    loadPlaylist();
  }

  function setConfig(config) {
    cfg = config || {};
    volume = cfg.volume != null ? cfg.volume : volume;
    autoPlay = cfg.autoPlay != null ? cfg.autoPlay : autoPlay;
    if (audio) audio.volume = volume;
  }

  async function loadPlaylist() {
    try {
      const r = await fetch(apiUrl('/api/song-request/playlist'));
      const data = await r.json();
      playlist = data.playlist || [];
      currentIndex = data.currentIndex != null ? data.currentIndex : -1;
      updateUI();
    } catch (e) { console.error('[song-player] loadPlaylist:', e); }
  }

  async function play(index) {
    if (_playLock) return;
    if (index < 0 || index >= playlist.length) return;
    if (!cfg.enabled) {
      showToast('请先打开点歌功能总开关（启用点歌功能）后再播放', false);
      return;
    }
    _playLock = true;
    currentIndex = index;
    paused = false;
    const song = playlist[index];
    updateUI();
    try {
      const r = await fetch(apiUrl('/api/song-request/song-url'), {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ index })
      });
      const data = await r.json();
      if (!data.ok || !data.url) {
        _failCount++;
        const pn = PLATFORM_NAMES[song.platform] || song.platform || '';
        showToast('无法播放《' + song.name + '》' + (data.vip ? '（VIP歌曲，需设置' + pn + 'Cookie）' : '（可能未设置' + pn + 'Cookie或版权受限）'), false);
        _playLock = false;
        if (autoPlay && _failCount < 3) skip();
        else if (_failCount >= 3) { showToast('连续播放失败 3 次，已停止自动播放', false); autoPlay = false; }
        return;
      }
      _failCount = 0;
      let playUrl = data.url;
      if (playUrl && playUrl.charAt(0) === '/') playUrl = apiUrl(playUrl);
      audio.src = playUrl;
      audio.volume = volume;
      audio.play().catch(e => {
        showToast('播放失败: ' + e.message, false);
      });
      updateUI();
    } catch (e) {
      _failCount++;
      showToast('获取播放URL失败: ' + e.message, false);
      _playLock = false;
      if (autoPlay && _failCount < 3) skip();
      else if (_failCount >= 3) { showToast('连续失败 3 次，已停止自动播放', false); autoPlay = false; }
    }
    _playLock = false;
  }

  async function prev() {
    if (_playLock) return;
    currentIndex--;
    if (currentIndex < 0) currentIndex = 0;
    play(currentIndex);
  }

  function pause() {
    if (audio) {
      if (paused) { audio.play().catch(()=>{}); paused = false; }
      else { audio.pause(); paused = true; }
      updateUI();
      emitProgress(true);
    }
  }

  async function skip() {
    if (_playLock) return;
    try {
      await fetch(apiUrl('/api/song-request/skip'), { method: 'POST' });
    } catch (e) {}
    currentIndex++;
    if (currentIndex >= playlist.length) { currentIndex = -1; if(audio){audio.pause(); audio.src='';} updateUI(); return; }
    if (autoPlay) play(currentIndex);
    else updateUI();
  }

  function seek(time) {
    if (audio && audio.duration) {
      _seeking = true;
      clearTimeout(_seekTimer);
      audio.currentTime = Math.max(0, Math.min(time, audio.duration));
      _seekTimer = setTimeout(() => { _seeking = false; }, 3000);
    }
  }

  function seekPreview(time) {
    const curEl = document.getElementById('sr-cur-time');
    if (curEl) curEl.textContent = fmtTime(time);
  }

  function onEnded() {
    skip();
  }

  function onError(e) {
    console.error('[song-player] audio error:', e);
    clearTimeout(_errorTimer);
    _errorTimer = setTimeout(() => {
      if (autoPlay && !_playLock) skip();
    }, 500);
  }

  function onTimeUpdate() {
    if (_seeking) return;
    // F1-1 修复：进度上报与点歌页 UI 解耦——先上报再更新 UI，
    // 用户不在点歌页时（进度条元素不存在）浮层歌词也能持续跟随
    emitProgress();
    const bar = document.getElementById('sr-progress');
    const curEl = document.getElementById('sr-cur-time');
    if (!bar || !audio || !audio.duration) return;
    bar.value = audio.currentTime;
    if (curEl) curEl.textContent = fmtTime(audio.currentTime);
  }

  function onLoadedMeta() {
    const bar = document.getElementById('sr-progress');
    const totalEl = document.getElementById('sr-total-time');
    if (!bar || !audio) return;
    bar.max = audio.duration || 100;
    if (totalEl) totalEl.textContent = fmtTime(audio.duration);
    emitProgress(true);
  }

  function onSeeked() {
    clearTimeout(_seekTimer);
    _seeking = false;
    _waitNotified = false;
    updateProgressBar();
    emitProgress(true); // seek → overlay lyrics jump immediately
  }

  function onWaiting() {
    if (_seeking && !_waitNotified) {
      _waitNotified = true;
      showToast('正在缓冲，请稍候...');
    }
  }

  function onPlaying() {
    _waitNotified = false;
    updateProgressBar();
    emitProgress();
  }

  function updateProgressBar() {
    const bar = document.getElementById('sr-progress');
    const curEl = document.getElementById('sr-cur-time');
    if (!bar || !audio || !audio.duration) return;
    bar.value = audio.currentTime;
    if (curEl) curEl.textContent = fmtTime(audio.currentTime);
  }

  function fmtTime(s) {
    if (!s || isNaN(s)) return '00:00';
    const m = Math.floor(s / 60);
    const sec = Math.floor(s % 60);
    return String(m).padStart(2, '0') + ':' + String(sec).padStart(2, '0');
  }

  async function removeFromPlaylist(index) {
    try {
      const wasCurrent = (index === currentIndex);
      const r = await fetch(apiUrl('/api/song-request/playlist/remove'), {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ index })
      });
      const data = await r.json();
      if (data.ok) {
        playlist = data.playlist || [];
        currentIndex = data.currentIndex != null ? data.currentIndex : currentIndex;
        if (wasCurrent) {
          if (audio) { audio.pause(); audio.src = ''; }
          if (currentIndex >= 0 && autoPlay) play(currentIndex);
        }
        updateUI();
      }
    } catch (e) { console.error('[song-player] remove:', e); }
  }

  async function reorderPlaylist(from, to) {
    try {
      const r = await fetch(apiUrl('/api/song-request/playlist/reorder'), {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ from, to })
      });
      const data = await r.json();
      if (data.ok) { playlist = data.playlist || []; updateUI(); }
    } catch (e) { console.error('[song-player] reorder:', e); }
  }

  async function clearPlaylist() {
    try {
      await fetch(apiUrl('/api/song-request/playlist/clear'), { method: 'POST' });
      playlist = []; currentIndex = -1;
      if (audio) { audio.pause(); audio.src = ''; }
      updateUI();
    } catch (e) { console.error('[song-player] clear:', e); }
  }

  async function addToPlaylist(song) {
    try {
      const r = await fetch(apiUrl('/api/song-request/playlist/add'), {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ song })
      });
      const data = await r.json();
      if (data.ok) {
        playlist = data.playlist || [];
        if (currentIndex < 0 && autoPlay) play(0);
        else updateUI();
      }
    } catch (e) { console.error('[song-player] add:', e); }
  }

  async function search(keyword, searchType) {
    try {
      const r = await fetch(apiUrl('/api/song-request/search'), {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ keyword, type: searchType || 'song' })
      });
      return await r.json();
    } catch (e) { return { error: e.message }; }
  }

  function showToast(msg, ok) {
    if (typeof w.toast === 'function') w.toast(msg, ok);
    else console.log('[song-player]', msg);
  }

  function updateUI() {
    const ev = new CustomEvent('song:status', {
      detail: { playlist, currentIndex, paused, volume }
    });
    w.dispatchEvent(ev);
    const row = document.getElementById('sr-progress-row');
    if (row) row.style.display = currentIndex >= 0 ? '' : 'none';
    emitProgress(true); // song changed / list changed → overlay refreshes immediately
  }

  // v1.1.9: broadcast play progress for the OBS lyrics overlay
  // 播放中节流 1/s；暂停时降频 3/s 一次（F1-2：避免暂停期间浮层进度倒跳错觉），force 调用不受限
  let _lastProgressEmit = 0;
  function emitProgress(force) {
    const now = Date.now();
    const isPaused = audio ? (audio.paused || paused || false) : true;
    if (!force && now - _lastProgressEmit < (isPaused ? 3000 : 1000)) return;
    _lastProgressEmit = now;
    const song = currentIndex >= 0 ? (playlist[currentIndex] || null) : null;
    try {
      w.dispatchEvent(new CustomEvent('song:progress', { detail: {
        position: audio ? (audio.currentTime || 0) : 0,
        duration: audio ? (audio.duration || 0) : 0,
        paused: audio ? (audio.paused || paused || false) : true,
        name: song ? (song.name || '') : '',
        artist: song ? (song.artist || '') : '',
        requester: song ? (song.requester || '') : '',
        platform: song ? (song.platform || '') : '',
        songId: song ? (song.id || '') : ''
      } }));
    } catch (e) {}
  }

  function getPlaylist() { return { playlist, currentIndex, paused }; }
  function getCurrent() { return currentIndex >= 0 ? playlist[currentIndex] : null; }

  function handleWsEvent(data) {
    if (!data) return;
    if (data.ok) {
      showToast('已添加《' + (data.song ? data.song.name : data.songName) + '》', true);
      loadPlaylist();
      if (currentIndex < 0 && autoPlay) play(0);
    } else {
      const reasonMap = {
        blacklist: '《' + (data.songName || '') + '》已被屏蔽',
        not_found: '未找到《' + (data.songName || '') + '》',
        cooldown: '点歌冷却中',
        user_cooldown: '点歌冷却中',
        dedup: '刚刚点过这首歌',
        daily_limit: '今日点歌已达上限',
        guard_only: '仅舰长可点歌',
        medal: '粉丝勋章等级不足',
        honor: '大航海等级不足',
        search_error: '搜索失败',
        error: '点歌出错'
      };
      showToast(reasonMap[data.reason] || '点歌失败', false);
    }
  }

  w.BLTSong = {
    init, setConfig, play, pause, skip, prev, seek, seekPreview,
    removeFromPlaylist, reorderPlaylist, clearPlaylist, addToPlaylist,
    search, getPlaylist, getCurrent, handleWsEvent, loadPlaylist, updateUI
  };
})();
