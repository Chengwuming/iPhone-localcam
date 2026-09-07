using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using LocalCam.Contracts;

namespace LocalCam.Desktop;

internal sealed class FramePipeServer : IDisposable
{
    private const uint ResponseMagic = 0x3143504C; // "LPC1"
    private const int ResponseHeaderLength = 16;
    private readonly CancellationTokenSource lifetime = new();
    private readonly object frameLock = new();
    private readonly byte[] latestFrame;
    private readonly Task serverTask;
    private readonly ConcurrentDictionary<int, Task> clients = new();
    private readonly ConcurrentDictionary<int, NamedPipeServerStream> clientPipes = new();
    private NamedPipeServerStream? pendingPipe;
    private int nextClientId;
    private bool hasFrame;
    private long timestamp100Nanoseconds;

    public FramePipeServer(int payloadLength)
    {
        latestFrame = new byte[payloadLength];
        // Keep pipe I/O off the WPF dispatcher. Disposal happens while the main
        // window is closing; capturing that dispatcher here would deadlock when
        // Dispose synchronously waits for the accept loop to finish.
        serverTask = Task.Run(() => RunAsync(lifetime.Token));
    }

    public void Publish(byte[] frame, long timestamp)
    {
        lock (frameLock)
        {
            frame.CopyTo(latestFrame, 0);
            timestamp100Nanoseconds = timestamp;
            hasFrame = true;
        }
    }

    public void Clear()
    {
        lock (frameLock)
        {
            hasFrame = false;
            timestamp100Nanoseconds = 0;
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreatePipe();
                pendingPipe = pipe;
                await pipe.WaitForConnectionAsync(cancellationToken);
                if (ReferenceEquals(pendingPipe, pipe))
                {
                    pendingPipe = null;
                }
                var clientId = Interlocked.Increment(ref nextClientId);
                clientPipes[clientId] = pipe;
                clients[clientId] = ServeClientOwnedAsync(clientId, pipe, cancellationToken);
                pipe = null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (IOException)
            {
                // The camera host closed or restarted. Accept its next connection.
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            finally
            {
                if (ReferenceEquals(pendingPipe, pipe))
                {
                    pendingPipe = null;
                }
                pipe?.Dispose();
            }
        }
    }

    private async Task ServeClientOwnedAsync(
        int clientId,
        NamedPipeServerStream pipe,
        CancellationToken cancellationToken)
    {
        await using (pipe)
        {
            try
            {
                await ServeClientAsync(pipe, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
            }
            finally
            {
                clientPipes.TryRemove(clientId, out _);
                clients.TryRemove(clientId, out _);
            }
        }
    }

    private async Task ServeClientAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        var request = new byte[1];
        var response = new byte[ResponseHeaderLength + latestFrame.Length];
        while (pipe.IsConnected && !cancellationToken.IsCancellationRequested)
        {
            var received = await pipe.ReadAsync(request, cancellationToken);
            if (received == 0)
            {
                return;
            }

            var responseLength = ResponseHeaderLength;
            BinaryPrimitives.WriteUInt32LittleEndian(response, ResponseMagic);
            lock (frameLock)
            {
                if (hasFrame)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(4), (uint)latestFrame.Length);
                    BinaryPrimitives.WriteInt64LittleEndian(response.AsSpan(8), timestamp100Nanoseconds);
                    latestFrame.CopyTo(response, ResponseHeaderLength);
                    responseLength += latestFrame.Length;
                }
                else
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(4), 0);
                    BinaryPrimitives.WriteInt64LittleEndian(response.AsSpan(8), 0);
                }
            }

            await pipe.WriteAsync(response.AsMemory(0, responseLength), cancellationToken);
            await pipe.FlushAsync(cancellationToken);
        }
    }

    private NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("Current user SID is unavailable."),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            FrameIpcContract.FramePipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            4096,
            ResponseHeaderLength + latestFrame.Length,
            security);
    }

    public void Dispose()
    {
        lifetime.Cancel();
        Interlocked.Exchange(ref pendingPipe, null)?.Dispose();
        foreach (var pipe in clientPipes.Values)
        {
            pipe.Dispose();
        }
        try
        {
            serverTask.GetAwaiter().GetResult();
            Task.WhenAll(clients.Values).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
        lifetime.Dispose();
    }
}
