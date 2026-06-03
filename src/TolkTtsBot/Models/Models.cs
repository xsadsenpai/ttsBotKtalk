namespace TolkTtsBot.Models;

public sealed class BotOptions
{
    public const string Section = "Bot";

    public string Name                  { get; init; } = "TTS Бот";
    public string TtsCommand            { get; init; } = "/tts";
    public int    MaxMessageLength      { get; init; } = 300;
    public double SpamCooldownSeconds   { get; init; } = 1.0;
    public int    QueueCapacity         { get; init; } = 50;
    public int    ReconnectDelaySeconds { get; init; } = 10;
    public int    MaxReconnectAttempts  { get; init; } = 0;

}

public sealed class TtsOptions
{
    public const string Section = "Tts";

    public string SidecarUrl     { get; init; } = "http://localhost:8765";
    public string Voice          { get; init; } = "xenia";
    public int    SampleRate     { get; init; } = 48000;
    public int    TimeoutSeconds { get; init; } = 15;
}

public sealed class BrowserOptions
{
    public const string Section = "Browser";

    public bool Headless            { get; init; } = true;
    public int  SlowMo              { get; init; } = 0;
    public int  NavigationTimeoutMs { get; init; } = 30000;
}

public enum BotStatus { Stopped, Connecting, Running, Reconnecting, Error }

public sealed class BotState
{
    public BotStatus       Status          { get; set; } = BotStatus.Stopped;
    public string?         RoomUrl         { get; set; }
    public string?         RoomId          { get; set; }
    public string?         ErrorMessage    { get; set; }
    public DateTimeOffset? StartedAt       { get; set; }
    public int             MessagesSpoken  { get; set; }
    public int             QueueLength     { get; set; }
    public int             ReconnectCount  { get; set; }
    public int             TtsCommandCount { get; set; }
}

public sealed record TtsQueueItem(
    string Phrase,
    string Sender,
    string OriginalText,
    DateTimeOffset EnqueuedAt);

public sealed class StartBotRequest
{
    public string RoomUrl { get; init; } = "";
}

public sealed class BotStatusResponse
{
    public string          Status          { get; init; } = "";
    public string?         RoomUrl         { get; init; }
    public string?         RoomId          { get; init; }
    public string?         ErrorMessage    { get; init; }
    public DateTimeOffset? StartedAt       { get; init; }
    public int             MessagesSpoken  { get; init; }
    public int             QueueLength     { get; init; }
    public int             ReconnectCount  { get; init; }
    public int             TtsCommandCount { get; init; }
    public string          UptimeSeconds   { get; init; } = "0";
    public string          TtsCommand      { get; init; } = "/tts";
}

public sealed class LogEntry
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string Level   { get; init; } = "Info";
    public string Message { get; init; } = "";
}
