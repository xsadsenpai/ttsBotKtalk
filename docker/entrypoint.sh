#!/bin/bash
set -e

echo "=== TolkTtsBot entrypoint ==="

# ── PulseAudio null-sink (виртуальное аудио) ──────────────────
export PULSE_RUNTIME_PATH=/run/pulse
mkdir -p "$PULSE_RUNTIME_PATH"

# Запускаем PulseAudio только если доступен
if command -v pulseaudio &> /dev/null; then
    pulseaudio \
        --start \
        --daemon \
        --system=false \
        --exit-idle-time=-1 \
        --disallow-exit \
        --log-level=warn \
        --load="module-null-sink sink_name=virtual_speaker" \
        --load="module-loopback source=virtual_speaker.monitor" \
        2>/dev/null || echo "PulseAudio: запуск не удался (не критично)"
    sleep 1
fi

# ── Chromium: проверяем наличие ───────────────────────────────
echo "Chromium: $(which chromium 2>/dev/null || echo 'не найден — используется Playwright встроенный')"

# ── Запуск supervisor ─────────────────────────────────────────
echo "Запуск supervisor..."
exec /usr/bin/supervisord -c /etc/supervisor/conf.d/supervisord.conf
