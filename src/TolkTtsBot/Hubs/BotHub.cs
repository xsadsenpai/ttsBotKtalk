using Microsoft.AspNetCore.SignalR;
using TolkTtsBot.Models;

namespace TolkTtsBot.Hubs;

/// <summary>Hub наследует Hub&lt;IBotHubClient&gt; для типизированного IHubContext.</summary>
public sealed class BotHub : Hub<IBotHubClient> { }

public interface IBotHubClient
{
    Task StatusUpdated(BotStatusResponse status);
    Task LogReceived(LogEntry entry);
    Task TtsCommandReceived(string sender, string ttsText);   // /tts команда обработана
}
