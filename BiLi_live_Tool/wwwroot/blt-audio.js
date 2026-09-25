// blt-audio.js — 新 UI 的音频层（TTS 播报 / 面板提示音 / 点歌播放器）。
// 原版发声全部在 legacy 前端 JS 里，这里为 Blazor 重写提供等价能力：
//   playBytes  —— 播放 base64 音频（edge mp3 / moss wav），返回 Promise<播放完成>
//   playUrl    —— 播放 URL 音频（面板提示音 /sounds/*.wav，不占用 TTS 通道）
//   speakSys   —— 系统语音（speechSynthesis 兜底）
//   song*      —— 点歌播放器（<audio> + 事件经 DotNetObjectReference 回传 C#）
(function () {
  'use strict';

  var cur = null;          // { a: Audio, resolve: fn } —— TTS 单通道
  var dotnetRef = null;    // SongPlayer 的 DotNetObjectReference
  var song = null;

  function clamp01(v) { v = Number(v); return isFinite(v) ? Math.max(0, Math.min(1, v)) : 1; }

  function finishCur(ok, err) {
    var c = cur; cur = null;
    if (c) { try { c.a.src = ''; } catch (e) { } c.resolve({ ok: !!ok, err: err || '' }); }
  }

  // ---- TTS 通道 ----
  function playBytes(b64, mime, volume, rate) {
    return new Promise(function (resolve) {
      try {
        stop();
        var a = new Audio('data:' + (mime || 'audio/mpeg') + ';base64,' + b64);
        a.volume = clamp01(volume);
        if (rate && Math.abs(rate - 1) > 0.001) a.playbackRate = Math.max(0.5, Math.min(2, rate));
        cur = { a: a, resolve: resolve };
        a.onended = function () { finishCur(true, ''); };
        a.onerror = function () { finishCur(false, 'media:' + (a.error ? a.error.code : '?')); };
        var p = a.play();
        if (p && p.catch) p.catch(function (e2) { finishCur(false, 'play:' + ((e2 && e2.name) || e2)); });
      } catch (e) { resolve({ ok: false, err: 'ctor:' + e }); }
    });
  }

  function stop() {
    if (cur) { try { cur.a.pause(); } catch (e) { } finishCur(true); }
    try { if (window.speechSynthesis) window.speechSynthesis.cancel(); } catch (e) { }
  }

  // ---- 面板提示音（可与 TTS 叠加，播放完自行结束）----
  function playUrl(url, volume) {
    return new Promise(function (resolve) {
      try {
        var a = new Audio(url);
        a.volume = clamp01(volume);
        a.onended = function () { resolve(true); };
        a.onerror = function () { resolve(false); };
        var p = a.play();
        if (p && p.catch) p.catch(function () { resolve(false); });
      } catch (e) { resolve(false); }
    });
  }

  // ---- 系统语音兜底 ----
  function speakSys(text, rate, pitch, volume, voice) {
    return new Promise(function (resolve) {
      try {
        if (!window.speechSynthesis) { resolve(false); return; }
        var u = new SpeechSynthesisUtterance(String(text || ''));
        u.lang = 'zh-CN';
        u.rate = Math.max(0.5, Math.min(2, Number(rate) || 1));
        u.pitch = Math.max(0.5, Math.min(2, Number(pitch) || 1));
        u.volume = clamp01(volume);
        // voice 是配置里的音色名（edge 的 ShortName 或系统音色名）；匹配不上则退回中文音色。
        var list = [];
        try { list = window.speechSynthesis.getVoices() || []; } catch (e) { }
        var want = String(voice || '');
        if (want.length > 0) {
          for (var i = 0; i < list.length; i++) {
            if (list[i].name === want) { u.voice = list[i]; break; }
          }
        }
        if (!u.voice) {
          for (var j = 0; j < list.length; j++) {
            if (/^zh/i.test(list[j].lang || '')) { u.voice = list[j]; break; }
          }
        }
        u.onend = function () { resolve(true); };
        u.onerror = function () { resolve(false); };
        window.speechSynthesis.speak(u);
      } catch (e) { resolve(false); }
    });
  }

  // ---- 点歌播放器 ----
  // Manual stop detaches the source, which makes the media element fire
  // 'error'/'ended'. Those must not reach the panel or the player would treat
  // the stop as a broken track and auto-advance to the next one.
  var songStopped = false;

  function ensureSong() {
    if (song) return song;
    song = new Audio();
    song.preload = 'auto';
    var emit = function (kind) {
      return function () {
        if (songStopped && (kind === 'ended' || kind === 'error' || kind === 'pause')) return;
        songEvent(kind);
      };
    };
    song.addEventListener('loadedmetadata', emit('loaded'));
    song.addEventListener('playing', emit('playing'));
    song.addEventListener('pause', emit('pause'));
    song.addEventListener('ended', emit('ended'));
    song.addEventListener('error', emit('error'));
    song.addEventListener('timeupdate', emit('time'));
    return song;
  }

  function songState() {
    return {
      current: song ? (song.currentTime || 0) : 0,
      duration: (song && isFinite(song.duration)) ? song.duration : 0,
      paused: song ? !!song.paused : true
    };
  }

  function songEvent(kind) {
    if (!dotnetRef) return;
    var st = songState();
    st.kind = kind;
    try { dotnetRef.invokeMethodAsync('OnSongEvent', JSON.stringify(st)); } catch (e) { }
  }

  function songLoad(url, volume) {
    var s = ensureSong();
    songStopped = false;
    s.volume = clamp01(volume);
    s.src = url;
    try { s.load(); } catch (e) { }
  }
  function songPlay() { songStopped = false; var s = ensureSong(); var p = s.play(); if (p && p.catch) p.catch(function () { songEvent('error'); }); }
  function songPause() { if (song) { try { song.pause(); } catch (e) { } } }
  function songSeek(t) { if (song) { try { song.currentTime = Math.max(0, Number(t) || 0); } catch (e) { } } }
  function songSetVolume(v) { if (song) song.volume = clamp01(v); }
  function songStop() {
    if (!song) return;
    songStopped = true;
    try { song.pause(); } catch (e) { }
    // Detach the source so the decoder releases the URL; events stay suppressed.
    try { song.removeAttribute('src'); song.load(); } catch (e) { }
  }

  window.bltAudio = {
    playBytes: playBytes,
    playUrl: playUrl,
    speakSys: speakSys,
    stop: stop,
    setDotNetRef: function (r) { dotnetRef = r; },
    songLoad: songLoad,
    songPlay: songPlay,
    songPause: songPause,
    songSeek: songSeek,
    songSetVolume: songSetVolume,
    songStop: songStop,
    songState: songState
  };
})();
