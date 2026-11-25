using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace ValoAutoPick;

// The Riot Client publishes its own state changes on a loopback-only WebSocket.
internal static class RiotLocalEvents
{
    internal enum Notification { None, QueueChanged, PregameChanged, Reconnect }

    internal static Task Listen(RiotApi api, Action changed, CancellationToken ct) =>
        ListenCore(api, () => api.Subject, _ => { }, (_, _) => changed(), _ => { }, ct);

    internal static Task Listen(RiotApi api, Action<Notification> changed, Action<string, bool>? connectionChanged, CancellationToken ct) =>
        ListenCore(api, () => api.Subject, subject => connectionChanged?.Invoke(subject, true),
            (_, notification) => changed(notification), subject => connectionChanged?.Invoke(subject, false), ct);

    internal static Task ListenAny(RiotApi api, Action<string> connected, Action<string> changed, Action disconnected, CancellationToken ct) =>
        ListenCore(api, () => null, connected, (subject, _) => changed(subject), _ => disconnected(), ct);

    private static async Task ListenCore(RiotApi api, Func<string?> expectedSubject, Action<string> connected, Action<string, Notification> changed, Action<string> disconnected, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var connection = await api.GetLocalEventConnection(expectedSubject(), ct);
                if (connection is { } local)
                    await ListenConnection(local.Port, local.Password, local.Subject, connected, changed, disconnected, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e) when (e is WebSocketException or HttpRequestException or IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException) { }

            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
    }

    private static async Task ListenConnection(int port, string password, string subject, Action<string> connected, Action<string, Notification> changed, Action<string> disconnected, CancellationToken ct)
    {
        using var socket = new ClientWebSocket();
        socket.Options.Proxy = null;
        socket.Options.AddSubProtocol("wamp");
        socket.Options.SetRequestHeader("Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("riot:" + password)));
        // This socket is restricted to the authenticated Riot Client on loopback.
        socket.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            await socket.ConnectAsync(new Uri($"wss://127.0.0.1:{port}"), timeout.Token);
        }
        var subscription = Encoding.UTF8.GetBytes("[5,\"OnJsonApiEvent\"]");
        await socket.SendAsync(subscription.AsMemory(), WebSocketMessageType.Text, true, ct);
        connected(subject);
        try
        {
            changed(subject, Notification.QueueChanged); // Reconcile once after connecting, in case an update was missed.
            var buffer = new byte[8192];
            using var message = new MemoryStream();
            string? lastPresence = null;
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var part = await socket.ReceiveAsync(buffer.AsMemory(), ct);
                if (part.MessageType == WebSocketMessageType.Close) break;
                if (part.MessageType != WebSocketMessageType.Text) continue;
                if (message.Length + part.Count > 1024 * 1024) throw new InvalidDataException("Riot Client event is too large");
                message.Write(buffer, 0, part.Count);
                if (!part.EndOfMessage) continue;

                var notification = Classify(message.GetBuffer().AsMemory(0, (int)message.Length), subject, ref lastPresence);
                message.SetLength(0);
                if (notification == Notification.Reconnect) break;
                if (notification is Notification.QueueChanged or Notification.PregameChanged) changed(subject, notification);
            }
        }
        finally { disconnected(subject); }
    }

    internal static Notification Classify(ReadOnlyMemory<byte> message, string subject, ref string? lastPresence)
    {
        try
        {
            using var document = JsonDocument.Parse(message);
            var frame = document.RootElement;
            if (frame.ValueKind != JsonValueKind.Array || frame.GetArrayLength() < 3 ||
                frame[0].ValueKind != JsonValueKind.Number || !frame[0].TryGetInt32(out var type) || type != 8 ||
                frame[2].ValueKind != JsonValueKind.Object) return Notification.None;
            var eventData = frame[2];
            var uri = GetText(eventData, "uri");
            if (uri.StartsWith("/entitlements/v1/token", StringComparison.OrdinalIgnoreCase) ||
                uri.StartsWith("/riot-client-auth/v1/authorization", StringComparison.OrdinalIgnoreCase))
                return Notification.Reconnect;
            var data = eventData.TryGetProperty("data", out var value) ? value : default;
            var resource = GetText(data, "resource");
            var service = GetText(data, "service");
            if (ContainsPregameResource(uri) || ContainsPregameResource(resource) || ContainsPregameResource(service)) return Notification.PregameChanged;
            if (ContainsPartyResource(uri) || ContainsPartyResource(resource) || ContainsPartyResource(service)) return Notification.QueueChanged;
            if (!uri.StartsWith("/chat/v4/presences", StringComparison.OrdinalIgnoreCase)) return Notification.None;

            if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("presences", out var presences) && presences.ValueKind == JsonValueKind.Array)
            {
                var result = Notification.None;
                foreach (var presence in presences.EnumerateArray())
                {
                    var current = PresenceNotification(presence, subject, ref lastPresence);
                    if (current == Notification.PregameChanged) return Notification.PregameChanged;
                    if (current == Notification.QueueChanged) result = Notification.QueueChanged;
                }
                return result;
            }
            return PresenceNotification(data, subject, ref lastPresence);
        }
        catch (JsonException) { }
        return Notification.None;
    }

    private static bool ContainsPregameResource(string value) =>
        value.Contains("ares-pregame", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("/pregame/v1/", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("pregame/v1/", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsPartyResource(string value) =>
        value.Contains("ares-parties", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("/parties/v1/parties/", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("parties/v1/parties/", StringComparison.OrdinalIgnoreCase);

    private static Notification PresenceNotification(JsonElement presence, string subject, ref string? lastPresence)
    {
        if (!string.Equals(GetText(presence, "puuid"), subject, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(GetText(presence, "product"), "valorant", StringComparison.OrdinalIgnoreCase)) return Notification.None;
        var encoded = GetText(presence, "private");
        if (encoded.Length == 0 || encoded.Length > 65536) return Notification.None;
        try
        {
            using var data = JsonDocument.Parse(Convert.FromBase64String(encoded));
            if (data.RootElement.ValueKind != JsonValueKind.Object) return Notification.None;
            var phase = GetText(data.RootElement, "sessionLoopState");
            var party = GetText(data.RootElement, "partyState");
            if (phase.Length == 0 && data.RootElement.TryGetProperty("matchPresenceData", out var matchData))
                phase = GetText(matchData, "sessionLoopState");
            if (data.RootElement.TryGetProperty("partyPresenceData", out var partyData))
            {
                if (party.Length == 0) party = GetText(partyData, "partyState");
                if (phase.Length == 0) phase = GetText(partyData, "partyOwnerSessionLoopState");
            }
            if (phase.Length == 0 && party.Length == 0) return Notification.None;
            var current = phase + "|" + party;
            if (current == lastPresence) return Notification.None;
            lastPresence = current;
            if (phase.Equals("PREGAME", StringComparison.OrdinalIgnoreCase) ||
                party.Equals("MATCHMADE_GAME_STARTING", StringComparison.OrdinalIgnoreCase))
                return Notification.PregameChanged;
            return Notification.QueueChanged;
        }
        catch (FormatException) { return Notification.None; }
        catch (JsonException) { return Notification.None; }
    }

    private static string GetText(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : "";
}
