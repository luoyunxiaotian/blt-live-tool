// maui-bridge.js — window.electronAPI shim for the MAUI host (synced from Assets by sync-legacy.ps1).
// Lets the untouched legacy panel drive the native shell (bilibili browser,
// music logins, TTS engines, tray actions) through /api/maui/* endpoints.
(function () {
  'use strict';
  if (window.electronAPI) return; // a real Electron preload wins

  function call(path, body) {
    return fetch('/api/maui/' + path, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: body === undefined ? 'null' : JSON.stringify(body),
    }).then(function (r) { return r.json(); }).catch(function () { return {}; });
  }

  // Event registrations resolve immediately; streams (server log, verify
  // status) are not replayed into the panel in the MAUI demo.
  var noopEvent = function () { return Promise.resolve({}); };

  var impl = {
    isElectron: true,
    __maui: true,
    server: {
      start: function () { return call('server/start'); },
      stop: function () { return call('server/stop'); },
      restart: function () { return call('server/restart'); },
      status: function () { return call('server/status'); },
      readLog: function () { return call('server/readlog'); },
      onLog: noopEvent,
      onStatus: noopEvent,
    },
    app: {
      quit: function () { return call('app/quit'); },
      focusPanel: function () { return call('app/focus-panel'); },
      minimizeToTray: function () { return call('app/minimize-tray'); },
      openDataFolder: function () { return call('shell/open-data-folder'); },
      openConfigFolder: function () { return call('shell/open-config-folder'); },
      setAutoLaunch: function (on) { return call('autolaunch/set', { enabled: !!on }); },
      getAutoLaunch: function () { return call('autolaunch/get'); },
      getVersion: function () { return call('version'); },
      update: {
        check: function () { return call('update/check'); },
        state: function () { return call('update/check'); },
        downloadInstall: function () { return call('update/download-install'); },
        openReleasePage: function () { return call('update/open-page'); },
        onEvent: noopEvent,
      },
    },
    shell: {
      openPath: function (p) { return call('shell/open-path', { path: p }); },
      openExternal: function (u) { return call('shell/open-external', { url: u }); },
    },
    bili: {
      showLogin: function () { return call('bili/show-login'); },
      openCurrent: function () { return call('bili/show'); },
      showLive: function (rid) { return call('bili/show', { roomId: rid }); },
      refresh: function () { return call('bili/refresh'); },
      back: function () { return call('bili/back'); },
      forward: function () { return call('bili/forward'); },
      state: function () { return call('bili/state'); },
      captureCookie: function () { return call('bili/capture-cookie'); },
      clearCookies: function () { return call('bili/clear-cookies'); },
      setVisible: function (v) { return call(v ? 'bili/show' : 'bili/hide'); },
      onState: noopEvent,
    },
    music: {
      showLogin: function (platform) { return call('music/login', { platform: platform }); },
      captureCookie: function (platform) { return call('music/capture', { platform: platform }); },
      hasSavedCookie: function (platform) { return call('music/has', { platform: platform }); },
      clearCookies: function (platform) { return call('music/clear', { platform: platform }); },
      onCookieCaptured: noopEvent,
    },
    verify: { onStatus: noopEvent },
    keyview: {
      start: function () { return call('keyview/start'); },
      stop: function () { return call('keyview/stop'); },
      status: function () { return call('keyview/status'); },
      getConfig: function () { return call('keyview/config-get'); },
      setConfig: function (c) { return call('keyview/config-set', { config: c }); },
      setAllConfig: function (c) { return call('keyview/config-set', { config: c }); },
      openOverlay: function () { return call('keyview/open-overlay'); },
      getThemes: function () { return call('keyview/themes'); },
    },
    tts: {
      mossEnsure: function () { return call('tts/moss/ensure'); },
      mossStatus: function () { return call('tts/moss/status'); },
      mossStop: function () { return call('tts/moss/stop'); },
      mossRestart: function () { return call('tts/moss/restart'); },
    },
  };

  // Anything not implemented resolves to {} so untouched call sites never crash.
  var fallback = new Proxy(function () {}, {
    get: function (t, k) {
      if (k === 'then' || k === Symbol.toPrimitive) return undefined;
      return fallback;
    },
    apply: function () { return Promise.resolve({}); },
  });

  function wrap(obj) {
    return new Proxy(obj, {
      get: function (t, k) {
        if (k in t) {
          var v = t[k];
          return (v && typeof v === 'object') ? wrap(v) : v;
        }
        return fallback;
      },
    });
  }

  window.electronAPI = wrap(impl);
})();
