'use strict';
/**
 * Danmu Overlay — OBS 浏览器源弹幕浮层
 * 实时连接主服务 WS (/ws)，接收弹幕消息并渲染，支持多套皮肤与全维度可调样式热更新。
 */
(function () {
  const boxEl = document.getElementById('danmu-box');
  const stage = document.getElementById('danmu-stage');
  let cfg = {
    enabled: true,
    theme: 'classic-glass',
    displayMode: 'card',
    nicknameFont: 'system-ui',
    nicknameColor: '#ffd04b',
    nicknameOpacity: 100,
    nicknameSize: 14,
    nicknameBold: true,
    showMedal: true,
    danmuFont: 'system-ui',
    danmuColor: '#ffffff',
    danmuOpacity: 100,
    danmuSize: 16,
    danmuLineHeight: 1.4,
    danmuStroke: 'shadow',
    bgColor: '#1e222d',
    bgOpacity: 75,
    cardRadius: 10,
    cardPadding: 8,
    cardGap: 8,
    showBorder: true,
    borderColor: '#3a4256',
    borderOpacity: 60,
    borderWidth: 1,
    showAvatar: true,
    avatarSize: 36,
    avatarShape: 'circle',
    showAvatarBorder: true,
    avatarBorderColor: '#ffffff',
    avatarBorderOpacity: 40,
    avatarBorderWidth: 2,
    // 常驻固定底框 (Permanent Danmu Box)
    showBox: true,
    boxWidth: 380,
    boxHeight: 500,
    boxBgColor: '#141824',
    boxBgOpacity: 65,
    boxRadius: 12,
    boxPadding: 12,
    showBoxBorder: true,
    boxBorderColor: '#3a4256',
    boxBorderOpacity: 60,
    boxBorderWidth: 1,
    // 全局边缘虚化与进房播报
    edgeFadeMode: 'none',
    edgeFadeDistance: 80,
    showEnterRoom: true,
    enterRoomText: '进入直播间',
    animation: 'slide',
    stayDuration: 15,
    maxCount: 15
  };

  // 内置高质量极简渐变 SVG 兜底默认头像（0依赖，永不404）
  const DEFAULT_AVATAR = 'data:image/svg+xml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHZpZXdCb3g9IjAgMCAxMDAgMTAwIj48ZGVmcz48bGluZWFyR3JhZGllbnQgaWQ9ImRiZyIgeDE9IjAlIiB5MT0iMCUiIHgyPSIxMDAlIiB5Mj0iMTAwJSI+PHN0b3Agb2Zmc2V0PSIwJSIgc3RvcC1jb2xvcj0iIzNhNDE1MiIvPjxzdG9wIG9mZnNldD0iMTAwJSIgc3RvcC1jb2xvcj0iIzFlMjIyZCIvPjwvbGluZWFyR3JhZGllbnQ+PC9kZWZzPjxjaXJjbGUgY3g9IjUwIiBjeT0iNTAiIHI9IjQ4IiBmaWxsPSJ1cmwoI2RiZykiIHN0cm9rZT0iIzUzNWQ3NSIgc3Ryb2tlLXdpZHRoPSIyIi8+PGNpcmNsZSBjeD0iNTAiIGN5PSIzOCIgcj0iMTgiIGZpbGw9IiM4ODkyYjAiLz48cGF0aCBkPSJNMjIsODQgQzI0LDY0IDM2LDU4IDUwLDU4IEM2NCw1OCA3Niw2NCA3OCw4NCBaIiBmaWxsPSIjODg5MmIwIi8+PC9zdmc+';
  const GUARD_NAMES = ['', '总督', '提督', '舰长'];

  function esc(s) {
    return String(s == null ? '' : s).replace(/[&<>"']/g, c => ({
      '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
    }[c]));
  }

  // 格式化弹幕内容：将 B站表情符号替换为原生表情图片，加载失败或无图时使用 alt 纯文字优雅兜底
  function formatDanmuContent(rawText, emotes) {
    if (!rawText) return '';
    let text = esc(rawText);
    if (!emotes || typeof emotes !== 'object') return text;

    const trimmed = rawText.trim();
    for (const key in emotes) {
      const em = emotes[key];
      if (!em || !em.url) continue;
      const safeKey = esc(key);
      const isSolo = (trimmed === key || trimmed === key.replace(/^\[(.*)\]$/, '$1'));
      const cls = isSolo ? 'danmu-emoji large' : 'danmu-emoji';
      const imgHtml = `<img class="${cls}" src="${esc(em.url)}" alt="${safeKey}" title="${safeKey}" referrerpolicy="no-referrer" onerror="this.outerHTML=this.alt" />`;
      if (text.includes(safeKey)) {
        text = text.split(safeKey).join(imgHtml);
      }
    }
    return text;
  }

  function hexToRgba(hex, opacityPercent) {
    if (!hex) return 'rgba(0,0,0,1)';
    let h = hex.replace('#', '');
    if (h.length === 3) h = h[0] + h[0] + h[1] + h[1] + h[2] + h[2];
    const r = parseInt(h.substring(0, 2), 16) || 0;
    const g = parseInt(h.substring(2, 4), 16) || 0;
    const b = parseInt(h.substring(4, 6), 16) || 0;
    const a = Math.max(0, Math.min(1, (opacityPercent != null ? opacityPercent : 100) / 100));
    return `rgba(${r}, ${g}, ${b}, ${a})`;
  }

  function applyConfig(newCfg) {
    if (!newCfg) return;
    // 兼容 PascalCase 与 camelCase
    const normalized = {};
    for (const k in newCfg) {
      const camel = k.charAt(0).toLowerCase() + k.slice(1);
      normalized[camel] = newCfg[k];
    }
    cfg = Object.assign({}, cfg, normalized);

    // 切换主题样式表
    const themeEl = document.getElementById('theme-css');
    if (themeEl) {
      const themeName = String(cfg.theme || 'classic-glass').replace(/[^a-z0-9_-]/gi, '');
      themeEl.href = `./themes/${themeName}.css`;
    }

    // 常驻固定底框 (Permanent Danmu Box)
    const isBoxEnabled = cfg.showBox !== false && cfg.showStageBox !== false;
    if (boxEl) {
      boxEl.classList.toggle('box-disabled', !isBoxEnabled);
      const bWidthPx = (cfg.boxWidth || 380) + 'px';
      const bHeightPx = (cfg.boxHeight || 500) + 'px';
      boxEl.style.setProperty('--danmu-box-width', bWidthPx);
      boxEl.style.setProperty('--danmu-box-height', bHeightPx);

      if (isBoxEnabled) {
        const bgCol = cfg.boxBgColor || cfg.stageBgColor || '#141824';
        const bgOp = cfg.boxBgOpacity != null ? cfg.boxBgOpacity : (cfg.stageBgOpacity != null ? cfg.stageBgOpacity : 65);
        boxEl.style.setProperty('--danmu-box-bg', hexToRgba(bgCol, bgOp));

        const radius = cfg.boxRadius != null ? cfg.boxRadius : (cfg.stageRadius != null ? cfg.stageRadius : 12);
        boxEl.style.setProperty('--danmu-box-radius', radius + 'px');

        const pad = cfg.boxPadding != null ? cfg.boxPadding : (cfg.stagePadding != null ? cfg.stagePadding : 12);
        boxEl.style.setProperty('--danmu-box-padding', pad + 'px');

        const showBorder = cfg.showBoxBorder !== false && cfg.showStageBorder !== false;
        const bWidth = showBorder ? (cfg.boxBorderWidth != null ? cfg.boxBorderWidth : (cfg.stageBorderWidth || 1)) : 0;
        const bColor = cfg.boxBorderColor || cfg.stageBorderColor || '#3a4256';
        const bOp = cfg.boxBorderOpacity != null ? cfg.boxBorderOpacity : (cfg.stageBorderOpacity != null ? cfg.stageBorderOpacity : 60);

        boxEl.style.setProperty('--danmu-box-border', bWidth > 0 ? `${bWidth}px solid ${hexToRgba(bColor, bOp)}` : 'none');
        boxEl.style.setProperty('--danmu-box-shadow', '0 8px 32px rgba(0,0,0,0.35)');
      } else {
        boxEl.style.setProperty('--danmu-box-bg', 'transparent');
        boxEl.style.setProperty('--danmu-box-border', 'none');
        boxEl.style.setProperty('--danmu-box-shadow', 'none');
      }
    }

    // 昵称样式
    const nickFont = cfg.nicknameFont === 'system-ui' ? 'system-ui, -apple-system, sans-serif' : cfg.nicknameFont;
    stage.style.setProperty('--danmu-nick-font', nickFont);
    stage.style.setProperty('--danmu-nick-color', hexToRgba(cfg.nicknameColor, cfg.nicknameOpacity));
    stage.style.setProperty('--danmu-nick-size', (cfg.nicknameSize || 14) + 'px');
    stage.style.setProperty('--danmu-nick-weight', cfg.nicknameBold ? '700' : '400');

    // 弹幕内容样式
    const danmuFont = cfg.danmuFont === 'system-ui' ? 'system-ui, -apple-system, sans-serif' : cfg.danmuFont;
    stage.style.setProperty('--danmu-text-font', danmuFont);
    stage.style.setProperty('--danmu-text-color', hexToRgba(cfg.danmuColor, cfg.danmuOpacity));
    stage.style.setProperty('--danmu-text-size', (cfg.danmuSize || 16) + 'px');
    stage.style.setProperty('--danmu-line-height', cfg.danmuLineHeight || 1.4);

    let shadowStyle = 'none';
    if (cfg.danmuStroke === 'shadow') shadowStyle = '0 1px 3px rgba(0,0,0,0.85)';
    else if (cfg.danmuStroke === 'heavy-shadow') shadowStyle = '0 2px 8px rgba(0,0,0,0.95), 0 0 2px rgba(0,0,0,0.8)';
    else if (cfg.danmuStroke === 'outline') shadowStyle = '-1px -1px 0 #000, 1px -1px 0 #000, -1px 1px 0 #000, 1px 1px 0 #000';
    stage.style.setProperty('--danmu-text-shadow', shadowStyle);

    // 背景与卡片
    stage.style.setProperty('--danmu-bg-color', hexToRgba(cfg.bgColor, cfg.bgOpacity));
    stage.style.setProperty('--danmu-card-radius', (cfg.cardRadius != null ? cfg.cardRadius : 10) + 'px');
    const pad = cfg.cardPadding != null ? cfg.cardPadding : 8;
    stage.style.setProperty('--danmu-card-padding', `${pad}px ${Math.round(pad * 1.4)}px`);
    stage.style.setProperty('--danmu-card-gap', (cfg.cardGap != null ? cfg.cardGap : 8) + 'px');

    // 外边框
    const bWidth = cfg.showBorder ? (cfg.borderWidth || 1) : 0;
    stage.style.setProperty('--danmu-border-width', bWidth + 'px');
    stage.style.setProperty('--danmu-border-color', hexToRgba(cfg.borderColor, cfg.borderOpacity));

    // 头像
    stage.style.setProperty('--danmu-avatar-size', (cfg.avatarSize || 36) + 'px');
    const shape = cfg.avatarShape || 'circle';
    const radius = shape === 'circle' ? '50%' : (shape === 'rounded' ? '8px' : '0px');
    stage.style.setProperty('--danmu-avatar-radius', radius);

    const aBorder = cfg.showAvatarBorder && (cfg.avatarBorderWidth || 2) > 0
      ? `${cfg.avatarBorderWidth || 2}px solid ${hexToRgba(cfg.avatarBorderColor, cfg.avatarBorderOpacity)}`
      : 'none';
    stage.style.setProperty('--danmu-avatar-border', aBorder);

    // 大航海专属个性化样式 (舰长 / 提督 / 总督)
    if (cfg.customGuardStyle) {
      stage.style.setProperty('--danmu-guard1-bg', hexToRgba(cfg.governorBgColor || '#581c2b', cfg.governorBgOpacity != null ? cfg.governorBgOpacity : 85));
      stage.style.setProperty('--danmu-guard1-border', hexToRgba(cfg.governorBorderColor || '#ff4d6d', cfg.governorBorderOpacity != null ? cfg.governorBorderOpacity : 90));
      stage.style.setProperty('--danmu-guard1-text', cfg.governorTextColor || '#ffccd5');

      stage.style.setProperty('--danmu-guard2-bg', hexToRgba(cfg.admiralBgColor || '#341c54', cfg.admiralBgOpacity != null ? cfg.admiralBgOpacity : 85));
      stage.style.setProperty('--danmu-guard2-border', hexToRgba(cfg.admiralBorderColor || '#c77dff', cfg.admiralBorderOpacity != null ? cfg.admiralBorderOpacity : 85));
      stage.style.setProperty('--danmu-guard2-text', cfg.admiralTextColor || '#e0aaff');

      stage.style.setProperty('--danmu-guard3-bg', hexToRgba(cfg.captainBgColor || '#1c3254', cfg.captainBgOpacity != null ? cfg.captainBgOpacity : 80));
      stage.style.setProperty('--danmu-guard3-border', hexToRgba(cfg.captainBorderColor || '#4ea8de', cfg.captainBorderOpacity != null ? cfg.captainBorderOpacity : 80));
      stage.style.setProperty('--danmu-guard3-text', cfg.captainTextColor || '#90e0ef');
    } else {
      stage.style.removeProperty('--danmu-guard1-bg');
      stage.style.removeProperty('--danmu-guard1-border');
      stage.style.removeProperty('--danmu-guard1-text');
      stage.style.removeProperty('--danmu-guard2-bg');
      stage.style.removeProperty('--danmu-guard2-border');
      stage.style.removeProperty('--danmu-guard2-text');
      stage.style.removeProperty('--danmu-guard3-bg');
      stage.style.removeProperty('--danmu-guard3-border');
      stage.style.removeProperty('--danmu-guard3-text');
    }

    // 全局边缘虚化渐隐遮罩 (Universal Edge Fade Mask)
    const fadeMode = cfg.edgeFadeMode || 'none';
    const fadeDist = Math.max(20, Math.min(200, Number(cfg.edgeFadeDistance) || 80));
    stage.style.setProperty('--fade-distance', fadeDist + 'px');
    stage.classList.toggle('fade-top', fadeMode === 'top');
    stage.classList.toggle('fade-bottom', fadeMode === 'bottom');
    stage.classList.toggle('fade-both', fadeMode === 'both');

    stage.classList.toggle('no-avatar', !cfg.showAvatar);
    stage.classList.toggle('display-flat', cfg.displayMode === 'flat');
  }

  async function loadConfig() {
    try {
      const res = await fetch('/api/danmu-overlay/config');
      if (res.ok) {
        const data = await res.json();
        if (data && data.config) {
          applyConfig(data.config);
        }
      }
    } catch (e) {
      console.warn('[DanmuOverlay] 读取配置异常，采用默认值', e);
    }
  }

  function addDanmu(ev) {
    if (!cfg.enabled) return;
    const uname = ev.uname || ev.Uname || (ev.uid || ev.Uid ? '用户' + (ev.uid || ev.Uid) : '观众');
    const msg = ev.msg || ev.Msg || '';
    if (!msg.trim()) return;

    let face = ev.uface || ev.Uface || DEFAULT_AVATAR;
    if (face.startsWith('//')) face = 'https:' + face;

    const isGuard = ev.isGuard || ev.IsGuard || ((ev.guardLevel || ev.GuardLevel) && (ev.guardLevel || ev.GuardLevel) > 0);
    const rawGuardLevel = Number(ev.guardLevel || ev.GuardLevel || (isGuard ? 3 : 0));
    const guardLevel = (rawGuardLevel >= 1 && rawGuardLevel <= 3) ? rawGuardLevel : (isGuard ? 3 : 0);
    const guardName = guardLevel > 0 ? GUARD_NAMES[guardLevel] : '';
    const medalLevel = ev.medalLevel || ev.MedalLevel || 0;

    const item = document.createElement('div');
    const animClass = 'anim-' + (cfg.animation || 'slide');
    const guardClass = guardLevel > 0 ? ` guard-level-${guardLevel} is-guard` : '';
    item.className = `danmu-item ${animClass}${guardClass}`;

    let badgesHtml = '';
    if (isGuard && guardName) {
      badgesHtml += `<span class="danmu-badge guard">${esc(guardName)}</span>`;
    }
    if (cfg.showMedal && medalLevel > 0) {
      badgesHtml += `<span class="danmu-badge medal">Lv.${medalLevel}</span>`;
    }

    item.innerHTML = `
      <div class="danmu-bar"></div>
      <div class="danmu-avatar-wrap">
        <img class="danmu-avatar" src="${esc(face)}" alt="${esc(uname)}" referrerpolicy="no-referrer" onerror="this.src='${DEFAULT_AVATAR}'" />
      </div>
      <div class="danmu-content">
        <div class="danmu-meta">
          ${badgesHtml}
          <span class="danmu-uname">${esc(uname)}</span>
        </div>
        <div class="danmu-text">${formatDanmuContent(msg, ev.emotes || ev.Emotes)}</div>
      </div>
    `;

    stage.appendChild(item);

    // 最大条数限制
    const maxCount = Math.max(3, cfg.maxCount || 15);
    while (stage.children.length > maxCount) {
      const oldest = stage.firstElementChild;
      if (oldest) stage.removeChild(oldest);
      else break;
    }

    // 停留时间自动淡出
    const staySec = Number(cfg.stayDuration != null ? cfg.stayDuration : 15);
    if (staySec > 0) {
      setTimeout(() => {
        if (item && item.parentNode) {
          item.classList.add('fading-out');
          setTimeout(() => {
            if (item && item.parentNode) {
              item.parentNode.removeChild(item);
            }
          }, 500);
        }
      }, staySec * 1000);
    }
  }

  function addEntry(ev) {
    if (!cfg.enabled || cfg.showEnterRoom === false) return;
    const uname = ev.uname || ev.Uname || (ev.uid || ev.Uid ? '用户' + (ev.uid || ev.Uid) : '观众');
    let face = ev.uface || ev.Uface || DEFAULT_AVATAR;
    if (face.startsWith('//')) face = 'https:' + face;
    const actionText = cfg.enterRoomText || '进入直播间';

    const item = document.createElement('div');
    const animClass = 'anim-' + (cfg.animation || 'slide');
    item.className = `danmu-item danmu-entry ${animClass}`;

    item.innerHTML = `
      <div class="danmu-bar"></div>
      <div class="danmu-avatar-wrap">
        <img class="danmu-avatar" src="${esc(face)}" alt="${esc(uname)}" referrerpolicy="no-referrer" onerror="this.src='${DEFAULT_AVATAR}'" />
      </div>
      <div class="entry-content">
        <span class="entry-uname">${esc(uname)}</span>
        <span class="entry-action">${esc(actionText)}</span>
      </div>
    `;

    stage.appendChild(item);

    // 最大条数限制
    const maxCount = Math.max(3, cfg.maxCount || 15);
    while (stage.children.length > maxCount) {
      const oldest = stage.firstElementChild;
      if (oldest) stage.removeChild(oldest);
      else break;
    }

    // 停留时间自动淡出
    const staySec = Number(cfg.stayDuration != null ? cfg.stayDuration : 15);
    if (staySec > 0) {
      setTimeout(() => {
        if (item && item.parentNode) {
          item.classList.add('fading-out');
          setTimeout(() => {
            if (item && item.parentNode) {
              item.parentNode.removeChild(item);
            }
          }, 500);
        }
      }, staySec * 1000);
    }
  }

  function connectWs() {
    const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
    const wsUrl = `${proto}//${location.host}/ws`;
    const ws = new WebSocket(wsUrl);

    ws.onopen = () => {
      console.log('[DanmuOverlay] WebSocket 已连接');
    };

    ws.onmessage = (e) => {
      try {
        const frame = JSON.parse(e.data);
        if (!frame) return;

        // 热更新样式
        if (frame.type === 'danmu_config_update') {
          applyConfig(frame.data);
          return;
        }

        // 弹幕事件 (普通弹幕 / 进房通知 / SC / 测试)
        if (frame.type === 'event' && frame.data) {
          const ev = frame.data;
          if (ev.type === 'danmu') {
            addDanmu(ev);
          } else if (ev.type === 'interact') {
            // 进房消息 (msgType === 1 或 0 视作进入直播间)
            if (ev.msgType == null || ev.msgType === 1 || ev.msgType === 0) {
              addEntry(ev);
            }
          } else if (ev.type === 'superchat') {
            addDanmu({
              uname: ev.uname || ev.Uname,
              msg: `[SC ￥${ev.price != null ? ev.price : (ev.Price || '')}] ` + (ev.msg || ev.Msg || ''),
              uface: ev.uface || ev.Uface,
              isGuard: ev.isGuard || ev.IsGuard,
              guardLevel: ev.guardLevel || ev.GuardLevel,
              medalLevel: ev.medalLevel || ev.MedalLevel
            });
          }
        } else if (frame.type === 'danmu_test' && frame.data) {
          if (frame.data.isEntry) {
            addEntry(frame.data);
          } else {
            addDanmu(frame.data);
          }
        }
      } catch (err) {
        console.error('[DanmuOverlay] 解析消息异常', err);
      }
    };

    ws.onclose = () => {
      setTimeout(connectWs, 2500);
    };

    ws.onerror = () => {
      ws.close();
    };
  }

  // 初始化
  loadConfig().then(() => {
    connectWs();
  });
})();
