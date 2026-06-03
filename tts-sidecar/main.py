"""
FastAPI sidecar for Russian Silero TTS.

The bot process keeps chat/WebRTC responsibilities in .NET, while this service
owns CPU-heavy speech synthesis, model download/cache and synthesis throttling.
"""
from __future__ import annotations

import asyncio
import io
import logging
import os
import struct
import time
from pathlib import Path
from typing import Final

import numpy as np
import torch
from fastapi import FastAPI, HTTPException
from fastapi.responses import Response
from pydantic import BaseModel, Field

logging.basicConfig(level=os.getenv("LOG_LEVEL", "INFO"), format="%(asctime)s [%(levelname)s] %(message)s")
log = logging.getLogger("silero")

MODEL_DIR: Final = Path(os.getenv("MODEL_DIR", "/app/models"))
DEFAULT_MODEL_ID: Final = os.getenv("TTS_MODEL_ID", "v5_5_ru")
DEFAULT_VOICE: Final = os.getenv("TTS_VOICE", "xenia")
DEFAULT_SAMPLE_RATE: Final = int(os.getenv("TTS_SAMPLE_RATE", "48000"))
TORCH_THREADS: Final = int(os.getenv("TORCH_THREADS", "2"))

MODEL_URLS: Final = {
    "v5_5_ru": "https://models.silero.ai/models/tts/ru/v5_5_ru.pt",
    "v5_ru": "https://models.silero.ai/models/tts/ru/v5_ru.pt",
    "v4_ru": "https://models.silero.ai/models/tts/ru/v4_ru.pt",
    "v3_1_ru": "https://models.silero.ai/models/tts/ru/v3_1_ru.pt",
}

app = FastAPI(title="Silero Russian TTS Sidecar", version="2.0")
model = None
model_id = DEFAULT_MODEL_ID
speakers: list[str] = []
synthesis_lock = asyncio.Lock()


class SynthRequest(BaseModel):
    text: str = Field(min_length=1, max_length=1000)
    model_id: str = DEFAULT_MODEL_ID
    voice: str = DEFAULT_VOICE
    sample_rate: int = Field(default=DEFAULT_SAMPLE_RATE, ge=8000, le=48000)
    put_accent: bool = True
    put_yo: bool = True
    speech_rate: float = Field(default=1.0, ge=0.5, le=2.0)


@app.on_event("startup")
async def startup() -> None:
    torch.set_num_threads(max(1, TORCH_THREADS))
    await asyncio.to_thread(load_model, DEFAULT_MODEL_ID)


def load_model(requested_model_id: str) -> None:
    global model, model_id, speakers

    if requested_model_id not in MODEL_URLS:
        raise RuntimeError(f"Unsupported Silero model_id: {requested_model_id}")

    path = MODEL_DIR / f"{requested_model_id}.pt"
    if not path.exists():
        log.info("Downloading Silero model %s to %s", requested_model_id, path)
        MODEL_DIR.mkdir(parents=True, exist_ok=True)
        torch.hub.download_url_to_file(MODEL_URLS[requested_model_id], str(path), progress=True)

    log.info("Loading Silero model %s", requested_model_id)
    started = time.perf_counter()
    loaded_model = torch.package.PackageImporter(str(path)).load_pickle("tts_models", "model")
    loaded_model.to("cpu")

    model = loaded_model
    model_id = requested_model_id
    speakers = list(getattr(model, "speakers", []) or [DEFAULT_VOICE])
    log.info("Silero %s ready in %.1fs; voices=%s", model_id, time.perf_counter() - started, speakers)


@app.get("/health")
async def health() -> dict:
    return {
        "status": "ok" if model is not None else "loading",
        "model_loaded": model is not None,
        "model_id": model_id,
        "voices": speakers,
    }


@app.get("/voices")
async def voices() -> dict:
    return {"voices": speakers, "current": DEFAULT_VOICE, "model_id": model_id}


@app.post("/synthesize")
async def synthesize(req: SynthRequest) -> Response:
    if model is None:
        raise HTTPException(503, "Model is not loaded")
    if req.model_id != model_id:
        raise HTTPException(400, f"Loaded model is {model_id}; restart sidecar to use {req.model_id}")

    text = req.text.strip()
    if not text:
        raise HTTPException(400, "Text is empty")
    if req.sample_rate not in (8000, 24000, 48000):
        raise HTTPException(400, "sample_rate must be one of 8000, 24000, 48000")

    voice = req.voice if req.voice in speakers else DEFAULT_VOICE
    started = time.perf_counter()
    log.info("Synthesizing voice=%s sr=%s rate=%.2f text=%r", voice, req.sample_rate, req.speech_rate, text[:80])

    async with synthesis_lock:
        try:
            wav = await asyncio.to_thread(render_wav, text, voice, req)
        except Exception as exc:
            log.exception("Synthesis failed")
            raise HTTPException(500, str(exc)) from exc

    log.info("Synthesis done in %.2fs; bytes=%s", time.perf_counter() - started, len(wav))
    return Response(content=wav, media_type="audio/wav")


def render_wav(text: str, voice: str, req: SynthRequest) -> bytes:
    with torch.inference_mode():
        audio = model.apply_tts(
            text=text,
            speaker=voice,
            sample_rate=req.sample_rate,
            put_accent=req.put_accent,
            put_yo=req.put_yo,
        )

    samples = audio.detach().cpu().numpy() if isinstance(audio, torch.Tensor) else np.asarray(audio)
    samples = apply_speech_rate(samples.astype(np.float32, copy=False), req.speech_rate)
    return to_wav(samples, req.sample_rate)


def apply_speech_rate(audio: np.ndarray, speech_rate: float) -> np.ndarray:
    if abs(speech_rate - 1.0) < 0.01 or audio.size < 2:
        return audio

    target_len = max(1, int(audio.size / speech_rate))
    x_old = np.linspace(0.0, 1.0, num=audio.size, dtype=np.float32)
    x_new = np.linspace(0.0, 1.0, num=target_len, dtype=np.float32)
    return np.interp(x_new, x_old, audio).astype(np.float32)


def to_wav(audio: np.ndarray, sample_rate: int) -> bytes:
    pcm = (np.clip(audio, -1.0, 1.0) * 32767).astype(np.int16)
    channels, bits_per_sample = 1, 16
    data_size = len(pcm) * channels * bits_per_sample // 8

    buf = io.BytesIO()
    buf.write(b"RIFF")
    buf.write(struct.pack("<I", 36 + data_size))
    buf.write(b"WAVE")
    buf.write(b"fmt ")
    buf.write(struct.pack("<I", 16))
    buf.write(struct.pack("<H", 1))
    buf.write(struct.pack("<H", channels))
    buf.write(struct.pack("<I", sample_rate))
    buf.write(struct.pack("<I", sample_rate * channels * bits_per_sample // 8))
    buf.write(struct.pack("<H", channels * bits_per_sample // 8))
    buf.write(struct.pack("<H", bits_per_sample))
    buf.write(b"data")
    buf.write(struct.pack("<I", data_size))
    buf.write(pcm.tobytes())
    return buf.getvalue()


if __name__ == "__main__":
    import uvicorn

    uvicorn.run(app, host="0.0.0.0", port=8765, log_level="info")
