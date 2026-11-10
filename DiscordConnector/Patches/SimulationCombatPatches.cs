using System;

using BepInEx.Bootstrap;

using DiscordConnector.Records;

using HarmonyLib;

namespace DiscordConnector.Patches;

internal static class SimulationCombatPatches
{
    internal const string ModGuid = "MVP.Valheim_Serverside_Simulations";
    internal static bool Present => Chainloader.PluginInfos.ContainsKey(ModGuid);
    [ThreadStatic] private static DeathCapture? _death;
    internal static bool SuppressReportedKill => _death?.Covered == true;
    internal static bool Completing(string victim) => _death?.Covered == true && _death.Victim == victim;

    internal sealed class DamageCapture
    {
        internal ActivityRuntime Runtime = null!;
        internal ActivityActor Actor = null!;
        internal ZDO View = null!;
        internal string World = "", Victim = "";
        internal double Before, After, Time, BowMeters;
        internal DateTime Utc;
        internal bool Applied;
        internal long Generation;
    }
    internal sealed class DeathCapture
    {
        internal DeathCapture? Previous;
        internal ActivityRuntime? Runtime;
        internal ActivityActor[]? Actors;
        internal string World = "", Victim = "";
        internal bool Covered, Ran;
        internal double Time;
        internal DateTime Utc;
        internal long Generation;
    }

    private static bool Owned(Character victim, out ZDO? zdo)
    {
        zdo = null;
        if (!Present || !victim || victim.IsPlayer() || victim.IsTamed() || !victim.m_nview || !victim.m_nview.IsValid() || !victim.m_nview.IsOwner()) return false;
        zdo = victim.m_nview.GetZDO();
        return zdo != null && zdo.GetOwner() == ZNet.GetUID() && !zdo.m_uid.IsNone();
    }

    private static bool Current(ActivityRuntime runtime, string world, long generation) => ReferenceEquals(ActivityRuntime.Current, runtime) && runtime.Simulation.Generation == generation &&
        Present && StablePlayerResolver.World(out string live) && live == world;

    [HarmonyPatch(typeof(Character), nameof(Character.ApplyDamage))]
    internal static class Damage
    {
        [HarmonyPrefix, HarmonyPriority(Priority.Last)]
        internal static void Prefix(Character __instance, HitData hit, out DamageCapture? __state)
        {
            __state = null;
            var runtime = ActivityRuntime.Current;
            if (runtime == null || !(runtime.Options.DamageDealt || runtime.Options.ReportedKillCredits || runtime.Options.LongestBowHit) || !Present) return;
            if (!runtime.Options.DamageDealt && !runtime.Options.ReportedKillCredits && (hit == null || hit.m_skill != Skills.SkillType.Bows)) return;
            try
            {
                if (!StablePlayerResolver.World(out string world) || !Owned(__instance, out var zdo) || hit == null ||
                    !StablePlayerResolver.Resolve(hit.m_attacker, DateTime.UtcNow, out var actor)) return;
                double health = __instance.GetHealth();
                if (!ObservationValues.Finite(health) || health <= 0) return;
                __state = new DamageCapture { Runtime = runtime, Actor = actor!, View = zdo!, World = world,
                    Victim = zdo!.m_uid.ToString(), Before = health, Utc = DateTime.UtcNow, Time = ActivityRuntime.Now, Generation = runtime.Simulation.Generation };
                if (runtime.Options.LongestBowHit && hit.m_skill == Skills.SkillType.Bows && runtime.TryFreshPosition(world, hit.m_attacker, __state.Time, out var attacker))
                {
                    var victim = zdo.GetPosition();
                    double distance = Math.Sqrt(Math.Pow((double)victim.x - attacker.x, 2) + Math.Pow((double)victim.y - attacker.y, 2) + Math.Pow((double)victim.z - attacker.z, 2));
                    if (ObservationValues.Finite(distance) && distance > 0 && distance <= ObservationValues.MaxBowHitMeters) __state.BowMeters = distance;
                }
            }
            catch (Exception error) { runtime.Failed(error); }
        }

        [HarmonyPostfix, HarmonyPriority(Priority.Last)]
        internal static void Postfix(Character __instance, DamageCapture? __state, bool __runOriginal)
        {
            if (__state == null || !__runOriginal) return;
            try
            {
                if (!Current(__state.Runtime, __state.World, __state.Generation) || !Owned(__instance, out var zdo) || !ReferenceEquals(zdo, __state.View) ||
                    !StablePlayerResolver.Current(__state.Actor)) return;
                __state.After = __instance.GetHealth(); __state.Applied = true;
            }
            catch (Exception error) { __state.Runtime.Failed(error); }
        }

        [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
        internal static void Finalizer(DamageCapture? __state, Exception? __exception)
        {
            if (__state == null || !__state.Applied || __exception != null) return;
            try
            {
                var s = __state;
                if (Current(s.Runtime, s.World, s.Generation) && s.View.GetOwner() == ZNet.GetUID() && StablePlayerResolver.Current(s.Actor))
                    s.Runtime.Simulation.Damage(s.World, s.Victim, s.Actor, s.Before, s.After, s.Utc, s.Time, s.Runtime.Options, s.BowMeters);
            }
            catch (Exception error) { __state.Runtime.Failed(error); }
        }
    }

    [HarmonyPatch(typeof(Character), nameof(Character.OnDeath))]
    internal static class Death
    {
        [HarmonyPrefix, HarmonyPriority(Priority.Last)]
        internal static void Prefix(Character __instance, out DeathCapture __state)
        {
            __state = new DeathCapture { Previous = _death }; _death = __state;
            var runtime = ActivityRuntime.Current;
            if (runtime == null || !runtime.Options.ReportedKillCredits || !Present) return;
            try
            {
                if (!StablePlayerResolver.World(out string world) || !Owned(__instance, out var zdo) || !__instance.IsDead()) return;
                double health = __instance.GetHealth();
                if (!ObservationValues.Finite(health) || health > 0) return;
                double time = ActivityRuntime.Now;
                var actors = runtime.Simulation.Participants(zdo!.m_uid.ToString(), time);
                if (actors == null) return;
                __state.Runtime = runtime; __state.World = world; __state.Victim = zdo.m_uid.ToString();
                __state.Actors = actors; __state.Covered = true; __state.Time = time; __state.Utc = DateTime.UtcNow;
                __state.Generation = runtime.Simulation.Generation;
            }
            catch (Exception error) { runtime.Failed(error); }
        }

        [HarmonyPostfix, HarmonyPriority(Priority.Last)]
        internal static void Postfix(DeathCapture __state, bool __runOriginal) => __state.Ran = __runOriginal;

        [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
        internal static void Finalizer(DeathCapture __state, Exception? __exception)
        {
            _death = __state.Previous;
            if (!__state.Covered || !__state.Ran || __exception != null || __state.Runtime == null) return;
            try
            {
                var s = __state;
                if (Current(s.Runtime, s.World, s.Generation) && s.Runtime.Options.ReportedKillCredits)
                    s.Runtime.Simulation.Death(s.World, s.Victim, s.Actors!, s.Utc, s.Time);
            }
            catch (Exception error) { __state.Runtime.Failed(error); }
        }
    }
}
