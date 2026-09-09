"""Generate a deterministic Annex B H.264 stream in DeskCam wire packets. Requires PyAV + numpy."""
from pathlib import Path
from fractions import Fraction
import struct
import av
import numpy as np

root = Path(__file__).parent
width, height = 1920, 1080
codec = av.CodecContext.create("libx264", "w")
codec.width, codec.height = width, height
codec.pix_fmt = "yuv420p"
codec.time_base = Fraction(1, 20)
codec.framerate = Fraction(20, 1)
codec.bit_rate = 8_000_000
codec.options = {"preset": "veryfast", "tune": "zerolatency", "profile": "baseline",
                 "x264-params": "keyint=20:min-keyint=20:scenecut=0:repeat-headers=1:colorprim=bt709:transfer=bt709:colormatrix=bt709"}
sequence = 0
with (root / "deskcam-test.dcv").open("wb") as target:
    for index in range(40):
        rgb = np.zeros((height, width, 3), np.uint8)
        rgb[:height//2, :width//2] = 40
        rgb[:height//2, width//2:] = 100
        rgb[height//2:, :width//2] = 160
        rgb[height//2:, width//2:] = 220
        rgb[10:60, 20 + index * 15:30 + index * 15] = 255
        frame = av.VideoFrame.from_ndarray(rgb, format="rgb24")
        frame.pts = index
        for packet in codec.encode(frame):
            data = bytes(packet)
            target.write(struct.pack("<IHHIIqII", 0x31564344, width, height, 1, sequence,
                                     round(float(packet.pts * packet.time_base) * 1e6), int(packet.is_keyframe), len(data)))
            target.write(data)
            sequence += 1
    assert not codec.encode(None), "zerolatency encoder unexpectedly buffered frames"
print(f"Generated {sequence} frames in {root / 'deskcam-test.dcv'}")
