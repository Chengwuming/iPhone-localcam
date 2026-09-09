import { cameraFrame } from './camera-frame.mjs';
import { qualities, adjustable, applyVerified, preparePhoto, deadline, highResolutionPhoto } from './camera-controls.mjs';
const quality = document.querySelector('#quality');
const focusMode = document.querySelector('#focus-mode'), focusDistance = document.querySelector('#focus-distance');
const zoom = document.querySelector('#camera-zoom'), hdButton = document.querySelector('#capture-hd');
const photoStatus = document.querySelector('#photo-status'), photoFile = document.querySelector('#photo-file');
let preferences = { quality: 'paper', focus: 'auto', distance: null, zoom: null };
try { Object.assign(preferences, JSON.parse(localStorage.getItem('deskcam-camera-settings') || '{}')); } catch {}
if (!Object.hasOwn(qualities, preferences.quality)) preferences.quality = 'paper';
quality.value = preferences.quality;
const savePreferences = () => { try { localStorage.setItem('deskcam-camera-settings', JSON.stringify(preferences)); } catch {} };
let photoBusy = false, systemPicker = false;
let pairedReady = false, commandBusy = false, commandAck = 0, commandResult = '', syncBusy = false;
const camera = document.querySelector('#camera');
const surface = document.createElement('canvas');
const surfaceContext = surface.getContext('2d', { alpha: false });
const status = document.querySelector('#status');
const button = document.querySelector('#start');
const metrics = document.querySelector('#metrics');
let stream, socket, encoder, wakeLock, callbackId, retryTimer, watchdog;
let running = false, wanted = false, starting = false, recovering = false, generation = 0, streamId = 0;
let sequence = 0, forceKey = true, discardUntilKey = false, colorFlags = 0;
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
    hdButton.disabled = true; focusMode.disabled = zoom.disabled = true;
    document.querySelector('#refocus').disabled = true;
    if (wakeLock) wakeLock.release().catch(() => {});
    wakeLock = undefined; pending.clear();
};
async function stop() {
    wanted = false; recovering = false; teardown(); button.disabled = false; button.textContent = '开始拍摄';
    button.className = ''; setStatus('已停止，摄像头已释放');
}
function reconnect(reason) {
    if (!wanted) return;
    recovering = true;
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
    if (metadata?.decoderConfig) {
        const c = metadata.decoderConfig;
        colorFlags = (['smpte170m', 'bt470bg'].includes(c.colorSpace?.matrix) ? 2 : 0) | (c.colorSpace?.fullRange ? 4 : 0);
        metrics.dataset.codec = c.codec + ' · ' + (c.colorSpace?.matrix || '默认色彩');
    }
    const packet = new Uint8Array(32 + chunk.byteLength);
    const header = new DataView(packet.buffer);
    header.setUint32(0, 0x31564344, true);
    header.setUint16(4, width, true); header.setUint16(6, height, true);
    header.setUint32(8, streamId, true); header.setUint32(12, seq, true);
    header.setBigInt64(16, BigInt(chunk.timestamp), true);
    header.setUint32(24, (chunk.type === 'key' ? 1 : 0) | colorFlags, true);
    header.setUint32(28, chunk.byteLength, true);
    chunk.copyTo(packet.subarray(32));
    socket.send(packet);
    sent++; byteCount += packet.length;
    if (sent === 1) setStatus('正在传输到电脑');
}
function capture(now) {
    if (!running) return;
    callbackId = camera.requestVideoFrameCallback(capture);
    if (photoBusy) return;
    if (encoder.encodeQueueSize >= 2 || socket.bufferedAmount > 98304) { skips++; return; }
    if (camera.videoWidth !== width || camera.videoHeight !== height) {
        reconnect('画面方向已变化'); return;
    }
    const timestamp = Math.round(now * 1000);
    let frame;
    try {
        frame = cameraFrame(camera, surfaceContext, width, height, timestamp);
        pending.set(timestamp, performance.now());
        // Some encoders may drop input frames in realtime mode. Bound timestamp tracking too.
        while (pending.size > 12) pending.delete(pending.keys().next().value);
        const keyFrame = forceKey || sequence % (qualities[preferences.quality].fps * 2) === 0;
        forceKey = false;
        encoder.encode(frame, { keyFrame });
    } catch (error) { reconnect('编码异常：' + errorText(error)); }
    finally { frame?.close(); }
}
async function begin() {
    if (starting || running || !wanted || photoBusy) return;
    starting = true; button.disabled = true; quality.disabled = true;
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
        const preset = qualities[preferences.quality];
        const media = await navigator.mediaDevices.getUserMedia({ audio: false, video: {
            facingMode: { ideal: 'environment' }, width: { ideal: preset.width }, height: { ideal: preset.height },
            frameRate: { ideal: preset.fps, max: preset.fps }
        }});
        if (gen !== generation || !wanted) { media.getTracks().forEach(t => t.stop()); return; }
        stream = media; camera.srcObject = media; await camera.play();
        await restoreCameraSettings(media.getVideoTracks()[0]);
        if (gen !== generation || !wanted) return;
        width = camera.videoWidth; height = camera.videoHeight;
        if (width * height !== preset.width * preset.height || Math.max(width, height) !== preset.width)
            throw new Error('相机实际输出 ' + width + '×' + height + '，未达到所选分辨率；可降低清晰度后重试');
        const config = { codec: preset.width > 1920 ? 'avc1.420033' : 'avc1.420028', width, height, bitrate: preset.bitrate, framerate: preset.fps,
            latencyMode: 'realtime', hardwareAcceleration: 'prefer-hardware', avc: { format: 'annexb' } };
        let supported = await VideoEncoder.isConfigSupported(config);
        if (!supported.supported) {
            config.hardwareAcceleration = 'no-preference';
            supported = await VideoEncoder.isConfigSupported(config);
        }
        if (!supported.supported) throw new Error('此 Safari 不支持所选分辨率的实时 H.264 编码');
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
        sequence = 0; streamId++; forceKey = true; discardUntilKey = false; colorFlags = 0;
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
        running = true; recovering = false;
        hdButton.disabled = false;
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
                '像素编码 ' + surface.width + '×' + surface.height + '\n' +
                (metrics.dataset.codec || '') + '\n' + navigator.userAgent;
            sent = 0; byteCount = 0; skips = 0; maxEncodeMs = 0; windowStart = now;
            if (!photoBusy && (connection.bufferedAmount > 262144 || now - lastOutput > 4000)) reconnect('视频暂时停滞');
        }, 1000);
    } catch (error) {
        if (gen !== generation) return;
        const retry = recovering && wanted && !['NotAllowedError', 'NotFoundError', 'NotSupportedError'].includes(error?.name);
        teardown(); setStatus(errorText(error));
        button.disabled = false; button.textContent = '重试连接'; button.className = '';
        if (retry) {
            setStatus('电脑暂时不可用，正在重新连接…');
            button.textContent = '停止拍摄'; button.className = 'stop';
            retryTimer = setTimeout(() => begin(), 3000);
        } else { wanted = false; recovering = false; }
    } finally { starting = false; quality.disabled = false; }
}
function showCameraSettings(track) {
    const c = track.getCapabilities?.() || {}, s = track.getSettings();
    document.querySelector('#capabilities').textContent = JSON.stringify({ capabilities: c, settings: s }, null, 2);
    focusMode.replaceChildren(new Option('自动对焦（系统默认）', 'auto'));
    if (adjustable(c.focusDistance)) focusMode.add(new Option('手动调焦', 'manual'));
    if (c.focusMode?.includes('none')) focusMode.add(new Option('锁定当前焦点', 'none'));
    focusMode.value = [...focusMode.options].some(o => o.value === preferences.focus) ? preferences.focus : 'auto';
    focusMode.disabled = !adjustable(c.focusDistance) && !c.focusMode?.length;
    document.querySelector('#refocus').disabled = false;
    document.querySelector('#focus-range').hidden = !adjustable(c.focusDistance) || focusMode.value !== 'manual';
    if (adjustable(c.focusDistance)) {
        focusDistance.min = c.focusDistance.min; focusDistance.max = c.focusDistance.max;
        focusDistance.step = c.focusDistance.step || (c.focusDistance.max - c.focusDistance.min) / 100;
        focusDistance.value = s.focusDistance ?? preferences.distance ?? c.focusDistance.min;
        document.querySelector('#focus-value').textContent = Number(focusDistance.value).toFixed(3);
    }
    document.querySelector('#focus-status').textContent = !adjustable(c.focusDistance) ?
        'Safari 未开放手动调焦。' + (s.focusMode ? '当前模式：' + s.focusMode : '自动对焦由系统管理，浏览器无法确认是否合焦。') :
        '实际模式：' + (s.focusMode ?? '浏览器未返回') + '；焦点值：' + (s.focusDistance ?? '未返回') + '。请放大纸面检查清晰度。';
    zoom.disabled = !adjustable(c.zoom);
    if (adjustable(c.zoom)) {
        zoom.min = c.zoom.min; zoom.max = c.zoom.max; zoom.step = c.zoom.step || .1;
        zoom.value = s.zoom ?? c.zoom.min;
        document.querySelector('#zoom-value').textContent = Number(zoom.value).toFixed(1) + '×';
    } else document.querySelector('#zoom-value').textContent = '未开放';
    document.querySelector('#zoom-status').textContent = adjustable(c.zoom) ?
        '控制采集端倍率；实际细节取决于手机选用的采集格式。' : 'Safari 未开放相机倍率。电脑端仍可裁剪，但不能增加细节。';
}
async function restoreCameraSettings(track) {
    const c = track.getCapabilities?.() || {};
    const changes = {};
    if (adjustable(c.zoom) && Number.isFinite(preferences.zoom)) changes.zoom = Math.min(c.zoom.max, Math.max(c.zoom.min, preferences.zoom));
    if (preferences.focus === 'manual' && adjustable(c.focusDistance) && Number.isFinite(preferences.distance)) {
        if (c.focusMode?.includes('manual')) changes.focusMode = 'manual';
        changes.focusDistance = Math.min(c.focusDistance.max, Math.max(c.focusDistance.min, preferences.distance));
    } else if (preferences.focus === 'auto' && c.focusMode?.includes('continuous')) changes.focusMode = 'continuous';
    else if (preferences.focus === 'none' && c.focusMode?.includes('none')) changes.focusMode = 'none';
    let restoreError;
    try { if (Object.keys(changes).length) await applyVerified(track, changes); }
    catch (error) { restoreError = '设置未确认生效：' + errorText(error); }
    showCameraSettings(track);
    if (restoreError) {
        if ('zoom' in changes) document.querySelector('#zoom-status').textContent += ' ' + restoreError;
        if ('focusMode' in changes || 'focusDistance' in changes) document.querySelector('#focus-status').textContent += ' ' + restoreError;
    }
}
async function changePreset(next) {
    if (!Object.hasOwn(qualities, next.quality) || !['auto','manual','none'].includes(next.focus)) throw new Error('未知相机预设');
    const previous = {...preferences};
    preferences = {...preferences, ...next}; quality.value = preferences.quality;
    teardown(); wanted = true; recovering = false; await begin();
    if (running) {
        // Validate actual encoder output, not only the advertised configuration.
        const until = performance.now() + 6000;
        while (running && sequence === 0 && performance.now() < until) await new Promise(r => setTimeout(r, 50));
    }
    if (!running || sequence === 0) {
        const reason = status.textContent;
        teardown(); preferences = previous; quality.value = previous.quality;
        wanted = true; recovering = false; await begin();
        throw new Error('切换未成功，已尝试恢复原档位：' + reason);
    }
    savePreferences();
}
quality.onchange = async () => {
    if (photoBusy || starting || commandBusy) { quality.value = preferences.quality; return; }
    if (!wanted) { preferences.quality=quality.value; savePreferences(); return; }
    commandBusy=true;
    try { await changePreset({...preferences,quality:quality.value}); }
    catch(error) { setStatus(errorText(error)); }
    finally { commandBusy=false; }
};
focusMode.onchange = async () => {
    const track = stream?.getVideoTracks()[0]; if (!track || photoBusy || starting || commandBusy) return;
    preferences.focus = focusMode.value; savePreferences();
    if (preferences.focus === 'auto') { teardown(); begin(); return; }
    showCameraSettings(track);
    if (preferences.focus === 'manual') { await focusDistance.onchange(); return; }
    if (preferences.focus === 'none') {
        try { await applyVerified(track, { focusMode: 'none' }); }
        catch (error) { document.querySelector('#focus-status').textContent = errorText(error); }
    }
};
focusDistance.oninput = () => { document.querySelector('#focus-value').textContent = Number(focusDistance.value).toFixed(3); };
focusDistance.onchange = async () => {
    const track = stream?.getVideoTracks()[0]; if (!track || photoBusy || starting || commandBusy) return;
    const changes = { focusDistance: Number(focusDistance.value) };
    if (track.getCapabilities().focusMode?.includes('manual')) changes.focusMode = 'manual';
    try { await applyVerified(track, changes); preferences.distance = changes.focusDistance; savePreferences(); showCameraSettings(track); }
    catch (error) { document.querySelector('#focus-status').textContent = errorText(error); }
};
document.querySelector('#refocus').onclick = () => {
    preferences.focus = 'auto'; preferences.distance = null; savePreferences();
    if (wanted && !photoBusy) { teardown(); begin(); }
};
let phoneZoom;
zoom.oninput = () => { document.querySelector('#zoom-value').textContent=Number(zoom.value).toFixed(1)+'×'; phoneZoom=Number(zoom.value); };
setInterval(async () => {
    if (phoneZoom === undefined || photoBusy || starting || commandBusy || !running) return;
    const value=phoneZoom; phoneZoom=undefined; commandBusy=true;
    try { await deadline(applyVerified(stream.getVideoTracks()[0],{zoom:value}),5000,'倍率设置超时'); preferences.zoom=value; savePreferences(); }
    catch(error) { document.querySelector('#zoom-status').textContent=errorText(error); }
    finally { commandBusy=false; }
},100);
async function uploadPhoto(blob, source) {
    photoStatus.textContent = '正在处理并传送原始照片…';
    const photo = await deadline(preparePhoto(blob), 12000, '图片处理超时');
    const response = await fetch('/api/photo?source=' + source, { method: 'POST', headers: { 'Content-Type': 'image/jpeg' }, body: photo.jpeg, signal: AbortSignal.timeout(15000) });
    if (!response.ok) throw new Error('照片上传失败：' + response.status);
    photoStatus.textContent = '已传到电脑：' + photo.width + '×' + photo.height +
        (photo.width * photo.height <= 1920 * 1080 ? '；照片像素未超过 1080p，可尝试“系统相机拍照”。' : '；电脑可放大、复制或保存。');
}
async function captureHD() {
    const track = stream?.getVideoTracks()[0]; if (!track || photoBusy || starting) throw new Error('请等待实时视频恢复后再抓拍');
    photoBusy = true; hdButton.disabled = quality.disabled = focusMode.disabled = focusDistance.disabled = zoom.disabled = true;
    if (camera.videoWidth * camera.videoHeight > 1920 * 1080) {
        try { await uploadPhoto(await highResolutionPhoto(camera), 'hd-frame'); }
        catch(error) { photoStatus.textContent='抓拍失败：'+errorText(error); throw error; }
        finally { photoBusy=false; hdButton.disabled=quality.disabled=focusDistance.disabled=false; showCameraSettings(track); lastOutput=performance.now(); forceKey=true; }
        return;
    }
    photoStatus.textContent = '正在切换高分辨率采集，实时视频会暂时暂停…';
    const deviceId = track.getSettings().deviceId;
    teardown();
    const gen = generation;
    try {
        const request = navigator.mediaDevices.getUserMedia({ audio: false, video: {
            ...(deviceId ? { deviceId: { exact: deviceId } } : { facingMode: { ideal: 'environment' } }),
            width: { ideal: 3840 }, height: { ideal: 2160 }, frameRate: { ideal: 15, max: 15 }
        }}).then(media => {
            if (gen !== generation) { media.getTracks().forEach(t => t.stop()); throw new Error('抓拍已取消'); }
            return media;
        });
        stream = await deadline(request, 12000, '高分辨率相机打开超时');
        camera.srcObject = stream;
        await deadline(camera.play(), 5000, '高清画面未就绪');
        const hdTrack = stream.getVideoTracks()[0];
        const caps = hdTrack.getCapabilities?.() || {}, changes = {};
        if (adjustable(caps.zoom) && Number.isFinite(preferences.zoom)) changes.zoom = preferences.zoom;
        if (preferences.focus === 'manual' && adjustable(caps.focusDistance) && Number.isFinite(preferences.distance)) {
            changes.focusDistance = preferences.distance;
            if (caps.focusMode?.includes('manual')) changes.focusMode = 'manual';
        }
        if (Object.keys(changes).length) await deadline(applyVerified(hdTrack, changes), 5000, '抓拍相机设置超时');
        await uploadPhoto(await highResolutionPhoto(camera), 'hd-frame');
    }
    catch (error) { photoStatus.textContent = '抓拍失败，未生成新照片：' + errorText(error) + '。可在手机使用“系统相机拍照”。'; throw error; }
    finally {
        teardown();
        photoBusy = false; hdButton.disabled = !running; quality.disabled = starting; focusDistance.disabled = false;
        lastOutput = performance.now(); forceKey = true;
        if (wanted) await begin();
    }
}
hdButton.onclick = () => captureHD().catch(() => {});
document.querySelector('#system-photo').onclick = () => {
    if (photoBusy || starting || commandBusy) return;
    photoStatus.textContent = '请用系统相机拍照并确认；取消后恢复实时画面。';
    photoBusy = systemPicker = true; teardown(); photoFile.value = ''; photoFile.click();
};
async function finishSystemPhoto() {
    if (!systemPicker) return;
    systemPicker = false;
    try { if (photoFile.files?.[0]) await uploadPhoto(photoFile.files[0], 'system-camera'); }
    catch (error) { photoStatus.textContent = errorText(error); }
    finally { if (!photoFile.files?.[0]) photoStatus.textContent = '已取消拍照，未生成新照片'; photoBusy = false; if (wanted) begin(); }
}
photoFile.onchange = finishSystemPhoto;
photoFile.addEventListener('cancel', finishSystemPhoto);
button.onclick = () => {
    if (wanted || running) stop();
    else { wanted = true; begin(); }
};
document.addEventListener('visibilitychange', () => {
    if (photoBusy) return;
    if (document.hidden) {
        const resume = wanted;
        teardown(); wanted = resume;
        setStatus('已暂停；回到 DeskCam 后恢复');
    } else if (wanted) begin();
});
window.addEventListener('pagehide', () => teardown());
window.addEventListener('hashchange', () => { if (location.hash.includes('session=')) { teardown(); init(); } });
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
        pairedReady = true;
        button.disabled = false;
        wanted = true; await begin();
    } catch (error) { setStatus(errorText(error)); button.disabled = false; }
}
init();

function cameraState() {
    const track = stream?.getVideoTracks()[0], s = track?.getSettings() || {}, c = track?.getCapabilities?.() || {};
    return { quality: preferences.quality, focus: preferences.focus, busy: photoBusy || starting || commandBusy,
        acquiring: photoBusy || starting, ready: running, zoomRange: c.zoom || null, focusRange: c.focusDistance || null, canLock: c.focusMode?.includes('none') || false,
        settings: { width:s.width,height:s.height,zoom:s.zoom,focusDistance:s.focusDistance,focusMode:s.focusMode },
        photoStatus: photoStatus.textContent };
}
async function executeCommand(command) {
    commandBusy = true;
    try {
        if (photoBusy || starting || !running) throw new Error('手机忙碌或视频未开始，请稍后重试');
        const track = stream.getVideoTracks()[0], caps = track.getCapabilities?.() || {};
        if (command.kind === 'photo') await captureHD();
        else if (command.kind === 'quality' || command.kind === 'preset') {
            const next = command.kind === 'quality' ? {...preferences,quality:command.text} : JSON.parse(command.text);
            for (const key of ['zoom','distance']) if (next[key] != null && !Number.isFinite(next[key])) throw new Error('预设参数无效');
            await changePreset(next);
            if (command.kind === 'preset') {
                const current=stream.getVideoTracks()[0], c=current.getCapabilities?.()||{}, changes={};
                if (next.zoom != null) changes.zoom=next.zoom;
                if (next.focus === 'manual' && next.distance != null) changes.focusDistance=next.distance;
                if (next.focus === 'manual' && c.focusMode?.includes('manual')) changes.focusMode='manual';
                if (next.focus === 'none') changes.focusMode='none';
                if (Object.keys(changes).length) await deadline(applyVerified(current,changes),5000,'预设设置超时');
            }
        } else if (command.kind === 'refocus' || command.kind === 'focus' && command.text === 'auto') {
            preferences.focus = 'auto'; preferences.distance = null; savePreferences(); teardown(); await begin();
            if (!running) throw new Error(status.textContent);
            const current = stream.getVideoTracks()[0].getSettings().focusMode;
            if (current && current !== 'continuous' && current !== 'single-shot') throw new Error('手机实际对焦模式仍为 ' + current + '，未确认自动模式生效');
        } else {
            const changes = {};
            if (command.kind === 'zoom') {
                if (!adjustable(caps.zoom) || command.value < caps.zoom.min || command.value > caps.zoom.max) throw new Error('倍率不在手机支持范围');
                changes.zoom = command.value;
            } else if (command.kind === 'distance' || command.kind === 'focus' && command.text === 'manual') {
                if (!adjustable(caps.focusDistance)) throw new Error('手机未开放手动调焦');
                changes.focusDistance = command.kind === 'distance' ? command.value : track.getSettings().focusDistance ?? caps.focusDistance.min;
                if (caps.focusMode?.includes('manual')) changes.focusMode = 'manual';
            } else if (command.kind === 'focus' && command.text === 'none' && caps.focusMode?.includes('none')) changes.focusMode = 'none';
            else throw new Error('手机不支持此项操作');
            await deadline(applyVerified(track, changes), 5000, '相机设置超时');
            if ('zoom' in changes) preferences.zoom = changes.zoom;
            if ('focusDistance' in changes) { preferences.focus = 'manual'; preferences.distance = changes.focusDistance; }
            if (changes.focusMode === 'none') preferences.focus = 'none';
            savePreferences(); showCameraSettings(track);
        }
        commandResult = command.kind === 'photo' ? photoStatus.textContent : '设置已应用；上方显示手机返回的实际值';
    } catch (error) { commandResult = '操作失败：' + errorText(error); }
    finally { commandAck = command.id; commandBusy = false; }
}
async function syncCamera() {
    if (!pairedReady || document.hidden || syncBusy) return;
    syncBusy = true;
    const ack = commandAck;
    try {
        const response = await fetch('/api/camera/sync', { method:'POST', headers:{'Content-Type':'application/json'},
            body:JSON.stringify({state:cameraState(),ack,message:commandResult}), signal:AbortSignal.timeout(3000) });
        if (!response.ok) return;
        const {command} = await response.json();
        if (commandAck === ack) commandAck = 0;
        if (command) {
            if (commandBusy) { commandAck = command.id; commandResult = '操作失败：手机仍在处理上一项操作'; }
            else executeCommand(command);
        }
    } catch {} finally { syncBusy = false; }
}
setInterval(syncCamera, 100);

const shade=document.querySelector('#stand-shade');
document.querySelector('#stand-mode').onclick=()=>{shade.hidden=false;};
shade.onclick=()=>{shade.hidden=true;};
