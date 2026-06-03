# ttsTalk — TTS-бот для Контур.Толк

Бот подключается к комнате Контур.Толк как гость, слушает чат по WebSocket-протоколу Толка и озвучивает сообщения, начинающиеся с команды `/tts`. Аудио воспроизводится в комнате через Playwright/Chromium и Web Audio API.

## Архитектура

```text
Контур.Толк room URL
        │
        ▼
ASP.NET Core API/UI
        │
        ├─ TolkChatService
        │    ├─ POST /api/authorize/session
        │    └─ WSS /system/ws: connect/auth/chat_join/chat_message
        │
        ├─ BotOrchestrator
        │    ├─ фильтр команды /tts
        │    ├─ антиспам и ограничение длины
        │    └─ bounded Channel<TtsQueueItem>, single reader
        │
        ├─ SileroTtsService
        │    └─ HTTP POST tts-sidecar /synthesize
        │
        └─ PlaywrightBrowserService
             ├─ гостевой вход в комнату
             └─ Web Audio API -> MediaStreamDestination -> WebRTC microphone stream
```

Слои проекта:

- `Controllers` и `Hubs` — внешний API, UI-события и SignalR-статусы.
- `Services/BotOrchestrator.cs` — сценарий работы бота, очередь, lifecycle и reconnect loop.
- `Services/TolkChatService.cs` — интеграция с протоколом Контур.Толк.
- `Services/BrowserService.cs` — браузерный вход и аудио-инжекция.
- `Services/TtsService.cs` — HTTP-клиент к TTS sidecar.
- `tts-sidecar/main.py` — FastAPI + Silero TTS, кэш модели и защита от параллельного синтеза.

## Команда

```text
/tts Добро пожаловать на встречу
```

Бот озвучит:

```text
Имя отправителя сказал: Добро пожаловать на встречу
```

Обычные сообщения без `/tts`, пустые команды и собственные сообщения бота игнорируются.

## Конфигурация

Основной файл: `src/TolkTtsBot/appsettings.json`.

```json
{
  "Bot": {
    "Name": "TTS Бот",
    "TtsCommand": "/tts",
    "MaxMessageLength": 300,
    "SpamCooldownSeconds": 1,
    "QueueCapacity": 50,
    "ReconnectDelaySeconds": 10,
    "MaxReconnectAttempts": 0
  },
  "Tts": {
    "SidecarUrl": "http://localhost:8765",
    "ModelId": "v5_5_ru",
    "Voice": "xenia",
    "SampleRate": 48000,
    "PutAccent": true,
    "PutYo": true,
    "SpeechRate": 1.0,
    "TimeoutSeconds": 15
  },
  "Browser": {
    "Headless": true,
    "SlowMo": 0,
    "NavigationTimeoutMs": 30000
  }
}
```

Переменные окружения .NET используют стандартный формат с двойным подчёркиванием:

```bash
Bot__Name="TTS Бот"
Bot__TtsCommand=/tts
Tts__SidecarUrl=http://localhost:8765
Tts__ModelId=v5_5_ru
Tts__Voice=xenia
Tts__SampleRate=48000
Tts__SpeechRate=1.0
Browser__Headless=true
```

Для совместимости также поддерживаются legacy-переменные `BOT_NAME`, `TTS_SIDECAR_URL`, `TTS_MODEL_ID`, `TTS_VOICE`, `TTS_SAMPLE_RATE`, `TTS_SPEECH_RATE`.

## Silero TTS

Sidecar автоматически скачивает модель при первом запуске и кладёт её в `MODEL_DIR` (`/app/models` в Docker). По умолчанию используется официальная русская модель `v5_5_ru`; для отката доступны `v5_ru`, `v4_ru` и `v3_1_ru`.

Параметры качества и скорости:

- `Tts__Voice` / `TTS_VOICE` — голос, например `xenia`.
- `Tts__SampleRate` / `TTS_SAMPLE_RATE` — `8000`, `24000` или `48000`.
- `Tts__PutAccent` — расстановка ударений.
- `Tts__PutYo` — нормализация буквы `ё`.
- `Tts__SpeechRate` — скорость речи `0.5..2.0`.
- `TORCH_THREADS` — число CPU threads для PyTorch sidecar.

## Локальный запуск через Docker

```bash
docker compose up --build
```

Открыть UI:

```text
http://localhost:5000
```

Первый запуск занимает больше времени из-за загрузки модели Silero. Модель кэшируется в Docker volume `silero-models`.

## Локальный запуск без Docker

1. Запустить TTS sidecar:

```bash
cd tts-sidecar
python -m venv .venv
.venv/Scripts/pip install -r requirements.txt
MODEL_DIR=./models .venv/Scripts/python main.py
```

2. Запустить ASP.NET Core приложение:

```bash
dotnet run --project src/TolkTtsBot/TolkTtsBot.csproj
```

3. Открыть `http://localhost:5000`.

Для Playwright нужен Chromium. В Docker используется системный `/usr/bin/chromium`; локально можно установить браузеры Playwright стандартным способом для .NET.

## Docker Compose

`docker-compose.yml` уже содержит пример production-like запуска:

```yaml
services:
  tolk-tts-bot:
    build: .
    ports:
      - "5000:5000"
    volumes:
      - silero-models:/app/models
      - ./logs:/app/logs
```

## Render.com

В репозитории есть `render.yaml`. Для публикации:

1. Создать новый Web Service на Render из GitHub-репозитория.
2. Runtime: Docker.
3. Render подхватит `render.yaml`, persistent disk `/app/models` и `healthCheckPath: /api/bot/status`.
4. Первый старт будет долгим: sidecar скачивает Silero `v5_5_ru` в persistent disk.

Важные env-переменные для Render уже описаны в `render.yaml`: `PORT`, `BOT_NAME`, `TTS_MODEL_ID`, `TTS_VOICE`, `TTS_SAMPLE_RATE`, `TTS_SPEECH_RATE`, `TTS_SIDECAR_URL`, `MODEL_DIR`.

## Зависимости

.NET:

- .NET 8
- Microsoft.Playwright
- Serilog.AspNetCore
- Serilog.Sinks.Console
- Serilog.Sinks.File

Python sidecar:

- FastAPI
- Uvicorn
- Pydantic
- NumPy
- PyTorch CPU
- Silero TTS model file, downloaded at runtime

Runtime:

- Chromium
- PulseAudio
- Supervisor

## Проверка перед публикацией

```bash
dotnet restore src/TolkTtsBot/TolkTtsBot.csproj
dotnet build src/TolkTtsBot/TolkTtsBot.csproj -c Release
docker compose build
docker compose up
```

Функциональная проверка:

- открыть UI;
- вставить ссылку на комнату Контур.Толк;
- убедиться, что статус перешёл в `Running`;
- отправить в чат `/tts Проверка связи`;
- проверить, что команда попала в лог, sidecar сгенерировал WAV, а звук воспроизвёлся в комнате;
- отправить серию сообщений и проверить, что очередь не запускает параллельный синтез.

## GitHub публикация

```bash
git status
git add .
git commit -m "Refactor Tolk TTS bot and integrate Silero sidecar"
git branch -M main
git remote add origin https://github.com/<owner>/<repo>.git
git push -u origin main
```
