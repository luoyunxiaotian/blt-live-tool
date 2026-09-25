'use strict';
/**
 * Now Playing OBS 浏览器源脚本
 * - 监听 WebSocket: ws://${location.host}/ws (system_media, system_media_progress, song_progress)
 * - 页面初次加载时拉取 /api/media/current 快速呈现当前曲目
 * - 支持黑胶唱片 (vinyl)、精致卡片 (card)、极简条 (compact) 样式
 * - 参数:
 *   ?style=vinyl|card|compact (默认 vinyl)
 *   ?theme=dark|light|glass (默认 dark)
 *   ?autohide=0|1 (停止/无歌曲时是否自动隐藏, 默认 0)
 *   ?scale=1.0 (缩放比例, 默认 1.0)
 */

(function () {
  const qs = new URLSearchParams(location.search);
  const styleParam = qs.get('style') || 'vinyl';
  const themeParam = qs.get('theme') || 'dark';
  const autohideParam = qs.get('autohide') === '1' || qs.get('autohide') === 'true';
  const scaleParam = parseFloat(qs.get('scale')) || 1.0;

  // DOM 元素
  const stage = document.getElementById('np-stage');
  const card = document.getElementById('np-card');
  const jacketImg = document.getElementById('np-jacket-img');
  const vinylLabel = document.getElementById('np-vinyl-label');
  const vinylDisc = document.getElementById('np-vinyl-disc');
  const coverImg = document.getElementById('np-cover-img');
  const sourceBadge = document.getElementById('np-source-badge');
  const reqBadge = document.getElementById('np-requester-badge');
  const eq = document.getElementById('np-equalizer');
  const titleWrap = document.getElementById('np-title-wrap');
  const titleEl = document.getElementById('np-title');
  const artistEl = document.getElementById('np-artist');
  const timeCur = document.getElementById('np-time-cur');
  const timeTotal = document.getElementById('np-time-total');
  const barFill = document.getElementById('np-bar-fill');

  // 应用配置与样式
  card.className = `style-${styleParam} theme-${themeParam}`;
  if (scaleParam !== 1.0) {
    card.style.transform = `scale(${scaleParam})`;
    card.style.transformOrigin = 'left center';
  }

  // 缺省纯黑底图（透明SVG防止404破图）
  const DEFAULT_COVER = "data:image/svg+xml;charset=utf-8,%3Csvg xmlns='http://www.w3.org/2000/svg' width='100' height='100' viewBox='0 0 100 100'%3E%3Crect width='100' height='100' fill='%231e222d'/%3E%3Ctext x='50' y='58' font-size='32' text-anchor='middle' fill='%23555c6d'%3E%E2%99%AA%3C/text%3E%3C/svg%3E";

  let currentTrack = null;
  let autoHideTimer = null;
  let progressTimer = null;
  let curSec = 0;
  let totalSec = 0;
  let isPlaying = false;

  function fmtTime(sec) {
    if (isNaN(sec) || sec < 0) sec = 0;
    const m = Math.floor(sec / 60);
    const s = Math.floor(sec % 60);
    return `${m.toString().padStart(2, '0')}:${s.toString().padStart(2, '0')}`;
  }

  function setCover(url) {
    const finalUrl = (url && url.trim().length > 0) ? url : DEFAULT_COVER;
    jacketImg.src = finalUrl;
    vinylLabel.src = finalUrl;
    coverImg.src = finalUrl;
  }

  function updateMarquee() {
    titleEl.classList.remove('marquee');
    if (titleEl.scrollWidth > titleWrap.clientWidth + 4) {
      titleEl.classList.add('marquee');
    }
  }

  function updateProgress(pos, dur) {
    curSec = Math.max(0, pos || 0);
    if (dur && dur > 0) totalSec = dur;

    timeCur.textContent = fmtTime(curSec);
    timeTotal.textContent = totalSec > 0 ? fmtTime(totalSec) : '00:00';

    if (totalSec > 0) {
      const pct = Math.min(100, Math.max(0, (curSec / totalSec) * 100));
      barFill.style.width = pct + '%';
    } else {
      barFill.style.width = '0%';
    }
  }

  function renderTrack(track) {
    if (!track) return;
    currentTrack = track;

    const hasSong = track.hasSong && (track.title || track.artist);
    if (!hasSong) {
      if (autohideParam) {
        stage.classList.add('np-hidden');
      } else {
        stage.classList.remove('np-hidden');
        titleEl.textContent = '暂无正在播放的歌曲';
        artistEl.textContent = '等待播放器启动';
        sourceBadge.textContent = '未播放';
        reqBadge.style.display = 'none';
        vinylDisc.classList.remove('spinning');
        eq.classList.add('paused');
        setCover('');
        updateProgress(0, 0);
      }
      isPlaying = false;
      return;
    }

    // 显示卡片
    stage.classList.remove('np-hidden');
    if (autoHideTimer) {
      clearTimeout(autoHideTimer);
      autoHideTimer = null;
    }

    // 标题与歌手
    titleEl.textContent = track.title || '未知曲目';
    let artistText = track.artist || '未知歌手';
    if (track.album && track.album.trim().length > 0 && track.album !== track.title) {
      artistText += ` · ${track.album}`;
    }
    artistEl.textContent = artistText;
    updateMarquee();

    // 来源平台徽标
    sourceBadge.textContent = track.sourceApp || '外部媒体';

    // 点歌人徽标
    if (track.requester && track.requester.trim().length > 0) {
      reqBadge.textContent = `点歌: ${track.requester}`;
      reqBadge.style.display = 'inline-flex';
    } else {
      reqBadge.style.display = 'none';
    }

    // 封面图片（带版本防抖/防缓存）
    let cover = track.coverUrl;
    if (cover && cover.startsWith('/api/media/cover') && track.coverHash) {
      cover = `${cover}?h=${encodeURIComponent(track.coverHash)}`;
    }
    setCover(cover);

    // 播放状态控制（黑胶旋转、动态音阶）
    isPlaying = track.status === 'Playing';
    if (isPlaying) {
      vinylDisc.classList.add('spinning');
      eq.classList.remove('paused');
    } else {
      vinylDisc.classList.remove('spinning');
      eq.classList.add('paused');

      if (autohideParam && track.status !== 'Paused') {
        autoHideTimer = setTimeout(() => {
          stage.classList.add('np-hidden');
        }, 6000);
      }
    }

    // 进度时间
    updateProgress(track.positionSec, track.durationSec);
  }

  // 内部点歌的 song_progress 帧桥接
  function renderSongProgress(data) {
    if (!data) return;
    if (currentTrack && currentTrack.sourceApp && currentTrack.sourceApp.includes('直播小帮手')) {
      updateProgress(data.current, data.duration);
    }
  }

  // 初始获取当前播放曲目
  async function fetchCurrent() {
    try {
      const res = await fetch('/api/media/current', { cache: 'no-store' });
      if (res.ok) {
        const data = await res.json();
        renderTrack(data);
      }
    } catch (e) {
      console.warn('[NowPlaying] fetchCurrent error:', e);
    }
  }

  // WebSocket 全双工推送通道
  let ws = null;
  let retryCount = 0;

  function connectWs() {
    const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
    const wsUrl = `${proto}//${location.host}/ws`;

    ws = new WebSocket(wsUrl);

    ws.onopen = function () {
      retryCount = 0;
      fetchCurrent();
    };

    ws.onmessage = function (ev) {
      try {
        const msg = JSON.parse(ev.data);
        if (!msg || !msg.type) return;

        if (msg.type === 'system_media') {
          renderTrack(msg.data);
        } else if (msg.type === 'system_media_progress') {
          if (msg.data) {
            updateProgress(msg.data.position, msg.data.duration);
          }
        } else if (msg.type === 'song_progress') {
          renderSongProgress(msg.data);
        }
      } catch (e) { }
    };

    ws.onclose = function () {
      const delay = Math.min(10000, 1000 * Math.pow(1.5, retryCount++));
      setTimeout(connectWs, delay);
    };

    ws.onerror = function () {
      try { ws.close(); } catch (e) { }
    };
  }

  // 启动
  fetchCurrent();
  connectWs();

  // 本地轻度自推进进度（在播放时每秒递增 1 秒，若无新帧到达保持平滑）
  setInterval(function () {
    if (isPlaying) {
      curSec += 1;
      if (totalSec > 0 && curSec > totalSec) curSec = totalSec;
      updateProgress(curSec, totalSec);
    }
  }, 1000);

})();
