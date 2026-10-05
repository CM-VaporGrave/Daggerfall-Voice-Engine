using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KokoroSharp;
using KokoroSharp.Core;
using KokoroSharp.Processing;
using KokoroSharp.Utilities;

namespace DaggerfallVoiceEngine;

public sealed class SynthesisService : IDisposable
{
    readonly EngineConfig config;
    readonly string baseDir;
    readonly SemaphoreSlim synthesisGate = new(1, 1);
    readonly KokoroWavSynthesizer synth;
    readonly HashSet<string> warnedMissingVoices = new(StringComparer.OrdinalIgnoreCase);

    public SynthesisService(EngineConfig config, string baseDir)
    {
        this.config = config;
        this.baseDir = baseDir;
        string model = ResolvePath(config.ModelPath);
        if (!File.Exists(model)) throw new FileNotFoundException("Kokoro ONNX model not found.", model);
        // Load the packaged official Kokoro voice catalog explicitly. The release builders
        // copy the NuGet package's content/voices folder into Assets/voices so published
        // player packages do not depend on NuGet's build-output copy behavior.
        string official = ResolvePath(config.OfficialVoicesPath);
        if (!Directory.Exists(official))
            throw new DirectoryNotFoundException("Official Kokoro voice catalog not found: " + official);
        try { KokoroVoiceManager.LoadVoicesFromPath(official); }
        catch (Exception ex) { throw new InvalidOperationException("Official Kokoro voice catalog failed to load.", ex); }

        // Merge optional/custom voices (currently am_granite) after stock voices are present.
        string custom = ResolvePath(config.CustomVoicesPath);
        if (Directory.Exists(custom))
        {
            try { KokoroVoiceManager.LoadVoicesFromPath(custom); }
            catch (Exception ex) { EngineLog.Warn("Custom voice load warning: " + ex.Message); }
        }

        EngineLog.Info("Loaded " + KokoroVoiceManager.Voices.Count + " Kokoro voice(s).");
        synth = KokoroWavSynthesizer.LoadModel(model);
        Directory.CreateDirectory(ResolvePath(config.CachePath));
        EngineLog.Info("Kokoro ONNX model loaded and Daggerfall Voice Engine is warm.");
    }

    public string[] Voices => KokoroVoiceManager.Voices.Select(v => v.Name).OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToArray();

    public async Task<(byte[] Wav, bool CacheHit)> SynthesizeAsync(SynthesisRequest original, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(original.Text)) throw new ArgumentException("text is empty");
        // Work on a request clone so emotional shaping does not mutate the HTTP request object.
        var r = Clone(original);
        AudioDsp.ApplyEmotion(r);
        r.Speed = Math.Clamp(r.Speed, .5f, 2f);
        r.PitchSemitones = Math.Clamp(r.PitchSemitones, -6f, 4f);

        string cacheKey = CacheKey(r);
        string cacheFile = Path.Combine(ResolvePath(config.CachePath), cacheKey + ".wav");
        if (config.EnableReactiveCache && File.Exists(cacheFile))
        {
            try
            {
                File.SetLastAccessTimeUtc(cacheFile, DateTime.UtcNow);
                return (await File.ReadAllBytesAsync(cacheFile, ct), true);
            }
            catch { }
        }

        await synthesisGate.WaitAsync(ct);
        try
        {
            // Another caller might have generated this while we were waiting for the model.
            if (config.EnableReactiveCache && File.Exists(cacheFile))
            {
                File.SetLastAccessTimeUtc(cacheFile, DateTime.UtcNow);
                return (await File.ReadAllBytesAsync(cacheFile, ct), true);
            }

            KokoroVoice voice = ResolveVoice(r.Voice);
            // Pitch post-processing changes duration. Counter-steer model speed so pitch depth does not
            // accidentally turn into a large speaking-rate control as well.
            float pitchRatio = (float)Math.Pow(2.0, r.PitchSemitones / 12.0);
            float synthSpeed = Math.Clamp(r.Speed / Math.Max(.01f, pitchRatio), .5f, 2f);
            var pipeline = new KokoroTTSPipelineConfig { Speed = synthSpeed };
            byte[] pcm;
            try
            {
                pcm = await Task.Run(() => synth.Synthesize(r.Text.Trim(), voice, pipeline), ct);
            }
            catch (IndexOutOfRangeException ex)
            {
                EngineLog.Warn("Kokoro inference indexing fault; retrying once: " + ex.Message);
                pcm = await Task.Run(() => synth.Synthesize(r.Text.Trim(), voice, pipeline), ct);
            }
            float[] samples = AudioDsp.Pcm16ToFloat(pcm);
            float[] processed = AudioDsp.Apply(r, samples, out int sampleRate, out bool eightBit);
            byte[] wav = AudioDsp.FloatToWave(processed, sampleRate, eightBit);

            if (config.EnableReactiveCache)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
                    await File.WriteAllBytesAsync(cacheFile, wav, ct);
                    _ = Task.Run(CleanupCache);
                }
                catch (Exception ex) { EngineLog.Warn("Reactive cache write failed: " + ex.Message); }
            }
            return (wav, false);
        }
        finally { synthesisGate.Release(); }
    }

    KokoroVoice ResolveVoice(string? spec)
    {
        string value = string.IsNullOrWhiteSpace(spec) ? "af_heart" : spec.Trim();
        string[] ids = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (ids.Length == 0) ids = new[] { "af_heart" };

        // Preserve the SharedKokoro shorthand where repeated IDs imply greater weight,
        // but never let one missing optional voice turn a synthesis request into HTTP 500.
        var grouped = ids
            .GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
            .Select(g => new { Id = g.Key, Count = g.Count(), Voice = FindVoice(g.Key) })
            .ToArray();

        foreach (var item in grouped)
            if (item.Voice == null) WarnMissingVoice(item.Id);

        var available = grouped.Where(x => x.Voice != null).ToArray();
        if (available.Length == 0)
            return FallbackVoice();
        if (available.Length == 1)
            return available[0].Voice!;

        return KokoroVoiceManager.Mix(
            available.Select(x => (Voice: x.Voice!, Weight: (float)x.Count)).ToArray());
    }

    KokoroVoice? FindVoice(string id)
    {
        return KokoroVoiceManager.Voices.FirstOrDefault(
            v => string.Equals(v.Name, id, StringComparison.OrdinalIgnoreCase));
    }

    KokoroVoice FallbackVoice()
    {
        KokoroVoice? fallback =
            FindVoice("am_granite") ??
            FindVoice("af_heart") ??
            KokoroVoiceManager.Voices.FirstOrDefault();

        if (fallback == null)
            throw new InvalidOperationException(
                "No Kokoro voices are loaded. Reinstall Daggerfall Voice Engine.");

        return fallback;
    }

    void WarnMissingVoice(string id)
    {
        lock (warnedMissingVoices)
        {
            if (warnedMissingVoices.Add(id))
                EngineLog.Warn("Requested Kokoro voice '" + id +
                    "' is not loaded; using available blend members or fallback.");
        }
    }

    string CacheKey(SynthesisRequest r)
    {
        // Turn/module are deliberately omitted. They coordinate ownership and are not audio identity.
        var id = new {
            text = r.Text.Trim(), voice = r.Voice, lang = r.Lang, speed = r.Speed,
            pitch = r.PitchSemitones, r.Gravel, r.Saturation, r.Presence, r.Compression,
            r.DoubleMix, r.DoublePitchSemitones, r.DoubleDelayMs, r.Spectral, r.Reverb,
            r.SubharmonicMix, r.SubharmonicPitch, r.Hiss, r.ThroatResonance, r.FlutterDepth,
            r.FlutterRate, r.Croak, r.PurrMix, r.PurrRate, r.FelineResonance, r.Growl, r.Breath,
            style = r.AudioStyle, emotion = r.Emotion, emotionIntensity = r.EmotionIntensity,
            schema = "dve-cache-v1"
        };
        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(id));
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    static SynthesisRequest Clone(SynthesisRequest r) => new()
    {
        Text=r.Text, Voice=r.Voice, Lang=r.Lang, Speed=r.Speed, TurnId=r.TurnId, Module=r.Module,
        PitchSemitones=r.PitchSemitones, Gravel=r.Gravel, Saturation=r.Saturation, Presence=r.Presence,
        Compression=r.Compression, DoubleMix=r.DoubleMix, DoublePitchSemitones=r.DoublePitchSemitones,
        DoubleDelayMs=r.DoubleDelayMs, Spectral=r.Spectral, Reverb=r.Reverb, SubharmonicMix=r.SubharmonicMix,
        SubharmonicPitch=r.SubharmonicPitch, Hiss=r.Hiss, ThroatResonance=r.ThroatResonance,
        FlutterDepth=r.FlutterDepth, FlutterRate=r.FlutterRate, Croak=r.Croak, PurrMix=r.PurrMix,
        PurrRate=r.PurrRate, FelineResonance=r.FelineResonance, Growl=r.Growl, Breath=r.Breath,
        AudioStyle=r.AudioStyle, Emotion=r.Emotion, EmotionIntensity=r.EmotionIntensity
    };

    void CleanupCache()
    {
        if (!config.EnableReactiveCache || config.MaxCacheBytes <= 0) return;
        try
        {
            var dir = new DirectoryInfo(ResolvePath(config.CachePath));
            if (!dir.Exists) return;
            var files = dir.GetFiles("*.wav").OrderBy(f => f.LastAccessTimeUtc).ToList();
            long total = files.Sum(f => f.Length);
            long target = (long)(config.MaxCacheBytes * .85);
            foreach (FileInfo f in files)
            {
                if (total <= target) break;
                long len = f.Length;
                try { f.Delete(); total -= len; } catch { }
            }
        }
        catch { }
    }

    string ResolvePath(string path) => Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(baseDir, path));
    public void Dispose() { synthesisGate.Dispose(); synth.Dispose(); }
}
