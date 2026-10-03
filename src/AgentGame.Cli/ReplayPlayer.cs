#nullable enable
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentGame.Protocol;
using AgentGame.Runtime.Replay;

namespace AgentGame.Cli;

/// <summary>
/// Interactive playback of a recorded run. Unlike the plain export, this path renders frames
/// through <see cref="TerminalView"/> and accepts pause / single-step / speed / quit keys via
/// <see cref="ReplayControls"/>. It never constructs Core state: the frames come straight from
/// the recorded Observer envelopes, so a recording can be watched without the rules or the Agent.
/// </summary>
internal static class ReplayPlayer
{
    private static readonly UTF8Encoding Utf8 = new(false);

    public static async Task<int> PlayAsync(string path, double speed, CancellationToken cancellationToken)
    {
        var controls = new ReplayControls(speed);
        using Stream output = Console.OpenStandardOutput();
        using var view = new TerminalView(output, SafeSize(Console.WindowWidth, 80), SafeSize(Console.WindowHeight, 24), useColor: true);
        using var reader = new ReplayReader(path);
        view.RenderSnapshot(reader.Header.InitialSnapshot);
        view.Draw();
        WriteHint(controls, output);
        try
        {
            while (reader.ReadNext() is { } record)
            {
                cancellationToken.ThrowIfCancellationRequested();
                controls.PollInput();
                if (controls.QuitRequested) break;
                object? envelope = record switch
                {
                    ReplayStatusRecord status => status.ObserverStatus,
                    ReplayStepRecord step => step.ObserverBatch,
                    _ => null
                };
                if (envelope is null) continue;
                if (!view.TryApply(envelope))
                {
                    // Keep the last trustworthy frame and stop: never render a world assembled
                    // from non-contiguous patches.
                    if (view.NeedsResync)
                    {
                        Console.Error.WriteLine(ProtocolJson.EncodeLine(new { error = "resync_required", reason = view.LastRejectReason, line = reader.LineNumber }));
                        break;
                    }
                    continue;
                }
                TimeSpan delay = controls.NextDelay();
                while (controls.Paused && controls.PendingSteps == 0 && !controls.QuitRequested)
                {
                    controls.PollInput();
                    await Task.Delay(50, cancellationToken);
                }
                if (controls.QuitRequested) break;
                if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
                view.Draw();
                WriteHint(controls, output);
            }
        }
        catch (OperationCanceledException)
        {
            // The operator interrupted playback; the summary below still reports the final position.
        }
        finally
        {
            byte[] restore = Utf8.GetBytes(view.RenderRestoreSequence());
            if (restore.Length > 0) { output.Write(restore); output.Flush(); }
        }
        Console.Error.WriteLine(ProtocolJson.EncodeLine(new ReplaySummary(reader.Status, reader.LastTick, reader.LastSeq, reader.LineNumber)));
        return reader.Status == "incomplete" ? 1 : 0;
    }

    private static void WriteHint(ReplayControls controls, Stream output)
    {
        byte[] bytes = Utf8.GetBytes(controls.RenderHint() + "\n");
        output.Write(bytes);
        output.Flush();
    }

    private static int SafeSize(int value, int fallback)
    {
        try { return value > 0 ? value : fallback; }
        catch (Exception) { return fallback; }
    }
}
