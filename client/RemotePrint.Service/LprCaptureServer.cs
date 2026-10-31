using System.Net;
using System.Net.Sockets;
using System.Text;
using RemotePrint.Client;

namespace RemotePrint.Service;

public sealed record LprTarget(string QueueId, string JobId);

// Windows' standard LPR monitor waits for this ACK. Only acknowledge the data
// file after it has been committed to the service spool, never merely to TCP.
public sealed class LprCaptureServer(string root,
    Func<string, CancellationToken, Task<LprTarget>> resolve,
    Func<LprTarget, string, long, CancellationToken, Task> commit,
    Action<string> log, int port = 515)
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, port);
    private readonly SemaphoreSlim _slots = new(2);
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public async Task RunAsync(CancellationToken ct)
    {
        var tasks = new List<Task>(); _listener.Start();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(ct);
                try { await _slots.WaitAsync(ct); }
                catch { client.Dispose(); throw; }
                tasks.RemoveAll(t => t.IsCompleted); tasks.Add(HandleAsync(client, ct));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { _listener.Stop(); await Task.WhenAll(tasks); }
    }
    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromMinutes(10)); var token = timeout.Token;
            var scratch = Path.Combine(root, "capture-" + Guid.NewGuid().ToString("N") + ".part");
            var stream = client.GetStream();
            try
            {
                var command = await ReadLineAsync(stream, token);
                if (command.Length > 1 && command[0] is 3 or 4)
                {
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("no entries\n"), token);
                    return;
                }
                if (command.Length < 2 || command[0] != 2) throw new InvalidDataException("Expected LPR receive-job command");
                var queue = Encoding.ASCII.GetString(command.AsSpan(1));
                var target = await resolve(queue, token);
                await stream.WriteAsync(new byte[] { 0 }, token);
                var control = false; var data = false;
                while (!control || !data)
                {
                    command = await ReadLineAsync(stream, token);
                    if (command.Length < 3 || command[0] is not (2 or 3)) throw new InvalidDataException("Invalid LPR subcommand");
                    var header = Encoding.ASCII.GetString(command.AsSpan(1)).Split(' ', 2);
                    if (header.Length != 2 || !long.TryParse(header[0], out var bytes) || bytes < 1 || bytes > NativePrintProtocol.MaxBytes ||
                        (command[0] == 2 && (control || bytes > 65536)) || (command[0] == 3 && data)) throw new InvalidDataException("Invalid LPR payload size/order");
                    await stream.WriteAsync(new byte[] { 0 }, token);
                    if (command[0] == 2)
                    {
                        var buffer = new byte[(int)bytes]; await stream.ReadExactlyAsync(buffer, token); control = true;
                    }
                    else
                    {
                        if (Directory.GetFiles(root, "*.raw").Sum(f => new FileInfo(f).Length) > NativePrintProtocol.MaxBytes * 4)
                            throw new IOException("Native RAW spool is full");
                        await using (var file = File.Create(scratch))
                        {
                            var buffer = new byte[65536]; var remaining = bytes;
                            while (remaining > 0)
                            {
                                var count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token);
                                if (count == 0) throw new EndOfStreamException();
                                await file.WriteAsync(buffer.AsMemory(0, count), token); remaining -= count;
                            }
                            file.Flush(true);
                        }
                        data = true;
                    }
                    var end = new byte[1]; await stream.ReadExactlyAsync(end, token);
                    if (end[0] != 0) throw new InvalidDataException("LPR payload terminator missing");
                    if (command[0] == 3) await commit(target, scratch, bytes, token);
                    await stream.WriteAsync(new byte[] { 0 }, token);
                }
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested) log("LPR capture: " + ex.Message);
                try { await stream.WriteAsync(new byte[] { 1 }, CancellationToken.None); } catch { }
            }
            finally { if (File.Exists(scratch)) File.Delete(scratch); _slots.Release(); }
        }
    }
    private static async Task<byte[]> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        using var result = new MemoryStream(); var one = new byte[1];
        while (result.Length < 1024)
        {
            await stream.ReadExactlyAsync(one, ct);
            if (one[0] == 10) return result.ToArray();
            result.WriteByte(one[0]);
        }
        throw new InvalidDataException("LPR command too long");
    }
}
