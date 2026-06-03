using System.Diagnostics;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using TolkTtsBot.Models;

namespace TolkTtsBot.Services;

public interface IBrowserService : IAsyncDisposable
{
    Task<bool> JoinRoomAsync(string roomUrl, string botName, CancellationToken ct);
    Task InjectAudioAsync(byte[] wavBytes, CancellationToken ct);
    Task LeaveRoomAsync();
    bool IsInRoom { get; }
}

public sealed class PlaywrightBrowserService : IBrowserService
{
    private readonly BrowserOptions _opts;
    private readonly ILogger<PlaywrightBrowserService> _log;
    private const string AudioInjectionScript = """
        (() => {
            if (window.__ttsReady) return;

            const AudioCtor = window.AudioContext || window.webkitAudioContext;
            if (!AudioCtor) {
                console.error('[TTS Bot] Web Audio API is unavailable');
                return;
            }

            const ctx = new AudioCtor({ sampleRate: 48000 });
            const dest = ctx.createMediaStreamDestination();
            const mediaDevices = navigator.mediaDevices || {};
            const originalGetUserMedia = mediaDevices.getUserMedia?.bind(mediaDevices);

            navigator.mediaDevices = mediaDevices;
            mediaDevices.getUserMedia = async constraints => {
                const wantsAudio = Boolean(constraints?.audio);
                const wantsVideo = Boolean(constraints?.video);

                if (!wantsAudio) {
                    if (!originalGetUserMedia) throw new Error('getUserMedia is unavailable');
                    return originalGetUserMedia(constraints);
                }

                if (!wantsVideo) return dest.stream;

                const videoStream = originalGetUserMedia
                    ? await originalGetUserMedia({ ...constraints, audio: false })
                    : new MediaStream();

                return new MediaStream([
                    ...dest.stream.getAudioTracks(),
                    ...videoStream.getVideoTracks()
                ]);
            };

            window.__ttsCtx = ctx;
            window.__ttsDest = dest;
            window.__ttsQueue = [];
            window.__ttsPlaying = false;
            window.__ttsReady = true;

            async function playNext() {
                if (window.__ttsPlaying || !window.__ttsQueue.length) return;
                window.__ttsPlaying = true;

                const b64 = window.__ttsQueue.shift();
                try {
                    if (ctx.state === 'suspended') await ctx.resume();

                    const bin = atob(b64);
                    const bytes = new Uint8Array(bin.length);
                    for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);

                    const audioBuffer = await ctx.decodeAudioData(bytes.buffer);
                    const source = ctx.createBufferSource();
                    source.buffer = audioBuffer;
                    source.connect(dest);
                    source.onended = () => {
                        window.__ttsPlaying = false;
                        playNext();
                    };
                    source.start(0);
                } catch (error) {
                    console.error('[TTS Bot]', error);
                    window.__ttsPlaying = false;
                    playNext();
                }
            }

            window.__ttsEnqueue = b64 => {
                window.__ttsQueue.push(b64);
                playNext();
            };

            console.log('[TTS Bot] Audio injection ready before page scripts');
        })();
        """;

    private IPlaywright?     _playwright;
    private IBrowser?        _browser;
    private IBrowserContext? _context;
    private IPage?           _page;
    private bool             _isInRoom;

    public bool IsInRoom => _isInRoom;

    public PlaywrightBrowserService(
        IOptions<BrowserOptions> opts,
        ILogger<PlaywrightBrowserService> log)
    {
        _opts = opts.Value;
        _log  = log;
    }

    public async Task<bool> JoinRoomAsync(string roomUrl, string botName, CancellationToken ct)
    {
        var started = Stopwatch.StartNew();
        try
        {
            await CleanupAsync();

            _log.LogInformation("[Browser] Инициализация Playwright...");
            _playwright = await Playwright.CreateAsync();

            var executablePath = FindChromium();
            _log.LogInformation("[Browser] Chromium путь: {Path}",
                executablePath ?? "(встроенный Playwright)");

            var launchStarted = Stopwatch.StartNew();
            var launchArgs = new[]
            {
                "--use-fake-ui-for-media-stream",
                "--use-fake-device-for-media-stream",
                "--autoplay-policy=no-user-gesture-required",
                "--no-sandbox",
                "--disable-setuid-sandbox",
                "--disable-dev-shm-usage",
                "--disable-gpu",
                "--disable-crash-reporter",
                "--disable-extensions",
                "--disable-background-networking",
                "--disable-background-timer-throttling",
                "--disable-renderer-backgrounding",
                "--no-first-run",
                "--no-default-browser-check",
            };
            _log.LogInformation("[Browser] Chromium launch args: {Args}", string.Join(" ", launchArgs));

            var launchTask = _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless       = _opts.Headless,
                SlowMo         = _opts.SlowMo,
                ExecutablePath = executablePath,
                Timeout        = 45000,
                Args           = launchArgs
            });
            _browser = await launchTask.WaitAsync(TimeSpan.FromSeconds(60), ct);
            _log.LogInformation("[Browser] ✓ Chromium запущен за {ElapsedMs}мс", launchStarted.ElapsedMilliseconds);

            _context = await _browser.NewContextAsync(new BrowserNewContextOptions
            {
                Permissions       = new[] { "microphone" },
                IgnoreHTTPSErrors = true,
                UserAgent         = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
                                    "AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36"
            });
            await _context.AddInitScriptAsync(AudioInjectionScript);

            _page = await _context.NewPageAsync();
            AttachPageDiagnostics(_page);

            // ── Шаг 1: открываем страницу ────────────────────────────────
            // Используем Load вместо NetworkIdle — SPA страницы никогда не достигают NetworkIdle
            _log.LogInformation("[Browser] Открываем: {Url}", roomUrl);
            var navigationStarted = Stopwatch.StartNew();
            var navigationTask = _page.GotoAsync(roomUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.Load,          // Load, не NetworkIdle!
                Timeout   = _opts.NavigationTimeoutMs
            });
            var resp = await navigationTask.WaitAsync(TimeSpan.FromMilliseconds(_opts.NavigationTimeoutMs + 10_000), ct);
            _log.LogInformation("[Browser] HTTP {Status} за {ElapsedMs}мс", resp?.Status, navigationStarted.ElapsedMilliseconds);

            // Ждём 3 секунды чтобы JS успел отрендерить форму входа
            await Task.Delay(3000, ct);

            await LogPageSnapshotAsync("после первичной загрузки");

            // ── Шаг 2: гостевой вход ─────────────────────────────────────
            await GuestJoinAsync(botName, ct);

            // ── Шаг 3: аудио инжекция ─────────────────────────────────────
            await EnsureAudioInjectionReadyAsync();

            _isInRoom = true;
            _log.LogInformation("[Browser] ✓ Бот в комнате как \"{N}\" за {ElapsedMs}мс", botName, started.ElapsedMilliseconds);
            return true;
        }
        catch (OperationCanceledException)
        {
            _log.LogWarning("[Browser] Отменено");
            await CleanupAsync();
            return false;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[Browser] ✗ Ошибка: {M}", ex.Message);
            await LogPageSnapshotAsync("ошибка браузерного входа");
            await CleanupAsync();
            return false;
        }
    }

    private void AttachPageDiagnostics(IPage page)
    {
        page.Console += (_, message) =>
        {
            var text = Truncate(message.Text, 500);
            switch (message.Type)
            {
                case "error":
                    _log.LogError("[Page.Console] {Type}: {Text}", message.Type, text);
                    break;
                case "warning":
                    _log.LogWarning("[Page.Console] {Type}: {Text}", message.Type, text);
                    break;
                default:
                    _log.LogDebug("[Page.Console] {Type}: {Text}", message.Type, text);
                    break;
            }
        };
        page.PageError += (_, error) => _log.LogError("[Page.Error] {Error}", error);
        page.RequestFailed += (_, request) =>
            _log.LogWarning("[Page.RequestFailed] {Method} {Url} | {Failure}",
                request.Method, request.Url, request.Failure ?? "unknown");
        page.Response += (_, response) =>
        {
            if (response.Status >= 400)
                _log.LogWarning("[Page.Response] HTTP {Status} {Url}", response.Status, response.Url);
            else
                _log.LogDebug("[Page.Response] HTTP {Status} {Url}", response.Status, response.Url);
        };
        page.Dialog += async (_, dialog) =>
        {
            _log.LogWarning("[Page.Dialog] {Type}: {Message}", dialog.Type, dialog.Message);
            await dialog.DismissAsync();
        };
        page.Crash += (_, _) => _log.LogError("[Page] Chromium page crashed");
        page.Close += (_, _) => _log.LogInformation("[Page] Closed");
    }

    private static string? FindChromium()
    {
        var fromEnv = Environment.GetEnvironmentVariable("PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH");
        var paths = new[]
        {
            fromEnv,
            "/usr/bin/chromium",
            "/usr/bin/chromium-browser",
            "/usr/bin/google-chrome-stable",
            "/usr/bin/google-chrome",
            "/snap/bin/chromium",
        };
        return paths.FirstOrDefault(p => !string.IsNullOrEmpty(p) && File.Exists(p));
    }

    private async Task GuestJoinAsync(string botName, CancellationToken ct)
    {
        if (_page is null) return;
        _log.LogInformation("[Join] Вход как \"{N}\"", botName);

        // Ждём появления формы (Angular/React рендерит асинхронно)
        await Task.Delay(2000, ct);

        // Поле имени
        var nameSelectors = new[]
        {
            "input[placeholder*='имя' i]",
            "input[placeholder*='name' i]",
            "input[placeholder*='Введите' i]",
            "input[name='name']",
            "input[name='displayName']",
            "input[name='userName']",
            "[data-testid*='name'] input",
            "input[type='text']",
        };

        var nameFilled = false;
        foreach (var sel in nameSelectors)
        {
            try
            {
                var loc = _page.Locator(sel).First;
                if (await loc.IsVisibleAsync())
                {
                    await loc.ClearAsync();
                    await loc.FillAsync(botName);
                    await loc.PressAsync("Tab");
                    await Task.Delay(300, ct);
                    _log.LogInformation("[Join] ✓ Имя введено: {S}", sel);
                    nameFilled = true;
                    break;
                }
            }
            catch (Exception ex) { _log.LogDebug("[Join] input {S}: {E}", sel, ex.Message); }
        }
        if (!nameFilled)
            _log.LogWarning("[Join] Поле имени не найдено; возможно, вход уже выполнен или изменилась форма Толка");

        // Кнопка входа
        var joinSelectors = new[]
        {
            "button:has-text('Присоединиться')",
            "button:has-text('Войти')",
            "button:has-text('Подключиться')",
            "button:has-text('Продолжить')",
            "button:has-text('Join')",
            "button:has-text('Enter')",
            "[data-testid*='join']",
            "[data-testid*='enter']",
        };

        var joinClicked = false;
        foreach (var sel in joinSelectors)
        {
            try
            {
                var btn = _page.Locator(sel).First;
                if (await btn.IsVisibleAsync())
                {
                    var txt = await btn.InnerTextAsync();
                    await btn.ClickAsync();
                    _log.LogInformation("[Join] ✓ Кнопка: \"{T}\"", txt.Trim());
                    joinClicked = true;
                    break;
                }
            }
            catch (Exception ex) { _log.LogDebug("[Join] btn {S}: {E}", sel, ex.Message); }
        }
        if (!joinClicked)
            _log.LogWarning("[Join] Кнопка входа не найдена; проверю признаки комнаты после ожидания");

        // Ждём загрузки комнаты
        await Task.Delay(5000, ct);

        var urlAfter  = _page.Url;
        var bodyAfter = await _page.EvaluateAsync<string>(
            "() => document.body?.innerText?.slice(0,200) ?? ''");
        _log.LogInformation("[Join] После входа URL={U}", urlAfter);
        _log.LogInformation("[Join] После входа body={B}", bodyAfter);
        await LogPageSnapshotAsync("после попытки гостевого входа");

        if (!await LooksLikeRoomAsync())
            throw new InvalidOperationException(
                $"Не удалось подтвердить вход в комнату. url={urlAfter}, body={bodyAfter[..Math.Min(120, bodyAfter.Length)]}");

        // Микрофон
        await EnsureMicrophoneOnAsync(ct);
    }

    private async Task<bool> LooksLikeRoomAsync()
    {
        if (_page is null) return false;

        var result = await _page.EvaluateAsync<bool>("""
            () => {
                const text = (document.body?.innerText || '').toLowerCase();
                const labels = [
                    'микрофон', 'камера', 'чат', 'участник', 'покинуть',
                    'microphone', 'camera', 'chat', 'leave'
                ];
                const hasRoomText = labels.some(label => text.includes(label));
                const hasMediaButton = [...document.querySelectorAll('button,[role="button"]')]
                    .some(el => /микрофон|microphone|камера|camera|покинуть|leave/i.test(
                        `${el.getAttribute('aria-label') || ''} ${el.textContent || ''}`));
                return hasRoomText || hasMediaButton;
            }
            """);
        _log.LogInformation("[Join] Признаки комнаты: {Result}", result);
        return result;
    }

    private async Task LogPageSnapshotAsync(string reason)
    {
        if (_page is null) return;

        try
        {
            var title = await _page.TitleAsync();
            var body = await _page.EvaluateAsync<string>("() => document.body?.innerText ?? ''");
            var inputs = await _page.EvaluateAsync<string[]>("""
                () => [...document.querySelectorAll('input, textarea')]
                    .slice(0, 30)
                    .map((el, i) => [
                        `#${i}`,
                        el.tagName.toLowerCase(),
                        `type=${el.getAttribute('type') || ''}`,
                        `name=${el.getAttribute('name') || ''}`,
                        `placeholder=${el.getAttribute('placeholder') || ''}`,
                        `aria=${el.getAttribute('aria-label') || ''}`,
                        `visible=${Boolean(el.offsetWidth || el.offsetHeight || el.getClientRects().length)}`
                    ].join('|'))
                """);
            var buttons = await _page.EvaluateAsync<string[]>("""
                () => [...document.querySelectorAll('button,[role="button"],a')]
                    .slice(0, 40)
                    .map((el, i) => [
                        `#${i}`,
                        el.tagName.toLowerCase(),
                        `role=${el.getAttribute('role') || ''}`,
                        `text=${(el.textContent || '').trim().replace(/\s+/g, ' ').slice(0, 80)}`,
                        `aria=${el.getAttribute('aria-label') || ''}`,
                        `href=${el.getAttribute('href') || ''}`,
                        `visible=${Boolean(el.offsetWidth || el.offsetHeight || el.getClientRects().length)}`
                    ].join('|'))
                """);
            var ttsState = await _page.EvaluateAsync<string>("""
                () => JSON.stringify({
                    ttsReady: Boolean(window.__ttsReady),
                    hasEnqueue: Boolean(window.__ttsEnqueue),
                    audioState: window.__ttsCtx?.state || null,
                    queueLength: window.__ttsQueue?.length || 0,
                    url: location.href
                })
                """);

            _log.LogInformation("[Diag:{Reason}] Url={Url}", reason, _page.Url);
            _log.LogInformation("[Diag:{Reason}] Title={Title}", reason, title);
            _log.LogInformation("[Diag:{Reason}] Body={Body}", reason, Truncate(body, 1500));
            _log.LogInformation("[Diag:{Reason}] Inputs={Inputs}", reason, string.Join("; ", inputs));
            _log.LogInformation("[Diag:{Reason}] Actions={Actions}", reason, string.Join("; ", buttons));
            _log.LogInformation("[Diag:{Reason}] TtsState={TtsState}", reason, ttsState);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[Diag:{Reason}] Не удалось собрать диагностику страницы", reason);
        }
    }

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return "";
        value = value.ReplaceLineEndings(" ").Trim();
        return value.Length <= maxLength ? value : value[..maxLength] + "...";
    }

    private async Task EnsureMicrophoneOnAsync(CancellationToken ct)
    {
        if (_page is null) return;
        _log.LogInformation("[Mic] Проверка микрофона...");

        var micOffSelectors = new[]
        {
            "[aria-label*='Включить микрофон' i]",
            "[aria-label*='Unmute' i]",
            "[aria-label*='microphone' i][aria-pressed='false']",
            "[data-testid*='mic'][aria-pressed='false']",
            "button[class*='muted'][class*='mic']",
        };

        foreach (var sel in micOffSelectors)
        {
            try
            {
                var btn = _page.Locator(sel).First;
                if (await btn.IsVisibleAsync())
                {
                    await btn.ClickAsync();
                    _log.LogInformation("[Mic] ✓ Микрофон включён: {S}", sel);
                    return;
                }
            }
            catch { }
        }
        _log.LogInformation("[Mic] Микрофон уже включён (OK)");
    }

    // ── Аудио инжекция ─────────────────────────────────────────────────────────

    private async Task EnsureAudioInjectionReadyAsync()
    {
        if (_page is null) return;
        _log.LogInformation("[Audio] Проверка Web Audio injection...");
        var ready = await _page.EvaluateAsync<bool>("() => Boolean(window.__ttsReady && window.__ttsEnqueue)");
        if (!ready)
            throw new InvalidOperationException("Web Audio injection не была установлена до загрузки комнаты");

        _log.LogInformation("[Audio] ✓ Готов");
    }

    public async Task InjectAudioAsync(byte[] wavBytes, CancellationToken ct)
    {
        if (_page is null || !_isInRoom)
            throw new InvalidOperationException("Браузер не подключён к комнате");
        await _page.EvaluateAsync("b64 => window.__ttsEnqueue(b64)", Convert.ToBase64String(wavBytes));
        _log.LogDebug("[Audio] {B} байт", wavBytes.Length);
    }

    public async Task LeaveRoomAsync()
    {
        _isInRoom = false;
        await CleanupAsync();
        _log.LogInformation("[Browser] Закрыт");
    }

    private async Task CleanupAsync()
    {
        _isInRoom = false;
        try { if (_page    is not null) await _page.CloseAsync();    } catch { }
        try { if (_context is not null) await _context.CloseAsync(); } catch { }
        try { if (_browser is not null) await _browser.CloseAsync(); } catch { }
        try { _playwright?.Dispose(); } catch { }
        _page = null; _context = null; _browser = null; _playwright = null;
    }

    public async ValueTask DisposeAsync() => await CleanupAsync();
}
