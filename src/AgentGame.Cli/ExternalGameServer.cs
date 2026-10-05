using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using AgentGame.Protocol;
using AgentGame.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentGame.Cli;

/// <summary>Loopback transport; game ownership and authority recording live in Runtime.</summary>
internal static class ExternalGameServer
{
    public static async Task<int> RunAsync(string[] args)
    {
        string? scenario = null, record = null;
        int port = 8765;
        bool tui = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                string key = args[i];
                if (!seen.Add(key)) throw new ArgumentException("Duplicate serve option.");
                if (key == "--tui") { tui = true; continue; }
                if (key is not ("--scenario" or "--port" or "--record") || i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException("Unknown or missing serve option.");
                string value = args[++i];
                if (key == "--scenario") scenario = value;
                else if (key == "--record") record = value;
                else if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 0 or > 65535)
                    throw new ArgumentException("--port must be in [0, 65535]; 0 selects an available port.");
            }
            if (string.IsNullOrWhiteSpace(scenario)) throw new ArgumentException("serve requires --scenario <file>.");
            if (tui && (Console.IsOutputRedirected || Console.IsInputRedirected))
                throw new ArgumentException("serve --tui requires an interactive terminal.");
        }
        catch (ArgumentException error) { Console.Error.WriteLine(error.Message); return 2; }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += handler;
        ExternalGameSession? session = null;
        WebApplication? app = null;
        TerminalObserverSink? terminal = null;
        using Stream output = Console.OpenStandardOutput();
        int exitCode = 0;
        try
        {
            // Kestrel binds a normal user socket; no HttpListener URL ACL or admin initialization.
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(server =>
            {
                server.Listen(IPAddress.Loopback, port, listen => listen.Protocols = HttpProtocols.Http1);
                server.Limits.MaxRequestBodySize = ProtocolLimits.MaxLineBytes;
                server.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
                server.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
            });
            app = builder.Build();
            session = await ExternalGameSession.CreateAsync(ScenarioService.Read(scenario!), new RunOptions
            {
                RecordPath = record,
                ObserverReady = tui ? hub => terminal = new TerminalObserverSink(hub.Register(), output,
                    interactiveControls: false) : null
            }, cancellation.Token);
            ExternalGameSession ownedSession = session;
            app.Use(async (context, next) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                // Browser-originated requests are not the external Agent control channel.
                if (context.Request.Headers.ContainsKey("Origin") ||
                    context.Request.Headers["Sec-Fetch-Site"] == "cross-site")
                {
                    await ErrorAsync(context, 403, "origin_rejected", "Use a direct local Agent client.");
                    return;
                }
                try { await next(context); }
                catch (ExternalSessionException error)
                { await ErrorAsync(context, error.StatusCode, error.Code, error.Message); }
                catch (Microsoft.AspNetCore.Http.BadHttpRequestException error)
                { await ErrorAsync(context, error.StatusCode, "invalid_request", "Invalid or oversized HTTP request."); }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
                catch (IOException) when (context.RequestAborted.IsCancellationRequested) { }
                catch (Exception)
                { await ErrorAsync(context, 500, "server_error", "The request could not be completed; refresh session status."); }
            });
            app.MapGet("/v1/session", async (HttpContext context) =>
                await JsonAsync(context, ProtocolJson.EncodeLine(await ownedSession.GetStatusAsync(context.RequestAborted))));
            app.MapGet("/v1/observation", async (HttpContext context) =>
                await JsonAsync(context, await ownedSession.ObserveJsonAsync(context.RequestAborted)));
            app.MapPost("/v1/actions", async (HttpContext context) =>
            {
                if (context.Request.ContentType?.Split(';')[0].Trim().Equals("application/json", StringComparison.OrdinalIgnoreCase) != true)
                {
                    await ErrorAsync(context, 415, "content_type", "Send application/json encoded as UTF-8.");
                    return;
                }
                byte[] bytes = new byte[ProtocolLimits.MaxLineBytes + 1];
                int count = 0;
                while (count < bytes.Length)
                {
                    int read = await context.Request.Body.ReadAsync(bytes.AsMemory(count), context.RequestAborted);
                    if (read == 0) break;
                    count += read;
                }
                if (count > ProtocolLimits.MaxLineBytes)
                {
                    await ErrorAsync(context, 413, "request_too_large", "Action JSON exceeds 64 KiB.");
                    return;
                }
                // Runtime checks cancellation before acceptance, then finishes accepted authority writes.
                string next = await ownedSession.SubmitJsonAsync(bytes.AsMemory(0, count), context.RequestAborted);
                await JsonAsync(context, next);
            });
            app.MapFallback(async (HttpContext context) =>
            {
                bool known = context.Request.Path is var path && (path == "/v1/session" || path == "/v1/observation" || path == "/v1/actions");
                if (known) context.Response.Headers.Allow = context.Request.Path == "/v1/actions" ? "POST" : "GET";
                await ErrorAsync(context, known ? 405 : 404, known ? "method_not_allowed" : "not_found",
                    "Use GET /v1/session, GET /v1/observation or POST /v1/actions.");
            });
            await app.StartAsync(cancellation.Token);
            string url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            ExternalSessionStatus status = await session.GetStatusAsync(cancellation.Token);
            Console.Error.WriteLine(JsonSerializer.Serialize(new
            {
                format = "external-server/1", url, session_id = status.SessionId,
                state = status.State, tick = status.Tick
            }));
            // Agent disconnects and rule ends do not stop this server. Ctrl+C owns its lifetime.
            await app.WaitForShutdownAsync(cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { exitCode = 130; }
        catch (Exception error)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { error = "serve_failed", detail = error.Message }));
            exitCode = 1;
        }
        finally
        {
            if (app is not null)
            {
                try { using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10)); await app.StopAsync(stop.Token); }
                catch (Exception) { exitCode = 1; }
            }
            if (session is not null)
            {
                try { await session.DisposeAsync(); }
                catch (Exception error)
                { Console.Error.WriteLine(JsonSerializer.Serialize(new { error = "record_error", detail = error.Message })); exitCode = 1; }
            }
            if (terminal is not null)
            {
                try { await terminal.Completion.WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (TimeoutException) { }
                await terminal.DisposeAsync();
                if (terminal.Error is not null)
                    Console.Error.WriteLine(JsonSerializer.Serialize(new { observer_error = terminal.Error }));
            }
            if (app is not null) await app.DisposeAsync();
            Console.CancelKeyPress -= handler;
        }
        return exitCode;
    }

    private static Task JsonAsync(HttpContext context, string json)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        return context.Response.WriteAsync(json, Encoding.UTF8, context.RequestAborted);
    }

    private static Task ErrorAsync(HttpContext context, int status, string code, string detail)
    {
        if (context.Response.HasStarted) return Task.CompletedTask;
        context.Response.StatusCode = status;
        return JsonAsync(context, JsonSerializer.Serialize(new { error = new { code, detail } }));
    }
}
