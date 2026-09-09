// Rasterize the presented camera image. Camera VideoFrames may carry rotation
// metadata over a landscape pixel buffer; Annex B does not transport that metadata.
// A canvas produces upright pixels with a matching coded/display size and square pixels.
export function cameraFrame(source, context, width, height, timestamp) {
    const canvas = context.canvas;
    if (canvas.width !== width) canvas.width = width;
    if (canvas.height !== height) canvas.height = height;
    context.drawImage(source, 0, 0, width, height);
    return new VideoFrame(canvas, { timestamp });
}
