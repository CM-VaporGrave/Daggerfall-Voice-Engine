using System.Collections.Concurrent;

namespace DaggerfallVoiceEngine;

public enum VoiceTurnState { Queued, Ready, Completed, Cancelled, Expired }

public sealed class VoiceTurn
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Module { get; init; } = "unknown";
    public int Priority { get; init; }
    public string Category { get; init; } = "speech";
    public string SourceKey { get; init; } = "";
    public long Sequence { get; init; }
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
    public DateTime ExpiresUtc { get; init; }
    public DateTime? ReadyUtc { get; set; }
    public VoiceTurnState State { get; set; } = VoiceTurnState.Queued;
}

public sealed class TurnCoordinator
{
    readonly object gate = new();
    readonly Dictionary<string, VoiceTurn> turns = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, DateTime> presence = new(StringComparer.OrdinalIgnoreCase);
    readonly EngineConfig config;
    long sequence;
    string? activeId;

    public TurnCoordinator(EngineConfig config) => this.config = config;

    public VoiceTurn Acquire(TurnAcquireRequest req)
    {
        lock (gate)
        {
            PurgeLocked();
            TouchLocked(req.Module);
            int ttl = req.ExpiresMs > 0 ? Math.Clamp(req.ExpiresMs, 500, 120000) : config.DefaultQueuedExpiryMilliseconds;
            var turn = new VoiceTurn {
                Module = Clean(req.Module, "unknown"), Priority = Math.Clamp(req.Priority, 0, 200),
                Category = Clean(req.Category, "speech"), SourceKey = req.SourceKey ?? "",
                Sequence = ++sequence, ExpiresUtc = DateTime.UtcNow.AddMilliseconds(ttl)
            };
            turns[turn.Id] = turn;
            PromoteLocked();
            return turn;
        }
    }

    public VoiceTurn? Get(string id)
    {
        lock (gate) { PurgeLocked(); return turns.TryGetValue(id ?? "", out var t) ? t : null; }
    }

    public bool IsReadyFor(string id, string module)
    {
        lock (gate)
        {
            PurgeLocked();
            if (!turns.TryGetValue(id ?? "", out var t)) return false;
            TouchLocked(module);
            return t.State == VoiceTurnState.Ready && string.Equals(activeId, t.Id, StringComparison.OrdinalIgnoreCase) &&
                   (string.IsNullOrWhiteSpace(module) || string.Equals(t.Module, module, StringComparison.OrdinalIgnoreCase));
        }
    }

    public bool Complete(TurnActionRequest req) => Finish(req, VoiceTurnState.Completed);
    public bool Cancel(TurnActionRequest req) => Finish(req, VoiceTurnState.Cancelled);

    bool Finish(TurnActionRequest req, VoiceTurnState state)
    {
        lock (gate)
        {
            PurgeLocked();
            if (!turns.TryGetValue(req.Id ?? "", out var t)) return false;
            if (!string.IsNullOrWhiteSpace(req.Module) && !string.Equals(req.Module, t.Module, StringComparison.OrdinalIgnoreCase)) return false;
            t.State = state;
            if (string.Equals(activeId, t.Id, StringComparison.OrdinalIgnoreCase)) activeId = null;
            TouchLocked(req.Module);
            PromoteLocked();
            return true;
        }
    }

    public void Touch(string module)
    {
        lock (gate) { TouchLocked(module); PurgeLocked(); }
    }

    public string[] ActiveModules()
    {
        lock (gate)
        {
            PurgeLocked();
            return presence.Where(p => (DateTime.UtcNow - p.Value).TotalSeconds < 45).Select(p => p.Key).OrderBy(x => x).ToArray();
        }
    }

    public int CancelModule(string module)
    {
        if (string.IsNullOrWhiteSpace(module)) return 0;
        lock (gate)
        {
            PurgeLocked();
            int count = 0;
            foreach (var t in turns.Values.Where(t => string.Equals(t.Module, module.Trim(), StringComparison.OrdinalIgnoreCase) &&
                (t.State == VoiceTurnState.Queued || t.State == VoiceTurnState.Ready)).ToArray())
            {
                t.State = VoiceTurnState.Cancelled;
                if (string.Equals(activeId, t.Id, StringComparison.OrdinalIgnoreCase)) activeId = null;
                count++;
            }
            TouchLocked(module);
            PromoteLocked();
            return count;
        }
    }

    public object Snapshot()
    {
        lock (gate)
        {
            PurgeLocked();
            var active = activeId != null && turns.TryGetValue(activeId, out var a) ? a : null;
            return new {
                active_turn = active == null ? null : new { id = active.Id, module = active.Module, category = active.Category, priority = active.Priority },
                queued = turns.Values.Count(t => t.State == VoiceTurnState.Queued),
                modules = presence.Where(p => (DateTime.UtcNow - p.Value).TotalSeconds < 45).Select(p => p.Key).OrderBy(x => x).ToArray()
            };
        }
    }

    void PurgeLocked()
    {
        var now = DateTime.UtcNow;
        if (activeId != null && turns.TryGetValue(activeId, out var active) && active.State == VoiceTurnState.Ready &&
            active.ReadyUtc.HasValue && (now - active.ReadyUtc.Value).TotalSeconds > config.ActiveTurnTimeoutSeconds)
        {
            active.State = VoiceTurnState.Expired;
            activeId = null;
        }
        foreach (var t in turns.Values)
            if (t.State == VoiceTurnState.Queued && now >= t.ExpiresUtc) t.State = VoiceTurnState.Expired;
        foreach (var key in turns.Where(kv =>
            (kv.Value.State == VoiceTurnState.Completed || kv.Value.State == VoiceTurnState.Cancelled || kv.Value.State == VoiceTurnState.Expired) &&
            (now - kv.Value.CreatedUtc).TotalMinutes > 3).Select(kv => kv.Key).ToArray()) turns.Remove(key);
        foreach (var key in presence.Where(kv => (now - kv.Value).TotalMinutes > 5).Select(kv => kv.Key).ToArray()) presence.Remove(key);
        PromoteLocked();
    }

    void PromoteLocked()
    {
        if (activeId != null) return;
        var next = turns.Values.Where(t => t.State == VoiceTurnState.Queued && DateTime.UtcNow < t.ExpiresUtc)
            .OrderByDescending(t => t.Priority).ThenBy(t => t.Sequence).FirstOrDefault();
        if (next == null) return;
        next.State = VoiceTurnState.Ready;
        next.ReadyUtc = DateTime.UtcNow;
        activeId = next.Id;
    }

    void TouchLocked(string? module)
    {
        if (!string.IsNullOrWhiteSpace(module)) presence[module.Trim()] = DateTime.UtcNow;
    }

    static string Clean(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
