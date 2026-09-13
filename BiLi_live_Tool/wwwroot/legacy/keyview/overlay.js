// KeyView overlay renderer: recent (key stream) or layout (real keyboard+mouse).
(function () {
  const params = new URLSearchParams(location.search);
  const urlTheme = params.get('theme') || '';
  let currentTheme = urlTheme;
  const root = document.getElementById('kv');
  const kbEl = document.getElementById('kv-kb');
  const msEl = document.getElementById('kv-ms');
  const gpEl = document.getElementById('kv-gp');

  let cfg = {};
  let renderer = null;

  function applyTheme(t) {
    currentTheme = t;
    root.setAttribute('data-theme', t);
    document.getElementById('kv-theme-link').href = 'themes/' + t + '.css';
  }

  function rebuildRenderer() {
    kbEl.innerHTML = '';
    msEl.innerHTML = '';
    gpEl.innerHTML = '';
    if (typeof gpInstances !== 'undefined') gpInstances.clear();
    fetch('themes.manifest.json').then((r) => r.json()).then((manifest) => {
      const info = (manifest.themes || []).find((x) => x.id === currentTheme);
      const mode = (info && info.render) || 'recent';
      if (mode === 'layout') renderer = createLayoutRenderer();
      else if (mode === 'heatmap') renderer = createHeatmapRenderer();
      else if (mode === 'trail') renderer = createTrailRenderer();
      else renderer = createRecentRenderer();
      if (info && info.devices && info.devices.length === 1 && info.devices[0] === 'gp') ensureGp(0);
      applyConfig(cfg);
    }).catch(() => {
      renderer = createRecentRenderer();
      applyConfig(cfg);
    });
  }

  function applyConfig(full) {
    const oldGpLayout = cfg['display.gpLayout'];
    cfg = full || cfg;
    const newTheme = cfg['overlay.theme'];
    if (newTheme && newTheme !== currentTheme) { applyTheme(newTheme); rebuildRenderer(); return; }
    if (oldGpLayout !== cfg['display.gpLayout'] && gpInstances.size > 0) {
      gpEl.innerHTML = ''; gpInstances.clear();
      const info = gpEl.closest('#kv').getAttribute('data-theme');
      if (info === 'pad-real' || info === 'pad-mech') ensureGp(0);
    }
    const r = document.documentElement;
    r.style.setProperty('--kv-opacity', cfg['overlay.opacity']);
    r.style.setProperty('--kv-scale', cfg['overlay.scale']);
    r.style.setProperty('--kv-key-hold-ms', (cfg['display.keyHoldMs'] || 800) + 'ms');
    r.style.setProperty('--kv-anim-speed', cfg['display.animSpeed'] || 1);
    kbEl.style.display = cfg['device.keyboard.enabled'] === false ? 'none' : '';
    msEl.style.display = cfg['device.mouse.enabled'] === false ? 'none' : '';
    gpEl.style.display = cfg['device.gamepad.enabled'] === false ? 'none' : '';
    applyPosition(cfg['overlay.position'] || 'bottom-left');
    if (renderer && renderer.applyConfig) renderer.applyConfig(cfg);
    setComboActive(currentTheme === 'dmc');
  }

  function applyPosition(pos) {
    root.style.left = ''; root.style.right = ''; root.style.top = ''; root.style.bottom = '';
    const scale = cfg['overlay.scale'] || 1;
    if (renderer && renderer.isLayout) {
      root.classList.add('kv-layout');
      root.style.bottom = '18px';
      root.style.left = '50%';
      root.style.transform = 'translateX(-50%) scale(' + scale + ')';
      root.style.transformOrigin = 'bottom center';
      return;
    }
    root.classList.remove('kv-layout');
    if (pos === 'bottom-left') { root.style.left = '18px'; root.style.bottom = '18px'; root.style.transformOrigin = 'bottom left'; }
    else if (pos === 'bottom-right') { root.style.right = '18px'; root.style.bottom = '18px'; root.style.transformOrigin = 'bottom right'; }
    else if (pos === 'top-left') { root.style.left = '18px'; root.style.top = '18px'; root.style.transformOrigin = 'top left'; }
    else if (pos === 'top-right') { root.style.right = '18px'; root.style.top = '18px'; root.style.transformOrigin = 'top right'; }
    root.style.transform = 'scale(' + scale + ')';
  }

  // ============ recent renderer (key stream) ============
  function createRecentRenderer() {
    let maxRecentKeys = 6, keyHoldMs = 800;
    const keyItems = new Map();
    function onKey(k, down) {
      if (!k) return;
      let item = keyItems.get(k);
      if (down) {
        if (!item) {
          const el = document.createElement('div');
          el.className = 'kv-key';
          el.textContent = k;
          kbEl.appendChild(el);
          item = { el, timer: null };
          keyItems.set(k, item);
          trim();
        } else { clearTimeout(item.timer); }
        item.el.classList.remove('kv-key-up');
        item.el.classList.add('kv-key-down');
        kbEl.appendChild(item.el);
      } else if (item) {
        item.el.classList.remove('kv-key-down');
        item.el.classList.add('kv-key-up');
        clearTimeout(item.timer);
        item.timer = setTimeout(() => { item.el.remove(); keyItems.delete(k); }, keyHoldMs);
      }
    }
    function trim() { while (keyItems.size > maxRecentKeys) { const k = keyItems.keys().next().value; const it = keyItems.get(k); it.el.remove(); keyItems.delete(k); } }
    function onMouseDown(b) { const r = document.createElement('div'); r.className = 'kv-ripple kv-ripple-' + (b || 'left'); msEl.appendChild(r); setTimeout(() => r.remove(), 650); }
    function onWheel(dy) { const w = document.createElement('div'); w.className = 'kv-wheel'; w.textContent = dy < 0 ? '▲' : '▼'; msEl.appendChild(w); setTimeout(() => w.remove(), 520); }
    return {
      onKey: (m) => onKey(m.k, m.e === 'down'),
      onMouseDown: (m) => onMouseDown(m.b), onMouseUp: () => {}, onWheel: (m) => onWheel(m.dy), onMouseMove: () => {},
      applyConfig: (c) => { maxRecentKeys = c['display.maxRecentKeys'] || 6; keyHoldMs = c['display.keyHoldMs'] || 800; },
    };
  }

  // ============ layout renderer (real keyboard + mouse, always visible) ============
  const KB_LAYOUTS = {
    full: [
      [{c:27,l:'Esc'},{g:1},{c:112,l:'F1'},{c:113,l:'F2'},{c:114,l:'F3'},{c:115,l:'F4'},{g:1},{c:116,l:'F5'},{c:117,l:'F6'},{c:118,l:'F7'},{c:119,l:'F8'},{g:1},{c:120,l:'F9'},{c:121,l:'F10'},{c:122,l:'F11'},{c:123,l:'F12'}],
      [{c:192,l:'`'},{c:49,l:'1'},{c:50,l:'2'},{c:51,l:'3'},{c:52,l:'4'},{c:53,l:'5'},{c:54,l:'6'},{c:55,l:'7'},{c:56,l:'8'},{c:57,l:'9'},{c:48,l:'0'},{c:189,l:'-'},{c:187,l:'='},{c:8,l:'Bksp',w:2}],
      [{c:9,l:'Tab',w:1.5},{c:81,l:'Q'},{c:87,l:'W'},{c:69,l:'E'},{c:82,l:'R'},{c:84,l:'T'},{c:89,l:'Y'},{c:85,l:'U'},{c:73,l:'I'},{c:79,l:'O'},{c:80,l:'P'},{c:219,l:'['},{c:221,l:']'},{c:220,l:'\\',w:1.5}],
      [{c:20,l:'Caps',w:1.75},{c:65,l:'A'},{c:83,l:'S'},{c:68,l:'D'},{c:70,l:'F'},{c:71,l:'G'},{c:72,l:'H'},{c:74,l:'J'},{c:75,l:'K'},{c:76,l:'L'},{c:186,l:';'},{c:222,l:"'"},{c:13,l:'Enter',w:2.25}],
      [{c:160,l:'Shift',w:2.25},{c:90,l:'Z'},{c:88,l:'X'},{c:67,l:'C'},{c:86,l:'V'},{c:66,l:'B'},{c:78,l:'N'},{c:77,l:'M'},{c:188,l:','},{c:190,l:'.'},{c:191,l:'/'},{c:161,l:'Shift',w:2.75}],
      [{c:162,l:'Ctrl',w:1.75},{c:91,l:'Win'},{c:164,l:'Alt'},{c:32,l:'',w:6.25},{c:165,l:'Alt'},{c:92,l:'Win'},{c:93,l:'Menu'},{c:163,l:'Ctrl',w:1.75}],
    ],
    alpha: [
      [{c:81,l:'Q'},{c:87,l:'W'},{c:69,l:'E'},{c:82,l:'R'},{c:84,l:'T'},{c:89,l:'Y'},{c:85,l:'U'},{c:73,l:'I'},{c:79,l:'O'},{c:80,l:'P'}],
      [{c:65,l:'A'},{c:83,l:'S'},{c:68,l:'D'},{c:70,l:'F'},{c:71,l:'G'},{c:72,l:'H'},{c:74,l:'J'},{c:75,l:'K'},{c:76,l:'L'}],
      [{c:90,l:'Z'},{c:88,l:'X'},{c:67,l:'C'},{c:86,l:'V'},{c:66,l:'B'},{c:78,l:'N'},{c:77,l:'M'}],
      [{c:32,l:'',w:7},{c:13,l:'Enter',w:2}],
    ],
    compact: [
      [{c:9,l:'Tab',w:1.5},{c:81,l:'Q'},{c:87,l:'W'},{c:69,l:'E'},{c:82,l:'R'}],
      [{c:160,l:'Shift',w:2},{c:65,l:'A'},{c:83,l:'S'},{c:68,l:'D'},{c:70,l:'F'}],
      [{c:162,l:'Ctrl',w:1.75},{c:90,l:'Z'},{c:88,l:'X'},{c:67,l:'C'},{c:86,l:'V'},{c:164,l:'Alt'}],
      [{c:32,l:'',w:6.25}],
    ],
    minimal: [
      [{g:1,gw:36},{c:87,l:'W'}],
      [{c:65,l:'A'},{c:83,l:'S'},{c:68,l:'D'}],
      [{c:160,l:'Shift',w:1.5},{c:162,l:'Ctrl',w:1.5}],
      [{c:32,l:'',w:3}],
    ],
  };

  function createLayoutRenderer() {
    const keyByCode = new Map();
    let currentLayout = 'full';
    let mouseSvg = null, mouseL = null, mouseR = null, mouseWheel = null, mouseWheelUp = null, mouseWheelDown = null, mouseDir = null, mouseSide1 = null, mouseSide2 = null, mouseLabel = null;
    let lastX = 0, lastY = 0, haveLast = false, moveTimer = null;

    function buildKeyboard(rows) {
      kbEl.innerHTML = '';
      keyByCode.clear();
      const panel = document.createElement('div');
      panel.className = 'kv-real-kb';
      rows.forEach((row) => {
        const rowEl = document.createElement('div');
        rowEl.className = 'kv-real-row';
        row.forEach((key) => {
          if (key.g) { const sp = document.createElement('div'); sp.className = 'kv-real-spacer'; if (key.gw) sp.style.width = key.gw + 'px'; rowEl.appendChild(sp); return; }
          const el = document.createElement('div');
          el.className = 'kv-real-key';
          el.textContent = key.l;
          const w = key.w || 1;
          el.style.width = (w * 36 + (w - 1) * 5) + 'px';
          rowEl.appendChild(el);
          keyByCode.set(key.c, el);
        });
        panel.appendChild(rowEl);
      });
      kbEl.appendChild(panel);
    }

    function buildMouse() {
      msEl.innerHTML = '';
      const wrap = document.createElement('div');
      wrap.className = 'kv-real-mouse-wrap';
      wrap.style.position = 'relative';
      const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
      svg.setAttribute('viewBox', '-10 0 86 104');
      svg.setAttribute('class', 'kv-real-mouse');
      svg.innerHTML =
        '<path class="kv-mouse-part kv-mouse-l" d="M36,4 Q4,4 4,46 L36,46 Z"/>' +
        '<path class="kv-mouse-part kv-mouse-r" d="M36,4 Q68,4 68,46 L36,46 Z"/>' +
        '<rect class="kv-mouse-wheel" x="32" y="16" width="8" height="18" rx="3"/>' +
        '<rect class="kv-mouse-wheel-half kv-mouse-wheel-up" x="32" y="16" width="8" height="9"/>' +
        '<rect class="kv-mouse-wheel-half kv-mouse-wheel-down" x="32" y="25" width="8" height="9"/>' +
        '<line class="kv-mouse-sep" x1="36" y1="6" x2="36" y2="44"/>' +
        '<line class="kv-mouse-sep" x1="6" y1="46" x2="66" y2="46"/>' +
        '<path class="kv-mouse-outline" d="M36,4 Q4,4 4,46 L4,86 Q4,100 36,100 Q68,100 68,86 L68,46 Q68,4 36,4 Z"/>' +
        '<rect class="kv-mouse-side kv-mouse-side-1" x="-8" y="28" width="7" height="11" rx="2"/>' +
        '<rect class="kv-mouse-side kv-mouse-side-2" x="-8" y="42" width="7" height="11" rx="2"/>' +
        '<g class="kv-mouse-dir"><polygon class="kv-mouse-dir-arrow" points="0,-11 7,3 -7,3"/></g>';
      wrap.appendChild(svg);
      const lab = document.createElement('div');
      lab.className = 'kv-mouse-label';
      wrap.appendChild(lab);
      msEl.appendChild(wrap);
      mouseSvg = svg; mouseLabel = lab;
      mouseL = svg.querySelector('.kv-mouse-l');
      mouseR = svg.querySelector('.kv-mouse-r');
      mouseWheel = svg.querySelector('.kv-mouse-wheel');
      mouseWheelUp = svg.querySelector('.kv-mouse-wheel-up');
      mouseWheelDown = svg.querySelector('.kv-mouse-wheel-down');
      mouseDir = svg.querySelector('.kv-mouse-dir');
      mouseSide1 = svg.querySelector('.kv-mouse-side-1');
      mouseSide2 = svg.querySelector('.kv-mouse-side-2');
    }

    buildKeyboard(KB_LAYOUTS[currentLayout]);
    buildMouse();

    function onKey(m) {
      const el = keyByCode.get(m.code);
      if (!el) return;
      if (m.e === 'down') el.classList.add('active');
      else el.classList.remove('active');
    }
    function onMouseDown(m) {
      const b = m.b || 'left';
      if (b === 'left' && mouseL) mouseL.classList.add('active');
      else if (b === 'right' && mouseR) mouseR.classList.add('active');
      else if (b === 'middle' && mouseWheel) mouseWheel.classList.add('active');
      else if (b === 'x1' && mouseSide1) mouseSide1.classList.add('active');
      else if (b === 'x2' && mouseSide2) mouseSide2.classList.add('active');
      flashLabel(b);
    }
    function onMouseUp(m) {
      const b = m.b || 'left';
      if (b === 'left' && mouseL) mouseL.classList.remove('active');
      else if (b === 'right' && mouseR) mouseR.classList.remove('active');
      else if (b === 'middle' && mouseWheel) mouseWheel.classList.remove('active');
      else if (b === 'x1' && mouseSide1) mouseSide1.classList.remove('active');
      else if (b === 'x2' && mouseSide2) mouseSide2.classList.remove('active');
    }
    function onWheel(m) {
      const up = m.dy < 0;
      if (up && mouseWheelUp) mouseWheelUp.classList.add('active');
      if (!up && mouseWheelDown) mouseWheelDown.classList.add('active');
      flashLabel(up ? 'Wheel ▲' : 'Wheel ▼');
      clearTimeout(onWheel._t);
      onWheel._t = setTimeout(() => {
        if (mouseWheelUp) mouseWheelUp.classList.remove('active');
        if (mouseWheelDown) mouseWheelDown.classList.remove('active');
      }, 280);
    }
    function onMouseMove(m) {
      if (!haveLast) { lastX = m.x; lastY = m.y; haveLast = true; return; }
      const dx = m.x - lastX, dy = m.y - lastY;
      lastX = m.x; lastY = m.y;
      if (!mouseDir) return;
      if (Math.hypot(dx, dy) < 2) return;
      const angle = Math.atan2(dy, dx);
      const snapDeg = Math.round(angle / (Math.PI / 4)) * 45;
      mouseDir.setAttribute('transform', 'translate(36,74) rotate(' + (snapDeg + 90) + ')');
      mouseDir.classList.add('active');
      if (moveTimer) clearTimeout(moveTimer);
      moveTimer = setTimeout(() => { if (mouseDir) mouseDir.classList.remove('active'); }, 220);
    }
    function flashLabel(text) {
      if (!mouseLabel) return;
      mouseLabel.textContent = text;
      clearTimeout(flashLabel._t);
      flashLabel._t = setTimeout(() => { if (mouseLabel) mouseLabel.textContent = ''; }, 500);
    }

    return {
      isLayout: true,
      onKey, onMouseDown, onMouseUp, onWheel, onMouseMove,
      onGpBtn: () => {}, onGpAxis: () => {}, onGpTrig: () => {}, onGpConn: () => {},
      applyConfig: (c) => {
        const nl = c['display.kbLayout'] || 'full';
        if (nl !== currentLayout && KB_LAYOUTS[nl]) { currentLayout = nl; buildKeyboard(KB_LAYOUTS[nl]); }
      },
    };
  }

  // ============ heatmap renderer (key frequency heat coloring) ============
  function createHeatmapRenderer() {
    const keyByCode = new Map();
    const freq = new Map();
    let currentLayout = 'full';

    function buildKeyboard(rows) {
      kbEl.innerHTML = '';
      keyByCode.clear();
      const panel = document.createElement('div');
      panel.className = 'kv-real-kb';
      rows.forEach((row) => {
        const rowEl = document.createElement('div');
        rowEl.className = 'kv-real-row';
        row.forEach((key) => {
          if (key.g) { const sp = document.createElement('div'); sp.className = 'kv-real-spacer'; if (key.gw) sp.style.width = key.gw + 'px'; rowEl.appendChild(sp); return; }
          const el = document.createElement('div');
          el.className = 'kv-real-key';
          el.textContent = key.l;
          const w = key.w || 1;
          el.style.width = (w * 36 + (w - 1) * 5) + 'px';
          rowEl.appendChild(el);
          keyByCode.set(key.c, el);
        });
        panel.appendChild(rowEl);
      });
      kbEl.appendChild(panel);
    }

    function heatColor(f) {
      const t = Math.min(1, f / 15);
      const hue = 240 * (1 - t);
      return 'hsl(' + hue + ', 85%, 55%)';
    }

    buildKeyboard(KB_LAYOUTS[currentLayout]);

    function onKey(m) {
      const el = keyByCode.get(m.code);
      if (!el || m.e !== 'down') return;
      const f = (freq.get(m.code) || 0) + 1;
      freq.set(m.code, f);
      const c = heatColor(f);
      el.style.background = c;
      el.style.boxShadow = '0 0 ' + (4 + Math.min(f, 20)) + 'px ' + c;
      el.classList.add('active');
      clearTimeout(el._ht);
      el._ht = setTimeout(() => el.classList.remove('active'), 180);
    }

    return {
      isLayout: true,
      onKey,
      onMouseDown: () => {}, onMouseUp: () => {}, onWheel: () => {}, onMouseMove: () => {},
      onGpBtn: () => {}, onGpAxis: () => {}, onGpTrig: () => {}, onGpConn: () => {},
      applyConfig: (c) => {
        const nl = c['display.kbLayout'] || 'full';
        if (nl !== currentLayout && KB_LAYOUTS[nl]) { currentLayout = nl; buildKeyboard(KB_LAYOUTS[nl]); }
      },
    };
  }

  // ============ trail renderer (mouse movement trail + ripples) ============
  function createTrailRenderer() {
    const dots = [];
    const MAX_DOTS = 20;
    let lastX = 0, lastY = 0, haveLast = false;
    let accX = 0, accY = 0;

    function addDot(x, y) {
      const d = document.createElement('div');
      d.className = 'kv-trail-dot';
      d.style.left = x + 'px';
      d.style.top = y + 'px';
      msEl.appendChild(d);
      dots.push(d);
      while (dots.length > MAX_DOTS) { const old = dots.shift(); old.remove(); }
      setTimeout(() => { d.classList.add('fade'); setTimeout(() => { d.remove(); const i = dots.indexOf(d); if (i >= 0) dots.splice(i, 1); }, 600); }, 250);
    }

    function onMouseMove(m) {
      if (!haveLast) { lastX = m.x; lastY = m.y; haveLast = true; return; }
      const dx = m.x - lastX, dy = m.y - lastY;
      lastX = m.x; lastY = m.y;
      if (Math.hypot(dx, dy) < 3) return;
      accX = Math.max(-60, Math.min(60, accX + dx * 0.35));
      accY = Math.max(-35, Math.min(35, accY + dy * 0.35));
      const cx = msEl.clientWidth / 2, cy = msEl.clientHeight / 2;
      addDot(cx + accX, cy + accY);
    }
    function onMouseDown(m) {
      const r = document.createElement('div');
      r.className = 'kv-ripple kv-ripple-' + (m.b || 'left');
      msEl.appendChild(r);
      setTimeout(() => r.remove(), 650);
    }
    function onWheel(m) {
      const w = document.createElement('div');
      w.className = 'kv-wheel';
      w.textContent = m.dy < 0 ? '▲' : '▼';
      msEl.appendChild(w);
      setTimeout(() => w.remove(), 520);
    }

    return {
      onKey: () => {}, onMouseDown, onMouseUp: () => {}, onWheel, onMouseMove,
      onGpBtn: () => {}, onGpAxis: () => {}, onGpTrig: () => {}, onGpConn: () => {},
      applyConfig: () => {},
    };
  }

  // ============ gamepad (SVG controller) ============
  const gpInstances = new Map();
  const GP_LAYOUTS = {
    xbox: {
      face: { A: { l: 'A', c: '#4caf50' }, B: { l: 'B', c: '#f44336' }, X: { l: 'X', c: '#2196f3' }, Y: { l: 'Y', c: '#ffeb3b' } },
      shoulder: { LB: 'LB', RB: 'RB' }, trig: { lt: 'LT', rt: 'RT' },
      menu: { Back: 'View', Start: 'Menu', Guide: 'Xbox' }
    },
    ps: {
      face: { A: { l: '×', c: '#00b0f0' }, B: { l: '○', c: '#ff5a5a' }, X: { l: '□', c: '#e8e8e8' }, Y: { l: '△', c: '#4caf50' } },
      shoulder: { LB: 'L1', RB: 'R1' }, trig: { lt: 'L2', rt: 'R2' },
      menu: { Back: 'Share', Start: 'Opts', Guide: 'PS' }
    },
    nintendo: {
      face: { A: { l: 'B', c: '#e91e63' }, B: { l: 'A', c: '#4caf50' }, X: { l: 'Y', c: '#ffeb3b' }, Y: { l: 'X', c: '#2196f3' } },
      shoulder: { LB: 'L', RB: 'R' }, trig: { lt: 'ZL', rt: 'ZR' },
      menu: { Back: '−', Start: '+', Guide: 'Home' }
    }
  };
  function buildGpSvg(L) {
    const f = L.face;
    return '<svg viewBox="0 0 441 383" class="kv-gp-svg">' +
      '<path class="kv-gp-body" d="M220.5 294.5C220.5 294.5 195 294.5 150 294.5C105 294.5 81.5 378.5 49.5 378.5C17.5 378.5 4 363.9 4 317.5C4 271.1 43.5 165.5 55 137.5C66.5 109.5 95.5 92 128 92C154 92 200.5 92 220.5 92"/>' +
      '<path class="kv-gp-body" d="M220 294.5C220 294.5 245.5 294.5 290.5 294.5C335.5 294.5 359 378.5 391 378.5C423 378.5 436.5 363.9 436.5 317.5C436.5 271.1 397 165.5 385.5 137.5C374 109.5 345 92 312.5 92C286.5 92 240 92 220 92"/>' +
      '<path class="kv-gp-trig kv-gp-trig-l" data-k="lt" d="m152.5,52.97c0,4.61 -3.35,8.36 -7.5,8.36l-13,0c-4.14,0 -7.5,-3.74 -7.5,-8.36l0,-22.86c0,-8.62 6.27,-15.61 14,-15.61c7.73,0 14,6.99 14,15.61l0,22.86z"/>' +
      '<rect class="kv-gp-trig-fill" data-k="lt-fill" x="130" y="16" width="17" height="0" rx="3"/>' +
      '<text class="kv-gp-trig-text" x="138.5" y="48">' + L.trig.lt + '</text>' +
      '<path class="kv-gp-trig kv-gp-trig-r" data-k="rt" d="m316.83,53.44c0,4.64 -3.44,8.39 -7.68,8.39l-13.31,0c-4.24,0 -7.68,-3.76 -7.68,-8.39l0,-22.94c0,-8.65 6.42,-15.67 14.33,-15.67c7.92,0 14.33,7.01 14.33,15.67l0,22.94z"/>' +
      '<rect class="kv-gp-trig-fill" data-k="rt-fill" x="294" y="16" width="17" height="0" rx="3"/>' +
      '<text class="kv-gp-trig-text" x="302.5" y="48">' + L.trig.rt + '</text>' +
      '<rect class="kv-gp-shoulder kv-gp-lb" data-k="LB" x="116.8" y="66.8" width="43.3" height="17" rx="4"/>' +
      '<text class="kv-gp-shoulder-text" x="138.5" y="79">' + L.shoulder.LB + '</text>' +
      '<rect class="kv-gp-shoulder kv-gp-rb" data-k="RB" x="281.3" y="67" width="42.6" height="17" rx="4"/>' +
      '<text class="kv-gp-shoulder-text" x="302.5" y="79">' + L.shoulder.RB + '</text>' +
      '<circle class="kv-gp-stick-outer" cx="113" cy="160" r="37.5"/>' +
      '<g class="kv-gp-stick-l-g" data-k="LStick">' +
        '<circle class="kv-gp-stick-inner" cx="113" cy="160" r="28"/>' +
        '<circle class="kv-gp-stick-ring" cx="113" cy="160" r="22"/>' +
        '<circle class="kv-gp-stick-dot" cx="113" cy="160" r="10"/>' +
      '</g>' +
      '<rect class="kv-gp-dpad-u" data-k="DUp" x="159" y="211" width="14" height="20" rx="2"/>' +
      '<rect class="kv-gp-dpad-d" data-k="DDown" x="159" y="239" width="14" height="20" rx="2"/>' +
      '<rect class="kv-gp-dpad-l" data-k="DLeft" x="142" y="228" width="20" height="14" rx="2"/>' +
      '<rect class="kv-gp-dpad-r" data-k="DRight" x="170" y="228" width="20" height="14" rx="2"/>' +
      '<rect class="kv-gp-dpad-c" x="159" y="228" width="14" height="14"/>' +
      '<circle class="kv-gp-face kv-gp-face-u" data-k="Y" cx="329" cy="140" r="13" style="--face-c:' + f.Y.c + '"/>' +
      '<text class="kv-gp-face-text" x="329" y="144" style="--face-c:' + f.Y.c + '">' + f.Y.l + '</text>' +
      '<circle class="kv-gp-face kv-gp-face-l" data-k="X" cx="310" cy="162" r="13" style="--face-c:' + f.X.c + '"/>' +
      '<text class="kv-gp-face-text" x="310" y="166" style="--face-c:' + f.X.c + '">' + f.X.l + '</text>' +
      '<circle class="kv-gp-face kv-gp-face-r" data-k="B" cx="348" cy="161" r="13" style="--face-c:' + f.B.c + '"/>' +
      '<text class="kv-gp-face-text" x="348" y="165" style="--face-c:' + f.B.c + '">' + f.B.l + '</text>' +
      '<circle class="kv-gp-face kv-gp-face-d" data-k="A" cx="330" cy="181" r="13" style="--face-c:' + f.A.c + '"/>' +
      '<text class="kv-gp-face-text" x="330" y="185" style="--face-c:' + f.A.c + '">' + f.A.l + '</text>' +
      '<circle class="kv-gp-stick-outer" cx="278" cy="238" r="37.5"/>' +
      '<g class="kv-gp-stick-r-g" data-k="RStick">' +
        '<circle class="kv-gp-stick-inner" cx="278" cy="238" r="28"/>' +
        '<circle class="kv-gp-stick-ring" cx="278" cy="238" r="22"/>' +
        '<circle class="kv-gp-stick-dot" cx="278" cy="238" r="10"/>' +
      '</g>' +
      '<circle class="kv-gp-menu-btn kv-gp-menu-back" data-k="Back" cx="188" cy="162" r="10"/>' +
      '<text class="kv-gp-menu-text" x="188" y="165">' + L.menu.Back + '</text>' +
      '<circle class="kv-gp-menu-btn kv-gp-menu-guide" data-k="Guide" cx="220.5" cy="125" r="16"/>' +
      '<text class="kv-gp-menu-text kv-gp-guide-text" x="220.5" y="129">' + L.menu.Guide + '</text>' +
      '<circle class="kv-gp-menu-btn kv-gp-menu-start" data-k="Start" cx="253" cy="162" r="10"/>' +
      '<text class="kv-gp-menu-text" x="253" y="165">' + L.menu.Start + '</text>' +
      '</svg>';
  }
  function ensureGp(i) {
    let g = gpInstances.get(i);
    if (g) return g;
    const layout = cfg['display.gpLayout'] || 'xbox';
    const L = GP_LAYOUTS[layout] || GP_LAYOUTS.xbox;
    const wrap = document.createElement('div');
    wrap.className = 'kv-gp';
    wrap.setAttribute('data-layout', layout);
    wrap.innerHTML = buildGpSvg(L);
    gpEl.appendChild(wrap);
    const btnMap = {};
    wrap.querySelectorAll('[data-k]').forEach((el) => { btnMap[el.dataset.k] = el; });
    btnMap['LT'] = btnMap['lt']; btnMap['RT'] = btnMap['rt'];
    const lStick = wrap.querySelector('.kv-gp-stick-l-g');
    const rStick = wrap.querySelector('.kv-gp-stick-r-g');
    lStick.__sx = 0; lStick.__sy = 0; rStick.__sx = 0; rStick.__sy = 0;
    g = { wrap, btnMap, lStick, rStick, ltEl: btnMap['lt'], rtEl: btnMap['rt'], ltFill: wrap.querySelector('[data-k="lt-fill"]'), rtFill: wrap.querySelector('[data-k="rt-fill"]') };
    gpInstances.set(i, g);
    return g;
  }
  function onGpBtn(m) { const g = ensureGp(m.i); const b = g.btnMap[m.k]; if (b) b.classList.toggle('active', !!m.v); }
  function onGpAxis(m) {
    const g = ensureGp(m.i), s = m.k[0] === 'l' ? g.lStick : g.rStick;
    if (m.k[1] === 'x') s.__sx = m.v * 11; else s.__sy = m.v * 11;
    s.setAttribute('transform', 'translate(' + s.__sx + ',' + s.__sy + ')');
  }
  function onGpTrig(m) {
    const g = ensureGp(m.i), v = Math.max(0, Math.min(1, m.v));
    const fill = m.k === 'lt' ? g.ltFill : g.rtFill;
    if (fill) { fill.setAttribute('height', v * 44); fill.setAttribute('y', 60 - v * 44); }
    const t = m.k === 'lt' ? g.ltEl : g.rtEl;
    t.style.setProperty('--tv', v);
  }
  function onGpConn(m) { if (!m.v) { const g = gpInstances.get(m.i); if (g) { g.wrap.remove(); gpInstances.delete(m.i); } } else { ensureGp(m.i); } }

  // ============ combo rating (dmc only) ============
  const RANKS = ['D', 'C', 'B', 'A', 'S', 'SS', 'SSS'];
  const comboState = { rank: 0, progress: 0, lastHit: 0, active: false };
  const comboEl = document.getElementById('kv-combo');
  const comboRankEl = comboEl ? comboEl.querySelector('.kv-combo-rank') : null;
  const comboFillEl = comboEl ? comboEl.querySelector('.kv-combo-fill') : null;
  function comboHit() {
    if (!comboState.active) return;
    comboState.progress = Math.min(100, comboState.progress + 8);
    comboState.lastHit = Date.now();
    if (comboState.progress >= 100) {
      comboState.rank = Math.min(RANKS.length - 1, comboState.rank + 1);
      comboState.progress = 0;
    }
    updateComboUI();
    if (comboEl) { comboEl.classList.remove('kv-combo-hit'); void comboEl.offsetWidth; comboEl.classList.add('kv-combo-hit'); }
  }
  function updateCombo() {
    if (!comboState.active) return;
    const now = Date.now();
    if (now - comboState.lastHit > 1500) {
      comboState.progress = Math.max(0, comboState.progress - 2);
      if (comboState.progress <= 0 && comboState.rank > 0) comboState.rank = 0;
    }
    updateComboUI();
  }
  function updateComboUI() {
    if (!comboRankEl || !comboFillEl) return;
    comboRankEl.textContent = RANKS[comboState.rank];
    comboFillEl.style.width = comboState.progress + '%';
    comboRankEl.className = 'kv-combo-rank kv-combo-rank-' + comboState.rank;
  }
  function setComboActive(active) {
    comboState.active = active;
    if (comboEl) comboEl.style.display = active ? '' : 'none';
    if (active) { comboState.rank = 0; comboState.progress = 0; comboState.lastHit = Date.now(); updateComboUI(); }
  }
  setInterval(updateCombo, 50);

  // ============ WS ============
  function connect() {
    const wsUrl = (location.protocol === 'https:' ? 'wss://' : 'ws://') + location.host;
    let ws;
    try { ws = new WebSocket(wsUrl); } catch (e) { setTimeout(connect, 1500); return; }
    ws.onopen = () => { try { ws.send(JSON.stringify({ t: 'hello', theme: currentTheme })); } catch (e) {} };
    ws.onmessage = (ev) => {
      let m;
      try { m = JSON.parse(ev.data); } catch (e) { return; }
      if (m.t === 'cfg') { if (m.full) applyConfig(m.full); else if (m.key) { cfg[m.key] = m.v; applyConfig(cfg); } return; }
      if (!renderer) return;
      if (m.t === 'kb') { renderer.onKey(m); if (m.e === 'down') comboHit(); }
      else if (m.t === 'ms') { if (m.e === 'down') { renderer.onMouseDown(m); comboHit(); } else if (m.e === 'up') renderer.onMouseUp(m); else if (m.e === 'wheel') renderer.onWheel(m); else if (m.e === 'move') renderer.onMouseMove(m); }
      else if (m.t === 'gp') { if (m.e === 'btn') onGpBtn(m); else if (m.e === 'axis') onGpAxis(m); else if (m.e === 'trig') onGpTrig(m); else if (m.e === 'conn') onGpConn(m); }
    };
    ws.onclose = () => setTimeout(connect, 1500);
    ws.onerror = () => { try { ws.close(); } catch (e) {} };
  }

  // ============ init ============
  function start() {
    if (currentTheme) {
      applyTheme(currentTheme);
      rebuildRenderer();
      connect();
    } else {
      fetch('/config').then((r) => r.json()).then((c) => {
        cfg = c;
        applyTheme(c['overlay.theme'] || 'glass');
        rebuildRenderer();
        connect();
      }).catch(() => {
        applyTheme('glass');
        rebuildRenderer();
        connect();
      });
    }
  }
  start();
})();
