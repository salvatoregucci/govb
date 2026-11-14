using System.IO;
using System.IO.Pipes;
using System.Text.Json;

namespace DynamicIsland.Core;

/// <summary>Local IPC pipeline for the Discord desktop client (Rich Presence / RPC).
/// This is a documented Discord mechanism used by overlays for speaker information.
/// Frames contain a 4-byte opcode, a 4-byte little-endian length, and UTF-8 JSON.</summary>
public class DiscordRpcClient : IDisposable
{
    private NamedPipeClientStream? _pipe;
    private readonly Dictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly object _lock = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private CancellationTokenSource? _pumpCts;

    public event Action<string, JsonElement>? Dispatch;
    public event Action? Disconnected;
    public bool Connected => _pipe?.IsConnected == true;

    public async Task<bool> ConnectAsync(string clientId)
    {
        for (int i = 0; i < 10; i++)
        {
            try
            {
                var pipe = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(250);
                _pipe = pipe;
                break;
            }
            catch { /* bu numarada Discord yok, sıradakini dene */ }
        }
        if (_pipe == null) return false;

        try
        {
            await SendRawAsync(0, JsonSerializer.SerializeToUtf8Bytes(new { v = 1, client_id = clientId }));
            await ReadFrameAsync(); // Consume the READY dispatch.
        }
        catch { _pipe.Dispose(); _pipe = null; return false; }

        _pumpCts = new CancellationTokenSource();
        _ = Task.Run(() => PumpAsync(_pumpCts.Token));
        return true;
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _pipe?.IsConnected == true)
            {
                var frame = await ReadFrameAsync();
                if (!frame.TryGetProperty("cmd", out var cmdEl)) continue;
                var cmd = cmdEl.GetString();
                var nonce = frame.TryGetProperty("nonce", out var n) && n.ValueKind == JsonValueKind.String
                    ? n.GetString() : null;

                if (cmd == "DISPATCH")
                {
                    var evt = frame.TryGetProperty("evt", out var e) ? e.GetString() ?? "" : "";
                    var data = frame.TryGetProperty("data", out var d) ? d : default;
                    Dispatch?.Invoke(evt, data);
                }
                else if (nonce != null)
                {
                    TaskCompletionSource<JsonElement>? tcs;
                    lock (_lock) { _pending.Remove(nonce, out tcs); }
                    var data = frame.TryGetProperty("data", out var d) ? d : default;
                    tcs?.TrySetResult(data);
                }
            }
        }
        catch { /* boru koptu, aşağıda bildirilir */ }
        Disconnected?.Invoke();
    }

    public async Task<JsonElement> RequestAsync(string cmd, object? args = null, string? evt = null)
    {
        var nonce = Guid.NewGuid().ToString();
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock) { _pending[nonce] = tcs; }

        var obj = new Dictionary<string, object?> { ["cmd"] = cmd, ["nonce"] = nonce };
        if (args != null) obj["args"] = args;
        if (evt != null) obj["evt"] = evt;

        await SendRawAsync(1, JsonSerializer.SerializeToUtf8Bytes(obj));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(cmd == "AUTHORIZE" ? 120 : 8));
        await using var reg = cts.Token.Register(() => { lock (_lock) { _pending.Remove(nonce); } tcs.TrySetCanceled(); });
        return await tcs.Task;
    }

    private async Task SendRawAsync(int op, byte[] payload)
    {
        await _writeLock.WaitAsync();
        try
        {
            var header = new byte[8];
            BitConverter.TryWriteBytes(header.AsSpan(0, 4), op);
            BitConverter.TryWriteBytes(header.AsSpan(4, 4), payload.Length);
            await _pipe!.WriteAsync(header);
            await _pipe!.WriteAsync(payload);
            await _pipe!.FlushAsync();
        }
        finally { _writeLock.Release(); }
    }

    private async Task<JsonElement> ReadFrameAsync()
    {
        var header = await ReadExactAsync(8);
        var len = BitConverter.ToInt32(header, 4);
        var payload = await ReadExactAsync(len);
        using var doc = JsonDocument.Parse(payload);
        return doc.RootElement.Clone();
    }

    private async Task<byte[]> ReadExactAsync(int count)
    {
        var buf = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = await _pipe!.ReadAsync(buf.AsMemory(read, count - read));
            if (n == 0) throw new IOException("Discord IPC borusu kapandı");
            read += n;
        }
        return buf;
    }

    public void Dispose()
    {
        _pumpCts?.Cancel();
        try { _pipe?.Dispose(); } catch { }
    }
}
