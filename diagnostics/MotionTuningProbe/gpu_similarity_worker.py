#!/usr/bin/env python3
"""Persistent PyTorch/CUDA worker for InnerTune's diagnostic chorus tuner."""

import struct
import sys

import numpy as np
import torch


def read_exact(size: int) -> bytes:
    chunks = bytearray()
    while len(chunks) < size:
        chunk = sys.stdin.buffer.read(size - len(chunks))
        if not chunk:
            raise EOFError
        chunks.extend(chunk)
    return bytes(chunks)


if not torch.cuda.is_available():
    raise RuntimeError("PyTorch cannot access CUDA")

device = torch.device("cuda")
torch.empty((32, 32), device=device).square_()
torch.cuda.synchronize()
name = f"PyTorch CUDA / {torch.cuda.get_device_name(device)}".encode("utf-8")
sys.stdout.buffer.write(struct.pack("<i", len(name)))
sys.stdout.buffer.write(name)
sys.stdout.buffer.flush()

while True:
    try:
        rows, bands = struct.unpack("<ii", read_exact(8))
    except EOFError:
        break
    if rows == 0 or bands == 0:
        break

    raw = read_exact(rows * bands * 4)
    host_features = np.frombuffer(raw, dtype="<f4").reshape(rows, bands)
    features = torch.from_numpy(host_features.copy()).to(device, non_blocking=False)
    pairwise = features @ features.T
    output = pairwise.to("cpu").contiguous().numpy().astype("<f4", copy=False)
    sys.stdout.buffer.write(struct.pack("<i", output.size))
    sys.stdout.buffer.write(output.tobytes(order="C"))
    sys.stdout.buffer.flush()
