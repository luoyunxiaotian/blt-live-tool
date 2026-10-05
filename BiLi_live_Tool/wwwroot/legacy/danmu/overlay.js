'use strict';
/**
 * Danmu Overlay — OBS 浏览器源弹幕浮层
 * 实时连接主服务 WS (/ws)，接收弹幕消息并渲染。
 * 支持 Laplace Chat 13 套现代主题模板无缝兼容、双 Class 架构 (event/danmu-item) 与全套细颗粒度事件控制。
 */
(function () {
  const boxEl = document.getElementById('danmu-box');
  const stage = document.getElementById('danmu-stage');
  const customCssEl = document.getElementById('custom-css');
  const themeEl = document.getElementById('theme-css');
  const isPreviewMode = location.search.includes('preview=1');

  let cfg = {
    enabled: true,
    theme: 'classic-glass',
    themeType: 'classic', // 'classic' (本地皮肤) 或 'laplace' (Laplace 模版)
    laplaceTemplate: 'laplace-nailv',
    customCss: '',
    colorScheme: 'dark', // 'dark', 'light', 'auto'
    baseFontSize: 16,
    displayMode: 'card', // 'card' 或 'flat'
    avatarPosition: 'left', // 'left', 'top-left', 'top-center'
    badgePosition: 'before-name', // 'before-name', 'after-name'

    // 基础元素控制开关
    showAvatar: true,
    showAvatarFrame: true,
    showUsername: true,
    showMedal: true,
    showMedalLightenedOnly: false,
    showWealthMedal: true,
    showModBadge: true,
    showGuardBadge: true,
    showCurrentRank: true,

    // 事件类型控制开关
    showDanmaku: true,
    showGift: true,
    showSuperChat: true,
    showToast: true, // 大航海购买
    showGuardBuy: true,
    showEnterRoom: true,
    showFollowEvent: true,
    showLottery: true,
    showRedEnvelop: true,
    showSystemMessage: true,

    // 过滤与阈值
    filterFreeGift: true,
    filterPhoneNotVerified: false,
    enterRoomGuardOnly: false,
    giftPriceAbove: 0,
    giftHighlightAbove: 29.99,

    // 样式参数 (向后兼容)
    nicknameFont: 'system-ui',
    nicknameColor: '#ffd04b',
    nicknameOpacity: 100,
    nicknameSize: 14,
    nicknameBold: true,
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

    // 边缘虚化与动画
    edgeFadeMode: 'none',
    edgeFadeDistance: 80,
    enterRoomText: '进入直播间',
    animation: 'slide',
    stayDuration: 15,
    maxCount: 20
  };

  const DEFAULT_AVATAR = 'data:image/svg+xml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHZpZXdCb3g9IjAgMCAxMDAgMTAwIj48ZGVmcz48bGluZWFyR3JhZGllbnQgaWQ9ImRiZyIgeDE9IjAlIiB5MT0iMCUiIHgyPSIxMDAlIiB5Mj0iMTAwJSI+PHN0b3Agb2Zmc2V0PSIwJSIgc3RvcC1jb2xvcj0iIzNhNDE1MiIvPjxzdG9wIG9mZnNldD0iMTAwJSIgc3RvcC1jb2xvcj0iIzFlMjIyZCIvPjwvbGluZWFyR3JhZGllbnQ+PC9kZWZzPjxjaXJjbGUgY3g9IjUwIiBjeT0iNTAiIHI9IjQ4IiBmaWxsPSJ1cmwoI2RiZykiIHN0cm9rZT0iIzUzNWQ3NSIgc3Ryb2tlLXdpZHRoPSIyIi8+PGNpcmNsZSBjeD0iNTAiIGN5PSIzOCIgcj0iMTgiIGZpbGw9IiM4ODkyYjAiLz48cGF0aCBkPSJNMjIsODQgQzI0LDY0IDM2LDU4IDUwLDU4IEM2NCw1OCA3Niw2NCA3OCw4NCBaIiBmaWxsPSIjODg5MmIwIi8+PC9zdmc+';
  const GUARD_NAMES = ['', '总督', '提督', '舰长'];

  // Laplace 模版 ID 集合快速判断
  const LAPLACE_TEMPLATES = new Set([
    'laplace-nailv', 'laplace-nailv-next', 'rhea-purple', 'sui-outline',
    'zelda-fantasy', 'nana7mi-simple', 'miki-modern', 'collab-dialog',
    'reversed-fun', 'orange-bubble', 'shed-bubble', 'shed-wave', 'orange-block'
  ]);

  function esc(s) {
    return String(s == null ? '' : s).replace(/[&<>"']/g, c => ({
      '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
    }[c]));
  }

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
      const cls = isSolo ? 'danmu-emoji large emoji' : 'danmu-emoji emoji';
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
    const normalized = {};
    for (const k in newCfg) {
      const camel = k.charAt(0).toLowerCase() + k.slice(1);
      normalized[camel] = newCfg[k];
    }
    cfg = Object.assign({}, cfg, normalized);

    // 1. 切换主题样式表 (支持 Laplace 模板与自研经典主题)
    if (themeEl) {
      const selectedTheme = cfg.theme || 'classic-glass';
      const isLaplace = cfg.themeType === 'laplace' || LAPLACE_TEMPLATES.has(selectedTheme);
      const vQuery = (location.protocol === 'http:' || location.protocol === 'https:') ? `?v=${Date.now()}` : '';
      if (isLaplace) {
        const tName = LAPLACE_TEMPLATES.has(selectedTheme) ? selectedTheme : (cfg.laplaceTemplate || 'laplace-nailv');
        themeEl.href = `./templates/${tName}.css${vQuery}`;
      } else {
        const themeName = String(selectedTheme).replace(/[^a-z0-9_-]/gi, '');
        themeEl.href = `./themes/${themeName}.css${vQuery}`;
      }
    }

    // 2. 注入自定义 CSS
    if (customCssEl) {
      customCssEl.textContent = cfg.customCss || '';
    }

    // 3. 配色模式 (深色 / 浅色) 与 模版类型
    const scheme = cfg.colorScheme || 'dark';
    document.body.classList.toggle('scheme-dark', scheme !== 'light');
    document.body.classList.toggle('scheme-light', scheme === 'light');

    const isLaplace = cfg.themeType === 'laplace' || LAPLACE_TEMPLATES.has(cfg.theme);
    document.body.classList.toggle('theme-type-laplace', isLaplace);
    document.body.classList.toggle('theme-type-classic', !isLaplace);
    document.body.setAttribute('data-theme-type', isLaplace ? 'laplace' : 'classic');
    if (stage) stage.setAttribute('data-avatar-pos', cfg.avatarPosition || 'left');

    // 4. 基准字号与 Laplace 变量支持
    const baseFs = Number(cfg.baseFontSize || cfg.danmuSize || 16);
    stage.style.setProperty('--event-font-size', baseFs + 'px');
    stage.style.setProperty('--size', baseFs + 'px');
    stage.style.setProperty('--event-font-family', cfg.danmuFont === 'system-ui' ? 'system-ui, -apple-system, sans-serif' : cfg.danmuFont);

    // 5. 常驻固定底框 (Permanent Danmu Box)
    const isBoxEnabled = isLaplace
      ? (cfg.showBox === true)
      : (cfg.showBox !== false && cfg.showStageBox !== false);
    if (boxEl) {
      boxEl.classList.toggle('box-disabled', !isBoxEnabled);
      boxEl.classList.toggle('box-explicit-enabled', isBoxEnabled);
      const bWidthPx = (cfg.boxWidth || 380) + 'px';
      const bHeightPx = (cfg.boxHeight || 500) + 'px';
      boxEl.style.setProperty('--danmu-box-width', bWidthPx);
      boxEl.style.setProperty('--danmu-box-height', bHeightPx);

      if (isBoxEnabled) {
        const bgCol = cfg.boxBgColor || cfg.stageBgColor || '#141824';
        const bgOp = cfg.boxBgOpacity != null ? cfg.boxBgOpacity : 65;
        boxEl.style.setProperty('--danmu-box-bg', hexToRgba(bgCol, bgOp));

        const radius = cfg.boxRadius != null ? cfg.boxRadius : 12;
        boxEl.style.setProperty('--danmu-box-radius', radius + 'px');

        const pad = cfg.boxPadding != null ? cfg.boxPadding : 12;
        boxEl.style.setProperty('--danmu-box-padding', pad + 'px');

        const showBorder = cfg.showBoxBorder !== false && cfg.showStageBorder !== false;
        const bWidth = showBorder ? (cfg.boxBorderWidth != null ? cfg.boxBorderWidth : 1) : 0;
        const bColor = cfg.boxBorderColor || cfg.stageBorderColor || '#3a4256';
        const bOp = cfg.boxBorderOpacity != null ? cfg.boxBorderOpacity : 60;

        boxEl.style.setProperty('--danmu-box-border', bWidth > 0 ? `${bWidth}px solid ${hexToRgba(bColor, bOp)}` : 'none');
        boxEl.style.setProperty('--danmu-box-shadow', '0 8px 32px rgba(0,0,0,0.35)');
      } else {
        boxEl.style.setProperty('--danmu-box-bg', 'transparent');
        boxEl.style.setProperty('--danmu-box-border', 'none');
        boxEl.style.setProperty('--danmu-box-shadow', 'none');
      }
    }

    // 动态同步舞台中已有弹幕项的布局 Class
    const newAvatarPos = cfg.avatarPosition || 'left';
    const newPosClass = 'layout-avatar-' + newAvatarPos;
    if (stage) {
      stage.setAttribute('data-avatar-pos', newAvatarPos);
      Array.from(stage.querySelectorAll('.danmu-item')).forEach(el => {
        el.classList.remove('layout-avatar-left', 'layout-avatar-top-left', 'layout-avatar-top-center', 'layout-avatar-hidden');
        el.classList.add(newPosClass);
      });
    }

    // 6. 昵称与弹幕样式
    const nickFont = cfg.nicknameFont === 'system-ui' ? 'system-ui, -apple-system, sans-serif' : cfg.nicknameFont;
    stage.style.setProperty('--danmu-nick-font', nickFont);
    stage.style.setProperty('--danmu-nick-color', hexToRgba(cfg.nicknameColor, cfg.nicknameOpacity));
    stage.style.setProperty('--danmu-nick-size', (cfg.nicknameSize || 14) + 'px');
    stage.style.setProperty('--danmu-nick-weight', cfg.nicknameBold ? '700' : '400');

    const danmuFont = cfg.danmuFont === 'system-ui' ? 'system-ui, -apple-system, sans-serif' : cfg.danmuFont;
    stage.style.setProperty('--danmu-text-font', danmuFont);
    stage.style.setProperty('--danmu-text-color', hexToRgba(cfg.danmuColor, cfg.danmuOpacity));
    stage.style.setProperty('--danmu-text-size', baseFs + 'px');
    stage.style.setProperty('--danmu-line-height', cfg.danmuLineHeight || 1.4);

    let shadowStyle = 'none';
    if (cfg.danmuStroke === 'shadow') shadowStyle = '0 1px 3px rgba(0,0,0,0.85)';
    else if (cfg.danmuStroke === 'heavy-shadow') shadowStyle = '0 2px 8px rgba(0,0,0,0.95), 0 0 2px rgba(0,0,0,0.8)';
    else if (cfg.danmuStroke === 'outline') shadowStyle = '-1px -1px 0 #000, 1px -1px 0 #000, -1px 1px 0 #000, 1px 1px 0 #000';
    stage.style.setProperty('--danmu-text-shadow', shadowStyle);

    // 7. 背景与卡片
    stage.style.setProperty('--danmu-bg-color', hexToRgba(cfg.bgColor, cfg.bgOpacity));
    stage.style.setProperty('--danmu-card-radius', (cfg.cardRadius != null ? cfg.cardRadius : 10) + 'px');
    const pad = cfg.cardPadding != null ? cfg.cardPadding : 8;
    stage.style.setProperty('--danmu-card-padding', `${pad}px ${Math.round(pad * 1.4)}px`);
    stage.style.setProperty('--danmu-card-gap', (cfg.cardGap != null ? cfg.cardGap : 8) + 'px');

    const bWidth = cfg.showBorder ? (cfg.borderWidth || 1) : 0;
    stage.style.setProperty('--danmu-border-width', bWidth + 'px');
    stage.style.setProperty('--danmu-border-color', hexToRgba(cfg.borderColor, cfg.borderOpacity));

    // 8. 头像
    stage.style.setProperty('--danmu-avatar-size', (cfg.avatarSize || 36) + 'px');
    const shape = cfg.avatarShape || 'circle';
    const radius = shape === 'circle' ? '50%' : (shape === 'rounded' ? '8px' : '0px');
    stage.style.setProperty('--danmu-avatar-radius', radius);

    const aBorder = cfg.showAvatarBorder && (cfg.avatarBorderWidth || 2) > 0
      ? `${cfg.avatarBorderWidth || 2}px solid ${hexToRgba(cfg.avatarBorderColor, cfg.avatarBorderOpacity)}`
      : 'none';
    stage.style.setProperty('--danmu-avatar-border', aBorder);

    // 9. 大航海个性化配色
    if (cfg.customGuardStyle) {
      stage.style.setProperty('--danmu-guard1-bg', hexToRgba(cfg.governorBgColor || '#581c2b', cfg.governorBgOpacity || 85));
      stage.style.setProperty('--danmu-guard1-border', hexToRgba(cfg.governorBorderColor || '#ff4d6d', cfg.governorBorderOpacity || 90));
      stage.style.setProperty('--danmu-guard1-text', cfg.governorTextColor || '#ffccd5');

      stage.style.setProperty('--danmu-guard2-bg', hexToRgba(cfg.admiralBgColor || '#341c54', cfg.admiralBgOpacity || 85));
      stage.style.setProperty('--danmu-guard2-border', hexToRgba(cfg.admiralBorderColor || '#c77dff', cfg.admiralBorderOpacity || 85));
      stage.style.setProperty('--danmu-guard2-text', cfg.admiralTextColor || '#e0aaff');

      stage.style.setProperty('--danmu-guard3-bg', hexToRgba(cfg.captainBgColor || '#1c3254', cfg.captainBgOpacity || 80));
      stage.style.setProperty('--danmu-guard3-border', hexToRgba(cfg.captainBorderColor || '#4ea8de', cfg.captainBorderOpacity || 80));
      stage.style.setProperty('--danmu-guard3-text', cfg.captainTextColor || '#90e0ef');
    }

    // 10. 边缘渐隐
    const fadeMode = cfg.edgeFadeMode || 'none';
    const fadeDist = Math.max(20, Math.min(200, Number(cfg.edgeFadeDistance) || 80));
    stage.style.setProperty('--fade-distance', fadeDist + 'px');
    stage.classList.toggle('fade-top', fadeMode === 'top');
    stage.classList.toggle('fade-bottom', fadeMode === 'bottom');
    stage.classList.toggle('fade-both', fadeMode === 'both');

    stage.classList.toggle('no-avatar', cfg.showAvatar === false);
    stage.classList.toggle('display-flat', cfg.displayMode === 'flat');

    if (isPreviewMode) {
      stage.innerHTML = '';
      initPreviewMock();
    }
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

  function appendItem(item) {
    stage.appendChild(item);

    // 最大条数限制
    const maxCount = Math.max(3, cfg.maxCount || 20);
    while (stage.children.length > maxCount) {
      const oldest = stage.firstElementChild;
      if (oldest) stage.removeChild(oldest);
      else break;
    }

    // 停留时间自动淡出 (预览模式下保持常驻展示，便于主播实时调参)
    const staySec = Number(cfg.stayDuration != null ? cfg.stayDuration : 15);
    if (staySec > 0 && !isPreviewMode) {
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

  // ---- 1. 普通文字弹幕 ----
  function addDanmu(ev) {
    if (!cfg.enabled || cfg.showDanmaku === false) return;
    if (cfg.filterPhoneNotVerified && ev.isPhoneVerified === false) return;

    const uname = ev.uname || ev.Uname || (ev.uid || ev.Uid ? '用户' + (ev.uid || ev.Uid) : '观众');
    const msg = ev.msg || ev.Msg || '';
    if (!msg.trim()) return;

    let face = ev.uface || ev.Uface || DEFAULT_AVATAR;
    if (face.startsWith('//')) face = 'https:' + face;

    const isGuard = ev.isGuard || ev.IsGuard || ((ev.guardLevel || ev.GuardLevel) && (ev.guardLevel || ev.GuardLevel) > 0);
    const rawGuardLevel = Number(ev.guardLevel || ev.GuardLevel || (isGuard ? 3 : 0));
    const guardLevel = (rawGuardLevel >= 1 && rawGuardLevel <= 3) ? rawGuardLevel : (isGuard ? 3 : 0);
    const guardName = guardLevel > 0 ? GUARD_NAMES[guardLevel] : '';
    const medalLevel = Number(ev.medalLevel || ev.MedalLevel || 0);
    const isMedalLight = ev.isLighted != null ? ev.isLighted : (ev.isMedalLight != null ? ev.isMedalLight : true);
    const isStreamer = ev.isStreamer || ev.uid === 'streamer' || false;
    const isMod = ev.isMod || ev.admin || false;

    const item = document.createElement('div');
    const animClass = 'anim-' + (cfg.animation || 'slide');
    const guardClass = guardLevel > 0
      ? ` guard-level-${guardLevel} guard-level--${guardLevel} is-guard`
      : ' guard-level--0';
    const streamerClass = isStreamer ? ' user-type--streamer' : '';
    const modClass = isMod ? ' user-type--moderator' : '';
    const avatarPos = cfg.avatarPosition || 'left';
    const posClass = 'layout-avatar-' + avatarPos;
    const isLaplace = cfg.themeType === 'laplace' || LAPLACE_TEMPLATES.has(cfg.theme);

    // 双 Class 架构：兼容原生 .danmu-item 以及 Laplace .event .event--message .event-type--message
    item.className = `danmu-item event event--message event-type--message event-show-as--normal event-size--normal ${animClass}${guardClass}${streamerClass}${modClass} ${posClass}`;
    item.setAttribute('data-event-type', 'danmaku');
    item.setAttribute('data-guard-level', guardLevel);

    // 徽章 HTML
    let badgesHtml = '';
    const honorLevel = Number(ev.honorLevel || ev.HonorLevel || ev.wealthLevel || ev.WealthLevel || 0);
    if (cfg.showWealthMedal !== false && honorLevel > 0) {
      badgesHtml += `<span class="danmu-badge event-badge wealth-medal event-wealth-medal" title="荣耀等级 UL.${honorLevel}"><span class="wealth-medal-level">UL.${honorLevel}</span></span>`;
    }
    if (cfg.showGuardBadge !== false && isGuard && guardName) {
      badgesHtml += `<span class="danmu-badge event-badge guard guard-badge guard-badge-in-meta">${esc(guardName)}</span>`;
    }
    if (cfg.showModBadge !== false && isMod) {
      badgesHtml += `<span class="danmu-badge event-badge mod mod-badge">房管</span>`;
    }
    if (cfg.showMedal !== false && medalLevel > 0) {
      if (!cfg.showMedalLightenedOnly || isMedalLight) {
        badgesHtml += `<span class="danmu-badge event-badge medal medal-badge fans-medal"><span class="fans-medal-name">粉丝</span><span class="fans-medal-level">Lv.${medalLevel}</span></span>`;
      }
    }

    // 头像 HTML
    let avatarHtml = '';
    if (cfg.showAvatar !== false && avatarPos !== 'hidden') {
      avatarHtml = `
        <div class="danmu-avatar-wrap event-avatar-wrap avatar-wrap">
          <img class="danmu-avatar event-avatar avatar" src="${esc(face)}" alt="${esc(uname)}" referrerpolicy="no-referrer" onerror="this.src='${DEFAULT_AVATAR}'" />
        </div>
      `;
    }

    // 用户名与徽章排版
    let colon = (cfg.showUsername !== false && (avatarPos === 'left' || avatarPos === 'hidden')) ? '：' : '';
    let unameOnlyHtml = `<span class="danmu-uname event-username username"><span class="username-text">${esc(uname)}</span>${colon}</span>`;
    let metaContent = '';
    if (cfg.showUsername !== false) {
      metaContent = (cfg.badgePosition === 'after-name')
        ? `${unameOnlyHtml} ${badgesHtml}`
        : `${badgesHtml} ${unameOnlyHtml}`;
    } else {
      metaContent = badgesHtml;
    }
    const unameHtml = `<div class="danmu-meta event-meta meta">${metaContent}</div>`;

    if (avatarPos === 'top-left' || avatarPos === 'top-center') {
      item.innerHTML = `
        <div class="danmu-bar event-bar"></div>
        <div class="event-header-row">
          ${avatarHtml}
          ${unameHtml}
        </div>
        <div class="danmu-content event-content content">
          <div class="danmu-text event-message message">${formatDanmuContent(msg, ev.emotes || ev.Emotes)}</div>
        </div>
      `;
    } else if (isLaplace) {
      item.innerHTML = `
        <div class="danmu-bar event-bar"></div>
        <div class="danmu-meta event-meta meta">
          ${avatarHtml}
          ${metaContent}
        </div>
        <div class="danmu-content event-content content">
          <div class="danmu-text event-message message">${formatDanmuContent(msg, ev.emotes || ev.Emotes)}</div>
        </div>
      `;
    } else {
      item.innerHTML = `
        <div class="danmu-bar event-bar"></div>
        ${avatarHtml}
        <div class="danmu-content event-content content">
          ${unameHtml}
          <div class="danmu-text event-message message">${formatDanmuContent(msg, ev.emotes || ev.Emotes)}</div>
        </div>
      `;
    }

    appendItem(item);
  }

  // ---- 2. 礼物事件 ----
  function addGift(ev) {
    if (!cfg.enabled || cfg.showGift === false) return;

    const uname = ev.uname || ev.Uname || '观众';
    let face = ev.uface || ev.Uface || DEFAULT_AVATAR;
    if (face.startsWith('//')) face = 'https:' + face;

    const giftName = ev.giftName || ev.GiftName || '礼物';
    const num = Number(ev.num || ev.Num || 1);
    const coinType = ev.coinType || ev.CoinType || 'gold';
    const price = Number(ev.price || ev.Price || 0) / 1000; // 毫分转元
    const totalAmount = Number(ev.totalPrice || ev.TotalPrice || price * num);

    // 免费礼物过滤
    if (cfg.filterFreeGift && (coinType === 'silver' || totalAmount <= 0)) return;

    // 金额最低限制过滤
    if (cfg.giftPriceAbove > 0 && totalAmount < cfg.giftPriceAbove) return;

    // 金额高亮阈值
    const isHighlight = totalAmount >= (cfg.giftHighlightAbove || 29.99);

    const item = document.createElement('div');
    const animClass = 'anim-' + (cfg.animation || 'slide');
    const highlightClass = isHighlight ? ' event-gift-highlight' : '';
    const posClass = 'layout-avatar-' + (cfg.avatarPosition || 'left');

    item.className = `danmu-item event event--gift ${animClass}${highlightClass} ${posClass}`;
    item.setAttribute('data-event-type', 'gift');
    item.setAttribute('data-gift-price', totalAmount);

    let avatarHtml = '';
    if (cfg.showAvatar !== false) {
      avatarHtml = `
        <div class="danmu-avatar-wrap event-avatar-wrap avatar-wrap">
          <img class="danmu-avatar event-avatar avatar" src="${esc(face)}" alt="${esc(uname)}" referrerpolicy="no-referrer" onerror="this.src='${DEFAULT_AVATAR}'" />
        </div>
      `;
    }

    const priceText = totalAmount > 0 ? `¥${totalAmount.toFixed(1)}` : '';

    const giftHonorLevel = Number(ev.honorLevel || ev.HonorLevel || ev.wealthLevel || ev.WealthLevel || 0);
    const giftHonorHtml = (cfg.showWealthMedal !== false && giftHonorLevel > 0)
      ? `<span class="danmu-badge event-badge wealth-medal event-wealth-medal" title="荣耀等级 UL.${giftHonorLevel}"><span class="wealth-medal-level">UL.${giftHonorLevel}</span></span> `
      : '';

    item.innerHTML = `
      <div class="danmu-bar event-bar"></div>
      ${avatarHtml}
      <div class="danmu-content event-content content">
        <div class="danmu-meta event-meta">
          ${giftHonorHtml}<span class="danmu-uname event-username username"><span class="username-text">${esc(uname)}</span></span>
          <span class="event-action" style="font-size:12px;opacity:0.75;margin-left:4px">送出</span>
        </div>
        <div class="danmu-text event-message message">
          <span class="gift-name" style="font-weight:600;color:var(--amber,#ffd04b)">${esc(giftName)}</span>
          <span class="gift-count" style="margin-left:4px;font-weight:700">×${num}</span>
          ${priceText ? `<span class="price event-price" style="margin-left:8px;font-size:12px;opacity:0.85;color:#ff6b81">${priceText}</span>` : ''}
        </div>
      </div>
    `;

    appendItem(item);
  }

  // ---- 3. 醒目留言 SuperChat ----
  function addSuperChat(ev) {
    if (!cfg.enabled || cfg.showSuperChat === false) return;

    const uname = ev.uname || ev.Uname || '观众';
    let face = ev.uface || ev.Uface || DEFAULT_AVATAR;
    if (face.startsWith('//')) face = 'https:' + face;
    const price = Number(ev.price != null ? ev.price : (ev.Price || 0));
    const msg = ev.msg || ev.Msg || '';

    const item = document.createElement('div');
    const animClass = 'anim-' + (cfg.animation || 'slide');
    const posClass = 'layout-avatar-' + (cfg.avatarPosition || 'left');
    item.className = `danmu-item event event--superchat ${animClass} ${posClass}`;
    item.setAttribute('data-event-type', 'superchat');
    item.setAttribute('data-sc-price', price);

    let avatarHtml = '';
    if (cfg.showAvatar !== false) {
      avatarHtml = `
        <div class="danmu-avatar-wrap event-avatar-wrap avatar-wrap">
          <img class="danmu-avatar event-avatar avatar" src="${esc(face)}" alt="${esc(uname)}" referrerpolicy="no-referrer" onerror="this.src='${DEFAULT_AVATAR}'" />
        </div>
      `;
    }

    const scHonorLevel = Number(ev.honorLevel || ev.HonorLevel || ev.wealthLevel || ev.WealthLevel || 0);
    const scHonorHtml = (cfg.showWealthMedal !== false && scHonorLevel > 0)
      ? `<span class="danmu-badge event-badge wealth-medal event-wealth-medal" title="荣耀等级 UL.${scHonorLevel}"><span class="wealth-medal-level">UL.${scHonorLevel}</span></span> `
      : '';

    item.innerHTML = `
      <div class="danmu-bar event-bar"></div>
      ${avatarHtml}
      <div class="danmu-content event-content content">
        <div class="danmu-meta event-meta superchat-meta">
          ${scHonorHtml}<span class="danmu-uname event-username username"><span class="username-text">${esc(uname)}</span></span>
          <span class="price event-price superchat-price" style="margin-left:auto;font-weight:700;color:#ff4757">CN¥${price}</span>
        </div>
        <div class="danmu-text event-message message superchat-text">${esc(msg)}</div>
      </div>
    `;

    appendItem(item);
  }

  // ---- 4. 大航海购买广播 (Toast) ----
  function addGuardBuy(ev) {
    if (!cfg.enabled || (cfg.showToast === false && cfg.showGuardBuy === false)) return;

    const uname = ev.uname || ev.Uname || '观众';
    let face = ev.uface || ev.Uface || DEFAULT_AVATAR;
    if (face.startsWith('//')) face = 'https:' + face;

    const guardLevel = Number(ev.guardLevel || ev.GuardLevel || 3);
    const guardName = GUARD_NAMES[guardLevel] || '舰长';
    const num = Number(ev.num || ev.Num || 1);
    const unit = ev.unit || ev.Unit || '月';

    const item = document.createElement('div');
    const animClass = 'anim-' + (cfg.animation || 'slide');
    const posClass = 'layout-avatar-' + (cfg.avatarPosition || 'left');
    item.className = `danmu-item event event--toast ${animClass} guard-level-${guardLevel} guard-level--${guardLevel} is-guard ${posClass}`;
    item.setAttribute('data-event-type', 'toast');
    item.setAttribute('data-guard-level', guardLevel);

    const gbHonorLevel = Number(ev.honorLevel || ev.HonorLevel || ev.wealthLevel || ev.WealthLevel || 0);
    const gbHonorHtml = (cfg.showWealthMedal !== false && gbHonorLevel > 0)
      ? `<span class="danmu-badge event-badge wealth-medal event-wealth-medal" title="荣耀等级 UL.${gbHonorLevel}"><span class="wealth-medal-level">UL.${gbHonorLevel}</span></span> `
      : '';

    item.innerHTML = `
      <div class="danmu-bar event-bar"></div>
      <div class="danmu-avatar-wrap event-avatar-wrap avatar-wrap">
        <img class="danmu-avatar event-avatar avatar" src="${esc(face)}" alt="${esc(uname)}" referrerpolicy="no-referrer" onerror="this.src='${DEFAULT_AVATAR}'" />
      </div>
      <div class="danmu-content event-content content">
        <div class="danmu-meta event-meta">
          ${gbHonorHtml}<span class="danmu-badge event-badge guard guard-badge guard-badge-in-meta">${esc(guardName)}</span>
          <span class="danmu-uname event-username username"><span class="username-text">${esc(uname)}</span></span>
        </div>
        <div class="danmu-text event-message message">
          开通了 <b style="color:var(--amber,#ffd04b)">${num} 个${esc(unit)}${esc(guardName)}</b> 👑
        </div>
      </div>
    `;

    appendItem(item);
  }

  // ---- 5. 进房通知 ----
  function addEntry(ev) {
    if (!cfg.enabled || cfg.showEnterRoom === false) return;
    const isGuard = ev.isGuard || ev.IsGuard || ((ev.guardLevel || ev.GuardLevel) && (ev.guardLevel || ev.GuardLevel) > 0);
    if (cfg.enterRoomGuardOnly && !isGuard) return;

    const uname = ev.uname || ev.Uname || (ev.uid || ev.Uid ? '用户' + (ev.uid || ev.Uid) : '观众');
    let face = ev.uface || ev.Uface || DEFAULT_AVATAR;
    if (face.startsWith('//')) face = 'https:' + face;
    const actionText = cfg.enterRoomText || '进入直播间';

    const item = document.createElement('div');
    const animClass = 'anim-' + (cfg.animation || 'slide');
    const posClass = 'layout-avatar-' + (cfg.avatarPosition || 'left');
    item.className = `danmu-item danmu-entry event event--interaction event--entry ${animClass} ${posClass}`;
    item.setAttribute('data-event-type', 'interaction');

    let avatarHtml = '';
    if (cfg.showAvatar !== false) {
      avatarHtml = `
        <div class="danmu-avatar-wrap event-avatar-wrap">
          <img class="danmu-avatar event-avatar avatar" src="${esc(face)}" alt="${esc(uname)}" referrerpolicy="no-referrer" onerror="this.src='${DEFAULT_AVATAR}'" />
        </div>
      `;
    }

    item.innerHTML = `
      <div class="danmu-bar event-bar"></div>
      ${avatarHtml}
      <div class="entry-content event-content content">
        <span class="entry-uname event-username username"><span class="username-text">${esc(uname)}</span></span>
        <span class="entry-action event-action">${esc(actionText)}</span>
      </div>
    `;

    appendItem(item);
  }

  // ---- WebSocket 消息总线 ----
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

        // 样式热更新
        if (frame.type === 'danmu_config_update') {
          applyConfig(frame.data);
          return;
        }

        // 真实直播事件
        if (frame.type === 'event' && frame.data) {
          const ev = frame.data;
          const evType = (ev.type || '').toLowerCase();
          if (evType === 'danmu') {
            addDanmu(ev);
          } else if (evType === 'gift') {
            addGift(ev);
          } else if (evType === 'superchat') {
            addSuperChat(ev);
          } else if (evType === 'guard') {
            addGuardBuy(ev);
          } else if (evType === 'interact') {
            // 进房广播 (msgType 1 或 0)
            if (ev.msgType == null || ev.msgType === 1 || ev.msgType === 0) {
              addEntry(ev);
            }
          }
          return;
        }

        // 测试事件分发
        if (frame.type === 'danmu_test' && frame.data) {
          const t = frame.data;
          if (t.isEntry) {
            addEntry(t);
          } else if (t.isGift) {
            addGift(t);
          } else if (t.isSuperChat) {
            addSuperChat(t);
          } else if (t.isGuardBuy) {
            addGuardBuy(t);
          } else {
            addDanmu(t);
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

  // 预览模式初始化模拟消息
  function initPreviewMock() {
    if (!isPreviewMode) return;
    if (stage.children.length > 0) return;
    addDanmu({
      uname: '喵喵小布丁',
      msg: '主播下午好呀！今天播什么好玩的游戏？✨',
      guardLevel: 0,
      medalLevel: 12,
      honorLevel: 22,
      uface: DEFAULT_AVATAR
    });
    addDanmu({
      uname: '热血新舰长',
      msg: '今天下班来打卡，这把游戏必拿下！🚀',
      guardLevel: 3,
      medalLevel: 18,
      honorLevel: 35,
      uface: DEFAULT_AVATAR
    });
    addSuperChat({
      uname: '榜一真爱粉',
      msg: '主播加油，这是今天的应援 SC，冲鸭！❤️',
      price: 50,
      honorLevel: 42,
      uface: DEFAULT_AVATAR
    });
  }

  // 初始化
  loadConfig().then(() => {
    initPreviewMock();
    connectWs();
  });
})();
