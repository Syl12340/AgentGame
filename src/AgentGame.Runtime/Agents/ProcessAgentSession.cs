using System.ComponentModel;
using System.Diagnostics;
using AgentGame.Protocol;

namespace AgentGame.Runtime.Agents;

/// <summary>Single-owner request/response session; only stderr draining runs concurrently.</summary>
internal sealed class ProcessAgentSession
{
    private readonly Process _process;
    private readonly AgentTimeouts _timeouts;
    private readonly JsonLineTransport _transport;
    private readonly BoundedByteTail _stderr = new(64 * 1024);
    private readonly CancellationTokenSource _drainCancellation = new();
    private readonly Task _stderrDrain;
    private string? _stderrError;
    private bool _closed;

    private ProcessAgentSession(Process process, AgentTimeouts timeouts)
    {
        _process = process;
        _timeouts = timeouts;
        _transport = new(process.StandardOutput.BaseStream, process.StandardInput.BaseStream);
        _stderrDrain = DrainStderrAsync();
    }

    public int ProcessId => _process.Id;
    public string StderrTail => _stderr.Read();

    public static ProcessAgentSession Start(AgentCommand command, AgentTimeouts timeouts)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.Executable) || command.Executable.Contains('\0'))
            throw new ArgumentException("Agent executable is required.", nameof(command));
        ArgumentNullException.ThrowIfNull(command.Arguments);
        string[] arguments = command.Arguments.ToArray();
        if (arguments.Any(a => a is null || a.Contains('\0'))) throw new ArgumentException("Invalid Agent argument.", nameof(command));
        var start = new ProcessStartInfo(command.Executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = command.WorkingDirectory ?? Environment.CurrentDirectory
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new Win32Exception("Agent process did not start.");
            return new(process, timeouts);
        }
        catch (Exception error) when (error is Win32Exception or IOException or InvalidOperationException)
        {
            process.Dispose();
            throw new AgentSessionException("start_failed", "start", error.Message);
        }
    }

    public Task<ReadyResponse> HandshakeAsync(CancellationToken cancellationToken) => ExchangeAsync(
        new HelloMessage(), bytes => AgentResponseParser.ParseReady(bytes), "handshake", null, _timeouts.Handshake, cancellationToken);

    public Task<ActionResponse> DecideAsync(ObservationMessage message, CancellationToken cancellationToken) => ExchangeAsync(
        message, bytes => AgentResponseParser.ParseAction(bytes, message.RequestId),
        "decision", message.RequestId, _timeouts.Decision, cancellationToken);

    private async Task<T> ExchangeAsync<T>(object message, Func<byte[], T> parse, string phase, string? requestId,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            RejectBufferedOutput(phase, requestId);
            // One deadline covers encoding, write, flush and the complete response line.
            byte[] bytes = ProtocolJson.EncodeLineUtf8(message);
            await _transport.WriteLineAsync(bytes, deadline.Token);
            byte[] response = await _transport.ReadLineAsync(deadline.Token);
            RejectBufferedOutput(phase, requestId);
            T result = parse(response);
            deadline.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AgentSessionException(phase == "handshake" ? "handshake_timeout" : "decision_timeout",
                phase, "Agent send/response deadline exceeded.", requestId);
        }
        catch (JsonLineException error) { throw new AgentSessionException(error.Code, phase, error.Message, requestId); }
        catch (ProtocolException error) { throw new AgentSessionException("protocol_violation", phase, error.Message, requestId); }
        catch (EndOfStreamException) { throw new AgentSessionException("agent_exited", phase, "Agent stdout ended before a complete response.", requestId); }
        catch (IOException error) { throw new AgentSessionException("transport_error", phase, error.Message, requestId); }
    }

    public async Task EndAsync(EpisodeEndMessage message, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeouts.Decision);
        try
        {
            RejectBufferedOutput("episode_end", message.RequestId);
            await _transport.WriteLineAsync(ProtocolJson.EncodeLineUtf8(message), deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new AgentSessionException("decision_timeout", "episode_end", "Agent end-message deadline exceeded.", message.RequestId); }
        catch (JsonLineException error) { throw new AgentSessionException(error.Code, "episode_end", error.Message, message.RequestId); }
        catch (IOException error) { throw new AgentSessionException("transport_error", "episode_end", error.Message, message.RequestId); }
    }

    private void RejectBufferedOutput(string phase, string? requestId)
    {
        if (_transport.HasBufferedData)
            throw new AgentSessionException("protocol_violation", phase, "Duplicate or unsolicited Agent stdout bytes.", requestId);
    }

    private async Task DrainStderrAsync()
    {
        byte[] buffer = new byte[4096];
        try
        {
            while (true)
            {
                int count = await _process.StandardError.BaseStream.ReadAsync(buffer, _drainCancellation.Token);
                if (count == 0) return;
                _stderr.Append(buffer.AsSpan(0, count));
            }
        }
        catch (OperationCanceledException) when (_drainCancellation.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_drainCancellation.IsCancellationRequested) { }
        catch (IOException error) { _stderrError = error.Message; }
    }

    public async Task<ShutdownInfo> CloseAsync(bool graceful)
    {
        if (_closed) throw new InvalidOperationException("Session already closed.");
        _closed = true;
        bool forced = false;
        AgentFailure? error = null;
        using var drainDeadline = new CancellationTokenSource(_timeouts.ShutdownGrace + _timeouts.KillWait);
        Task<bool>? trailing = graceful ? _transport.ReadTrailingByteAsync(drainDeadline.Token) : null;
        try
        {
            try { _process.StandardInput.Close(); }
            catch (IOException) { }
            if (graceful && !_process.HasExited)
            {
                using var grace = new CancellationTokenSource(_timeouts.ShutdownGrace);
                try { await _process.WaitForExitAsync(grace.Token); }
                catch (OperationCanceledException) { }
            }
            if (!_process.HasExited)
            {
                forced = true;
                try { _process.Kill(entireProcessTree: true); }
                catch (Exception failure) when (failure is Win32Exception or InvalidOperationException or AggregateException)
                {
                    if (!_process.HasExited) error = new("cleanup_failed", "shutdown", failure.Message);
                }
                using var killWait = new CancellationTokenSource(_timeouts.KillWait);
                try { await _process.WaitForExitAsync(killWait.Token); }
                catch (OperationCanceledException) { error = new("shutdown_timeout", "shutdown", "Agent did not exit after forced termination."); }
            }
            if (trailing is not null)
            {
                try
                {
                    if (await trailing.WaitAsync(drainDeadline.Token))
                        error ??= new("protocol_violation", "shutdown", "Unexpected Agent stdout after the last response.");
                }
                catch (OperationCanceledException) { }
                catch (IOException) { }
            }
            // Descendants retaining pipe handles cannot make the owner wait indefinitely.
            try { await _stderrDrain.WaitAsync(drainDeadline.Token); }
            catch (OperationCanceledException) { }
            _drainCancellation.Cancel();
            _process.StandardError.Close();
            _process.StandardOutput.Close();
            try { await _stderrDrain.WaitAsync(_timeouts.KillWait); }
            catch (TimeoutException) { error ??= new("cleanup_failed", "shutdown", "Agent stderr drain did not stop."); }
            if (_stderrError is not null) error ??= new("stderr_read_failed", "shutdown", _stderrError);
            int? exitCode = _process.HasExited ? _process.ExitCode : null;
            if (graceful && !forced && exitCode is not null and not 0)
                error ??= new("agent_exited", "shutdown", $"Agent exited with code {exitCode}.");
            return new(exitCode, forced, error);
        }
        finally { _drainCancellation.Cancel(); _drainCancellation.Dispose(); _process.Dispose(); }
    }
}
