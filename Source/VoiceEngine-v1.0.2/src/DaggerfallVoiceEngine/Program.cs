using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

namespace DaggerfallVoiceEngine;

internal static class Program
{
    const string EngineName = "Daggerfall Voice Engine";
    const string EngineVersion = "1.0.2";
    const string MutexName = "DaggerfallVoiceEngine.SingleInstance.v1";
    static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = false };
    static volatile bool shuttingDown;

    static async Task<int> Main(string[] args)
    {
        string baseDir = AppContext.BaseDirectory;
        EngineLog.Configure(baseDir);
        EngineConfig config = LoadConfig(baseDir, args);
        bool selfTest = args.Any(a => string.Equals(a, "--self-test", StringComparison.OrdinalIgnoreCase));
        int parentPid = ReadIntArg(args, "--parent-pid", 0);

        using var instanceMutex = new Mutex(true, MutexName, out bool firstInstance);
        if (!firstInstance)
        {
            EngineLog.Info("Another Daggerfall Voice Engine instance is already running. Exiting cleanly.");
            return 0;
        }

        try
        {
            using var synthesis = new SynthesisService(config, baseDir);
            var turns = new TurnCoordinator(config);
            if (selfTest)
            {
                // Validate both catalogs so a package containing only Granite can never pass.
                if (!synthesis.Voices.Contains("af_heart", StringComparer.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Self-test failed: official voice af_heart is not loaded.");
                if (!synthesis.Voices.Contains("am_granite", StringComparer.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Self-test failed: custom voice am_granite is not loaded.");

                _ = await synthesis.SynthesizeAsync(
                    new SynthesisRequest {
                        Text = "Daggerfall Voice Engine official voice test.",
                        Voice = "af_heart",
                        Emotion = "neutral"
                    }, CancellationToken.None);

                _ = await synthesis.SynthesizeAsync(
                    new SynthesisRequest {
                        Text = "Daggerfall Voice Engine custom voice test.",
                        Voice = "am_granite",
                        Emotion = "neutral"
                    }, CancellationToken.None);

                EngineLog.Info("Offline self-test passed for official and custom voice catalogs.");
                return 0;
            }

            using var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{config.Port}/");
            listener.Start();
            EngineLog.Info($"{EngineName} v{EngineVersion} listening on 127.0.0.1:{config.Port}.");

            if (parentPid > 0)
                _ = Task.Run(() => WatchParentAsync(parentPid, listener));

            Console.CancelKeyPress += (_, e) => { e.Cancel = true; RequestShutdown(listener); };
            while (!shuttingDown)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); }
                catch when (shuttingDown || !listener.IsListening) { break; }
                _ = Task.Run(() => HandleAsync(ctx, config, turns, synthesis, listener));
            }
            EngineLog.Info("Daggerfall Voice Engine stopped.");
            return 0;
        }
        catch (HttpListenerException ex) when (ex.ErrorCode == 183 || ex.Message.Contains("conflict", StringComparison.OrdinalIgnoreCase))
        {
            EngineLog.Info("Voice Engine port is already owned by another process. Exiting cleanly.");
            return 0;
        }
        catch (Exception ex)
        {
            EngineLog.Error(ex.ToString());
            return 1;
        }
    }

    static async Task WatchParentAsync(int pid, HttpListener listener)
    {
        try
        {
            using Process p = Process.GetProcessById(pid);
            while (!shuttingDown && !p.HasExited) await Task.Delay(1500);
        }
        catch { }
        if (!shuttingDown)
        {
            EngineLog.Info("Parent Daggerfall Unity process exited; shutting down Voice Engine.");
            RequestShutdown(listener);
        }
    }

    static void RequestShutdown(HttpListener listener)
    {
        shuttingDown = true;
        try { listener.Stop(); } catch { }
    }

    static async Task HandleAsync(HttpListenerContext ctx, EngineConfig config, TurnCoordinator turns, SynthesisService synthesis, HttpListener listener)
    {
        try
        {
            string path = ctx.Request.Url?.AbsolutePath.TrimEnd('/') ?? "";
            if (path.Length == 0) path = "/";

            if (ctx.Request.HttpMethod == "GET" && path == "/")
            {
                await Json(ctx, new {
                    ok=true,
                    engine=EngineName,
                    version=EngineVersion,
                    backend="KokoroSharp/ONNX",
                    voices=synthesis.Voices.Length,
                    endpoints=new[] { "/health", "/status", "/version", "/voices" }
                }); return;
            }

            if (ctx.Request.HttpMethod == "GET" && path == "/health")
            {
                await Json(ctx, new {
                    ok=true,
                    engine=EngineName,
                    version=EngineVersion,
                    backend="KokoroSharp/ONNX",
                    voices=synthesis.Voices.Length,
                    cache="reactive-only",
                    queue=turns.Snapshot()
                }); return;
            }
            if (ctx.Request.HttpMethod == "GET" && path == "/status")
            {
                await Json(ctx, new { ok=true, engine=EngineName, version=EngineVersion, backend="KokoroSharp/ONNX", voices=synthesis.Voices.Length, queue=turns.Snapshot() }); return;
            }
            if (ctx.Request.HttpMethod == "GET" && path == "/version") { await Json(ctx, new { engine=EngineName, version=EngineVersion }); return; }
            if (ctx.Request.HttpMethod == "GET" && path == "/voices") { await Json(ctx, new { voices=synthesis.Voices }); return; }
            if (ctx.Request.HttpMethod == "POST" && path == "/voices/install-english")
            {
                await Json(ctx, new { ok=true, installed=false, bundled=true, voices=synthesis.Voices }); return;
            }
            if (ctx.Request.HttpMethod == "POST" && path == "/presence/register")
            {
                var req = await Body<PresenceRequest>(ctx); turns.Touch(req.Module); await Json(ctx, new { ok=true, modules=turns.ActiveModules() }); return;
            }
            if (ctx.Request.HttpMethod == "POST" && path == "/turn/acquire")
            {
                VoiceTurn t = turns.Acquire(await Body<TurnAcquireRequest>(ctx)); await Json(ctx, TurnJson(t)); return;
            }
            if (ctx.Request.HttpMethod == "GET" && path.StartsWith("/turn/", StringComparison.OrdinalIgnoreCase))
            {
                string id = path.Substring("/turn/".Length); VoiceTurn? t = turns.Get(id);
                if (t == null) { ctx.Response.StatusCode=404; await Json(ctx,new { error="turn_not_found"}); return; }
                await Json(ctx, TurnJson(t)); return;
            }
            if (ctx.Request.HttpMethod == "POST" && path == "/turn/complete") { bool ok=turns.Complete(await Body<TurnActionRequest>(ctx)); await Json(ctx,new { ok }); return; }
            if (ctx.Request.HttpMethod == "POST" && path == "/turn/cancel") { bool ok=turns.Cancel(await Body<TurnActionRequest>(ctx)); await Json(ctx,new { ok }); return; }
            if (ctx.Request.HttpMethod == "POST" && path == "/turn/cancel-module")
            {
                var req=await Body<TurnActionRequest>(ctx); int cancelled=turns.CancelModule(req.Module); await Json(ctx,new { ok=true, cancelled }); return;
            }
            if (ctx.Request.HttpMethod == "POST" && path == "/synthesize")
            {
                SynthesisRequest req = await Body<SynthesisRequest>(ctx);
                if (string.IsNullOrWhiteSpace(req.Text)) { ctx.Response.StatusCode=400; await Json(ctx,new { error="text_is_empty"}); return; }
                bool managed = !string.IsNullOrWhiteSpace(req.TurnId);
                if (managed && !turns.IsReadyFor(req.TurnId, req.Module)) { ctx.Response.StatusCode=409; await Json(ctx,new { error="turn_not_ready"}); return; }
                if (!managed && config.RequireReadyTurnForManagedClients && !string.Equals(req.Module,"legacy",StringComparison.OrdinalIgnoreCase))
                { ctx.Response.StatusCode=409; await Json(ctx,new { error="managed_client_requires_turn"}); return; }
                var result = await synthesis.SynthesizeAsync(req, CancellationToken.None);
                ctx.Response.StatusCode=200; ctx.Response.ContentType="audio/wav"; ctx.Response.Headers["X-DVE-Cache"] = result.CacheHit ? "hit" : "miss";
                ctx.Response.ContentLength64=result.Wav.Length; await ctx.Response.OutputStream.WriteAsync(result.Wav); ctx.Response.Close(); return;
            }
            if (ctx.Request.HttpMethod == "POST" && path == "/shutdown")
            {
                await Json(ctx,new { ok=true }); RequestShutdown(listener); return;
            }
            ctx.Response.StatusCode=404; await Json(ctx,new { error="not_found", path });
        }
        catch (JsonException ex) { ctx.Response.StatusCode=400; await Json(ctx,new { error="invalid_json", detail=ex.Message }); }
        catch (Exception ex)
        {
            EngineLog.Error(ex.ToString());
            try { ctx.Response.StatusCode=500; await Json(ctx,new { error="engine_error", detail=ex.Message }); } catch { try { ctx.Response.Abort(); } catch { } }
        }
    }

    static object TurnJson(VoiceTurn t) => new { id=t.Id, module=t.Module, priority=t.Priority, category=t.Category, source_key=t.SourceKey, state=t.State.ToString().ToLowerInvariant() };
    static async Task<T> Body<T>(HttpListenerContext ctx) where T:new()
    {
        using var sr = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding ?? Encoding.UTF8);
        string s = await sr.ReadToEndAsync(); return string.IsNullOrWhiteSpace(s) ? new T() : (JsonSerializer.Deserialize<T>(s,JsonOptions) ?? new T());
    }
    static async Task Json(HttpListenerContext ctx, object value)
    {
        byte[] bytes=Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value,JsonOptions)); ctx.Response.ContentType="application/json"; ctx.Response.ContentEncoding=Encoding.UTF8;
        ctx.Response.ContentLength64=bytes.Length; await ctx.Response.OutputStream.WriteAsync(bytes); ctx.Response.Close();
    }

    static EngineConfig LoadConfig(string baseDir, string[] args)
    {
        string configPath=Path.Combine(baseDir,"DaggerfallVoiceEngine.json");
        for(int i=0;i<args.Length-1;i++) if(args[i]=="--config") configPath=Path.GetFullPath(args[++i]);
        EngineConfig cfg=new();
        if(File.Exists(configPath)) try { cfg=JsonSerializer.Deserialize<EngineConfig>(File.ReadAllText(configPath),JsonOptions) ?? cfg; } catch(Exception ex){EngineLog.Warn("Config parse failed; defaults used: "+ex.Message);} 
        for(int i=0;i<args.Length-1;i++) if(args[i]=="--port" && int.TryParse(args[++i],out int p)) cfg.Port=Math.Clamp(p,1,65535);
        return cfg;
    }

    static int ReadIntArg(string[] args, string name, int fallback)
    {
        for (int i=0;i<args.Length-1;i++) if (string.Equals(args[i],name,StringComparison.OrdinalIgnoreCase) && int.TryParse(args[i+1],out int v)) return v;
        return fallback;
    }
}
