export const qualities = {
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
        const tolerance = Math.max(Number(capabilities[key]?.step) || 0, .01);
        if (typeof value === 'number' ? Math.abs(actual[key] - value) > tolerance : actual[key] !== value)
            throw new Error('相机未采用请求的 ' + key + '，实际值为 ' + actual[key]);
    }
    return actual;
}
export async function takePhoto(track) {
    if (typeof ImageCapture === 'undefined' || !ImageCapture.prototype.takePhoto)
        throw new Error('此浏览器未提供照片拍摄接口，请点“系统相机拍照”');
    const capture = new ImageCapture(track);
    let options = {};
    if (capture.getPhotoCapabilities) {
        try {
            const c = await capture.getPhotoCapabilities();
            if (c.imageWidth?.max > 0 && c.imageWidth.max <= 8192) options.imageWidth = c.imageWidth.max;
            if (c.imageHeight?.max > 0 && c.imageHeight.max <= 8192) options.imageHeight = c.imageHeight.max;
        } catch { /* takePhoto may be supported without photo capabilities. */ }
    }
    try { return await capture.takePhoto(options); }
    catch (error) {
        if (Object.keys(options).length && ['OverconstrainedError', 'NotSupportedError'].includes(error.name)) return capture.takePhoto();
        throw error;
    }
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
