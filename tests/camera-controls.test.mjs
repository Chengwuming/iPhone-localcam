import { test } from 'node:test';
import assert from 'node:assert/strict';
import { adjustable, applyVerified, preparePhoto, deadline, highResolutionPhoto } from '../src/LocalCam.Server/Web/camera-controls.mjs';

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


test('camera operations time out instead of hanging controls', async () => {
    await assert.rejects(deadline(new Promise(() => {}), 10, 'timed out'), /timed out/);
});
test('HD frame rejects 1080p and keeps native 4K dimensions', async () => {
    globalThis.document = { createElement: () => ({ getContext: () => ({ drawImage() {} }), toBlob: cb => cb('jpeg') }) };
    const video = { videoWidth:1920, videoHeight:1080, requestVideoFrameCallback: cb => setTimeout(cb, 460), cancelVideoFrameCallback: clearTimeout };
    await assert.rejects(highResolutionPhoto(video), /未达到/);
    video.videoWidth = 3840; video.videoHeight = 2160;
    assert.equal(await highResolutionPhoto(video), 'jpeg');
});
