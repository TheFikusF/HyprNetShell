#!/usr/bin/env python3
"""Exercise the native QR ABI. Requires qrcode only as a test-time generator.

Run after ./build.sh native: python3 scripts/test-qr.py
"""
import ctypes
from pathlib import Path

import qrcode

ROOT = Path(__file__).resolve().parent.parent
LIBRARY = ctypes.CDLL(str(ROOT / "Native/build/libhypr_qr.so"))
LIBRARY.hypr_qr_scan.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_int]
LIBRARY.hypr_qr_scan.restype = ctypes.c_void_p
LIBRARY.hypr_qr_destroy.argtypes = [ctypes.c_void_p]
LIBRARY.hypr_qr_count.argtypes = [ctypes.c_void_p]
LIBRARY.hypr_qr_decode.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_void_p,
                                 ctypes.c_int, ctypes.POINTER(ctypes.c_uint32),
                                 ctypes.POINTER(ctypes.c_int)]


def matrix(text):
    code = qrcode.QRCode(border=4)
    code.add_data(text.encode("utf-8"))
    code.make(fit=True)
    return code.get_matrix()


def scan(codes, mirrored=False):
    scale = 6
    width = (sum(len(code) for code in codes) + 8) * scale
    height = (max((len(code) for code in codes), default=21) + 8) * scale
    pixels = bytearray([255]) * (width * height)
    offset = 4
    for code in codes:
        for y, row in enumerate(code):
            for x, black in enumerate(row):
                if black:
                    for dy in range(scale):
                        start = ((y + 4) * scale + dy) * width + (offset + x) * scale
                        pixels[start:start + scale] = bytes(scale)
        offset += len(code)
    if mirrored:
        pixels = bytearray().join(pixels[y:y + width][::-1]
                                  for y in range(0, len(pixels), width))
    buffer = (ctypes.c_uint8 * len(pixels)).from_buffer(pixels)
    scanner = LIBRARY.hypr_qr_scan(buffer, width, height)
    assert scanner
    try:
        output = ctypes.create_string_buffer(8896)
        eci, kind = ctypes.c_uint32(), ctypes.c_int()
        assert LIBRARY.hypr_qr_decode(scanner, -1, output, 8896,
                                     ctypes.byref(eci), ctypes.byref(kind)) == -1
        results = []
        for index in range(LIBRARY.hypr_qr_count(scanner)):
            assert LIBRARY.hypr_qr_decode(scanner, index, output, 1,
                                         ctypes.byref(eci), ctypes.byref(kind)) == -1
            length = LIBRARY.hypr_qr_decode(scanner, index, output, 8896,
                                           ctypes.byref(eci), ctypes.byref(kind))
            assert length >= 0
            if length:
                results.append(output.raw[:length].decode("utf-8"))
        return sorted(results)
    finally:
        LIBRARY.hypr_qr_destroy(scanner)


assert not LIBRARY.hypr_qr_scan(None, 1, 1)
assert not LIBRARY.hypr_qr_scan(None, 2147483647, 2)
assert scan([]) == []
for text in ("https://example.org/qr-test", "QR test: café 日本語"):
    assert scan([matrix(text)]) == [text]
    assert scan([matrix(text)], mirrored=True) == [text]
assert scan([matrix("first"), matrix("second")]) == ["first", "second"]
print("QR ABI tests passed: ASCII, UTF-8, mirrored, multiple, blank, invalid arguments")
