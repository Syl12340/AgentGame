#nullable enable
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AgentGame.Protocol;
using AgentGame.Runtime.Observer;

namespace AgentGame.Cli;

internal sealed class ConsoleObserverSink : IAsyncDisposable
{
    private readonly ObserverRegistration _registration;
    private readonly Stream _output;
    private readonly TimeSpan _writeTimeout;
    private readonly CancellationTokenSource _cts;

    public Task Completion { get; }
    public string? Error { get; private set; }

    public ConsoleObserverSink(ObserverRegistration registration, Stream output, TimeSpan? writeTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(output);
        _registration = registration;
        _output = output;
        _writeTimeout = writeTimeout ?? TimeSpan.FromSeconds(1);
        if (_writeTimeout <= TimeSpan.Zero || _writeTimeout.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(writeTimeout));
        _cts = new CancellationTokenSource();

        Completion = Task.Run(ConsumeAsync);
    }

    private async Task ConsumeAsync()
    {
        try
        {
            string snapshotJson = ObserverCodec.Encode(_registration.Snapshot);
            await WriteLineAsync(_output, snapshotJson, _writeTimeout, _cts.Token);

            await foreach (var item in _registration.Subscription.Reader.ReadAllAsync(_cts.Token))
            {
                if (_registration.Subscription.RequiresResync)
                {
                    Error = "resync_required";
                    break;
                }

                string itemJson = ObserverCodec.Encode(item);
                await WriteLineAsync(_output, itemJson, _writeTimeout, _cts.Token);

                if (_registration.Subscription.RequiresResync)
                {
                    Error = "resync_required";
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // Clean exit if canceled by own token
        }
        catch (TimeoutException)
        {
            Error = "output_timeout";
        }
        catch (ObserverResyncException)
        {
            Error = "resync_required";
        }
        catch (ChannelClosedException) when (_registration.Subscription.RequiresResync) { Error = "resync_required"; }
        catch (Exception) { Error = "output_error"; }
        finally
        {
            // A detach that raced with our own cancellation must still be reported honestly
            // instead of looking like a clean end of stream.
            if (Error is null && _registration.Subscription.RequiresResync) Error = "resync_required";
            _registration.Subscription.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _registration.Subscription.Dispose();

        try
        {
            await Completion.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (TimeoutException)
        {
            // Bounded wait expired
        }
        catch (Exception)
        {
            // Ignore other exceptions from completion task
        }
        finally
        {
            _cts.Dispose();
        }
    }

    public static async Task WriteLineAsync(Stream output, string json, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (json.Contains('\r') || json.Contains('\n')) throw new ArgumentException("JSON must not contain raw CR/LF.", nameof(json));

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        var token = linkedCts.Token;

        try
        {
            byte[] bytes = new UTF8Encoding(false).GetBytes(json + "\n");
            await output.WriteAsync(bytes, token).AsTask().WaitAsync(token);
            await output.FlushAsync(token).WaitAsync(token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Output operation timed out.");
        }
    }
}
