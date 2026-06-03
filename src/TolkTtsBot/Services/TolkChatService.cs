using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using TolkTtsBot.Models;

namespace TolkTtsBot.Services;

/// <summary>
/// Реализует нативный протокол Контур.Толка (восстановлен из HAR-анализа):
///   POST {baseUrl}/api/authorize/session  → signInToken
///   WSS  {baseUrl}/system/ws             → connect → auth → chat_join → recv chat_message
///
/// baseUrl извлекается из ссылки на комнату, переданной пользователем.
/// </summary>
public sealed class TolkChatService : IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly BotOptions _opts;
    private readonly ILogger<TolkChatService> _log;

    private ClientWebSocket? _ws;
    private string? _sessionId;
    private string? _signInToken;
    private string? _botAnonymousId;
    private string? _baseUrl;          // задаётся при каждом вызове ListenAsync

    private static string NewReqId()
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        return new string(Enumerable.Range(0, 10)
            .Select(_ => chars[Random.Shared.Next(chars.Length)]).ToArray());
    }

    public TolkChatService(
        HttpClient http,
        IOptions<BotOptions> opts,
        ILogger<TolkChatService> log)
    {
        _http = http;
        _opts = opts.Value;
        _log  = log;
    }

    // ── Публичный метод ───────────────────────────────────────────────────────

    /// <summary>
    /// baseUrl — извлекается из ссылки на комнату (scheme + host).
    /// Например: https://kontur.ktalk.ru или https://tolk.kontur.ru
    /// </summary>
    public async Task ListenAsync(
        string baseUrl,
        string roomId,
        Func<string, string, Task> onMessage,
        CancellationToken ct)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _log.LogInformation("[Chat] baseUrl={B} room={R}", _baseUrl, roomId);

        _signInToken = await AuthorizeAsync(roomId, ct);
        if (_signInToken is null)
        {
            _log.LogError("[Chat] Не удалось получить signInToken");
            return;
        }

        await RunWebSocketAsync(roomId, onMessage, ct);
    }

    // ── Авторизация ───────────────────────────────────────────────────────────

    private async Task<string?> AuthorizeAsync(string roomId, CancellationToken ct)
    {
        try
        {
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            var secret = new string(Enumerable.Range(0, 15)
                .Select(_ => chars[Random.Shared.Next(chars.Length)]).ToArray());

            var payload = new
            {
                name            = _opts.Name,
                anonymousSecret = secret,
                consentOnCreate = true
            };

            var url = $"{_baseUrl}/api/authorize/session";
            _log.LogInformation("[Auth] POST {U} name={N}", url, _opts.Name);

            // Временно меняем BaseAddress под конкретный хост
            var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(payload)
            };
            request.Headers.Add("Referer", $"{_baseUrl}/{roomId}");
            request.Headers.Add("Origin", _baseUrl);

            var response = await _http.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync(ct);
                _log.LogError("[Auth] HTTP {S}: {B}", (int)response.StatusCode, err[..Math.Min(200, err.Length)]);
                return null;
            }

            var result = await response.Content.ReadFromJsonAsync<AuthResponse>(cancellationToken: ct);
            _botAnonymousId = result?.AnonymousId;
            _log.LogInformation("[Auth] ✓ token={T}... anonymousId={A}...",
                result?.Token?[..Math.Min(8, result.Token.Length)],
                result?.AnonymousId?[..Math.Min(16, result.AnonymousId.Length)]);
            return result?.Token;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[Auth] Ошибка");
            return null;
        }
    }

    // ── WebSocket протокол ────────────────────────────────────────────────────

    private async Task RunWebSocketAsync(
        string roomId,
        Func<string, string, Task> onMessage,
        CancellationToken ct)
    {
        var wsBase = _baseUrl!
            .Replace("https://", "wss://", StringComparison.OrdinalIgnoreCase)
            .Replace("http://",  "ws://",  StringComparison.OrdinalIgnoreCase);
        var wsUrl = $"{wsBase}/system/ws";

        _log.LogInformation("[WS] Подключение: {U}", wsUrl);

        _ws?.Dispose();
        _ws = new ClientWebSocket();
        _ws.Options.SetRequestHeader("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124.0.0.0 Safari/537.36");
        _ws.Options.SetRequestHeader("Origin", _baseUrl);

        await _ws.ConnectAsync(new Uri(wsUrl), ct);
        _log.LogInformation("[WS] ✓ Соединение установлено");

        // 1. connect
        await SendAsync(new
        {
            a     = "connect",
            reqId = NewReqId(),
            data  = new { clientType = "Web", webAppVersion = "master-bot-1.0" }
        }, ct);

        var connectDoc = await ReceiveAsync(ct);
        _sessionId = connectDoc?.RootElement
            .TryGetProperty("data", out var d) == true &&
            d.TryGetProperty("sessionId", out var sid)
            ? sid.GetString() : null;
        _log.LogInformation("[WS] sessionId={S}", _sessionId);

        // 2. message_subscribe
        await SendAsync(new
        {
            a       = "message_subscribe",
            reqId   = NewReqId(),
            data    = new { topic = "personal" },
            session = _sessionId
        }, ct);
        await ReceiveAsync(ct);

        // 3. auth
        await SendAsync(new
        {
            a       = "auth",
            reqId   = NewReqId(),
            data    = new { signInToken = _signInToken },
            session = _sessionId
        }, ct);
        var authDoc = await ReceiveAsync(ct);
        // Обновляем sessionId после auth (он может измениться)
        if (authDoc?.RootElement.TryGetProperty("data", out var ad) == true &&
            ad.TryGetProperty("sessionId", out var newSid))
            _sessionId = newSid.GetString();
        _log.LogInformation("[WS] ✓ Авторизован, sessionId={S}", _sessionId);

        // 4. chat_join
        await SendAsync(new
        {
            a       = "chat_join",
            reqId   = NewReqId(),
            data    = new { name = roomId, popup = false, platform = "web" },
            session = _sessionId
        }, ct);
        await ReceiveAsync(ct);
        _log.LogInformation("[WS] ✓ Подключён к чату room={R}", roomId);

        // 5. resource_active_get
        await SendAsync(new
        {
            a       = "resource_active_get",
            reqId   = NewReqId(),
            data    = new { roomName = roomId },
            session = _sessionId
        }, ct);
        await ReceiveAsync(ct);

        _log.LogInformation("[WS] ✓ Слушаю сообщения...");

        // Ping-loop + receive-loop параллельно
        using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pingTask = PingLoopAsync(pingCts.Token);
        try
        {
            await ReceiveLoopAsync(onMessage, ct);
        }
        finally
        {
            pingCts.Cancel();
            try { await pingTask; } catch { }
        }
    }

    // ── Основной цикл получения сообщений ─────────────────────────────────────

    private async Task ReceiveLoopAsync(
        Func<string, string, Task> onMessage,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
        {
            try
            {
                var doc = await ReceiveAsync(ct);
                if (doc is null) continue;

                var root   = doc.Value.RootElement;
                var action = root.TryGetProperty("a", out var ap) ? ap.GetString() : null;
                var hasReqId = root.TryGetProperty("reqId", out _);

                _log.LogDebug("[WS] recv a={A} hasReqId={H}: {J}",
                    action, hasReqId,
                    root.ToString()[..Math.Min(150, root.ToString().Length)]);

                // Push-событие нового сообщения чата
                if (action == "chat_message" &&
                    root.TryGetProperty("data", out var data))
                {
                    await HandleChatMessageAsync(data, onMessage);
                    continue;
                }

                // Любое push без reqId может быть входящим событием
                if (!hasReqId && root.TryGetProperty("data", out var pushData))
                {
                    if (pushData.TryGetProperty("text", out _) ||
                        pushData.TryGetProperty("messages", out _))
                        await HandleChatMessageAsync(pushData, onMessage);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (WebSocketException wex)
            {
                _log.LogWarning("[WS] WebSocket ошибка: {E}", wex.Message);
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[WS] Ошибка приёма");
                await Task.Delay(500, ct);
            }
        }
        _log.LogInformation("[WS] Цикл приёма завершён (state={S})",
            _ws?.State.ToString() ?? "null");
    }

    private async Task HandleChatMessageAsync(
        JsonElement data,
        Func<string, string, Task> onMessage)
    {
        try
        {
            var text = data.TryGetProperty("text", out var tp) ? tp.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(text)) return;

            // Имя отправителя — несколько вариантов структуры
            var sender = "";
            if (data.TryGetProperty("sender", out var sp))
            {
                var first   = sp.TryGetProperty("firstname", out var fn) ? fn.GetString() ?? "" : "";
                var surname = sp.TryGetProperty("surname",   out var sn) ? sn.GetString() ?? "" : "";
                var name    = sp.TryGetProperty("name",      out var nm) ? nm.GetString() ?? "" : "";
                sender = $"{first} {surname}".Trim();
                if (string.IsNullOrEmpty(sender)) sender = name;
            }
            if (string.IsNullOrEmpty(sender) &&
                data.TryGetProperty("senderName", out var snp))
                sender = snp.GetString() ?? "";
            if (string.IsNullOrEmpty(sender)) sender = "Участник";

            // Не озвучиваем свои сообщения
            if (data.TryGetProperty("senderId", out var sidp) &&
                sidp.GetString() == _botAnonymousId)
            {
                _log.LogDebug("[Chat] Пропуск: собственное сообщение");
                return;
            }

            _log.LogDebug("[Chat] [{S}] {T}", sender, text[..Math.Min(60, text.Length)]);
            await onMessage(sender, text);
        }
        catch (Exception ex) { _log.LogWarning(ex, "[Chat] Ошибка обработки"); }
    }

    private async Task PingLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(30_000, ct);
                if (_ws?.State == WebSocketState.Open && _sessionId is not null)
                {
                    await SendAsync(new { a = "ping", reqId = NewReqId(), session = _sessionId }, ct);
                    _log.LogDebug("[WS] ping");
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogDebug("[WS] ping error: {E}", ex.Message); }
        }
    }

    // ── HTTP helpers ──────────────────────────────────────────────────────────

    private async Task SendAsync(object payload, CancellationToken ct)
    {
        if (_ws is null || _ws.State != WebSocketState.Open) return;
        var json  = JsonSerializer.Serialize(payload);
        _log.LogDebug("[WS] send: {J}", json[..Math.Min(150, json.Length)]);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    private async Task<JsonDocument?> ReceiveAsync(CancellationToken ct)
    {
        if (_ws is null || _ws.State != WebSocketState.Open) return null;

        var buffer = new ArraySegment<byte>(new byte[65536]);
        using var ms = new System.IO.MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await _ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                _log.LogWarning("[WS] Сервер закрыл соединение");
                return null;
            }
            ms.Write(buffer.Array!, buffer.Offset, result.Count);
        } while (!result.EndOfMessage);

        ms.Seek(0, System.IO.SeekOrigin.Begin);
        try { return await JsonDocument.ParseAsync(ms, cancellationToken: ct); }
        catch (JsonException ex) { _log.LogDebug("[WS] JSON err: {E}", ex.Message); return null; }
    }

    public async ValueTask DisposeAsync()
    {
        if (_ws is not null)
        {
            try
            {
                if (_ws.State == WebSocketState.Open)
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }
            catch { }
            _ws.Dispose();
        }
    }

    private sealed class AuthResponse
    {
        [JsonPropertyName("token")]       public string? Token       { get; init; }
        [JsonPropertyName("anonymousId")] public string? AnonymousId { get; init; }
    }
}
