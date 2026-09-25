(function () {
    const root = document.documentElement;
    let ws = null;
    let reconnectTimer = null;
    let npBubbleTimer = null;

    function applyChannelStyles(ch) {
        const id = ch.id;
        const elem = document.getElementById(`lower-third-${id}`);
        if (!elem) return;

        // 设置文本
        const nameEl = document.getElementById(`alt-${id}-name`);
        const infoEl = document.getElementById(`alt-${id}-info`);
        if (nameEl) nameEl.textContent = ch.name || '';
        if (infoEl) infoEl.textContent = ch.info || '';

        // 样式 class
        const styleNum = ch.style || 1;
        const align = ch.align || 'left';

        elem.classList.remove('style-1', 'style-2', 'style-3');
        elem.classList.add(`style-${styleNum}`);

        elem.classList.remove('left', 'center', 'right');
        elem.classList.add(align);

        // Logo
        const logoDiv = document.getElementById(`alt-${id}-logo`);
        const logoImg = document.getElementById(`alt-${id}-logo-image`);
        if (logoDiv && logoImg) {
            if (ch.showLogo && ch.logo) {
                logoDiv.classList.remove('no-logo');
                logoImg.src = ch.logo;
            } else {
                logoDiv.classList.add('no-logo');
            }
        }

        // CSS Variables 注入
        const animTime = (ch.animationTime || 1.0) + 's';
        root.style.setProperty(`--alt-${id}-animation-time`, animTime);
        root.style.setProperty(`--alt-${id}-style-color-1`, ch.color1 || '#1E40AF');
        root.style.setProperty(`--alt-${id}-style-color-2`, ch.color2 || '#3B82F6');
        root.style.setProperty(`--alt-${id}-name-color`, ch.textColor1 || '#FFFFFF');
        root.style.setProperty(`--alt-${id}-info-color`, ch.textColor2 || '#E0E7FF');
        if (ch.fontFamily) root.style.setProperty(`--alt-${id}-font`, ch.fontFamily);
        if (ch.fontSize) root.style.setProperty(`--alt-${id}-size`, (ch.fontSize || 32) + 'px');

        // 进出场动效
        if (ch.active) {
            elem.classList.remove('hide-anim', 'animation-out');
            void elem.offsetWidth; // 触发 reflow
            elem.classList.add('animation-in');
        } else {
            if (elem.classList.contains('animation-in')) {
                elem.classList.remove('animation-in');
                void elem.offsetWidth;
                elem.classList.add('animation-out');
            }
        }
    }

    function showMusicBubble(data) {
        const bubble = document.getElementById('now-playing-bubble');
        if (!bubble) return;

        const titleEl = document.getElementById('np-title');
        const artistEl = document.getElementById('np-artist');
        const coverEl = document.getElementById('np-cover-image');

        if (titleEl) titleEl.textContent = '🎵 ' + (data.title || '正在播放');
        if (artistEl) {
            let desc = data.artist || '';
            if (data.album) desc += ' · ' + data.album;
            if (data.source) desc += ` [${data.source}]`;
            artistEl.textContent = desc;
        }
        if (coverEl) {
            coverEl.src = data.coverUrl ? (data.coverUrl + '?t=' + Date.now()) : 'logos/logo_1.png';
        }

        // 默认音乐气泡使用主题渐变
        root.style.setProperty('--alt-1-style-color-1', '#059669'); // Emerald
        root.style.setProperty('--alt-1-style-color-2', '#10B981');
        root.style.setProperty('--alt-1-animation-time', '0.8s');

        bubble.classList.remove('hide-anim', 'animation-out');
        void bubble.offsetWidth;
        bubble.classList.add('animation-in');

        if (npBubbleTimer) clearTimeout(npBubbleTimer);
        const durationMs = (data.duration || 6) * 1000;
        npBubbleTimer = setTimeout(() => {
            bubble.classList.remove('animation-in');
            void bubble.offsetWidth;
            bubble.classList.add('animation-out');
        }, durationMs);
    }

    function setAvoidConflict(collapse) {
        const bubble = document.getElementById('now-playing-bubble');
        if (bubble) {
            if (collapse) {
                bubble.classList.add('collapsed');
            } else {
                bubble.classList.remove('collapsed');
            }
        }
    }

    async function fetchInitialState() {
        try {
            const res = await fetch('/api/lower-thirds/config');
            if (res.ok) {
                const data = await res.json();
                if (data && data.channels) {
                    data.channels.forEach(ch => applyChannelStyles(ch));
                }
            }
        } catch (e) {
            console.warn('[LowerThirds] Initial state fetch error:', e);
        }
    }

    function connectWs() {
        if (ws) {
            try { ws.close(); } catch (e) { }
        }

        const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
        const wsUrl = `${proto}//${location.host}/ws`;

        ws = new WebSocket(wsUrl);

        ws.onopen = () => {
            document.body.classList.remove('ws-disconnected');
            if (reconnectTimer) {
                clearTimeout(reconnectTimer);
                reconnectTimer = null;
            }
            fetchInitialState();
        };

        ws.onmessage = (event) => {
            try {
                const msg = JSON.parse(event.data);
                if (!msg) return;

                if (msg.type === 'lower_third') {
                    applyChannelStyles(msg.data);
                    if (msg.data.id === 1 && !msg.data.active) {
                        setAvoidConflict(false);
                    }
                } else if (msg.type === 'music_bubble') {
                    showMusicBubble(msg.data);
                } else if (msg.type === 'overlay_avoid') {
                    setAvoidConflict(true);
                }
            } catch (err) {
                console.error('[LowerThirds] WS message error:', err);
            }
        };

        ws.onclose = () => {
            document.body.classList.add('ws-disconnected');
            if (!reconnectTimer) {
                reconnectTimer = setTimeout(connectWs, 3000);
            }
        };

        ws.onerror = () => {
            try { ws.close(); } catch (e) { }
        };
    }

    // 启动连接与初始拉取
    connectWs();
})();
