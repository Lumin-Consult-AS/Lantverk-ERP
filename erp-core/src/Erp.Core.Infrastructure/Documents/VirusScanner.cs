using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace Erp.Core.Infrastructure.Documents;

public sealed class ClamAvOptions
{
    public const string SectionName = "ClamAv";

    /// <summary>Host of the clamd daemon (ClamAV container). When empty no scanner is available.</summary>
    public string? Host { get; set; }

    public int Port { get; set; } = 3310;
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
}

public enum ScanOutcome
{
    Clean,
    Infected,

    /// <summary>The scanner could not give an answer. The file must stay unavailable and be scanned again later.</summary>
    Unavailable
}

public sealed record ScanResult(ScanOutcome Outcome, string? Signature = null, string? Error = null);

public interface IVirusScanner
{
    Task<ScanResult> ScanAsync(byte[] content, CancellationToken cancellationToken);
}

/// <summary>Used when no ClamAV host is configured. Nothing is scanned, so nothing becomes available (fail closed).</summary>
public sealed class UnavailableVirusScanner : IVirusScanner
{
    public Task<ScanResult> ScanAsync(byte[] content, CancellationToken cancellationToken) =>
        Task.FromResult(new ScanResult(ScanOutcome.Unavailable, Error: "No virus scanner is configured."));
}

/// <summary>
/// Scans through a ClamAV daemon with the clamd <c>INSTREAM</c> command: the content is sent in length-prefixed
/// chunks and the daemon answers <c>stream: OK</c> or <c>stream: &lt;signature&gt; FOUND</c>.
/// </summary>
public sealed class ClamAvScanner(ClamAvOptions options) : IVirusScanner
{
    private const int ChunkSize = 64 * 1024;

    public async Task<ScanResult> ScanAsync(byte[] content, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout);

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(options.Host!, options.Port, timeout.Token);
            await using var stream = client.GetStream();

            await stream.WriteAsync(Encoding.ASCII.GetBytes("zINSTREAM\0"), timeout.Token);

            var length = new byte[4];
            for (var offset = 0; offset < content.Length; offset += ChunkSize)
            {
                var count = Math.Min(ChunkSize, content.Length - offset);
                BinaryPrimitives.WriteUInt32BigEndian(length, (uint)count);
                await stream.WriteAsync(length, timeout.Token);
                await stream.WriteAsync(content.AsMemory(offset, count), timeout.Token);
            }

            BinaryPrimitives.WriteUInt32BigEndian(length, 0);
            await stream.WriteAsync(length, timeout.Token);
            await stream.FlushAsync(timeout.Token);

            using var response = new MemoryStream();
            var buffer = new byte[512];
            int read;
            while ((read = await stream.ReadAsync(buffer, timeout.Token)) > 0)
            {
                response.Write(buffer, 0, read);
                if (buffer[read - 1] == 0)
                {
                    break;
                }
            }

            return Parse(Encoding.ASCII.GetString(response.ToArray()).TrimEnd('\0', '\n', ' '));
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or ObjectDisposedException)
        {
            return new ScanResult(ScanOutcome.Unavailable, Error: ex.Message);
        }
    }

    internal static ScanResult Parse(string reply)
    {
        // "stream: OK", "stream: Eicar-Test-Signature FOUND", "INSTREAM size limit exceeded. ERROR"
        if (reply.EndsWith(" OK", StringComparison.Ordinal))
        {
            return new ScanResult(ScanOutcome.Clean);
        }

        if (reply.EndsWith(" FOUND", StringComparison.Ordinal))
        {
            var body = reply[..^" FOUND".Length];
            var colon = body.IndexOf(':', StringComparison.Ordinal);
            return new ScanResult(ScanOutcome.Infected, (colon >= 0 ? body[(colon + 1)..] : body).Trim());
        }

        return new ScanResult(ScanOutcome.Unavailable, Error: reply);
    }
}
