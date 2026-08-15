using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Jalyro.Convert.Host;

/// <summary>
/// Listens on \\.\pipe\Jalyro.Convert.Host for job-file paths pushed by the
/// shell extension.
///
/// Open question this exists to answer: named pipes live in NPFS, which is a
/// different namespace from the BaseNamedObjects one we proved is shared
/// across the package boundary. So a pipe created by this (unpackaged) Host
/// may or may not be reachable from the packaged shell extension. If it is
/// not, the shell falls back to leaving the job in the spool and the directory
/// watcher picks it up - slower, but correct either way.
/// </summary>
internal sealed class PipeServer : IDisposable
{
    public const string PipeName = "Jalyro.Convert.Host";

    private readonly CancellationTokenSource _cts = new();
    private readonly Action<string> _onJobPath;
    private Task? _loop;
    private Task? _consumer;

    /// <summary>
    /// Ordered handoff between the accept loop and the handler.
    ///
    /// Invoking the handler inline blocked the next accept; firing it as an
    /// untracked Task.Run fixed that but lost ORDER - jobs could be queued in a
    /// different sequence from the right-clicks - and left handlers running
    /// after shutdown, able to claim and delete manifests the queue would never
    /// process. A single-consumer channel keeps both properties.
    /// </summary>
    private readonly Channel<string> _inbox =
        Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });

    public PipeServer(Action<string> onJobPath)
    {
        _onJobPath = onJobPath;
    }

    public void Start()
    {
        _loop = Task.Run(() => ListenLoopAsync(_cts.Token));
        _consumer = Task.Run(() => ConsumeAsync(_cts.Token));
        Storage.Log($"PipeServer: listening on \\\\.\\pipe\\{PipeName}");
    }

    private async Task ConsumeAsync(CancellationToken token)
    {
        // Deliberately NOT passing the cancellation token to ReadAllAsync: the
        // channel is completed at shutdown, and the consumer must drain what is
        // already queued rather than abandon it.
        try
        {
            await foreach (string payload in _inbox.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try { _onJobPath(payload); }
                catch (Exception ex)
                {
                    Storage.Log($"PipeServer: handler threw {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    /// <summary>
    /// True if a listener is currently published under our pipe name.
    ///
    /// Used to detect a half-dead Host: one that claimed the singleton mutex
    /// but never got its listener up. Without this check that instance would
    /// hold the mutex, refuse to let a healthy Host start, and silently
    /// blackhole every conversion - which is exactly the failure mode seen
    /// while debugging v0.2.1.
    /// </summary>
    /// <summary>A job path is a path; nothing legitimate approaches this.</summary>
    private const int MaxPayloadBytes = 64 * 1024;

    private static async Task<string> ReadBoundedAsync(
        NamedPipeServerStream server, CancellationToken token)
    {
        using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        readTimeout.CancelAfter(TimeSpan.FromSeconds(10));

        var buffer = new byte[4096];
        var collected = new List<byte>(4096);

        try
        {
            while (true)
            {
                int read = await server.ReadAsync(buffer.AsMemory(), readTimeout.Token)
                                       .ConfigureAwait(false);
                if (read == 0)
                    break;

                collected.AddRange(new ArraySegment<byte>(buffer, 0, read));

                // Reject the whole message rather than silently truncating it.
                // A truncated path is still a path, and acting on half of one
                // is worse than refusing.
                if (collected.Count > MaxPayloadBytes)
                {
                    Storage.Log($"PipeServer: payload exceeded {MaxPayloadBytes} bytes - rejected.");
                    return string.Empty;
                }
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            Storage.Log("PipeServer: client did not finish sending within 10s - dropped.");
            return string.Empty;
        }

        return Encoding.UTF8.GetString(collected.ToArray()).Trim();
    }

    /// <summary>
    /// Writes a bare verb (not a job path) to a running Host. Used by
    /// --settings so the Start Menu shortcut reaches the resident instance
    /// instead of exiting silently.
    /// </summary>
    public static bool SendVerb(string verb)
    {
        try
        {
            using var client = new NamedPipeClientStream(
                ".", PipeName, PipeDirection.Out);

            client.Connect(2000);

            byte[] payload = Encoding.UTF8.GetBytes("VERB " + verb);
            client.Write(payload, 0, payload.Length);
            client.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsListenerPresent()
    {
        try
        {
            foreach (string entry in Directory.GetFiles(@"\\.\pipe\"))
            {
                if (entry.EndsWith(PipeName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch
        {
            // Enumerating the pipe namespace can fail; assume nothing is there.
        }
        return false;
    }

    private async Task ListenLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(token).ConfigureAwait(false);

                // Bounded read with a timeout. A client that connects and never
                // closes would otherwise hold the only server instance in
                // ReadToEndAsync forever, and settings forwarding would stop
                // working entirely.
                string payload = await ReadBoundedAsync(server, token).ConfigureAwait(false);

                if (payload.Length > 0)
                {
                    Storage.Log($"PipeServer: received '{payload}'");

                    // Queue it and loop immediately: the accept stays
                    // responsive, and the single consumer preserves order.
                    await _inbox.Writer.WriteAsync(payload, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Storage.Log($"PipeServer: {ex.GetType().Name}: {ex.Message}");
                await Task.Delay(500, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    public void Dispose()
    {
        try
        {
            // Order matters. Cancelling first passed a cancelled token to
            // ReadAllAsync, so anything already queued was abandoned rather
            // than drained - the comment claimed shutdown awaited pending
            // handlers, which was stronger than the implementation.
            //
            // Stop the producer, close the channel, let the consumer finish,
            // and only then cancel.
            _cts.Cancel();          // stops the accept loop
            _loop?.Wait(2000);

            _inbox.Writer.TryComplete();

            // Wait for a COMPLETE drain, with no cap. Any cap - five seconds,
            // thirty - leaves the consumer able to touch the queue after
            // Dispose returns and Program has disposed it, which is precisely
            // the race the channel was introduced to remove.
            //
            // This cannot hang: the writer is complete, so ReadAllAsync ends
            // once the queue empties, and each handler only claims a file and
            // enqueues it - bounded work that never waits on a conversion.
            if (_consumer is not null)
            {
                while (!_consumer.Wait(TimeSpan.FromSeconds(10)))
                    Storage.Log("PipeServer: still draining queued pipe messages...");
            }
        }
        catch
        {
            // Shutdown is best effort.
        }
        _cts.Dispose();
    }
}
