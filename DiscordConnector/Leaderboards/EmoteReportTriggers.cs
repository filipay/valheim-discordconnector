using System;
using System.Collections.Generic;

namespace DiscordConnector.Leaderboards;

internal sealed class EmoteReportTriggers : IDisposable
{
    internal readonly struct Snapshot
    {
        internal readonly string Character, Name;
        internal readonly uint Revision;
        internal readonly int Id;
        internal Snapshot(string character, uint revision, int id, string name)
        { Character = character; Revision = revision; Id = id; Name = name != null && name.Length <= 16 ? name : ""; }
    }
    internal sealed class Frame
    {
        internal readonly Frame? Previous;
        internal readonly int Depth;
        internal readonly long Epoch;
        internal readonly List<Observation> Pending = new();
        internal bool Failed, Closed;
        internal Frame(Frame? previous, long epoch)
        { Previous = previous; Depth = (previous?.Depth ?? 0) + 1; Epoch = epoch; }
    }
    internal sealed class Observation
    {
        internal readonly string World, Session, Character;
        internal readonly Snapshot Before, After;
        internal readonly double Time;
        internal readonly Action<string> Dispatch;
        internal Observation(string world, string session, string character, Snapshot before, Snapshot after, double time, Action<string> dispatch)
        { World = world; Session = session; Character = character; Before = before; After = after; Time = time; Dispatch = dispatch; }
    }
    private static readonly HashSet<string> NativeEmotes = new(StringComparer.Ordinal)
    {
        "wave", "sit", "challenge", "cheer", "nonono", "thumbsup", "point", "blowkiss", "bow", "cower", "cry", "despair", "flex",
        "comehere", "headbang", "kneel", "laugh", "roar", "shrug", "dance", "relax", "toast", "rest", "vibe", "loveyou"
    };
    private readonly string daily, weekly;
    private readonly Dictionary<(string Session, string Character), (Snapshot State, double Time)> states = new();
    private string world = "";
    private Frame? current;
    private int overflow;
    private long epoch;
    private bool disposed;
    internal bool Enabled { get; private set; }
    internal EmoteReportTriggers(bool enabled, string daily, string weekly, Action<string>? warn = null)
    {
        Enabled = enabled;
        this.daily = Binding(daily); this.weekly = Binding(weekly);
        bool invalid = this.daily.Length == 0 || this.weekly.Length == 0;
        if (this.daily.Length != 0 && this.daily == this.weekly)
        { this.daily = ""; this.weekly = ""; invalid = true; }
        if (invalid)
            try { warn?.Invoke("Manual leaderboard emote bindings invalid or ambiguous; affected bindings disabled."); } catch { }
    }
    private static string Binding(string value)
    { string normalized = (value ?? "").Trim().ToLowerInvariant(); return NativeEmotes.Contains(normalized) ? normalized : ""; }
    internal string? CommandFor(string emote) => !Enabled || disposed ? null :
        daily.Length != 0 && emote == daily ? "!dc daily" : weekly.Length != 0 && emote == weekly ? "!dc weekly" : null;

    internal Frame? Enter()
    {
        if (!Enabled || disposed) return null;
        if (overflow != 0 || current?.Depth >= 16)
        { if (current != null) current.Failed = true; overflow++; return null; }
        return current = new Frame(current, epoch);
    }
    internal void Stage(Frame? frame, string world, string session, string character, Snapshot before, Snapshot after, double time, Action<string> dispatch)
    {
        if (frame == null || frame.Closed || !ReferenceEquals(current, frame) || overflow != 0 || frame.Epoch != epoch) return;
        if (frame.Pending.Count >= 16) { frame.Failed = true; return; }
        frame.Pending.Add(new Observation(world, session, character, before, after, time, dispatch));
    }
    internal void Finish(Frame? frame, bool originalRan, Exception? error)
    {
        if (frame == null)
        { if (overflow != 0) overflow--; return; }
        if (frame.Closed) return;
        frame.Closed = true;
        if (!ReferenceEquals(current, frame)) { current = null; overflow = 0; Reset(); frame.Pending.Clear(); return; }
        current = frame.Previous;
        if (!originalRan || error != null || frame.Failed)
        {
            if (current != null) current.Failed = true;
            Reset(); frame.Pending.Clear(); return;
        }
        if (frame.Epoch != epoch || !Enabled || disposed) { frame.Pending.Clear(); return; }
        if (current != null)
        {
            if (current.Pending.Count + frame.Pending.Count > 16) current.Failed = true;
            else current.Pending.AddRange(frame.Pending);
        }
        else
        {
            foreach (Observation observation in frame.Pending)
            {
                if (frame.Epoch != epoch || !Enabled || disposed) break;
                string? command = Apply(observation);
                if (command == null) continue;
                try { observation.Dispatch(command); }
                catch (Exception failure) { ManualReportDiagnostics.Emit(ManualReportStage.Emote, ManualReportReason.Failed, failure); }
            }
        }
        frame.Pending.Clear();
    }

    private string? Apply(Observation observation)
    {
        Snapshot before = observation.Before, after = observation.After;
        if (observation.World != world || world.Length == 0 || observation.Session.Length == 0 || observation.Character.Length == 0 ||
            after.Character != observation.Character || after.Revision == 0 || double.IsNaN(observation.Time) || double.IsInfinity(observation.Time)) return null;
        bool exists = before.Character == observation.Character;
        if (exists && after.Revision <= before.Revision) return null;
        var key = (observation.Session, observation.Character);
        bool retained = states.TryGetValue(key, out var old) && observation.Time >= old.Time && observation.Time - old.Time <= 300;
        // One snapshot per connection/character, never a revision/ID history or an unbounded eviction queue.
        if (!states.ContainsKey(key) && states.Count >= 128) states.Clear();
        states[key] = (after, observation.Time);
        if (!exists || !retained || old.State.Revision != before.Revision || old.State.Id != before.Id || old.State.Name != before.Name ||
            before.Id < 0 || after.Id <= before.Id) return null;
        return CommandFor(after.Name);
    }

    private void Reset() { states.Clear(); epoch++; }
    internal void SetWorld(string world)
    { if (this.world != world) { this.world = world; Reset(); } }
    internal void SetEnabled(bool enabled)
    { if (Enabled != enabled) { Enabled = enabled && !disposed; Reset(); } }
    public void Dispose() { disposed = true; Enabled = false; Reset(); }
}
