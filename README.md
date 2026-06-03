# ttsTalk — TTS-бот для Контур.Толк

Бот входит в комнату Контур.Толка как гость и озвучивает сообщения по команде `/tts`.

---

## Как пользоваться

### Команда `/tts`

Участник пишет в чат:
```
/tts Добро пожаловать на встречу
```

Бот озвучивает голосом в комнате:
> **Иван Иванов сказал: Добро пожаловать на встречу**

### Правила обработки

| Сообщение в чате | Действие бота |
|-----------------|---------------|
| `/tts Привет, коллеги!` | ✅ Озвучивает: *Иван сказал: Привет, коллеги!* |
| `/TTS Как дела?` | ✅ Озвучивает (регистр не важен) |
| `/Tts Начинаем совещание` | ✅ Озвучивает |
| `Как дела?` | ❌ Игнорируется — нет команды `/tts` |
| `/tts` | ❌ Игнорируется — нет текста после команды |
| `/tts   ` | ❌ Игнорируется — пустой текст |

### Формат озвучивания
```
{Имя отправителя} сказал: {текст после /tts}
```

---

## Архитектура

```
Пользователь пишет /tts текст
         │
         ▼
Playwright читает DOM чата (каждые 1.2с)
         │
         ▼
BotOrchestrator.TryExtractTtsText()
  ├─ нет /tts → игнорировать
  └─ есть /tts → извлечь текст
         │
         ▼
Спам-фильтр + обрезка длины
         │
         ▼
Python FastAPI (Silero TTS)
  POST /synthesize → WAV bytes
         │
         ▼
Playwright: window.__ttsEnqueue(base64)
  Web Audio API → MediaStreamDestination → WebRTC
         │
         ▼
Участники слышат голос бота в комнате
```

---

## Деплой на Render.com

1. Запушить репозиторий на GitHub
2. render.com → **New** → **Web Service** → выбрать репо
3. Runtime: **Docker** (подхватит `render.yaml` автоматически)
4. Plan: **Standard** (2GB RAM минимум)
5. **Create Web Service**

Первый запуск ~10–15 минут (скачивается модель Silero ~300 МБ).

### Переменные окружения (опционально)

| Переменная | По умолчанию | Описание |
|-----------|-------------|----------|
| `BOT_NAME` | `TTS Бот` | Имя бота в комнате |
| `TTS_VOICE` | `xenia` | Голос: xenia, aidar, baya, kseniya, filipp, eugene |

---

## Локальный запуск

```bash
docker-compose up --build
# Открыть: http://localhost:5000
```

---

## Структура проекта

```
ttsTalk/
├── src/TolkTtsBot/
│   ├── Controllers/BotController.cs    REST API
│   ├── Hubs/BotHub.cs                  SignalR
│   ├── Models/Models.cs                Модели данных
│   ├── Services/
│   │   ├── BotOrchestrator.cs          Логика /tts фильтрации и очереди
│   │   ├── BrowserService.cs           Playwright + DOM чат + аудио инжекция
│   │   └── TtsService.cs               HTTP клиент Silero sidecar
│   ├── wwwroot/index.html              Веб-интерфейс
│   ├── appsettings.json
│   └── Program.cs
├── tts-sidecar/
│   ├── main.py                         FastAPI + Silero TTS
│   └── requirements.txt
├── docker/
│   ├── supervisord.conf
│   ├── pulse-default.pa
│   └── entrypoint.sh
├── Dockerfile
├── docker-compose.yml
└── render.yaml
```

---

## Настройка команды

По умолчанию команда `/tts`. Можно изменить в `appsettings.json`:
```json
{
  "Bot": {
    "TtsCommand": "/speak"
  }
}
```
Или через переменную окружения: `Bot__TtsCommand=/speak`
