const camera = document.querySelector('#camera');
const status = document.querySelector('#status');
const button = document.querySelector('#start');
const metrics = document.querySelector('#metrics');
let stream, socket, encoder, wakeLock, callbackId, retryTimer, watchdog;
let running = false, wanted = false, starting = false, generation = 0, streamId = 0;
let sequence = 0, forceKey = true, discardUntilKey = false, lastCapture = 0;
let width = 0, height = 0, sent = 0, byteCount = 0, skips = 0, startedAt = 0, windowStart = 0, lastOutput = 0;
let maxEncodeMs = 0;
const pending = new Map();
const setStatus = text => { status.textContent = text; };
const errorText = error => error?.message || String(error);
const teardown = () => {
    generation++;
    running = false;
    if (callbackId && camera.cancelVideoFrameCallback) camera.cancelVideoFrameCallback(callbackId);
    callbackId = undefined;
    clearTimeout(retryTimer); clearInterval(watchdog);
    if (encoder && encoder.state !== 'closed') { try { encoder.close(); } catch {} }
    encoder = undefined;
    if (socket) { socket.onclose = null; socket.onerror = null; socket.close(); socket = undefined; }
    if (stream) stream.getTracks().forEach(track => track.stop());
    stream = undefined; camera.srcObject = null;
    if (wakeLock) wakeLock.release().catch(() => {});
    wakeLock = undefined; pending.clear();
};
async function stop() {
    wanted = false; teardown(); button.disabled = false; button.textContent = '开始拍摄';
    button.className = ''; setStatus('已停止，摄像头已释放');
}
function reconnect(reason) {
    if (!wanted) return;
    teardown();
    button.disabled = false; button.textContent = '停止拍摄'; button.className = 'stop';
    setStatus(reason + '，正在重新连接…');
    retryTimer = setTimeout(() => begin(), 1200);
}
function sendChunk(chunk, metadata, gen) {
    if (!running || gen !== generation || !socket || socket.readyState !== WebSocket.OPEN) return;
    const captureTime = pending.get(chunk.timestamp);
    pending.delete(chunk.timestamp);
    if (captureTime) maxEncodeMs = Math.max(maxEncodeMs, performance.now() - captureTime);
    lastOutput = performance.now();
    const seq = sequence++;
    if (socket.bufferedAmount > 131072) { skips++; discardUntilKey = true; forceKey = true; return; }
    if (discardUntilKey && chunk.type !== 'key') { skips++; forceKey = true; return; }
    discardUntilKey = false;
    const packet = new Uint8Array(32 + chunk.byteLength);
    const header = new DataView(packet.buffer);
    header.setUint32(0, 0x31564344, true);
    header.setUint16(4, width, true); header.setUint16(6, height, true);
    header.setUint32(8, streamId, true); header.setUint32(12, seq, true);
    header.setBigInt64(16, BigInt(chunk.timestamp), true);
    header.setUint32(24, chunk.type === 'key' ? 1 : 0, true);
    header.setUint32(28, chunk.byteLength, true);
    chunk.copyTo(packet.subarray(32));
    socket.send(packet);
    sent++; byteCount += packet.length;
    if (sent === 1) setStatus('正在传输到电脑');
    if (metadata?.decoderConfig) {
        const c = metadata.decoderConfig;
        metrics.dataset.codec = c.codec + ' · ' + (c.colorSpace?.matrix || '默认色彩');
    }
}
function capture(now) {
    if (!running) return;
    callbackId = camera.requestVideoFrameCallback(capture);
    if (now - lastCapture < 48) return;
    if (encoder.encodeQueueSize >= 2 || socket.bufferedAmount > 98304) { skips++; return; }
    if (camera.videoWidth !== width || camera.videoHeight !== height) {
        reconnect('画面方向已变化'); return;
    }
    lastCapture = now;
    const timestamp = Math.round(now * 1000);
    let frame;
    try {
        frame = new VideoFrame(camera, { timestamp });
        pending.set(timestamp, performance.now());
        // Some encoders may drop input frames in realtime mode. Bound timestamp tracking too.
        while (pending.size > 12) pending.delete(pending.keys().next().value);
        const keyFrame = forceKey || sequence % 40 === 0;
        forceKey = false;
        encoder.encode(frame, { keyFrame });
    } catch (error) { reconnect('编码异常：' + errorText(error)); }
    finally { frame?.close(); }
}
async function begin() {
    if (starting || running || !wanted) return;
    starting = true; button.disabled = true;
    const gen = ++generation;
    try {
        if (!window.isSecureContext) throw new Error('需要先信任电脑证书并通过 HTTPS 打开');
        if (!window.VideoEncoder || !window.VideoFrame || !camera.requestVideoFrameCallback)
            throw new Error('此 Safari 没有可用的 WebCodecs 视频编码接口');
        const paired = await fetch('/api/paired', { cache: 'no-store' });
        if (!paired.ok) {
            wanted = false;
            throw new Error('请先用电脑 DeskCam 的“连接手机”二维码配对');
        }
        setStatus('正在打开后置摄像头…');
        const media = await navigator.mediaDevices.getUserMedia({ audio: false, video: {
            facingMode: { ideal: 'environment' }, width: { ideal: 1920 }, height: { ideal: 1080 },
            frameRate: { ideal: 20, max: 20 }
        }});
        if (gen !== generation || !wanted) { media.getTracks().forEach(t => t.stop()); return; }
        stream = media; camera.srcObject = media; await camera.play();
        width = camera.videoWidth; height = camera.videoHeight;
        if (width * height !== 1920 * 1080 || Math.max(width, height) !== 1920)
            throw new Error('相机实际输出 ' + width + '×' + height + '，未达到 1080p。请关闭其他相机应用后重试');
        const config = { codec: 'avc1.420028', width, height, bitrate: 8000000, framerate: 20,
            latencyMode: 'realtime', hardwareAcceleration: 'prefer-hardware', avc: { format: 'annexb' } };
        let supported = await VideoEncoder.isConfigSupported(config);
        if (!supported.supported) {
            config.hardwareAcceleration = 'no-preference';
            supported = await VideoEncoder.isConfigSupported(config);
        }
        if (!supported.supported) throw new Error('此 Safari 不支持 1080p 实时 H.264 编码');
        if (gen !== generation || !wanted) return;
        setStatus('正在连接电脑…');
        socket = new WebSocket('wss://' + location.host + '/ws/phone');
        const connection = socket;
        await new Promise((resolve, reject) => {
            const timeout = setTimeout(() => reject(new Error('连接超时')), 8000);
            connection.onopen = () => { clearTimeout(timeout); resolve(); };
            connection.onerror = () => { clearTimeout(timeout); reject(new Error('电脑连接失败')); };
            connection.onclose = () => { clearTimeout(timeout); reject(new Error('配对已失效或电脑拒绝连接')); };
        });
        if (gen !== generation || !wanted) return;
        sequence = 0; streamId++; forceKey = true; discardUntilKey = false; lastCapture = 0;
        sent = 0; byteCount = 0; skips = 0; maxEncodeMs = 0;
        startedAt = windowStart = lastOutput = performance.now();
        encoder = new VideoEncoder({
            output: (chunk, metadata) => sendChunk(chunk, metadata, gen),
            error: error => { if (gen === generation) reconnect('编码器错误：' + errorText(error)); }
        });
        encoder.configure(config);
        connection.onmessage = event => { try { if (JSON.parse(event.data).type === 'keyframe') forceKey = true; } catch {} };
        connection.onclose = () => { if (gen === generation) reconnect('连接已断开'); };
        connection.onerror = () => { if (gen === generation) reconnect('网络暂时不可用'); };
        running = true;
        button.disabled = false; button.textContent = '停止拍摄'; button.className = 'stop';
        callbackId = camera.requestVideoFrameCallback(capture);
        if (navigator.wakeLock) navigator.wakeLock.request('screen').then(lock => {
            if (gen !== generation) lock.release().catch(() => {}); else wakeLock = lock;
        }).catch(() => {});
        document.querySelector('#install').hidden = false;
        watchdog = setInterval(() => {
            if (!running || gen !== generation) return;
            const now = performance.now(), dt = (now - windowStart) / 1000;
            const track = stream.getVideoTracks()[0].getSettings();
            metrics.textContent = width + ' × ' + height + ' · H.264 / WSS\n' +
                (sent / dt).toFixed(1) + ' fps · ' + (byteCount * 8 / dt / 1e6).toFixed(1) + ' Mbps\n' +
                '发送缓冲 ' + Math.round(connection.bufferedAmount / 1024) + ' KB · 编码峰值 ' + Math.round(maxEncodeMs) + ' ms\n' +
                '跳过 ' + skips + ' 帧 · 已运行 ' + Math.floor((now - startedAt) / 1000) + ' 秒\n' +
                '相机设置 ' + track.width + '×' + track.height + ' @ ' + track.frameRate + '\n' +
                (metrics.dataset.codec || '') + '\n' + navigator.userAgent;
            sent = 0; byteCount = 0; skips = 0; maxEncodeMs = 0; windowStart = now;
            if (connection.bufferedAmount > 262144 || now - lastOutput > 4000) reconnect('视频暂时停滞');
        }, 1000);
    } catch (error) {
        if (gen !== generation) return;
        teardown(); setStatus(errorText(error));
        button.disabled = false; button.textContent = '重试连接'; button.className = '';
        wanted = false;
    } finally { starting = false; }
}
button.onclick = () => {
    if (wanted || running) stop();
    else { wanted = true; begin(); }
};
document.addEventListener('visibilitychange', () => {
    if (document.hidden) {
        const resume = wanted;
        teardown(); wanted = resume;
        setStatus('已暂停；回到 DeskCam 后恢复');
    } else if (wanted) begin();
});
window.addEventListener('pagehide', () => teardown());
async function init() {
    try {
        const pair = new URLSearchParams(location.hash.slice(1));
        if (pair.has('session') && pair.has('token')) {
            const response = await fetch('/api/pair', { method: 'POST', headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ session: pair.get('session'), token: pair.get('token') }) });
            history.replaceState(null, '', '/phone');
            if (!response.ok) throw new Error('二维码已过期，请在电脑重新生成');
        }
        const response = await fetch('/api/paired');
        if (!response.ok) throw new Error('请扫描电脑 DeskCam 的“连接手机”二维码');
        button.disabled = false;
        wanted = true; await begin();
    } catch (error) { setStatus(errorText(error)); button.disabled = false; }
}
init();
