export const qualities = {
    smooth: { label: '流畅 1080p · 30 fps', width: 1920, height: 1080, fps: 30, bitrate: 16000000 },
    ultra: { label: '连续高清截图 4K · 10 fps', width: 3840, height: 2160, fps: 10, bitrate: 20000000 },
    economy: { label: '省流 720p · 20 fps', width: 1280, height: 720, fps: 20, bitrate: 4000000 },
    balanced: { label: '标准 1080p · 20 fps', width: 1920, height: 1080, fps: 20, bitrate: 8000000 },
    paper: { label: '纸面清晰 1080p · 15 fps', width: 1920, height: 1080, fps: 15, bitrate: 12000000 }
};
export function adjustable(range) {
    return range && Number.isFinite(range.min) && Number.isFinite(range.max) && range.max > range.min;
}
export async function applyVerified(track, changes) {
    await track.applyConstraints({ advanced: [changes] });
    const actual = track.getSettings(), capabilities = track.getCapabilities?.() || {};
    for (const [key, value] of Object.entries(changes)) {
        if (actual[key] === undefined) throw new Error('浏览器没有返回 ' + key + ' 的实际值，无法确认生效');
        const tolerance = Math.max((Number(capabilities[key]?.step) || 0) / 2, .0001);
        if (typeof value === 'number' ? Math.abs(actual[key] - value) > tolerance : actual[key] !== value)
            throw new Error('相机未采用请求的 ' + key + '，实际值为 ' + actual[key]);
    }
    return actual;
}
export async function preparePhoto(blob) {
    // Rasterization applies EXIF orientation and strips metadata. Keep up to 16 MP
    // to bound phone/desktop memory; never upscale a lower-resolution photo.
    const image = await createImageBitmap(blob, { imageOrientation: 'from-image' });
    try {
        const scale = Math.min(1, 8192 / image.width, 8192 / image.height, Math.sqrt(16000000 / (image.width * image.height)));
        const canvas = document.createElement('canvas');
        canvas.width = Math.max(1, Math.floor(image.width * scale)); canvas.height = Math.max(1, Math.floor(image.height * scale));
        canvas.getContext('2d', { alpha: false }).drawImage(image, 0, 0, canvas.width, canvas.height);
        const jpeg = await new Promise(resolve => canvas.toBlob(resolve, 'image/jpeg', .97));
        if (!jpeg) throw new Error('照片转换失败');
        return { jpeg, width: canvas.width, height: canvas.height };
    } finally { image.close(); }
}

export async function deadline(promise, milliseconds, message) {
    let timer;
    try { return await Promise.race([promise, new Promise((_, reject) => { timer = setTimeout(() => reject(new Error(message)), milliseconds); })]); }
    finally { clearTimeout(timer); }
}
export async function highResolutionPhoto(video) {
    // Wait for fresh frames after format/zoom changes, then take a native-resolution raster.
    let callback;
    try {
        await deadline(new Promise(resolve => {
            const start = performance.now();
            const next = () => {
                if (performance.now() - start >= 450) resolve();
                else callback = video.requestVideoFrameCallback(next);
            };
            callback = video.requestVideoFrameCallback(next);
        }), 8000, '高分辨率相机没有返回新画面');
    } finally { if (callback) video.cancelVideoFrameCallback(callback); }
    const width = video.videoWidth, height = video.videoHeight;
    if (width * height <= 1920 * 1080) throw new Error('相机仅返回 ' + width + '×' + height + '，未达到高清抓拍分辨率');
    if (width * height > 16000000) throw new Error('相机返回的画面超过抓拍尺寸限制');
    const canvas = document.createElement('canvas'); canvas.width = width; canvas.height = height;
    canvas.getContext('2d', {alpha:false}).drawImage(video,0,0,width,height);
    const blob = await deadline(new Promise(resolve => canvas.toBlob(resolve,'image/jpeg',.97)), 8000, '高清图片生成超时');
    if (!blob) throw new Error('高清图片生成失败');
    return blob;
}

export function focusLockChanges(track) {
    const caps=track.getCapabilities?.() || {}, actual=track.getSettings();
    if (caps.focusMode?.includes('none')) return {focusMode:'none'};
    if (caps.focusMode?.includes('manual') && Number.isFinite(actual.focusDistance))
        return {focusMode:'manual',focusDistance:actual.focusDistance};
    throw new Error('Safari 未开放锁焦；可以使用冻结预览固定截图画面');
}
export function canLockFocus(track) {
    if (!track) return false;
    try { focusLockChanges(track); return true; } catch { return false; }
}
