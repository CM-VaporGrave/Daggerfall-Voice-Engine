using System.Text.Json.Serialization;

namespace DaggerfallVoiceEngine;

public sealed class EngineConfig
{
    public int Port { get; set; } = 5000;
    public string ModelPath { get; set; } = "Assets/kokoro.onnx";
    public string OfficialVoicesPath { get; set; } = "Assets/voices";
    public string CustomVoicesPath { get; set; } = "Assets/voices-custom";
    public string CachePath { get; set; } = "Cache";
    public bool EnableReactiveCache { get; set; } = true;
    public long MaxCacheBytes { get; set; } = 2147483648L;
    public int ActiveTurnTimeoutSeconds { get; set; } = 180;
    public int DefaultQueuedExpiryMilliseconds { get; set; } = 12000;
    public bool RequireReadyTurnForManagedClients { get; set; } = true;
}

public sealed class TurnAcquireRequest
{
    [JsonPropertyName("module")] public string Module { get; set; } = "unknown";
    [JsonPropertyName("priority")] public int Priority { get; set; } = 50;
    [JsonPropertyName("category")] public string Category { get; set; } = "speech";
    [JsonPropertyName("source_key")] public string SourceKey { get; set; } = "";
    [JsonPropertyName("expires_ms")] public int ExpiresMs { get; set; }
}

public sealed class TurnActionRequest
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("module")] public string Module { get; set; } = "";
}

public sealed class PresenceRequest
{
    [JsonPropertyName("module")] public string Module { get; set; } = "unknown";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
}

public sealed class SynthesisRequest
{
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("voice")] public string Voice { get; set; } = "af_heart";
    [JsonPropertyName("lang")] public string Lang { get; set; } = "auto";
    [JsonPropertyName("speed")] public float Speed { get; set; } = 1f;
    [JsonPropertyName("turn_id")] public string TurnId { get; set; } = "";
    [JsonPropertyName("module")] public string Module { get; set; } = "legacy";
    [JsonPropertyName("pitch_semitones")] public float PitchSemitones { get; set; }
    [JsonPropertyName("gravel")] public float Gravel { get; set; }
    [JsonPropertyName("saturation")] public float Saturation { get; set; }
    [JsonPropertyName("presence")] public float Presence { get; set; }
    [JsonPropertyName("compression")] public float Compression { get; set; }
    [JsonPropertyName("double_mix")] public float DoubleMix { get; set; }
    [JsonPropertyName("double_pitch_semitones")] public float DoublePitchSemitones { get; set; }
    [JsonPropertyName("double_delay_ms")] public float DoubleDelayMs { get; set; }
    [JsonPropertyName("spectral")] public float Spectral { get; set; }
    [JsonPropertyName("reverb")] public float Reverb { get; set; }
    [JsonPropertyName("subharmonic_mix")] public float SubharmonicMix { get; set; }
    [JsonPropertyName("subharmonic_pitch")] public float SubharmonicPitch { get; set; } = -5.5f;
    [JsonPropertyName("hiss")] public float Hiss { get; set; }
    [JsonPropertyName("throat_resonance")] public float ThroatResonance { get; set; }
    [JsonPropertyName("flutter_depth")] public float FlutterDepth { get; set; }
    [JsonPropertyName("flutter_rate")] public float FlutterRate { get; set; }
    [JsonPropertyName("croak")] public float Croak { get; set; }
    [JsonPropertyName("purr_mix")] public float PurrMix { get; set; }
    [JsonPropertyName("purr_rate")] public float PurrRate { get; set; }
    [JsonPropertyName("feline_resonance")] public float FelineResonance { get; set; }
    [JsonPropertyName("growl")] public float Growl { get; set; }
    [JsonPropertyName("breath")] public float Breath { get; set; }
    [JsonPropertyName("audio_style")] public string AudioStyle { get; set; } = "clean";
    [JsonPropertyName("emotion")] public string Emotion { get; set; } = "neutral";
    [JsonPropertyName("emotion_intensity")] public float EmotionIntensity { get; set; }
}
