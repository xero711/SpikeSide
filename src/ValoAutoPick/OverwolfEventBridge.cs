using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ValoAutoPick;

// Local-only bridge for the separately loaded SpikeSide Overwolf companion.
internal sealed class OverwolfEventBridge : IDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    private readonly Action<string> received;

    internal OverwolfEventBridge(string folder, Action<string> received)
    {
        this.received = received;
        listener.Start();
        try
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "spikeside-overwolf-bridge.json");
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new { port = ((IPEndPoint)listener.LocalEndpoint).Port, token }));
            File.Move(temp, path, true);
            _ = ListenAsync(stop.Token);
        }
        catch { listener.Stop(); stop.Dispose(); throw; }
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                _ = HandleAsync(client, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (ct.IsCancellationRequested) { }
        catch (SocketException) when (ct.IsCancellationRequested) { }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                var stream = client.GetStream();
                var headers = new List<byte>(512);
                var single = new byte[1];
                while (headers.Count < 8192)
                {
                    if (await stream.ReadAsync(single, timeout.Token) == 0) return;
                    headers.Add(single[0]);
                    var n = headers.Count;
                    if (n >= 4 && headers[n - 4] == 13 && headers[n - 3] == 10 && headers[n - 2] == 13 && headers[n - 1] == 10) break;
                }
                var lines = Encoding.ASCII.GetString(headers.ToArray()).Split("\r\n");
                if (lines.Length == 0) return;
                var expectedPath = $"/events?token={token}";
                if (lines[0] == $"OPTIONS {expectedPath} HTTP/1.1")
                {
                    var preflight = Encoding.ASCII.GetBytes("HTTP/1.1 204 No Content\r\nAccess-Control-Allow-Origin: *\r\nAccess-Control-Allow-Methods: POST, OPTIONS\r\nAccess-Control-Allow-Headers: content-type\r\nAccess-Control-Allow-Private-Network: true\r\nConnection: close\r\nContent-Length: 0\r\n\r\n");
                    await stream.WriteAsync(preflight, timeout.Token);
                    return;
                }
                if (lines[0] != $"POST {expectedPath} HTTP/1.1") return;
                var lengthLine = lines.FirstOrDefault(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                if (lengthLine is null || !int.TryParse(lengthLine.AsSpan(lengthLine.IndexOf(':') + 1).Trim(), out var length) || length is < 1 or > 262144) return;
                var body = new byte[length];
                await stream.ReadExactlyAsync(body, timeout.Token);
                received(Encoding.UTF8.GetString(body));
                var response = Encoding.ASCII.GetBytes("HTTP/1.1 204 No Content\r\nAccess-Control-Allow-Origin: *\r\nConnection: close\r\nContent-Length: 0\r\n\r\n");
                await stream.WriteAsync(response, timeout.Token);
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        }
    }

    public void Dispose() { stop.Cancel(); listener.Stop(); }
}
