using System;
using System.Collections.Generic;
using System.Reflection;

using DiscordConnector.Records;

using HarmonyLib;

namespace DiscordConnector.Patches;

// Actual server ownership and accepted slot transitions are required for attribution.
[HarmonyPatch]
internal static class CookingPatches
{
    internal sealed class Invocation
    {
        internal Invocation? Previous;
        internal CookingSlotSnapshot[] Before = Array.Empty<CookingSlotSnapshot>();
        internal CookingSlotSnapshot[] After = Array.Empty<CookingSlotSnapshot>();
        internal CookingConversion?[] Conversions = Array.Empty<CookingConversion?>();
        internal ActivityActor? Actor;
        internal string Input = "";
        internal bool Add, Update, Ran, Failed;
        internal readonly List<Action> Pending = new();
    }
    [ThreadStatic] private static Invocation? _current;
    [ThreadStatic] private static int _depth;
    private static bool _warnedSlots, _warnedConversion;

    [HarmonyTargetMethods]
    internal static IEnumerable<MethodBase> Targets() => new[]
    {
        AccessTools.Method(typeof(CookingStation), nameof(CookingStation.RPC_AddItem)),
        AccessTools.Method(typeof(CookingStation), nameof(CookingStation.UpdateCooking)),
        AccessTools.Method(typeof(CookingStation), nameof(CookingStation.RPC_RemoveDoneItem)),
        AccessTools.Method(typeof(CookingStation), nameof(CookingStation.DropAllItems))
    };

    [HarmonyPrefix, HarmonyPriority(Priority.Last)]
    internal static void Prefix(CookingStation __instance, MethodBase __originalMethod, object[] __args, out Invocation? __state)
    {
        __state = null;
        var runtime = ActivityRuntime.Current;
        if (runtime == null) return;
        try
        {
            runtime.Cooking.SetAvailability(runtime.Options.CookingCompletions);
            if (!Eligible(__instance, runtime)) return;
            if (_depth >= 4096)
            { if (_current != null) _current.Failed = true; runtime.Cooking.Reset(); return; }
            var frame = new Invocation { Previous = _current, Before = Slots(__instance, runtime.Cooking.CollectorEpoch),
                Add = __originalMethod.Name == nameof(CookingStation.RPC_AddItem),
                Update = __originalMethod.Name == nameof(CookingStation.UpdateCooking) };
            if (frame.Before.Length == 0) return;
            for (var parent = _current; parent != null; parent = parent.Previous)
                if (parent.Before[0].WorldId == frame.Before[0].WorldId && parent.Before[0].StationId == frame.Before[0].StationId)
                { parent.Failed = true; frame.Failed = true; }
            __state = frame; _current = frame; _depth++;
            if (frame.Add)
            {
                var sender = ActivityPatches.Sender;
                if (sender != null && !sender.Failed && !sender.Data && sender.Actor != null &&
                    sender.MethodHash == "RPC_AddItem".GetStableHashCode() && sender.Target == __instance.m_nview.GetZDO().m_uid &&
                    sender.Sender == (long)__args[0] && sender.CookingItem == (string)__args[1] && StablePlayerResolver.Current(sender.Actor))
                { frame.Actor = sender.Actor; frame.Input = sender.CookingItem; }
            }
        }
        catch (Exception error) { ActivityPatches.Fail(error); }
    }

    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    internal static void Postfix(CookingStation __instance, Invocation? __state, bool __runOriginal)
    {
        if (__state == null) return;
        var runtime = ActivityRuntime.Current;
        try
        {
            __state.Ran = __runOriginal && runtime != null && Eligible(__instance, runtime);
            if (!__state.Ran || runtime == null) return;
            __state.After = Slots(__instance, runtime.Cooking.CollectorEpoch);
            __state.Conversions = new CookingConversion?[__state.Before.Length];
            if (__state.After.Length != __state.Before.Length) { __state.Failed = true; return; }
            if (__state.Update)
                for (int i = 0; i < __state.Before.Length; i++)
                    __state.Conversions[i] = Conversion(__instance, __state.Before[i], __state.After[i]);
        }
        catch (Exception error) { __state.Failed = true; ActivityPatches.Fail(error); }
    }

    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    internal static void Finalizer(Invocation? __state, Exception? __exception)
    {
        if (__state == null) return;
        var frame = __state;
        _current = frame.Previous; _depth--;
        var runtime = ActivityRuntime.Current;
        if (runtime == null) return;
        try
        {
            if (__exception != null || !frame.Ran || frame.Failed)
            {
                if (_current != null) _current.Failed = true;
                runtime.Cooking.InvalidateStation(frame.Before[0].WorldId, frame.Before[0].StationId);
                return;
            }
            var before = frame.Before; var after = frame.After; var conversions = frame.Conversions;
            DateTime utc = DateTime.UtcNow;
            if (frame.Add)
            {
                int accepted = -1; bool ambiguous = false;
                for (int i = 0; i < before.Length; i++)
                {
                    if (Unchanged(before[i], after[i])) continue;
                    if (accepted != -1 || before[i].Status != CookingStatus.Free || after[i].Status != CookingStatus.NotDone ||
                        after[i].ItemPrefab != frame.Input || before[i].Owner != after[i].Owner)
                    { ambiguous = true; break; }
                    accepted = i;
                }
                if (!ambiguous && accepted >= 0 && frame.Actor != null)
                {
                    int slot = accepted; var actor = frame.Actor;
                    frame.Pending.Add(() => { if (StablePlayerResolver.Current(actor))
                        runtime.Cooking.ObserveInput(before[slot], after[slot], actor, true, utc); });
                }
                else
                    for (int i = 0; i < before.Length; i++)
                        if (!Unchanged(before[i], after[i]))
                        {
                            int slot = i;
                            frame.Pending.Add(() => runtime.Cooking.Invalidate(before[slot].WorldId, before[slot].StationId, slot));
                        }
            }
            else
            {
                for (int i = 0; i < before.Length; i++)
                {
                    if (Unchanged(before[i], after[i])) continue;
                    int slot = i;
                    if (frame.Update)
                        frame.Pending.Add(() => runtime.Cooking.ObserveCooking(before[slot], after[slot], conversions[slot], true, utc));
                    else frame.Pending.Add(() => runtime.Cooking.Invalidate(before[slot].WorldId, before[slot].StationId, slot));
                }
            }
            if (frame.Pending.Count > 4096 || _current != null && _current.Pending.Count + frame.Pending.Count > 4096)
            {
                if (_current != null) _current.Failed = true;
                runtime.Cooking.Reset(); return;
            }
            if (_current != null) _current.Pending.AddRange(frame.Pending);
            else
            {
                string world = before[0].WorldId, station = before[0].StationId;
                foreach (var capture in frame.Pending)
                    ActivityPatches.Stage(capture, () => runtime.Cooking.InvalidateStation(world, station));
            }
        }
        catch (Exception error) { ActivityPatches.Fail(error); }
    }

    private static bool Eligible(CookingStation station, ActivityRuntime runtime) =>
        runtime.Cooking.Available && runtime.Options.CookingCompletions &&
        StablePlayerResolver.World(out _) && station && station.m_nview && station.m_nview.IsValid() &&
        station.m_nview.IsOwner() && station.m_nview.GetZDO().GetOwner() == ZNet.GetUID();

    private static CookingSlotSnapshot[] Slots(CookingStation station, string epoch)
    {
        if (station.m_slots == null || station.m_slots.Length == 0 || station.m_slots.Length > 32 || !StablePlayerResolver.World(out string world))
        {
            if (!_warnedSlots) { _warnedSlots = true; DiscordConnectorPlugin.StaticLogger.LogWarning("Cooking unsupported slot layout; station skipped."); }
            return Array.Empty<CookingSlotSnapshot>();
        }
        var zdo = station.m_nview.GetZDO();
        var slots = new CookingSlotSnapshot[station.m_slots.Length];
        for (int i = 0; i < slots.Length; i++)
        {
            station.GetSlot(i, out string name, out float progress, out CookingStation.Status status, out _);
            CookingStatus state = name.Length == 0 ? CookingStatus.Free : status == CookingStation.Status.NotDone ? CookingStatus.NotDone :
                status == CookingStation.Status.Done ? CookingStatus.Done : status == CookingStation.Status.Burnt ? CookingStatus.Burnt : CookingStatus.Unknown;
            slots[i] = new CookingSlotSnapshot(world, zdo.m_uid.ToString(), i, zdo.GetOwner(), name, state, progress, epoch);
        }
        return slots;
    }

    private static CookingConversion? Conversion(CookingStation station, CookingSlotSnapshot before, CookingSlotSnapshot after)
    {
        if (before.Status != CookingStatus.NotDone || after.Status != CookingStatus.Done) return null;
        if (station.m_conversion == null) return UnknownConversion();
        CookingStation.ItemConversion? match = null;
        foreach (var conversion in station.m_conversion)
        {
            if (conversion == null || !conversion.m_from || !conversion.m_to || conversion.m_from.name != before.ItemPrefab) continue;
            if (match != null) return UnknownConversion();
            match = conversion;
        }
        if (match == null || match.m_to.name != after.ItemPrefab || !ObservationValues.Finite(match.m_cookTime) || match.m_cookTime <= 0 ||
            after.CookProgress <= match.m_cookTime || before.ItemPrefab == after.ItemPrefab) return UnknownConversion();
        var output = match.m_to.m_itemData;
        if (output == null || output.m_shared == null || output.m_stack != 1 ||
            output.m_shared.m_food <= 0 && output.m_shared.m_foodStamina <= 0 && output.m_shared.m_foodEitr <= 0) return UnknownConversion();
        // Native UpdateCooking converts one occupied input slot to one food slot; retrieval bonus is separate.
        return new CookingConversion(after.ItemPrefab, 1);
    }

    private static CookingConversion? UnknownConversion()
    {
        if (!_warnedConversion)
        { _warnedConversion = true; DiscordConnectorPlugin.StaticLogger.LogWarning("Cooking output quantity/conversion unknown; completion skipped."); }
        return null;
    }

    private static bool Unchanged(CookingSlotSnapshot a, CookingSlotSnapshot b) =>
        a.WorldId == b.WorldId && a.StationId == b.StationId && a.Owner == b.Owner && a.CollectorEpoch == b.CollectorEpoch &&
        a.ItemPrefab == b.ItemPrefab && a.Status == b.Status && a.CookProgress == b.CookProgress;
}
