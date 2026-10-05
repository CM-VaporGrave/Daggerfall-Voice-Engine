namespace DaggerfallVoiceEngine;

public static class EngineLog
{
    static readonly object Gate = new();
    static string? path;
    public static void Configure(string baseDir) => path = Path.Combine(baseDir, "Daggerfall Voice Engine.log");
    public static void Info(string text) => Write("INFO", text);
    public static void Warn(string text) => Write("WARN", text);
    public static void Error(string text) => Write("ERROR", text);
    static void Write(string level, string text)
    {
        string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {text}";
        Console.WriteLine(line);
        if (string.IsNullOrWhiteSpace(path)) return;
        lock (Gate) { try { File.AppendAllText(path, line + Environment.NewLine); } catch { } }
    }
}
