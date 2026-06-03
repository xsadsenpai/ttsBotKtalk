# Changelog

## 2026-06-03

### Added
- Silero TTS sidecar defaults to cached Russian `v5_5_ru` model with automatic first-run download.
- Configurable TTS model, voice, sample rate, accent/yo normalization, speech rate and timeout.
- Validation for bot, browser and TTS configuration at application startup.
- Bounded single-reader TTS queue and sidecar-level synthesis lock to prevent concurrent audio generation.
- Docker and Compose examples with persistent model cache and explicit environment variables.
- Production `.gitignore`.
- Production `.dockerignore` to keep local build artifacts out of Docker context.
- Render.com environment defaults for Silero model, voice and sample rate.

### Changed
- Removed obsolete explicit ASP.NET Core SignalR package and redundant Channels package references.
- Updated Python sidecar dependencies to newer stable FastAPI/Uvicorn/Pydantic versions.
- Aligned Docker/Render environment variable names with .NET configuration binding while keeping legacy aliases.
- Updated README to describe the actual WebSocket chat listener plus Playwright WebRTC audio injection flow.
- Made Docker publish more deterministic by restoring packages after source copy.

### Fixed
- `TTS_SIDECAR_URL` no longer silently fails to configure the .NET TTS client.
- Start API now returns the current bot state on conflicts/errors instead of always reporting "already running".
- TTS sidecar now rejects unsupported model switches at runtime instead of mixing model state.
- Render logs now include .NET bot and TTS sidecar stdout/stderr instead of only supervisor lifecycle messages.
- Playwright installs TTS microphone injection before the Tolk page loads, so WebRTC can capture the generated audio stream.
- Browser diagnostics now log page console errors, failed requests, 4xx/5xx responses and DOM snapshots for Render troubleshooting.
