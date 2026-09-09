import { test } from 'node:test';
import assert from 'node:assert/strict';
import { adjustable, applyVerified, takePhoto, preparePhoto } from '../src/LocalCam.Server/Web/camera-controls.mjs';

test('ignored zoom and missing focus readback are not reported as working', async () => {
    const track = { applyConstraints: async () => {}, getSettings: () => ({ zoom: 1 }), getCapabilities: () => ({}) };
    await assert.rejects(applyVerified(track, { zoom: 2 }), /未采用/);
    await assert.rejects(applyVerified(track, { focusDistance: .5 }), /无法确认/);
    track.getCapabilities = () => ({ zoom: { step: 1 } });
    await assert.rejects(applyVerified(track, { zoom: 2 }), /未采用/);
    assert.equal(!!adjustable({ min: 1, max: 1 }), false);
});
test('manual focus works without a focusMode capability when distance is applied', async () => {
    let state = {};
    const track = { applyConstraints: async c => { state = c.advanced[0]; }, getSettings: () => state,
        getCapabilities: () => ({ focusDistance: { min: 0, max: 1, step: .01 } }) };
    assert.equal((await applyVerified(track, { focusDistance: .4 })).focusDistance, .4);
});
test('photo requests maximum still size and retries only unsupported size options', async () => {
    const calls = [];
    globalThis.ImageCapture = class {
        async getPhotoCapabilities() { return { imageWidth: { max: 4000 }, imageHeight: { max: 3000 } }; }
        async takePhoto(options) { calls.push(options); if (options) throw Object.assign(new Error(), { name: 'NotSupportedError' }); return 'photo'; }
    };
    assert.equal(await takePhoto({}), 'photo');
    assert.deepEqual(calls, [{ imageWidth: 4000, imageHeight: 3000 }, undefined]);
    delete globalThis.ImageCapture;
    await assert.rejects(takePhoto({}), /系统相机/);
});
test('photo normalization never invents resolution and bounds large images', async () => {
    let closed = 0;
    globalThis.document = { createElement: () => ({ getContext: () => ({ drawImage() {} }), toBlob: cb => cb('jpeg') }) };
    for (const [width, height] of [[1920,1080], [3000,4000], [8000,6000]]) {
        globalThis.createImageBitmap = async () => ({ width, height, close() { closed++; } });
        const photo = await preparePhoto({});
        assert.ok(photo.width <= width && photo.height <= height && photo.width * photo.height <= 16000000);
        if (width * height <= 16000000) assert.deepEqual([photo.width, photo.height], [width, height]);
    }
    assert.equal(closed, 3);
});
