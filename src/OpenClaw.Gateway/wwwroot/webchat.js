        // Set up marked to use highlight.js
        marked.setOptions({
            highlight: function (code, lang) {
                const language = hljs.getLanguage(lang) ? lang : 'plaintext';
                return hljs.highlight(code, { language }).value;
            },
            langPrefix: 'hljs language-'
        });

        const chatWrapper = document.getElementById('chat-wrapper');
        const chatContainer = document.getElementById('chat-container');
        const messageInput = document.getElementById('message-input');
        const sendButton = document.getElementById('send-button');
        const tokenInput = document.getElementById('token-input');
        const rememberToken = document.getElementById('remember-token');
        const doctorButton = document.getElementById('doctor-button');
        const oidcLoginButton = document.getElementById('oidc-login-button');
        const oidcLogoutButton = document.getElementById('oidc-logout-button');
        const typingRow = document.getElementById('typing-row');
        const fileInput = document.getElementById('file-input');
        const attachBtn = document.getElementById('attach-btn');
        const imagePreviewStrip = document.getElementById('image-preview-strip');
        const TOKEN_KEY_PERSIST = 'openclaw_token';
        const TOKEN_KEY_SESSION = 'openclaw_token_session';

        const OIDC_CONFIG = {
            enabled: true,
            authority: 'http://test-passport.zyagi.cn:1080/realms/ai4cbrain',
            //authority: 'http://localhost:8080/realms/ai4cbrain', // local-keycloak
            clientId: 'kingcrab-console',
            scope: 'openid profile email'
        };

        let ws = null;
        let activeResponseDiv = null;
        let activeRawContent = "";
        let isAwaitingResponse = false;
        let reconnectAttempts = 0;
        let reconnectTimer = null;
        let streamRenderTimer = null;
        let oidcClient = null;
        let viewMode = 'live'; // 'live' | 'history'
        let sidebarVisible = true;
        let sessionSearchTimer = null;
        let liveChatNodes = []; // snapshot of live chat nodes
        // pendingAttachments item shapes:
        //   image:  { type:'image', name, dataUri, mimeType }
        //   file:   { type:'file',  name, mimeType, sizeBytes, mediaUrl, uploading, uploadError }
        let pendingImages = [];
        let currentSessionId = null;      // explicit session ID for routing (null = server default)
        let streamEpoch = 0;               // incremented on session switch to discard stale stream frames
        let boundEpoch = 0;               // epoch captured at typing_start; checked on each chunk
        let sessionAutoRefreshTimer = null; // setInterval handle for 15-second session list refresh

        const WEBCHAT_CONFIG = {
            streamRenderDebounceMs: Math.max(20, Number(window.OPENCLAW_WEBCHAT_CONFIG?.streamRenderDebounceMs ?? 120)),
            initialReconnectDelayMs: Math.max(250, Number(window.OPENCLAW_WEBCHAT_CONFIG?.initialReconnectDelayMs ?? 1000)),
            maxReconnectDelayMs: Math.max(1000, Number(window.OPENCLAW_WEBCHAT_CONFIG?.maxReconnectDelayMs ?? 30000)),
            reconnectBackoffFactor: Math.max(1.1, Number(window.OPENCLAW_WEBCHAT_CONFIG?.reconnectBackoffFactor ?? 2)),
            maxReconnectAttempts: Math.max(0, Number(window.OPENCLAW_WEBCHAT_CONFIG?.maxReconnectAttempts ?? 0)),
            retryOnAuthCloseCodes: Boolean(window.OPENCLAW_WEBCHAT_CONFIG?.retryOnAuthCloseCodes ?? false)
        };

        // Derives the URL prefix when served under a sub-path (e.g. sandbox reverse-proxy).
        // e.g. pathname "/abc/18789/webchat.html" -> basePath "/abc/18789"
        // e.g. pathname "/webchat.html" or "/" -> basePath ""
        function getBasePath() {
            const path = window.location.pathname;
            const lastSlash = path.lastIndexOf('/');
            return lastSlash > 0 ? path.substring(0, lastSlash) : '';
        }

        function getStoredToken() {
            return sessionStorage.getItem(TOKEN_KEY_SESSION) || localStorage.getItem(TOKEN_KEY_PERSIST) || '';
        }

        function getCurrentToken() {
            return tokenInput.value.trim() || getStoredToken();
        }

        async function getAuthTokenForRequest(minValidSeconds = 60) {
            if (oidcClient) {
                const refreshed = await oidcClient.refreshIfNeeded(minValidSeconds);
                if (refreshed) {
                    tokenInput.value = refreshed;
                    persistToken(refreshed);
                    return refreshed;
                }
            }

            return getCurrentToken();
        }

        async function getAuthHeaders(minValidSeconds = 60) {
            const token = await getAuthTokenForRequest(minValidSeconds);
            if (!token || token === 'bypass') {
                return {};
            }

            return { Authorization: `Bearer ${token}` };
        }

        function persistToken(token) {
            if (!token) return;
            sessionStorage.setItem(TOKEN_KEY_SESSION, token);
            if (rememberToken.checked) {
                localStorage.setItem(TOKEN_KEY_PERSIST, token);
            } else {
                localStorage.removeItem(TOKEN_KEY_PERSIST);
            }
        }

        function clearStoredToken() {
            sessionStorage.removeItem(TOKEN_KEY_SESSION);
            localStorage.removeItem(TOKEN_KEY_PERSIST);
        }

        // ─── Header state machine ─────────────────────────────────────────────
        const connStatus   = document.getElementById('conn-status');
        const connLabel    = document.getElementById('conn-label');
        const userChip     = document.getElementById('user-chip');
        const userAvatar   = document.getElementById('user-avatar-mini');
        const userNameEl   = document.getElementById('user-display-name');
        const tokenPanel   = document.getElementById('token-panel');
        const tokenPanelToggle = document.getElementById('token-panel-toggle');
        const tokenPanelLabel  = document.getElementById('token-panel-label');
        const tokenDropdown    = document.getElementById('token-dropdown');
        const tokenApplyBtn    = document.getElementById('token-apply-btn');

        tokenPanelToggle.addEventListener('click', (e) => {
            e.stopPropagation();
            tokenDropdown.classList.toggle('open');
        });
        document.addEventListener('click', (e) => {
            if (!tokenPanel.contains(e.target)) tokenDropdown.classList.remove('open');
        });
        tokenApplyBtn.addEventListener('click', async () => {
            const typed = tokenInput.value.trim();
            if (!typed) return;
            persistToken(typed);
            tokenDropdown.classList.remove('open');
            if (ws && ws.readyState === WebSocket.OPEN) {
                ws.close(1000, 'Reconnect with updated token');
            }
            await connect();
        });

        function setConnectionStatus(state) {
            connStatus.className = state;
            if (state === 'connected') connLabel.textContent = 'Connected';
            else if (state === 'connecting') connLabel.textContent = 'Connecting…';
            else connLabel.textContent = 'Disconnected';
        }

        function updateOidcButtons() { updateHeaderState(); }

        function updateHeaderState() {
            const hasOidc = Boolean(oidcClient);
            const isOidcLoggedIn = hasOidc && Boolean(getCurrentToken());

            // OIDC: show login when OIDC available but not authenticated
            oidcLoginButton.style.display  = (hasOidc && !isOidcLoggedIn) ? 'inline-flex' : 'none';
            // OIDC: show logout when authenticated via OIDC
            oidcLogoutButton.style.display = isOidcLoggedIn ? 'inline-flex' : 'none';

            // Token panel: show when OIDC not available
            tokenPanel.style.display = hasOidc ? 'none' : '';

            if (isOidcLoggedIn) {
                // Parse display name from OIDC token (JWT payload.name or email)
                let displayName = 'User';
                try {
                    const payload = JSON.parse(atob(getCurrentToken().split('.')[1].replace(/-/g,'+').replace(/_/g,'/')));
                    displayName = payload.name || payload.preferred_username || payload.email || 'User';
                } catch (_) {}
                const initials = displayName.split(/\s+/).map(w => w[0]).slice(0,2).join('').toUpperCase() || 'U';
                userAvatar.textContent   = initials;
                userNameEl.textContent   = displayName.split(' ')[0]; // first name only
                userChip.style.display   = 'inline-flex';
            } else if (!hasOidc && getCurrentToken()) {
                // Bearer token mode: show a generic token chip
                userAvatar.textContent   = '#';
                userNameEl.textContent   = 'Token';
                userChip.style.display   = 'inline-flex';
                tokenPanelLabel.textContent = 'Token ✓';
            } else {
                userChip.style.display   = 'none';
                tokenPanelLabel.textContent = 'Token';
            }
        }

        function setStopMode(active) {
            isAwaitingResponse = active;
            if (active) {
                sendButton.classList.add('stop-mode');
                sendButton.disabled = false;
                sendButton.title = 'Stop generation';
                messageInput.disabled = true;
                attachBtn.disabled = true;
            } else {
                sendButton.classList.remove('stop-mode');
                sendButton.title = 'Send Message';
                // Re-enable input only when WS is open and in live mode
                if (ws && ws.readyState === WebSocket.OPEN && viewMode === 'live') {
                    messageInput.disabled = false;
                    sendButton.disabled = false;
                    attachBtn.disabled = false;
                    messageInput.focus();
                }
            }
        }

        function setDisconnectedUi() {
            if (sessionAutoRefreshTimer) { clearInterval(sessionAutoRefreshTimer); sessionAutoRefreshTimer = null; }
            setConnectionStatus('disconnected');
            messageInput.disabled = true;
            sendButton.disabled = true;
            attachBtn.disabled = true;
            typingRow.style.display = 'none';
            sendButton.classList.remove('stop-mode');
            sendButton.title = 'Send Message';
            isAwaitingResponse = false;
        }

        function shouldRetryConnection(event) {
            const token = getCurrentToken();
            if (!token) {
                appendSystem('未检测到 token，停止自动重连。', true);
                return false;
            }

            const isAuthClose = event.code === 1008 || (event.code >= 4000 && event.code < 5000);
            if (isAuthClose) {
                appendSystem('连接因鉴权失败关闭，停止自动重连。请重新登录。', true);
                return false;
            }

            return true;
        }

        function preprocessMediaMarkers(raw) {
            const lines = (raw || "").split('\n');
            const out = [];
            for (const line of lines) {
                const trimmed = line.trim();
                const mImgUrl = trimmed.match(/^\[IMAGE_URL:(.+)\]$/);
                if (mImgUrl) {
                    const url = mImgUrl[1].trim();
                    out.push(`![](${url})`);
                    continue;
                }
                const mFileUrl = trimmed.match(/^\[FILE_URL:(.+)\]$/);
                if (mFileUrl) {
                    const url = mFileUrl[1].trim();
                    const name = url.split('/').pop() || 'file';
                    out.push(`[⬇ ${name}](${url})`);
                    continue;
                }
                // Strip raw FILE_PATH markers – they are internal and should not be shown.
                if (/^\[FILE_PATH:.+\]$/.test(trimmed)) {
                    continue;
                }
                out.push(line);
            }
            return out.join('\n');
        }

        // Intercept clicks on /media/ links rendered inside chat messages.
        // Browser navigation strips auth headers, so we fetch with Bearer and serve via blob URL.
        chatContainer.addEventListener('click', async (e) => {
            const anchor = e.target.closest('a[href]');
            if (!anchor) return;
            const href = anchor.getAttribute('href');
            if (!href || !href.includes('/media/')) return;
            e.preventDefault();
            try {
                const headers = await getAuthHeaders();
                const resp = await fetch(href, { headers });
                if (!resp.ok) { alert('Download failed: ' + resp.status); return; }
                const blob = await resp.blob();
                const blobUrl = URL.createObjectURL(blob);
                const a = document.createElement('a');
                a.href = blobUrl;
                a.download = anchor.textContent.replace(/^[⬇\s]+/, '').trim() || href.split('/').pop() || 'file';
                document.body.appendChild(a);
                a.click();
                document.body.removeChild(a);
                setTimeout(() => URL.revokeObjectURL(blobUrl), 10000);
            } catch (err) {
                alert('Download error: ' + err.message);
            }
        });

        function appendAssistantMarkdown(md) {
            const row = createRow('assistant');
            const div = document.createElement('div');
            div.className = 'message assistant';
            div.innerHTML = DOMPurify.sanitize(marked.parse(preprocessMediaMarkers(md)));
            row.appendChild(div);
            chatContainer.insertBefore(row, typingRow);
            scrollToBottom();
        }

        function scrollToBottom(smooth = true) {
            chatWrapper.scrollTo({
                top: chatWrapper.scrollHeight,
                behavior: smooth ? 'smooth' : 'auto'
            });
        }

        function renderActiveResponse(finalRender = false) {
            if (!activeResponseDiv || !activeRawContent) {
                return;
            }

            activeResponseDiv.innerHTML = DOMPurify.sanitize(marked.parse(preprocessMediaMarkers(activeRawContent)));

            if (finalRender) {
                activeResponseDiv.querySelectorAll('pre code').forEach((block) => {
                    hljs.highlightElement(block);
                });
                scrollToBottom();
                return;
            }

            scrollToBottom(false);
        }

        function scheduleActiveResponseRender(finalRender = false) {
            if (finalRender) {
                if (streamRenderTimer) {
                    clearTimeout(streamRenderTimer);
                    streamRenderTimer = null;
                }
                renderActiveResponse(true);
                return;
            }

            if (streamRenderTimer) {
                return;
            }

            streamRenderTimer = setTimeout(() => {
                streamRenderTimer = null;
                renderActiveResponse(false);
            }, WEBCHAT_CONFIG.streamRenderDebounceMs);
        }

        function createRow(type) {
            const row = document.createElement('div');
            row.className = `message-row ${type}-row`;
            
            if (type === 'assistant') {
                const avatar = document.createElement('div');
                avatar.className = 'agent-avatar';
                avatar.innerHTML = '<img src="image.png" alt="Agent" />';
                row.appendChild(avatar);
            } else if (type === 'user') {
                const avatar = document.createElement('div');
                avatar.className = 'user-avatar';
                avatar.innerHTML = 'U';
                row.appendChild(avatar);
            }

            return row;
        }

        function appendSystem(text, isError = false) {
            const row = createRow('system');
            const div = document.createElement('div');

            if (isError) {
                div.className = 'message system error';
                const icon = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
                icon.setAttribute('width', '16');
                icon.setAttribute('height', '16');
                icon.setAttribute('viewBox', '0 0 24 24');
                icon.setAttribute('fill', 'none');
                icon.setAttribute('stroke', 'currentColor');
                icon.setAttribute('stroke-width', '2');

                const circle = document.createElementNS('http://www.w3.org/2000/svg', 'circle');
                circle.setAttribute('cx', '12');
                circle.setAttribute('cy', '12');
                circle.setAttribute('r', '10');

                const line1 = document.createElementNS('http://www.w3.org/2000/svg', 'line');
                line1.setAttribute('x1', '12');
                line1.setAttribute('y1', '8');
                line1.setAttribute('x2', '12');
                line1.setAttribute('y2', '12');

                const line2 = document.createElementNS('http://www.w3.org/2000/svg', 'line');
                line2.setAttribute('x1', '12');
                line2.setAttribute('y1', '16');
                line2.setAttribute('x2', '12.01');
                line2.setAttribute('y2', '16');

                icon.appendChild(circle);
                icon.appendChild(line1);
                icon.appendChild(line2);

                const textNode = document.createTextNode(` ${text}`);
                div.appendChild(icon);
                div.appendChild(textNode);
            } else {
                div.className = 'message system';
                div.textContent = text;
            }

            row.appendChild(div);

            chatContainer.insertBefore(row, typingRow);
            scrollToBottom();
        }

        function appendToolPill(toolName) {
            const row = document.createElement('div');
            row.className = 'message-row system-row';
            const pill = document.createElement('div');
            pill.className = 'tool-pill';
            pill.textContent = `Agent invoked tool: ${toolName}`;

            row.appendChild(pill);
            chatContainer.insertBefore(row, typingRow);
            scrollToBottom();
        }

        async function connect() {
            const token = await getAuthTokenForRequest(60);
            if (!token) {
                setDisconnectedUi();
                appendSystem('未登录，已阻止 WebSocket 连接。请先点击 OIDC Login 或输入 token。', true);
                return;
            }

            if (ws && (ws.readyState === WebSocket.OPEN || ws.readyState === WebSocket.CONNECTING)) {
                return;
            }

            setConnectionStatus('connecting');
            appendSystem('Connecting to King Crab Gateway...');
            const protocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
            const baseWsUrl = `${protocol}//${window.location.host}${getBasePath()}/ws`;
            const wsUrl = token
                ? `${baseWsUrl}?token=${encodeURIComponent(token)}`
                : baseWsUrl;

            persistToken(tokenInput.value.trim());

            ws = new WebSocket(wsUrl);

            ws.onopen = () => {
                reconnectAttempts = 0;
                if (reconnectTimer) {
                    clearTimeout(reconnectTimer);
                    reconnectTimer = null;
                }
                setConnectionStatus('connected');
                updateHeaderState();
                messageInput.disabled = false;
                sendButton.disabled = false;
                attachBtn.disabled = false;
                messageInput.focus();
                setViewMode('live');
                loadSessions();
                if (sessionAutoRefreshTimer) clearInterval(sessionAutoRefreshTimer);
                sessionAutoRefreshTimer = setInterval(() => loadSessions(), 15000);
            };

            ws.onmessage = (event) => {
                try {
                    const env = JSON.parse(event.data);

                    switch (env.type) {
                        case 'typing_start':
                            boundEpoch = streamEpoch;  // bind this stream to current epoch
                            typingRow.style.display = 'flex';
                            setStopMode(true);
                            scrollToBottom();
                            break;

                        case 'typing_stop':
                            if (boundEpoch !== streamEpoch) break;  // stale — discard
                            typingRow.style.display = 'none';
                            setStopMode(false);
                            scheduleActiveResponseRender(true);
                            activeResponseDiv = null;
                            activeRawContent = "";
                            scrollToBottom();
                            break;

                        case 'assistant_message':
                        case 'assistant_chunk':
                        case 'text_delta':
                            if (boundEpoch !== streamEpoch) break;  // stale — discard
                            if (!activeResponseDiv) {
                                const row = createRow('assistant');
                                activeResponseDiv = document.createElement('div');
                                activeResponseDiv.className = 'message assistant';
                                row.appendChild(activeResponseDiv);
                                chatContainer.insertBefore(row, typingRow);
                            }

                            activeRawContent += (env.text ?? env.content ?? "");
                            scheduleActiveResponseRender(false);
                            break;

                        case 'assistant_done':
                            if (boundEpoch !== streamEpoch) break;  // stale — discard
                            typingRow.style.display = 'none';
                            setStopMode(false);
                            scheduleActiveResponseRender(true);
                            activeResponseDiv = null;
                            activeRawContent = "";
                            scrollToBottom();
                            break;

                        case 'error':
                            if (boundEpoch !== streamEpoch) break;  // stale — discard
                            setStopMode(false);
                            typingRow.style.display = 'none';
                            appendSystem(env.text ?? env.content ?? 'An unknown error occurred.', true);
                            break;

                        case 'tool_start':
                            appendToolPill(env.text ?? env.content ?? 'tool');
                            break;

                        case 'tool_result':
                            break;

                        case 'file_attachment': {
                            const fileUrl = env.fileUrl || '';
                            const fileName = env.fileName || env.text || 'file';
                            const mimeType = env.mimeType || '';
                            const sizeBytes = env.fileSizeBytes;
                            const sizeLabel = sizeBytes != null
                                ? (sizeBytes < 1024 ? sizeBytes + ' B'
                                    : sizeBytes < 1048576 ? (sizeBytes / 1024).toFixed(1) + ' KB'
                                    : (sizeBytes / 1048576).toFixed(1) + ' MB')
                                : '';
                            const row = createRow('assistant');
                            const card = document.createElement('div');
                            card.className = 'message assistant';
                            card.style.cssText = 'display:flex;align-items:center;gap:10px;padding:10px 14px;';
                            const icon = document.createElement('span');
                            icon.textContent = '📎';
                            icon.style.fontSize = '1.4em';
                            const info = document.createElement('div');
                            info.style.flex = '1';
                            const link = document.createElement('a');
                            link.href = fileUrl; // delegated handler on chatContainer intercepts this
                            link.textContent = fileName;
                            link.style.cssText = 'font-weight:600;word-break:break-all;';
                            info.appendChild(link);
                            if (sizeLabel) {
                                const meta = document.createElement('div');
                                meta.textContent = sizeLabel + (mimeType ? ' · ' + mimeType : '');
                                meta.style.cssText = 'font-size:0.8em;opacity:0.65;margin-top:2px;';
                                info.appendChild(meta);
                            }
                            card.appendChild(icon);
                            card.appendChild(info);
                            row.appendChild(card);
                            chatContainer.insertBefore(row, typingRow);
                            scrollToBottom();
                            break;
                        }

                        case 'tool_approval_required': {
                            const toolName = env.toolName || 'unknown';
                            const approvalId = env.approvalId || '';
                            const argsPreview = env.argumentsPreview || '';
                            const prompt = `Tool approval required:\n\nTool: ${toolName}\nApprovalId: ${approvalId}\n\nArgs:\n${argsPreview}\n\nApprove?`;
                            const approved = window.confirm(prompt);
                            appendSystem(`Tool approval ${approved ? 'approved' : 'denied'}: ${toolName}`);
                            ws.send(JSON.stringify({
                                type: "tool_approval_decision",
                                approvalId: approvalId,
                                approved: approved
                            }));
                            break;
                        }
                    }
                } catch (e) {
                    if (!activeResponseDiv) {
                        const row = createRow('assistant');
                        activeResponseDiv = document.createElement('div');
                        activeResponseDiv.className = 'message assistant';
                        row.appendChild(activeResponseDiv);
                        chatContainer.insertBefore(row, typingRow);
                    }
                    activeRawContent += event.data;
                    scheduleActiveResponseRender(false);
                }
            };

            ws.onclose = (event) => {
                if (sessionAutoRefreshTimer) { clearInterval(sessionAutoRefreshTimer); sessionAutoRefreshTimer = null; }
                setConnectionStatus('disconnected');
                messageInput.disabled = true;
                sendButton.disabled = true;
                typingRow.style.display = 'none';

                if (streamRenderTimer) {
                    clearTimeout(streamRenderTimer);
                    streamRenderTimer = null;
                }

                if (!shouldRetryConnection(event)) {
                    return;
                }

                reconnectAttempts += 1;
                if (WEBCHAT_CONFIG.maxReconnectAttempts > 0 && reconnectAttempts > WEBCHAT_CONFIG.maxReconnectAttempts) {
                    appendSystem('Connection dropped and reconnect limit reached. Refresh to retry.', true);
                    return;
                }

                const delay = Math.min(
                    WEBCHAT_CONFIG.maxReconnectDelayMs,
                    Math.round(
                        WEBCHAT_CONFIG.initialReconnectDelayMs *
                        Math.pow(WEBCHAT_CONFIG.reconnectBackoffFactor, Math.max(0, reconnectAttempts - 1))
                    )
                );
                appendSystem(`Connection dropped. Retrying in ${Math.max(1, Math.ceil(delay / 1000))}s...`, true);
                reconnectTimer = setTimeout(() => {
                    connect();
                }, delay);
            };

            ws.onerror = () => {
                console.error('WebSocket encountered an error.');
            };
        }

        function readFileAsDataUrl(file) {
            return new Promise((resolve, reject) => {
                const reader = new FileReader();
                reader.onload = e => resolve(e.target.result);
                reader.onerror = reject;
                reader.readAsDataURL(file);
            });
        }

        function formatFileSize(bytes) {
            if (bytes < 1024) return `${bytes} B`;
            if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
            return `${(bytes / 1024 / 1024).toFixed(1)} MB`;
        }

        function hasUploadingAttachments() {
            return pendingImages.some(a => a.type === 'file' && a.uploading);
        }

        function renderImagePreviews() {
            imagePreviewStrip.innerHTML = '';
            if (pendingImages.length === 0) {
                imagePreviewStrip.style.display = 'none';
                return;
            }
            imagePreviewStrip.style.display = 'flex';
            pendingImages.forEach((item, i) => {
                const removeBtn = document.createElement('button');
                removeBtn.className = 'preview-thumb-remove';
                removeBtn.textContent = '×';
                removeBtn.title = 'Remove';
                removeBtn.addEventListener('click', (e) => {
                    e.stopPropagation();
                    pendingImages.splice(i, 1);
                    renderImagePreviews();
                });

                if (item.type === 'image') {
                    const thumb = document.createElement('div');
                    thumb.className = 'preview-thumb';
                    const imgEl = document.createElement('img');
                    imgEl.src = item.dataUri;
                    imgEl.alt = item.name;
                    thumb.appendChild(imgEl);
                    thumb.appendChild(removeBtn);
                    imagePreviewStrip.appendChild(thumb);
                } else {
                    const chip = document.createElement('div');
                    chip.className = 'preview-thumb-file' +
                        (item.uploading ? ' uploading' : '') +
                        (item.uploadError ? ' upload-error' : '');
                    const icon = document.createElement('span');
                    icon.className = 'file-icon';
                    icon.textContent = item.uploading ? '⏳' : (item.uploadError ? '⚠️' : '📄');
                    const nameEl = document.createElement('span');
                    nameEl.className = 'file-name';
                    nameEl.textContent = item.name;
                    nameEl.title = item.uploadError || item.name;
                    const sizeEl = document.createElement('span');
                    sizeEl.className = 'file-size';
                    sizeEl.textContent = item.uploading ? 'Uploading…' : (item.uploadError ? 'Error' : formatFileSize(item.sizeBytes));
                    chip.appendChild(icon);
                    chip.appendChild(nameEl);
                    chip.appendChild(sizeEl);
                    chip.appendChild(removeBtn);
                    imagePreviewStrip.appendChild(chip);
                }
            });
        }

        attachBtn.addEventListener('click', () => fileInput.click());

        // MIME type lookup by extension, used when file.type is absent (some browsers/OS combos).
        const IMAGE_MIME_BY_EXT = {
            jpg: 'image/jpeg', jpeg: 'image/jpeg', png: 'image/png',
            gif: 'image/gif', webp: 'image/webp', bmp: 'image/bmp',
            ico: 'image/x-icon', svg: 'image/svg+xml',
            tiff: 'image/tiff', tif: 'image/tiff',
            avif: 'image/avif', heic: 'image/heic', heif: 'image/heif'
        };

        // Compress/resize a raster image to JPEG at up to maxSide×maxSide using Canvas.
        // SVG and GIF are returned as-is (Canvas would lose animation / vector data).
        // Falls back to the original data URI on any error.
        async function compressImageIfNeeded(file, originalDataUri) {
            const mimeType = file.type || IMAGE_MIME_BY_EXT[file.name.split('.').pop()?.toLowerCase()] || '';
            if (mimeType === 'image/svg+xml' || mimeType === 'image/gif') {
                return originalDataUri;
            }
            const MAX_SIDE = 2048;
            const QUALITY  = 0.85;
            return new Promise((resolve) => {
                const img = new Image();
                img.onload = () => {
                    const scale = Math.min(1, MAX_SIDE / Math.max(img.naturalWidth, img.naturalHeight, 1));
                    const w = Math.round(img.naturalWidth * scale);
                    const h = Math.round(img.naturalHeight * scale);
                    try {
                        const canvas = document.createElement('canvas');
                        canvas.width  = w;
                        canvas.height = h;
                        canvas.getContext('2d').drawImage(img, 0, 0, w, h);
                        resolve(canvas.toDataURL('image/jpeg', QUALITY));
                    } catch (_) {
                        resolve(originalDataUri);
                    }
                };
                img.onerror = () => resolve(originalDataUri);
                img.src = originalDataUri;
            });
        }

        fileInput.addEventListener('change', async () => {
            const files = Array.from(fileInput.files);
            fileInput.value = '';
            for (const file of files) {
                // Determine MIME type; fall back to extension map if file.type is empty.
                let mimeType = file.type;
                if (!mimeType) {
                    const ext = file.name.split('.').pop()?.toLowerCase() || '';
                    mimeType = IMAGE_MIME_BY_EXT[ext] || 'application/octet-stream';
                }

                if (mimeType.startsWith('image/')) {
                    // Images: compress and send inline as base64 data URI via WebSocket.
                    const rawDataUri = await readFileAsDataUrl(file);
                    const dataUri = await compressImageIfNeeded(file, rawDataUri);
                    pendingImages.push({ type: 'image', name: file.name, dataUri, mimeType });
                } else {
                    // Non-image files: upload via HTTP to /media/upload, then reference by URL.
                    const placeholder = { type: 'file', name: file.name, mimeType, sizeBytes: file.size, uploading: true, uploadError: null, mediaUrl: null };
                    pendingImages.push(placeholder);
                    renderImagePreviews();

                    (async () => {
                        try {
                            const headers = await getAuthHeaders();
                            const formData = new FormData();
                            formData.append('file', file);
                            const resp = await fetch('/media/upload', { method: 'POST', headers, body: formData });
                            if (!resp.ok) {
                                const err = await resp.json().catch(() => ({ error: `HTTP ${resp.status}` }));
                                placeholder.uploadError = err.error || `Upload failed (${resp.status})`;
                            } else {
                                const data = await resp.json();
                                placeholder.mediaUrl = data.url;
                                placeholder.sizeBytes = data.sizeBytes;
                            }
                        } catch (e) {
                            placeholder.uploadError = e.message || 'Upload failed';
                        } finally {
                            placeholder.uploading = false;
                            renderImagePreviews();
                        }
                    })();
                    continue;
                }
            }
            renderImagePreviews();
        });

        // Maximum WebSocket payload bytes we will send. Mirrors the server-side
        // MaxMessageBytes setting (1 048 576) minus a small safety margin.
        const MAX_WS_PAYLOAD_BYTES = 900 * 1024;

        function sendMessage() {
            const text = messageInput.value.trim();
            const hasImages = pendingImages.length > 0;
            if (!text && !hasImages) return;
            if (!ws || ws.readyState !== WebSocket.OPEN) return;

            // Check for pending uploads.
            if (hasUploadingAttachments()) {
                appendSystem('Some files are still uploading. Please wait a moment and try again.', false);
                return;
            }
            // Reject attachments with upload errors.
            const erroredFiles = pendingImages.filter(a => a.type === 'file' && a.uploadError);
            if (erroredFiles.length > 0) {
                appendSystem(`Upload failed for: ${erroredFiles.map(f => f.name).join(', ')}. Remove them and try again.`, true);
                return;
            }

            // Build [IMAGE_URL:...] markers for inline images and [FILE_URL:...] for uploaded files.
            const markers = pendingImages.map(item => {
                if (item.type === 'image') return `[IMAGE_URL:${item.dataUri}]`;
                return `[FILE_URL:${item.mediaUrl}]\nAttached file: ${item.name} (${formatFileSize(item.sizeBytes)})`;
            }).join('\n');
            const fullText = markers ? (markers + (text ? '\n' + text : '')) : text;

            // Preflight: abort if the serialised payload would exceed the server limit.
            const msgObj = { type: "user_message", text: fullText };
            if (currentSessionId) msgObj.sessionId = currentSessionId;
            const payload = JSON.stringify(msgObj);
            if (payload.length > MAX_WS_PAYLOAD_BYTES) {
                appendSystem(
                    `Image(s) too large after compression (${Math.round(payload.length / 1024)} KB). ` +
                    `Please use smaller or fewer images (limit ≈ 900 KB per message).`,
                    true
                );
                return;
            }

            // Render user bubble: thumbnails + file chips + text
            const row = createRow('user');
            const div = document.createElement('div');
            div.className = 'message user';
            if (hasImages) {
                const thumbsWrap = document.createElement('div');
                thumbsWrap.style.cssText = `display:flex;flex-wrap:wrap;align-items:center;gap:6px;margin-bottom:${text ? '8px' : '0'}`;
                pendingImages.forEach(item => {
                    if (item.type === 'image') {
                        const imgEl = document.createElement('img');
                        imgEl.src = item.dataUri;
                        imgEl.style.cssText = 'width:72px;height:72px;object-fit:cover;border-radius:8px;';
                        thumbsWrap.appendChild(imgEl);
                    } else {
                        const chip = document.createElement('span');
                        chip.style.cssText = 'display:inline-flex;align-items:center;gap:4px;padding:4px 8px;border-radius:6px;background:rgba(255,255,255,0.1);font-size:12px;max-width:150px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;';
                        chip.textContent = `📄 ${item.name}`;
                        chip.title = `${item.name} (${formatFileSize(item.sizeBytes)})`;
                        thumbsWrap.appendChild(chip);
                    }
                });
                div.appendChild(thumbsWrap);
            }
            if (text) div.appendChild(document.createTextNode(text));
            row.appendChild(div);
            chatContainer.insertBefore(row, typingRow);

            ws.send(payload);

            messageInput.value = '';
            messageInput.style.height = 'auto';
            pendingImages = [];
            renderImagePreviews();
            activeResponseDiv = null;
            activeRawContent = "";
            scrollToBottom();
        }

        messageInput.addEventListener('input', function () {
            this.style.height = 'auto';
            this.style.height = Math.min(this.scrollHeight, 200) + 'px';
            if (this.value === '') this.style.height = 'auto';

            if (this.value.trim().length > 0) {
                sendButton.style.opacity = '1';
                sendButton.style.transform = 'scale(1)';
            } else {
                sendButton.style.opacity = '0.7';
                sendButton.style.transform = 'scale(0.95)';
            }
        });

        messageInput.addEventListener('keydown', (e) => {
            if (e.key === 'Enter' && !e.shiftKey) {
                e.preventDefault();
                sendMessage();
            }
        });

        sendButton.addEventListener('click', () => {
            if (isAwaitingResponse) {
                // Stop mode: send /stop command to abort in-flight execution
                if (ws && ws.readyState === WebSocket.OPEN) {
                    const stopMsg = { type: 'user_message', text: '/stop' };
                    if (currentSessionId) stopMsg.sessionId = currentSessionId;
                    ws.send(JSON.stringify(stopMsg));
                }
                setStopMode(false);
                typingRow.style.display = 'none';
                if (activeResponseDiv) {
                    scheduleActiveResponseRender(true);
                    activeResponseDiv = null;
                    activeRawContent = '';
                }
            } else {
                sendMessage();
            }
        });

        const sessionToken = sessionStorage.getItem(TOKEN_KEY_SESSION);
        const persistedToken = localStorage.getItem(TOKEN_KEY_PERSIST);
        if (sessionToken) {
            tokenInput.value = sessionToken;
        } else if (persistedToken) {
            tokenInput.value = persistedToken;
            rememberToken.checked = true;
        }

        rememberToken.addEventListener('change', () => {
            const token = tokenInput.value.trim();
            if (rememberToken.checked && token) {
                localStorage.setItem(TOKEN_KEY_PERSIST, token);
            } else if (!rememberToken.checked) {
                localStorage.removeItem(TOKEN_KEY_PERSIST);
            }
        });

        tokenInput.addEventListener('keydown', async (event) => {
            if (event.key !== 'Enter') return;
            const typed = tokenInput.value.trim();
            if (!typed) return;
            persistToken(typed);
            tokenDropdown.classList.remove('open');
            if (ws && ws.readyState === WebSocket.OPEN) {
                ws.close(1000, 'Reconnect with updated token');
            }
            await connect();
        });

        oidcLoginButton.addEventListener('click', async () => {
            if (!oidcClient) {
                appendSystem('OIDC is not configured for this page.', true);
                return;
            }
            await oidcClient.login({ returnTo: window.location.pathname + window.location.search });
        });

        oidcLogoutButton.addEventListener('click', async () => {
            clearStoredToken();
            tokenInput.value = '';
            if (reconnectTimer) {
                clearTimeout(reconnectTimer);
                reconnectTimer = null;
            }
            if (ws && ws.readyState === WebSocket.OPEN) {
                ws.close(1000, 'OIDC logout');
            }
            setDisconnectedUi();
            if (oidcClient) {
                await oidcClient.logout();
            }
        });

        doctorButton.addEventListener('click', async () => {
            try {
                const headers = await getAuthHeaders(60);
                const resp = await fetch(`${getBasePath()}/doctor/text`, { method: 'GET', headers: headers });
                if (!resp.ok) {
                    appendSystem(`Doctor request failed (${resp.status}).`, true);
                    return;
                }
                const text = await resp.text();
                appendAssistantMarkdown("```\n" + text + "\n```");
            } catch (e) {
                appendSystem('Doctor request failed.', true);
            }
        });

        // ─── Session History Panel ─────────────────────────────────────────────
        const sidebar = document.getElementById('sidebar');
        const sessionList = document.getElementById('session-list');
        const sessionSearch = document.getElementById('session-search');
        const historyBanner = document.getElementById('history-banner');
        const historyBannerText = document.getElementById('history-banner-text');
        const backToLiveBtn = document.getElementById('back-to-live-btn');
        const newChatBtn = document.getElementById('new-chat-btn');
        const sidebarToggleBtn = document.getElementById('sidebar-toggle');

        // On mobile start collapsed
        if (window.innerWidth <= 768) {
            sidebarVisible = false;
            sidebar.classList.add('collapsed');
        }

        function toggleSidebar() {
            sidebarVisible = !sidebarVisible;
            sidebar.classList.toggle('collapsed', !sidebarVisible);
        }

        function setViewMode(mode, label) {
            viewMode = mode;
            if (mode === 'history') {
                historyBanner.classList.add('visible');
                historyBannerText.textContent = label || 'Viewing session history (read-only)';
                messageInput.disabled = true;
                sendButton.disabled = true;
                attachBtn.disabled = true;
            } else {
                // Note: do NOT hide the banner here — switchToSession keeps it visible in live mode
                // to show which session is active. Only startNewChatSession hides it.
                if (ws && ws.readyState === WebSocket.OPEN) {
                    messageInput.disabled = false;
                    sendButton.disabled = false;
                    attachBtn.disabled = false;
                }
            }
        }

        function relativeTime(iso) {
            const ms = Date.now() - new Date(iso).getTime();
            const sec = Math.floor(ms / 1000);
            if (sec < 60) return 'just now';
            const min = Math.floor(sec / 60);
            if (min < 60) return `${min}m ago`;
            const hr = Math.floor(min / 60);
            if (hr < 24) return `${hr}h ago`;
            const day = Math.floor(hr / 24);
            if (day < 7) return `${day}d ago`;
            return new Date(iso).toLocaleDateString();
        }

        function renderSessionItem(summary) {
            const item = document.createElement('div');
            item.className = 'session-item';
            item.dataset.id = summary.id;

            const title = document.createElement('div');
            title.className = 'session-item-title';
            const rawLabel = summary.senderId || summary.id;
            title.textContent = rawLabel.length > 26 ? rawLabel.slice(0, 24) + '…' : rawLabel;
            title.title = `${summary.senderId} | ch: ${summary.channelId}`;

            const meta = document.createElement('div');
            meta.className = 'session-item-meta';

            if (summary.isActive) {
                const dot = document.createElement('span');
                dot.className = 'session-active-dot';
                meta.appendChild(dot);
            }

            const time = document.createElement('span');
            time.textContent = relativeTime(summary.lastActiveAt);

            const badge = document.createElement('span');
            badge.className = 'session-turns-badge';
            badge.textContent = `${summary.historyTurns} turns`;

            meta.appendChild(time);
            meta.appendChild(badge);
            item.appendChild(title);
            item.appendChild(meta);

            if (summary.id === currentSessionId) {
                item.classList.add('active');
            }

            item.addEventListener('click', () => switchToSession(summary.id, !!summary.isActive));
            return item;
        }

        async function loadSessions(search) {
            const headers = await getAuthHeaders(10);
            const q = search ? `&search=${encodeURIComponent(search)}` : '';
            try {
                const resp = await fetch(`${getBasePath()}/admin/sessions?pageSize=60${q}`, { headers });
                if (!resp.ok) return;
                const data = await resp.json();
                renderSessionList(data.active || [], (data.persisted && data.persisted.items) || []);
            } catch (e) {
                // silent – admin API unavailable in some modes
            }
        }

        function renderSessionList(active, persisted) {
            sessionList.innerHTML = '';
            const all = [
                ...active.map(s => ({ ...s, isActive: true })),
                ...persisted.map(s => ({ ...s, isActive: !!s.isActive }))
            ];
            const seen = new Set();
            const deduped = all.filter(s => { if (seen.has(s.id)) return false; seen.add(s.id); return true; });
            deduped.sort((a, b) => new Date(b.lastActiveAt) - new Date(a.lastActiveAt));

            if (deduped.length === 0) {
                const empty = document.createElement('div');
                empty.id = 'sidebar-empty';
                empty.textContent = 'No sessions found.';
                sessionList.appendChild(empty);
                return;
            }
            deduped.forEach(s => sessionList.appendChild(renderSessionItem(s)));
        }

        async function switchToSession(id, isActive) {
            if (id === currentSessionId) {
                // Already on this session — ensure live mode and scroll
                if (viewMode !== 'live') setViewMode('live');
                scrollToBottom(false);
                return;
            }
            // Increment epoch so any in-flight stream for the previous session is discarded
            streamEpoch++;
            currentSessionId = id;
            document.querySelectorAll('.session-item').forEach(el =>
                el.classList.toggle('active', el.dataset.id === id));
            // Tear down any active streaming state
            if (streamRenderTimer) { clearTimeout(streamRenderTimer); streamRenderTimer = null; }
            setStopMode(false);
            typingRow.style.display = 'none';
            activeResponseDiv = null;
            activeRawContent = '';
            chatContainer.innerHTML = '';
            chatContainer.appendChild(typingRow);

            const headers = await getAuthHeaders(10);
            try {
                const resp = await fetch(`${getBasePath()}/admin/sessions/${encodeURIComponent(id)}`, { headers });
                if (!resp.ok) {
                    appendSystem('Failed to load session.', true);
                } else {
                    const detail = await resp.json();
                    if (detail.session) {
                        const turns = detail.session.history || [];
                        turns.forEach(turn => {
                            if (turn.role === 'user') {
                                const row = createRow('user');
                                const div = document.createElement('div');
                                div.className = 'message user';
                                div.textContent = turn.content || '';
                                row.appendChild(div);
                                chatContainer.insertBefore(row, typingRow);
                            } else if (turn.role === 'assistant') {
                                const row = createRow('assistant');
                                const div = document.createElement('div');
                                div.className = 'message assistant';
                                div.innerHTML = DOMPurify.sanitize(marked.parse(preprocessMediaMarkers(turn.content || '')));
                                div.querySelectorAll('pre code').forEach(b => hljs.highlightElement(b));
                                row.appendChild(div);
                                chatContainer.insertBefore(row, typingRow);
                            }
                            if (turn.toolCalls && turn.toolCalls.length > 0) {
                                turn.toolCalls.forEach(tc => appendToolPill(tc.toolName));
                            }
                        });
                        const sid = id.length > 16 ? id.slice(0, 14) + '…' : id;
                        const statusLabel = isActive ? '● Active' : 'Historical';
                        historyBannerText.textContent = `Session ${sid} · ${turns.length} turns · ${statusLabel}`;
                        historyBanner.classList.add('visible');
                    }
                }
            } catch (e) {
                appendSystem('Error loading session.', true);
            }
            setViewMode('live');
            scrollToBottom(false);
        }

        function startNewChatSession() {
            // Generate a fresh client-side session UUID so the server creates a new conversation
            const uuid = typeof crypto.randomUUID === 'function'
                ? crypto.randomUUID()
                : (() => {
                    const b = crypto.getRandomValues(new Uint8Array(16));
                    b[6] = (b[6] & 0x0f) | 0x40; b[8] = (b[8] & 0x3f) | 0x80;
                    const h = Array.from(b, x => x.toString(16).padStart(2, '0')).join('');
                    return `${h.slice(0,8)}-${h.slice(8,12)}-${h.slice(12,16)}-${h.slice(16,20)}-${h.slice(20)}`;
                })();
            currentSessionId = `session-${uuid}`;
            streamEpoch++;   // discard any stale in-flight chunks
            liveChatNodes = [];
            chatContainer.innerHTML = '';
            chatContainer.appendChild(typingRow);
            activeResponseDiv = null;
            activeRawContent = '';
            document.querySelectorAll('.session-item').forEach(el => el.classList.remove('active'));
            historyBanner.classList.remove('visible');
            setViewMode('live');
            if (ws && ws.readyState === WebSocket.OPEN) {
                messageInput.disabled = false;
                sendButton.disabled = false;
                attachBtn.disabled = false;
                messageInput.focus();
            } else {
                connect();
            }
        }

        newChatBtn.addEventListener('click', startNewChatSession);
        sidebarToggleBtn.addEventListener('click', toggleSidebar);

        backToLiveBtn.addEventListener('click', () => {
            // Reset to default (no explicit session routing) and start fresh
            currentSessionId = null;
            streamEpoch++;  // discard any stale in-flight stream
            liveChatNodes = [];
            chatContainer.innerHTML = '';
            chatContainer.appendChild(typingRow);
            activeResponseDiv = null;
            activeRawContent = '';
            document.querySelectorAll('.session-item').forEach(el => el.classList.remove('active'));
            historyBanner.classList.remove('visible');
            setViewMode('live');
            if (ws && ws.readyState === WebSocket.OPEN) {
                messageInput.disabled = false;
                sendButton.disabled = false;
                messageInput.focus();
            } else {
                connect();
            }
            scrollToBottom(false);
        });

        sessionSearch.addEventListener('input', () => {
            clearTimeout(sessionSearchTimer);
            sessionSearchTimer = setTimeout(() => loadSessions(sessionSearch.value.trim()), 400);
        });
        // ───────────────────────────────────────────────────────────────────────

        async function bootstrapAuth() {
            if (OIDC_CONFIG.enabled && OIDC_CONFIG.authority && OIDC_CONFIG.clientId && window.OpenClawOidc) {
                oidcClient = window.OpenClawOidc.create({
                    authority: OIDC_CONFIG.authority,
                    clientId: OIDC_CONFIG.clientId,
                    scope: OIDC_CONFIG.scope,
                    persist: rememberToken.checked
                });

                const callback = await oidcClient.handleRedirectCallback();
                if (callback.handled && !callback.ok) {
                    appendSystem(`OIDC login failed: ${callback.error}`, true);
                }
            }

            updateOidcButtons();
            await connect();
        }

        bootstrapAuth();

        // --- MCP Server Management ---
        (function () {
            const overlay     = document.getElementById('mcp-overlay');
            const openBtn     = document.getElementById('mcp-panel-btn');
            const closeBtn    = document.getElementById('mcp-close-btn');
            const serverList  = document.getElementById('mcp-server-list');
            const addBtn      = document.getElementById('mcp-add-btn');
            const formSection = document.getElementById('mcp-form-section');
            const formTitle   = document.getElementById('mcp-form-title');
            const formError   = document.getElementById('mcp-form-error');
            const cancelBtn   = document.getElementById('mcp-form-cancel-btn');
            const saveBtn     = document.getElementById('mcp-form-save-btn');
            const statusBar   = document.getElementById('mcp-panel-status');

            const fId             = document.getElementById('mcp-f-id');
            const fName           = document.getElementById('mcp-f-name');
            const fTransport      = document.getElementById('mcp-f-transport');
            const fUrl            = document.getElementById('mcp-f-url');
            const fToken          = document.getElementById('mcp-f-token');
            const fHeadersRows    = document.getElementById('mcp-f-headers-rows');
            const fAddHeaderBtn   = document.getElementById('mcp-f-add-header-btn');
            const fPrefix         = document.getElementById('mcp-f-prefix');
            const fStartupTimeout = document.getElementById('mcp-f-startup-timeout');
            const fRequestTimeout = document.getElementById('mcp-f-request-timeout');
            const fEnabled        = document.getElementById('mcp-f-enabled');

            // user config: editable, saved to .kingcrab/mcp.json
            let mcpConfig     = { Enabled: true, Servers: {} };
            // builtin config: read-only, from appsettings
            let builtinConfig = { Enabled: false, Servers: {} };
            // null = adding new; string = editing existing user server id
            let editingId = null;

            // --- Headers editor helpers ---
            function clearHeaderRows() { fHeadersRows.innerHTML = ''; }

            function addHeaderRow(key, value, disabled) {
                const row = document.createElement('div');
                row.className = 'mcp-header-row';

                const kInput = document.createElement('input');
                kInput.type = 'text';
                kInput.className = 'mcp-input mcp-header-key';
                kInput.placeholder = 'Header name';
                kInput.value = key || '';
                kInput.autocomplete = 'off';
                kInput.disabled = !!disabled;

                const vInput = document.createElement('input');
                vInput.type = 'text';
                vInput.className = 'mcp-input mcp-header-val';
                vInput.placeholder = 'Value';
                vInput.value = value || '';
                vInput.autocomplete = 'off';
                vInput.disabled = !!disabled;

                const delBtn = document.createElement('button');
                delBtn.type = 'button';
                delBtn.className = 'mcp-icon-btn danger';
                delBtn.title = 'Remove';
                delBtn.disabled = !!disabled;
                delBtn.innerHTML = '<svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"><line x1="18" y1="6" x2="6" y2="18"/><line x1="6" y1="6" x2="18" y2="18"/></svg>';
                delBtn.addEventListener('click', () => row.remove());

                row.append(kInput, vInput, delBtn);
                fHeadersRows.appendChild(row);
            }

            function getHeadersFromRows() {
                const result = {};
                fHeadersRows.querySelectorAll('.mcp-header-row').forEach(row => {
                    const k = row.querySelector('.mcp-header-key').value.trim();
                    const v = row.querySelector('.mcp-header-val').value.trim();
                    if (k) result[k] = v;
                });
                return Object.keys(result).length ? result : null;
            }

            function setHeadersFromConfig(headers, disabled) {
                clearHeaderRows();
                if (headers && typeof headers === 'object') {
                    for (const [k, v] of Object.entries(headers)) {
                        addHeaderRow(k, v, disabled);
                    }
                }
            }

            function setAllFormDisabled(disabled) {
                [fId, fName, fTransport, fUrl, fToken, fPrefix, fStartupTimeout, fRequestTimeout, fEnabled]
                    .forEach(el => { el.disabled = disabled; });
                fAddHeaderBtn.style.display = disabled ? 'none' : '';
                fHeadersRows.querySelectorAll('input, button').forEach(el => { el.disabled = disabled; });
            }

            fAddHeaderBtn.addEventListener('click', () => addHeaderRow('', '', false));

            // Normalize camelCase keys to PascalCase (builtin config comes as camelCase from ASP.NET Core JSON)
            function normalizePascal(obj) {
                if (!obj || typeof obj !== 'object' || Array.isArray(obj)) return obj;
                const out = {};
                for (const [k, v] of Object.entries(obj)) {
                    const key = k.charAt(0).toUpperCase() + k.slice(1);
                    out[key] = (v && typeof v === 'object' && !Array.isArray(v)) ? normalizePascal(v) : v;
                }
                return out;
            }

            function showStatus(msg, isErr) {
                statusBar.textContent = msg;
                statusBar.className = 'mcp-panel-status ' + (isErr ? 'err' : 'ok');
                statusBar.style.display = '';
                clearTimeout(showStatus._t);
                if (!isErr) showStatus._t = setTimeout(() => { statusBar.style.display = 'none'; }, 3000);
            }

            // Build a single server card element.
            // builtin=true => read-only: no toggle/delete, no token shown, view-only
            function buildCard(id, cfg, builtin) {
                const card = document.createElement('div');
                card.className = 'mcp-server-card' + (cfg.Enabled === false ? ' disabled' : '');

                const info = document.createElement('div');
                info.className = 'mcp-server-info';

                const nameEl = document.createElement('div');
                nameEl.className = 'mcp-server-name';
                nameEl.textContent = cfg.Name || id;

                const urlEl = document.createElement('div');
                urlEl.className = 'mcp-server-url';
                urlEl.textContent = cfg.Url || cfg.Command || '';

                info.appendChild(nameEl);
                info.appendChild(urlEl);

                const badge = document.createElement('span');
                badge.className = 'mcp-server-badge' + (builtin ? ' builtin' : '');
                badge.textContent = builtin
                    ? (cfg.Enabled === false ? 'builtin \u00b7 disabled' : 'builtin')
                    : (cfg.Enabled === false ? 'disabled' : (cfg.Transport || 'http'));

                const actions = document.createElement('div');
                actions.className = 'mcp-server-actions';

                if (!builtin) {
                    const isEnabled = cfg.Enabled !== false;
                    const toggleBtn = document.createElement('button');
                    toggleBtn.className = 'mcp-icon-btn';
                    toggleBtn.title = isEnabled ? 'Disable' : 'Enable';
                    toggleBtn.innerHTML = isEnabled
                        ? '<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="10"/><line x1="8" y1="12" x2="16" y2="12"/></svg>'
                        : '<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="10"/><line x1="12" y1="8" x2="12" y2="16"/><line x1="8" y1="12" x2="16" y2="12"/></svg>';
                    toggleBtn.addEventListener('click', () => {
                        mcpConfig.Servers[id] = Object.assign({}, cfg, { Enabled: !isEnabled });
                        renderList();
                        saveConfig();
                    });

                    const editBtn = document.createElement('button');
                    editBtn.className = 'mcp-icon-btn';
                    editBtn.title = 'Edit';
                    editBtn.innerHTML = '<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M11 4H4a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7"/><path d="M18.5 2.5a2.121 2.121 0 0 1 3 3L12 15l-4 1 1-4 9.5-9.5z"/></svg>';
                    editBtn.addEventListener('click', () => openForm(id));

                    const delBtn = document.createElement('button');
                    delBtn.className = 'mcp-icon-btn danger';
                    delBtn.title = 'Delete';
                    delBtn.innerHTML = '<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polyline points="3 6 5 6 21 6"/><path d="M19 6l-1 14a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2L5 6"/><path d="M10 11v6"/><path d="M14 11v6"/><path d="M9 6V4h6v2"/></svg>';
                    delBtn.addEventListener('click', () => {
                        if (!confirm('Delete server "' + (cfg.Name || id) + '"?')) return;
                        delete mcpConfig.Servers[id];
                        renderList();
                        saveConfig();
                    });

                    actions.append(toggleBtn, editBtn, delBtn);
                } else {
                    // Builtin: view-only button
                    const viewBtn = document.createElement('button');
                    viewBtn.className = 'mcp-icon-btn';
                    viewBtn.title = 'View (read-only)';
                    viewBtn.innerHTML = '<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M1 12s4-8 11-8 11 8 11 8-4 8-11 8-11-8-11-8z"/><circle cx="12" cy="12" r="3"/></svg>';
                    viewBtn.addEventListener('click', () => openBuiltinView(id, cfg));
                    actions.appendChild(viewBtn);
                }

                card.append(info, badge, actions);
                return card;
            }

            function makeSectionLabel(text) {
                const label = document.createElement('div');
                label.className = 'mcp-section-label';
                label.textContent = text;
                return label;
            }

            function renderList() {
                serverList.innerHTML = '';
                const builtinIds = Object.keys(builtinConfig.Servers || {});
                const userIds    = Object.keys(mcpConfig.Servers   || {});

                if (builtinIds.length === 0 && userIds.length === 0) {
                    const empty = document.createElement('div');
                    empty.className = 'mcp-server-empty';
                    empty.textContent = 'No MCP servers configured.';
                    serverList.appendChild(empty);
                    return;
                }

                if (builtinIds.length > 0) {
                    serverList.appendChild(makeSectionLabel('Built-in'));
                    builtinIds.forEach(id => {
                        serverList.appendChild(buildCard(id, builtinConfig.Servers[id], true));
                    });
                }

                if (userIds.length > 0) {
                    serverList.appendChild(makeSectionLabel('Workspace'));
                    userIds.forEach(id => {
                        serverList.appendChild(buildCard(id, mcpConfig.Servers[id], false));
                    });
                }
            }

            function openBuiltinView(id, cfg) {
                editingId = null;
                formTitle.textContent = 'Built-in Server (read-only)';
                formError.style.display = 'none';

                fId.value             = id;
                fId.readOnly          = true;
                fName.value           = cfg.Name || '';
                fTransport.value      = cfg.Transport || 'streamable-http';
                fUrl.value            = cfg.Url || cfg.Command || '';
                // Show token from Authorization header if present
                const authHeader = (cfg.Headers && cfg.Headers['Authorization']) || '';
                fToken.value = authHeader.startsWith('Bearer ') ? authHeader.slice(7) : authHeader;
                // Show extra headers (everything except Authorization)
                const extra = Object.fromEntries(
                    Object.entries(cfg.Headers || {}).filter(([k]) => k !== 'Authorization')
                );
                setHeadersFromConfig(Object.keys(extra).length ? extra : null, true);
                if (!cfg.Headers && cfg.HasToken) fToken.value = '(hidden)';
                fPrefix.value         = cfg.ToolNamePrefix || '';
                fStartupTimeout.value = cfg.StartupTimeoutSeconds != null ? cfg.StartupTimeoutSeconds : '';
                fRequestTimeout.value = cfg.RequestTimeoutSeconds != null ? cfg.RequestTimeoutSeconds : '';
                fEnabled.checked      = cfg.Enabled !== false;

                setAllFormDisabled(true);
                fId.readOnly = true;
                saveBtn.style.display = 'none';
                cancelBtn.textContent = 'Close';

                formSection.style.display = '';
                addBtn.style.display      = 'none';
            }

            function openForm(serverId) {
                editingId = serverId || null;
                formTitle.textContent = serverId ? 'Edit Server' : 'Add Server';
                formError.style.display = 'none';

                setAllFormDisabled(false);
                fId.readOnly = !!serverId;
                saveBtn.style.display = '';
                cancelBtn.textContent = 'Cancel';

                if (serverId) {
                    const cfg = mcpConfig.Servers[serverId] || {};
                    fId.value             = serverId;
                    fName.value           = cfg.Name || '';
                    fTransport.value      = cfg.Transport || 'streamable-http';
                    fUrl.value            = cfg.Url || '';
                    const authHeader      = (cfg.Headers && cfg.Headers['Authorization']) || '';
                    fToken.value          = authHeader.startsWith('Bearer ') ? authHeader.slice(7) : authHeader;
                    // populate extra headers (all except Authorization)
                    const extra = Object.fromEntries(
                        Object.entries(cfg.Headers || {}).filter(([k]) => k !== 'Authorization')
                    );
                    setHeadersFromConfig(Object.keys(extra).length ? extra : null, false);
                    fPrefix.value         = cfg.ToolNamePrefix || '';
                    fStartupTimeout.value = cfg.StartupTimeoutSeconds != null ? cfg.StartupTimeoutSeconds : '';
                    fRequestTimeout.value = cfg.RequestTimeoutSeconds != null ? cfg.RequestTimeoutSeconds : '';
                    fEnabled.checked      = cfg.Enabled !== false;
                } else {
                    fId.value             = 'streaming-' + Math.random().toString(36).slice(2, 8);
                    fName.value           = '';
                    fTransport.value      = 'streamable-http';
                    fUrl.value            = '';
                    fToken.value          = '';
                    clearHeaderRows();
                    fPrefix.value         = 'streaming.';
                    fStartupTimeout.value = '';
                    fRequestTimeout.value = '';
                    fEnabled.checked      = true;
                }

                formSection.style.display = '';
                addBtn.style.display      = 'none';
                (serverId ? fName : fUrl).focus();
            }

            function closeForm() {
                setAllFormDisabled(false);
                saveBtn.style.display     = '';
                cancelBtn.textContent     = 'Cancel';
                formSection.style.display = 'none';
                addBtn.style.display      = '';
                editingId                 = null;
                clearHeaderRows();
            }

            function buildServerConfig() {
                const id = fId.value.trim();
                if (!id) return { error: 'Server ID is required.' };
                if (!/^[\w\-\.]+$/.test(id)) return { error: 'Server ID may only contain letters, digits, hyphens, underscores and dots.' };

                const url = fUrl.value.trim();
                if (!url) return { error: 'URL is required.' };
                try { new URL(url); } catch (_) { return { error: 'URL is not valid.' }; }

                const cfg = { Transport: fTransport.value || 'streamable-http', Url: url, Enabled: fEnabled.checked };
                const name = fName.value.trim();
                if (name) cfg.Name = name;

                // Merge bearer token + extra headers into cfg.Headers
                const allHeaders = {};
                const token = fToken.value.trim();
                if (token) allHeaders['Authorization'] = 'Bearer ' + token;
                const extra = getHeadersFromRows();
                if (extra) Object.assign(allHeaders, extra);
                if (Object.keys(allHeaders).length) cfg.Headers = allHeaders;

                const prefix = fPrefix.value.trim();
                if (prefix) cfg.ToolNamePrefix = prefix;

                const startupSec = parseInt(fStartupTimeout.value, 10);
                if (!isNaN(startupSec) && startupSec > 0) cfg.StartupTimeoutSeconds = startupSec;

                const requestSec = parseInt(fRequestTimeout.value, 10);
                if (!isNaN(requestSec) && requestSec > 0) cfg.RequestTimeoutSeconds = requestSec;

                return { id, cfg };
            }

            async function saveConfig() {
                try {
                    const headers = Object.assign({ 'Content-Type': 'application/json' }, await getAuthHeaders());
                    const resp = await fetch(getBasePath() + '/admin/workspace/mcp', {
                        method: 'PUT',
                        headers,
                        body: JSON.stringify(mcpConfig)
                    });
                    if (!resp.ok) {
                        const text = await resp.text().catch(() => resp.status);
                        showStatus('Save failed (' + resp.status + '): ' + text, true);
                    } else {
                        showStatus('Saved \u2014 hot-reload will apply changes automatically.', false);
                    }
                } catch (e) {
                    showStatus('Save failed: ' + e.message, true);
                }
            }

            async function loadConfig() {
                try {
                    const headers = await getAuthHeaders();
                    const resp = await fetch(getBasePath() + '/admin/workspace/mcp', { headers });
                    if (resp.ok) {
                        const data = await resp.json();
                        // builtin comes as camelCase from ASP.NET Core serializer — normalize to PascalCase
                        const rawBuiltin = data.builtin || {};
                        builtinConfig = { Enabled: !!(rawBuiltin.enabled ?? rawBuiltin.Enabled), Servers: {} };
                        for (const [id, srv] of Object.entries(rawBuiltin.servers || rawBuiltin.Servers || {})) {
                            builtinConfig.Servers[id] = normalizePascal(srv);
                        }
                        // user config is raw file JSON — already PascalCase
                        mcpConfig = Object.assign({ Enabled: true, Servers: {} }, data.user || {});
                        if (!mcpConfig.Servers) mcpConfig.Servers = {};
                    } else {
                        showStatus('Load failed (' + resp.status + ').', true);
                    }
                } catch (e) {
                    showStatus('Load failed: ' + e.message, true);
                }
                renderList();
            }

            openBtn.addEventListener('click', async () => {
                overlay.classList.add('open');
                closeForm();
                statusBar.style.display = 'none';
                await loadConfig();
            });

            closeBtn.addEventListener('click', () => {
                overlay.classList.remove('open');
                closeForm();
            });

            overlay.addEventListener('click', e => {
                if (e.target === overlay) {
                    overlay.classList.remove('open');
                    closeForm();
                }
            });

            addBtn.addEventListener('click', () => openForm(null));
            cancelBtn.addEventListener('click', closeForm);

            saveBtn.addEventListener('click', async () => {
                const result = buildServerConfig();
                if (result.error) {
                    formError.textContent   = result.error;
                    formError.style.display = '';
                    return;
                }
                formError.style.display = 'none';
                mcpConfig.Servers[result.id] = result.cfg;
                renderList();
                closeForm();
                await saveConfig();
            });
        })();
        // ---

        // ── Channel Config Panel ──────────────────────────────────────────────
        (() => {
            const overlay    = document.getElementById('channel-overlay');
            const openBtn    = document.getElementById('channel-panel-btn');
            const closeBtn   = document.getElementById('channel-close-btn');
            const statusBar  = document.getElementById('ch-panel-status');
            const formError  = document.getElementById('ch-form-error');
            const loadBtn    = document.getElementById('channel-load-btn');
            const revertBtn  = document.getElementById('channel-revert-btn');
            const saveBtn    = document.getElementById('channel-save-btn');

            // Feishu field refs
            const fAppId         = document.getElementById('feishu-appid');
            const fAppSecret     = document.getElementById('feishu-appsecret');
            const fAppSecretRef  = document.getElementById('feishu-appsecret-ref');

            let activeChannel = 'feishu';

            function showStatus(msg, isErr) {
                statusBar.textContent = msg;
                statusBar.className = 'mcp-panel-status ' + (isErr ? 'err' : 'ok');
                statusBar.style.display = '';
            }

            function showFormError(msg) {
                formError.textContent = msg;
                formError.style.display = '';
            }

            function clearFormError() {
                formError.style.display = 'none';
            }

            // Populate the Feishu form from a config object (PascalCase keys from server)
            function populateFeishuForm(cfg) {
                fAppId.value        = cfg.AppId        ?? cfg.appId        ?? '';
                fAppSecret.value    = cfg.AppSecret    ?? cfg.appSecret    ?? '';
                fAppSecretRef.value = cfg.AppSecretRef ?? cfg.appSecretRef ?? '';
            }

            // Build config object from the Feishu form
            function buildFeishuConfig() {
                // Always enable when saving from this simple form; all other fields use C# defaults.
                // Keys must be camelCase to match CoreJsonContext (CamelCase naming policy).
                const cfg = { enabled: true };
                const appId = fAppId.value.trim();
                if (appId) cfg.appId = appId;
                const appSecret = fAppSecret.value.trim();
                if (appSecret) cfg.appSecret = appSecret;
                const appSecretRef = fAppSecretRef.value.trim();
                if (appSecretRef) cfg.appSecretRef = appSecretRef;
                return cfg;
            }

            async function loadChannelConfig() {
                clearFormError();
                showStatus('正在加载…', false);
                try {
                    const headers = await getAuthHeaders();
                    const resp = await fetch(getBasePath() + '/admin/channels/' + activeChannel, { headers });
                    if (!resp.ok) {
                        showStatus('加载失败 (' + resp.status + ')', true);
                        return;
                    }
                    const cfg = await resp.json();
                    if (activeChannel === 'feishu') populateFeishuForm(cfg);
                    showStatus('加载成功 — 当前为生效中的配置', false);
                } catch (e) {
                    showStatus('加载失败: ' + e.message, true);
                }
            }

            async function saveChannelConfig() {
                clearFormError();
                let body;
                if (activeChannel === 'feishu') {
                    body = buildFeishuConfig();
                } else {
                    showFormError('未知渠道: ' + activeChannel);
                    return;
                }
                showStatus('正在保存…', false);
                try {
                    const headers = Object.assign({ 'Content-Type': 'application/json' }, await getAuthHeaders());
                    const resp = await fetch(
                        getBasePath() + '/admin/channels/' + activeChannel + '/update',
                        { method: 'POST', headers, body: JSON.stringify(body) }
                    );
                    const data = await resp.json().catch(() => null);
                    if (!resp.ok) {
                        const msg = data?.error ?? data?.Error ?? resp.status;
                        showStatus('保存失败: ' + msg, true);
                    } else {
                        showStatus('已保存并重连渠道 ✓', false);
                    }
                } catch (e) {
                    showStatus('保存失败: ' + e.message, true);
                }
            }

            async function revertChannelConfig() {
                if (!confirm('确定要恢复为 appsettings 默认配置吗？这将清除所有通过 API 保存的覆盖。')) return;
                showStatus('正在恢复…', false);
                try {
                    const headers = await getAuthHeaders();
                    const resp = await fetch(
                        getBasePath() + '/admin/channels/' + activeChannel + '/override',
                        { method: 'DELETE', headers }
                    );
                    const data = await resp.json().catch(() => null);
                    if (!resp.ok) {
                        const msg = data?.error ?? data?.Error ?? resp.status;
                        showStatus('恢复失败: ' + msg, true);
                    } else {
                        showStatus('已恢复默认配置并重连渠道 ✓', false);
                        await loadChannelConfig();
                    }
                } catch (e) {
                    showStatus('恢复失败: ' + e.message, true);
                }
            }

            openBtn.addEventListener('click', async () => {
                statusBar.style.display = 'none';
                clearFormError();
                overlay.classList.add('open');
                await loadChannelConfig();
            });

            closeBtn.addEventListener('click', () => overlay.classList.remove('open'));

            overlay.addEventListener('click', e => {
                if (e.target === overlay) overlay.classList.remove('open');
            });

            loadBtn.addEventListener('click', loadChannelConfig);
            saveBtn.addEventListener('click', saveChannelConfig);
            revertBtn.addEventListener('click', revertChannelConfig);

            // Tab switching (extensible for future channels)
            document.querySelectorAll('.ch-tab').forEach(tab => {
                tab.addEventListener('click', async () => {
                    document.querySelectorAll('.ch-tab').forEach(t => t.classList.remove('active'));
                    tab.classList.add('active');
                    activeChannel = tab.dataset.channel;
                    clearFormError();
                    statusBar.style.display = 'none';
                    await loadChannelConfig();
                });
            });
        })();
        // ---

        // ── Cron / Automation Management Panel ───────────────────────────────
        (() => {
            const overlay       = document.getElementById('cron-overlay');
            const openBtn       = document.getElementById('cron-panel-btn');
            const closeBtn      = document.getElementById('cron-close-btn');
            const refreshBtn    = document.getElementById('cron-refresh-btn');
            const addBtn        = document.getElementById('cron-add-btn');
            const jobList       = document.getElementById('cron-job-list');
            const formSection   = document.getElementById('cron-form-section');
            const formTitle     = document.getElementById('cron-form-title');
            const formError     = document.getElementById('cron-form-error');
            const cancelBtn     = document.getElementById('cron-form-cancel-btn');
            const saveBtn       = document.getElementById('cron-form-save-btn');
            const statusBar     = document.getElementById('cron-panel-status');
            const histSection   = document.getElementById('cron-history-section');
            const histTitle     = document.getElementById('cron-history-title');
            const histList      = document.getElementById('cron-history-list');
            const histCloseBtn  = document.getElementById('cron-history-close-btn');
            const sessViewBtn   = document.getElementById('cron-session-view-btn');
            const sessSection   = document.getElementById('cron-session-section');
            const sessTitle     = document.getElementById('cron-session-title');
            const sessList      = document.getElementById('cron-session-list');
            const sessCloseBtn  = document.getElementById('cron-session-close-btn');

            let currentHistJob = null;

            const fName         = document.getElementById('cron-f-name');
            const fPrompt       = document.getElementById('cron-f-prompt');
            const fTimezone     = document.getElementById('cron-f-timezone');
            const fModel        = document.getElementById('cron-f-model');
            const fChannel      = document.getElementById('cron-f-channel');
            const fRecipient    = document.getElementById('cron-f-recipient');
            const fEnabled      = document.getElementById('cron-f-enabled');
            const fScheduleRaw  = document.getElementById('cron-f-schedule-raw');
            const dailyTime     = document.getElementById('cron-daily-time');
            const dailyWeekday  = document.getElementById('cron-daily-weekday');
            const intervalVal   = document.getElementById('cron-interval-val');
            const intervalUnit  = document.getElementById('cron-interval-unit');

            let editingId  = null;  // null = new, string = id being edited
            let currentPreset = 'daily';

            // ── Frequency tab switching ────────────────────────────────────
            document.querySelectorAll('.cron-freq-tab').forEach(tab => {
                tab.addEventListener('click', () => {
                    document.querySelectorAll('.cron-freq-tab').forEach(t => t.classList.remove('active'));
                    tab.classList.add('active');
                    currentPreset = tab.dataset.preset;
                    document.getElementById('cron-preset-daily').style.display    = currentPreset === 'daily'    ? '' : 'none';
                    document.getElementById('cron-preset-interval').style.display = currentPreset === 'interval' ? '' : 'none';
                    document.getElementById('cron-preset-custom').style.display   = currentPreset === 'custom'   ? '' : 'none';
                });
            });

            // ── Status bar ────────────────────────────────────────────────
            function showStatus(msg, isErr) {
                statusBar.textContent = msg;
                statusBar.className = 'mcp-panel-status ' + (isErr ? 'err' : 'ok');
                statusBar.style.display = '';
                clearTimeout(showStatus._t);
                if (!isErr) showStatus._t = setTimeout(() => { statusBar.style.display = 'none'; }, 3500);
            }

            // ── Build schedule cron expression from preset UI ──────────────
            function buildSchedule() {
                if (currentPreset === 'custom') {
                    return fScheduleRaw.value.trim();
                }
                if (currentPreset === 'daily') {
                    const parts = (dailyTime.value || '09:00').split(':');
                    const h = parseInt(parts[0], 10) || 9;
                    const m = parseInt(parts[1], 10) || 0;
                    const days = dailyWeekday.checked ? '1-5' : '*';
                    return `${m} ${h} * * ${days}`;
                }
                if (currentPreset === 'interval') {
                    const v = parseInt(intervalVal.value, 10) || 1;
                    const unit = intervalUnit.value;
                    if (unit === 'min') return `*/${v} * * * *`;
                    return `0 */${v} * * *`;
                }
                return '';
            }

            // ── Parse cron expression back into preset UI (best-effort) ───
            function applyScheduleToPreset(schedule) {
                if (!schedule) return;
                // Try to detect daily pattern: "M H * * *" or "M H * * 1-5"
                const dailyMatch = schedule.match(/^(\d+)\s+(\d+)\s+\*\s+\*\s+([\d\-,]+|\*)$/);
                if (dailyMatch) {
                    const m = parseInt(dailyMatch[1], 10);
                    const h = parseInt(dailyMatch[2], 10);
                    const d = dailyMatch[3];
                    dailyTime.value = `${String(h).padStart(2,'0')}:${String(m).padStart(2,'0')}`;
                    dailyWeekday.checked = (d === '1-5');
                    setPresetTab('daily');
                    return;
                }
                // Interval in minutes: "*/N * * * *"
                const minMatch = schedule.match(/^\*\/(\d+)\s+\*\s+\*\s+\*\s+\*$/);
                if (minMatch) {
                    intervalVal.value = minMatch[1];
                    intervalUnit.value = 'min';
                    setPresetTab('interval');
                    return;
                }
                // Interval in hours: "0 */N * * *"
                const hrMatch = schedule.match(/^0\s+\*\/(\d+)\s+\*\s+\*\s+\*$/);
                if (hrMatch) {
                    intervalVal.value = hrMatch[1];
                    intervalUnit.value = 'hour';
                    setPresetTab('interval');
                    return;
                }
                // Fallback: show raw
                fScheduleRaw.value = schedule;
                setPresetTab('custom');
            }

            function setPresetTab(preset) {
                currentPreset = preset;
                document.querySelectorAll('.cron-freq-tab').forEach(t => {
                    t.classList.toggle('active', t.dataset.preset === preset);
                });
                document.getElementById('cron-preset-daily').style.display    = preset === 'daily'    ? '' : 'none';
                document.getElementById('cron-preset-interval').style.display = preset === 'interval' ? '' : 'none';
                document.getElementById('cron-preset-custom').style.display   = preset === 'custom'   ? '' : 'none';
            }

            // ── Relative time helper ───────────────────────────────────────
            function relTime(iso) {
                if (!iso) return '—';
                const ms = Date.now() - new Date(iso).getTime();
                const sec = Math.floor(ms / 1000);
                if (sec < 60) return '刚刚';
                const min = Math.floor(sec / 60);
                if (min < 60) return `${min} 分钟前`;
                const hr = Math.floor(min / 60);
                if (hr < 24) return `${hr} 小时前`;
                return new Date(iso).toLocaleString('zh-CN');
            }

            // ── Source badge label ─────────────────────────────────────────
            function sourceLabel(source) {
                if (!source) return '未知';
                if (source === 'legacy-cron') return '内置';
                if (source === 'agent') return 'Agent';
                if (source === 'webchat') return 'Web';
                return source;
            }
            function sourceCls(source) {
                if (source === 'legacy-cron') return 'source-legacy';
                if (source === 'agent') return 'source-agent';
                return 'source-webchat';
            }

            // ── Build a job card element ───────────────────────────────────
            function buildCard(job) {
                const isStatic = job.source === 'legacy-cron';

                const card = document.createElement('div');
                card.className = 'cron-job-card' + (job.enabled === false ? ' disabled' : '');
                card.dataset.id = job.id;

                // Status dot
                const dot = document.createElement('div');
                dot.className = 'cron-job-status';

                // Info
                const info = document.createElement('div');
                info.className = 'cron-job-info';

                const nameEl = document.createElement('div');
                nameEl.className = 'cron-job-name';
                nameEl.textContent = job.name || job.id;

                const metaEl = document.createElement('div');
                metaEl.className = 'cron-job-meta';
                metaEl.textContent = job.schedule || '';

                info.appendChild(nameEl);
                info.appendChild(metaEl);

                // Source badge
                const badge = document.createElement('span');
                badge.className = `cron-job-badge ${sourceCls(job.source)}`;
                badge.textContent = sourceLabel(job.source);

                // Actions
                const actions = document.createElement('div');
                actions.className = 'cron-job-actions';

                // Run now
                const runBtn = document.createElement('button');
                runBtn.className = 'mcp-icon-btn';
                runBtn.title = '立即执行';
                runBtn.innerHTML = '<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polygon points="5 3 19 12 5 21 5 3"/></svg>';
                runBtn.addEventListener('click', () => runJob(job.id, nameEl));

                // History
                const histBtn = document.createElement('button');
                histBtn.className = 'mcp-icon-btn';
                histBtn.title = '执行历史';
                histBtn.innerHTML = '<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="10"/><polyline points="12 6 12 12 16 14"/></svg>';
                histBtn.addEventListener('click', () => loadHistory(job));

                actions.appendChild(runBtn);
                actions.appendChild(histBtn);

                if (!isStatic) {
                    // Edit
                    const editBtn = document.createElement('button');
                    editBtn.className = 'mcp-icon-btn';
                    editBtn.title = '编辑';
                    editBtn.innerHTML = '<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M11 4H4a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7"/><path d="M18.5 2.5a2.121 2.121 0 0 1 3 3L12 15l-4 1 1-4 9.5-9.5z"/></svg>';
                    editBtn.addEventListener('click', () => openForm(job));

                    // Delete
                    const delBtn = document.createElement('button');
                    delBtn.className = 'mcp-icon-btn danger';
                    delBtn.title = '删除';
                    delBtn.innerHTML = '<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polyline points="3 6 5 6 21 6"/><path d="M19 6l-1 14a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2L5 6"/><path d="M10 11v6"/><path d="M14 11v6"/><path d="M9 6V4h6v2"/></svg>';
                    delBtn.addEventListener('click', () => deleteJob(job.id, job.name || job.id));

                    actions.appendChild(editBtn);
                    actions.appendChild(delBtn);
                }

                card.append(dot, info, badge, actions);
                return card;
            }

            // ── Render job list ────────────────────────────────────────────
            function renderJobs(items) {
                jobList.innerHTML = '';
                if (!items || items.length === 0) {
                    const empty = document.createElement('div');
                    empty.className = 'mcp-server-empty';
                    empty.textContent = '暂无定时任务。';
                    jobList.appendChild(empty);
                    return;
                }
                // Static/legacy first, then managed/webchat/agent
                const sorted = [...items].sort((a, b) => {
                    const aStatic = a.source === 'legacy-cron' ? 0 : 1;
                    const bStatic = b.source === 'legacy-cron' ? 0 : 1;
                    return aStatic - bStatic || (a.name || a.id).localeCompare(b.name || b.id, 'zh-CN');
                });
                sorted.forEach(job => jobList.appendChild(buildCard(job)));
            }

            // ── Load job list from server ──────────────────────────────────
            async function loadJobs() {
                try {
                    const headers = await getAuthHeaders();
                    const resp = await fetch(getBasePath() + '/admin/automations', { headers });
                    if (!resp.ok) {
                        showStatus('加载失败 (' + resp.status + ')', true);
                        return;
                    }
                    const data = await resp.json();
                    renderJobs(data.items || []);
                } catch (e) {
                    showStatus('加载失败: ' + e.message, true);
                }
            }

            // ── Load history for a specific job ───────────────────────────
            async function loadHistory(job) {
                const id = job.id;
                const name = job.name || job.id;
                currentHistJob = job;
                histTitle.textContent = `${name} · 执行历史`;
                histList.innerHTML = '<div style="opacity:0.5;font-size:0.8rem;padding:8px">加载中…</div>';
                formSection.style.display = 'none';
                sessSection.style.display = 'none';
                histSection.style.display = '';
                sessViewBtn.style.display = 'none';

                try {
                    const headers = await getAuthHeaders();
                    const resp = await fetch(getBasePath() + '/admin/automations/' + encodeURIComponent(id), { headers });
                    if (!resp.ok) {
                        histList.innerHTML = '<div style="color:#ef4444;font-size:0.8rem;padding:8px">加载失败 (' + resp.status + ')</div>';
                        return;
                    }
                    const data = await resp.json();
                    renderHistory(data.runState);
                } catch (e) {
                    histList.innerHTML = `<div style="color:#ef4444;font-size:0.8rem;padding:8px">加载失败: ${e.message}</div>`;
                }
            }

            function renderHistory(runState) {
                histList.innerHTML = '';
                // Show "查看完整会话" button if there's a session to view
                if (currentHistJob) {
                    sessViewBtn.style.display = '';
                }
                if (!runState) {
                    histList.innerHTML = '<div style="opacity:0.5;font-size:0.8rem;padding:8px">无历史记录。</div>';
                    return;
                }

                // Last run summary
                if (runState.lastRunAtUtc) {
                    const summary = document.createElement('div');
                    summary.style.cssText = 'font-size:0.78rem;opacity:0.65;margin-bottom:10px;padding:0 2px';
                    const outcomeLabel = runState.outcome === 'success' ? '✅ 成功'
                        : runState.outcome === 'failure' ? '❌ 失败'
                        : runState.outcome || '—';
                    summary.textContent = `上次执行: ${relTime(runState.lastRunAtUtc)} · ${outcomeLabel}`;
                    if (runState.messagePreview) {
                        const prev = document.createElement('div');
                        prev.style.cssText = 'opacity:0.5;margin-top:2px;font-size:0.73rem;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;';
                        prev.textContent = runState.messagePreview;
                        summary.appendChild(prev);
                    }
                    histList.appendChild(summary);
                }

                const runs = runState.recentRuns || [];
                if (runs.length === 0) {
                    const empty = document.createElement('div');
                    empty.style.cssText = 'opacity:0.5;font-size:0.8rem;padding:8px 2px';
                    empty.textContent = '暂无详细执行记录。';
                    histList.appendChild(empty);
                    return;
                }

                runs.forEach(run => {
                    const row = document.createElement('div');
                    row.className = 'cron-history-row';
                    row.style.cursor = 'pointer';

                    const dotEl = document.createElement('div');
                    dotEl.className = 'cron-history-dot ' + (run.outcome === 'success' ? 'ok' : run.outcome === 'failure' ? 'error' : '');

                    const meta = document.createElement('div');
                    meta.className = 'cron-history-meta';
                    const tokens = (run.inputTokens || run.outputTokens)
                        ? ` · ${(run.inputTokens || 0) + (run.outputTokens || 0)} tokens`
                        : '';
                    meta.textContent = `${relTime(run.ranAtUtc)}${tokens}`;

                    const prev = document.createElement('div');
                    prev.className = 'cron-history-preview';
                    prev.textContent = run.messagePreview || '—';
                    prev.title = run.messagePreview || '';

                    // expandable detail panel
                    const detail = document.createElement('div');
                    detail.className = 'cron-history-detail';
                    detail.style.display = 'none';

                    const outcomeLabel = run.outcome === 'success' ? '✅ 成功'
                        : run.outcome === 'failure' ? '❌ 失败'
                        : run.outcome || '—';
                    const ranAt = run.ranAtUtc ? new Date(run.ranAtUtc).toLocaleString() : '—';
                    detail.innerHTML =
                        `<div class="cron-history-detail-row"><span>执行时间</span><span>${ranAt}</span></div>` +
                        `<div class="cron-history-detail-row"><span>结果</span><span>${outcomeLabel}</span></div>` +
                        `<div class="cron-history-detail-row"><span>输入 tokens</span><span>${run.inputTokens ?? 0}</span></div>` +
                        `<div class="cron-history-detail-row"><span>输出 tokens</span><span>${run.outputTokens ?? 0}</span></div>` +
                        (run.messagePreview
                            ? `<div class="cron-history-detail-preview">${run.messagePreview.replace(/</g, '&lt;')}</div>`
                            : '');

                    row.addEventListener('click', () => {
                        const isOpen = detail.style.display !== 'none';
                        detail.style.display = isOpen ? 'none' : 'block';
                        row.classList.toggle('cron-history-row-open', !isOpen);
                    });

                    row.append(dotEl, meta, prev);
                    histList.appendChild(row);
                    histList.appendChild(detail);
                });
            }

            // ── Open add/edit form ─────────────────────────────────────────
            function openForm(job) {
                editingId = job ? job.id : null;
                formTitle.textContent = job ? '编辑定时任务' : '新建定时任务';
                formError.style.display = 'none';
                histSection.style.display = 'none';

                if (job) {
                    fName.value     = job.name || '';
                    fPrompt.value   = job.prompt || '';
                    fTimezone.value = job.timezone || '';
                    fModel.value    = job.modelId || '';
                    fChannel.value  = job.deliveryChannelId || '';
                    fRecipient.value = job.deliveryRecipientId || '';
                    fEnabled.checked = job.enabled !== false;
                    applyScheduleToPreset(job.schedule || '');
                } else {
                    fName.value     = '';
                    fPrompt.value   = '';
                    fTimezone.value = 'Asia/Shanghai';
                    fModel.value    = '';
                    fChannel.value  = '';
                    fRecipient.value = '';
                    fEnabled.checked = true;
                    dailyTime.value = '09:00';
                    dailyWeekday.checked = false;
                    intervalVal.value = '1';
                    intervalUnit.value = 'hour';
                    fScheduleRaw.value = '';
                    setPresetTab('daily');
                }

                formSection.style.display = '';
                addBtn.style.display = 'none';
                fName.focus();
            }

            function closeForm() {
                formSection.style.display = 'none';
                histSection.style.display = 'none';
                addBtn.style.display = '';
                editingId = null;
            }

            // ── Save (create or update) ────────────────────────────────────
            async function saveJob() {
                const name = fName.value.trim();
                const schedule = buildSchedule();
                const prompt = fPrompt.value.trim();

                if (!name) {
                    formError.textContent = '请填写任务名称。';
                    formError.style.display = '';
                    return;
                }
                if (!schedule) {
                    formError.textContent = '请设置执行计划。';
                    formError.style.display = '';
                    return;
                }
                if (!prompt) {
                    formError.textContent = '请填写提示词。';
                    formError.style.display = '';
                    return;
                }
                formError.style.display = 'none';

                const payload = {
                    id: editingId || '',
                    name,
                    schedule,
                    prompt,
                    timezone: fTimezone.value.trim() || null,
                    modelId: fModel.value.trim() || null,
                    deliveryChannelId: fChannel.value.trim() || null,
                    deliveryRecipientId: fRecipient.value.trim() || null,
                    enabled: fEnabled.checked,
                    source: editingId ? undefined : 'webchat'
                };

                try {
                    const headers = Object.assign({ 'Content-Type': 'application/json' }, await getAuthHeaders());
                    let resp;
                    if (editingId) {
                        resp = await fetch(getBasePath() + '/admin/automations/' + encodeURIComponent(editingId), {
                            method: 'PUT',
                            headers,
                            body: JSON.stringify(payload)
                        });
                    } else {
                        resp = await fetch(getBasePath() + '/admin/automations', {
                            method: 'POST',
                            headers,
                            body: JSON.stringify(payload)
                        });
                    }
                    if (!resp.ok) {
                        const err = await resp.json().catch(() => ({ error: '保存失败 (' + resp.status + ')' }));
                        formError.textContent = err.error || '保存失败 (' + resp.status + ')';
                        formError.style.display = '';
                        return;
                    }
                    closeForm();
                    showStatus('保存成功！', false);
                    await loadJobs();
                } catch (e) {
                    formError.textContent = '保存失败: ' + e.message;
                    formError.style.display = '';
                }
            }

            // ── Delete a job ──────────────────────────────────────────────
            async function deleteJob(id, name) {
                if (!confirm(`确定删除定时任务 "${name}"？`)) return;
                try {
                    const headers = Object.assign({ 'Content-Type': 'application/json' }, await getAuthHeaders());
                    const resp = await fetch(getBasePath() + '/admin/automations/' + encodeURIComponent(id), {
                        method: 'DELETE',
                        headers
                    });
                    if (!resp.ok) {
                        const err = await resp.json().catch(() => ({ error: '删除失败 (' + resp.status + ')' }));
                        showStatus(err.error || '删除失败 (' + resp.status + ')', true);
                        return;
                    }
                    showStatus('已删除。', false);
                    await loadJobs();
                } catch (e) {
                    showStatus('删除失败: ' + e.message, true);
                }
            }

            // ── Load full session conversation ─────────────────────────────
            async function loadSession() {
                if (!currentHistJob) return;
                const sessionId = currentHistJob.sessionId || ('automation:' + currentHistJob.id);
                const label = currentHistJob.name || currentHistJob.id;
                sessTitle.textContent = `${label} · 完整会话`;
                sessList.innerHTML = '<div style="opacity:0.5;font-size:0.8rem;padding:8px">加载中…</div>';
                histSection.style.display = 'none';
                sessSection.style.display = '';

                try {
                    const headers = await getAuthHeaders();
                    const resp = await fetch(getBasePath() + '/admin/sessions/' + encodeURIComponent(sessionId), { headers });
                    if (resp.status === 404) {
                        sessList.innerHTML = '<div style="opacity:0.5;font-size:0.8rem;padding:8px">暂无会话记录（该任务尚未执行过）。</div>';
                        return;
                    }
                    if (!resp.ok) {
                        sessList.innerHTML = `<div style="color:#ef4444;font-size:0.8rem;padding:8px">加载失败 (${resp.status})</div>`;
                        return;
                    }
                    const data = await resp.json();
                    renderSession(data.session?.history || []);
                } catch (e) {
                    sessList.innerHTML = `<div style="color:#ef4444;font-size:0.8rem;padding:8px">加载失败: ${e.message}</div>`;
                }
            }

            function renderSession(history) {
                sessList.innerHTML = '';
                if (!history || history.length === 0) {
                    sessList.innerHTML = '<div style="opacity:0.5;font-size:0.8rem;padding:8px">暂无对话记录。</div>';
                    return;
                }
                // Filter out system-only turns (tool-only with no content, etc.)
                history.forEach(turn => {
                    if (!turn.content && (!turn.toolCalls || turn.toolCalls.length === 0)) return;
                    const wrap = document.createElement('div');
                    wrap.className = 'cron-sess-turn cron-sess-turn-' + (turn.role === 'user' ? 'user' : 'assistant');

                    const header = document.createElement('div');
                    header.className = 'cron-sess-turn-header';
                    const roleLabel = turn.role === 'user' ? '指令' : 'AI 回复';
                    const ts = turn.timestamp ? new Date(turn.timestamp).toLocaleString('zh-CN') : '';
                    header.textContent = roleLabel + (ts ? '  ' + ts : '');

                    const body = document.createElement('div');
                    body.className = 'cron-sess-turn-body';
                    body.textContent = turn.content || '';

                    wrap.appendChild(header);
                    wrap.appendChild(body);

                    if (turn.toolCalls && turn.toolCalls.length > 0) {
                        turn.toolCalls.forEach(tc => {
                            const tcEl = document.createElement('div');
                            tcEl.className = 'cron-sess-tool-call';
                            tcEl.textContent = `🔧 ${tc.toolName}`;
                            tcEl.title = tc.arguments || '';
                            wrap.appendChild(tcEl);
                        });
                    }

                    sessList.appendChild(wrap);
                });
                // Scroll to bottom
                sessList.scrollTop = sessList.scrollHeight;
            }

            // ── Run now ───────────────────────────────────────────────────
            async function runJob(id, nameEl) {
                try {
                    const headers = Object.assign({ 'Content-Type': 'application/json' }, await getAuthHeaders());
                    const resp = await fetch(getBasePath() + '/admin/automations/' + encodeURIComponent(id) + '/run', {
                        method: 'POST',
                        headers
                    });
                    if (!resp.ok) {
                        const err = await resp.json().catch(() => ({ error: '执行失败 (' + resp.status + ')' }));
                        showStatus(err.error || '执行失败 (' + resp.status + ')', true);
                        return;
                    }
                    showStatus('已触发立即执行！', false);
                } catch (e) {
                    showStatus('执行失败: ' + e.message, true);
                }
            }

            // ── Event wiring ──────────────────────────────────────────────
            openBtn.addEventListener('click', async () => {
                overlay.classList.add('open');
                closeForm();
                statusBar.style.display = 'none';
                await loadJobs();
            });

            closeBtn.addEventListener('click', () => {
                overlay.classList.remove('open');
                closeForm();
            });

            overlay.addEventListener('click', e => {
                if (e.target === overlay) {
                    overlay.classList.remove('open');
                    closeForm();
                }
            });

            refreshBtn.addEventListener('click', loadJobs);
            addBtn.addEventListener('click', () => openForm(null));
            cancelBtn.addEventListener('click', closeForm);
            histCloseBtn.addEventListener('click', closeForm);
            sessViewBtn.addEventListener('click', loadSession);
            sessCloseBtn.addEventListener('click', () => {
                sessSection.style.display = 'none';
                histSection.style.display = '';
            });
            saveBtn.addEventListener('click', saveJob);
        })();
        // ---
