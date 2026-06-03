using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using TolkTtsBot.Models;

namespace TolkTtsBot.Services;

public interface ITtsService
{
    Task<byte[]> SynthesizeAsync(string text, CancellationToken ct = default);
    Task<bool>   IsHealthyAsync(CancellationToken ct = default);
}

public sealed class SileroTtsService : ITtsService
{
    private readonly HttpClient _http;
    private readonly TtsOptions _opts;
    private readonly ILogger<SileroTtsService> _log;

    public SileroTtsService(
        HttpClient http,
        IOptions<TtsOptions> opts,
        ILogger<SileroTtsService> log)
    {
        _http = http;
        _opts = opts.Value;
        _log  = log;
    }

    public async Task<byte[]> SynthesizeAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Text for synthesis is empty", nameof(text));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_opts.TimeoutSeconds));

        var payload = new SynthesizeRequest(
            text,
            _opts.ModelId,
            _opts.Voice,
            _opts.SampleRate,
            _opts.PutAccent,
            _opts.PutYo,
            _opts.SpeechRate);
        _log.LogDebug("TTS запрос: {Text}", text[..Math.Min(text.Length, 80)]);

        using var response = await _http.PostAsJsonAsync("/synthesize", payload, cts.Token);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cts.Token);
            throw new HttpRequestException(
                $"TTS sidecar returned {(int)response.StatusCode}: {body[..Math.Min(body.Length, 300)]}",
                null,
                response.StatusCode);
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(cts.Token);
        if (bytes.Length == 0)
            throw new InvalidOperationException("TTS sidecar returned empty audio");

        _log.LogDebug("TTS ответ: {Bytes} байт", bytes.Length);
        return bytes;
    }

    public async Task<bool> IsHealthyAsync(CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.GetAsync("/health", ct);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private sealed record SynthesizeRequest(
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("model_id")] string ModelId,
        [property: JsonPropertyName("voice")] string Voice,
        [property: JsonPropertyName("sample_rate")] int SampleRate,
        [property: JsonPropertyName("put_accent")] bool PutAccent,
        [property: JsonPropertyName("put_yo")] bool PutYo,
        [property: JsonPropertyName("speech_rate")] double SpeechRate);
}
