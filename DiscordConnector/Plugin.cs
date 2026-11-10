using System;
using System.IO;
using System.Threading.Tasks;

using BepInEx;

using DiscordConnector.Common;
using DiscordConnector.Config;
using DiscordConnector.Leaderboards;
using DiscordConnector.Patches;
using DiscordConnector.Records;

using HarmonyLib;

using UnityEngine.Device;
using UnityEngine.Rendering;

namespace DiscordConnector;

[BepInPlugin(ModGuid, ModName, ModVersion)]
public class DiscordConnectorPlugin : BaseUnityPlugin
{
    internal const string ModName = "DiscordConnector";
    internal const string ModVersion = "3.1.3";
    internal const string Author = "nwesterhausen";
    private const string ModGuid = Author + "." + ModName;
    internal const string LegacyConfigPath = "games.nwest.valheim.discordconnector";
    internal const string LegacyModName = "discordconnector";

    internal static VdcLogger StaticLogger = null!; // initialized in constructor
    internal static PluginConfig StaticConfig = null!; // initialized in constructor
    internal static CombatConfig StaticCombatConfig = null!; // initialized in constructor
    internal static Database StaticDatabase = null!; // initialized in constructor
    private static readonly LeaderbBoard StaticLeaderBoards = new();
    internal static readonly EventWatcher StaticEventWatcher = new();
    private static string s_publicIpAddress = "";
    private Harmony? _harmony;
    private CombatReports? _combatReports;
    private ManualLeaderboardReports? _manualReports;
    private EmoteReportTriggers? _emoteTriggers;
    private readonly ManualLeaderboardTriggerConfig _manualTriggerConfig;
    private ActivityRuntime? _activityRuntime;
    private NativeSignalCollector? _nativeSignals;
    private readonly ActivityConfig _activityConfig;
    private readonly ObservedLeaderboardsConfig _observedConfig;
    private DateTime _nextCombatReportCheckUtc;
    private LeaderboardsOptions? _effectiveOptions;
    private readonly DatabaseFailureBudget _stageFailures = new();
    private ZNet? _sessionNet;
    private World? _sessionWorld;
    private ZDOMan? _sessionZdos;
    private long _sessionServer;
    private string _sessionWorldId = "", _sessionEpoch = "", _observationWorld = "";
    private double _nextObservationCoverage, _nextObservationMaintenance;
    private ActivityStore? _observationStore;
    private Task _lossMaintenance = Task.CompletedTask;
    private Task? _observationShutdown;
    private bool _stopping, _cleanupFailed, _quitting;

    public DiscordConnectorPlugin()
    {
        StaticLogger = new VdcLogger(Logger, Path.Combine(Paths.ConfigPath, LegacyConfigPath));
        StaticConfig = new PluginConfig(Config);
        StaticCombatConfig = new CombatConfig(Config);
        _activityConfig = new ActivityConfig(Config);
        _observedConfig = new ObservedLeaderboardsConfig(Config);
        _manualTriggerConfig = new ManualLeaderboardTriggerConfig(Config);
        Config.SettingChanged += (_, _) =>
        {
            _effectiveOptions = null;
            _nextObservationCoverage = 0;
            NativeSignalPatches.InvalidateOptions();
        };
        StaticDatabase = new Database(Paths.GameRootPath);
    }


    internal static string PublicIpAddress
    {
        get
        {
            if (!string.IsNullOrEmpty(s_publicIpAddress))
            {
                return s_publicIpAddress;
            }

            s_publicIpAddress = PublicIPChecker.GetPublicIP();
            return s_publicIpAddress;
        }
    }

    private void Awake()
    {
        ManualReportDiagnostics.Current = new ManualReportDiagnostics(message => StaticLogger.LogInfo(message));
        // Plugin startup logic
        StaticLogger.LogDebug($"Plugin {ModName} is loaded!");

        if (string.IsNullOrEmpty(StaticConfig.PrimaryWebhook.Url) &&
            string.IsNullOrEmpty(StaticConfig.SecondaryWebhook.Url))
        {
            StaticLogger.LogWarning(
                "No value set for WebHookURL! Plugin will run without using a main Discord webhook.");
        }

        if (StaticConfig.LeaderBoards[0].Enabled)
        {
            System.Timers.Timer leaderBoard1Timer = new();
            leaderBoard1Timer.Elapsed += StaticLeaderBoards.LeaderBoard1.SendLeaderBoardOnTimer;
            // Interval is learned from config file in minutes
            leaderBoard1Timer.Interval = 60 * 1000 * StaticConfig.LeaderBoards[0].PeriodInMinutes;
            StaticLogger.LogInfo(
                $"Enabling LeaderBoard.1 timer with interval {Strings.HumanReadableMs(leaderBoard1Timer.Interval)}");
            leaderBoard1Timer.Start();
        }

        if (StaticConfig.LeaderBoards[1].Enabled)
        {
            System.Timers.Timer leaderBoard2Timer = new();
            leaderBoard2Timer.Elapsed += StaticLeaderBoards.LeaderBoard2.SendLeaderBoardOnTimer;
            // Interval is learned from config file in minutes
            leaderBoard2Timer.Interval = 60 * 1000 * StaticConfig.LeaderBoards[1].PeriodInMinutes;
            StaticLogger.LogInfo(
                $"Enabling LeaderBoard.2 timer with interval {Strings.HumanReadableMs(leaderBoard2Timer.Interval)}");
            leaderBoard2Timer.Start();
        }

        if (StaticConfig.LeaderBoards[2].Enabled)
        {
            System.Timers.Timer leaderBoard3Timer = new();
            leaderBoard3Timer.Elapsed += StaticLeaderBoards.LeaderBoard3.SendLeaderBoardOnTimer;
            // Interval is learned from config file in minutes
            leaderBoard3Timer.Interval = 60 * 1000 * StaticConfig.LeaderBoards[2].PeriodInMinutes;
            StaticLogger.LogInfo(
                $"Enabling LeaderBoard.3 timer with interval {Strings.HumanReadableMs(leaderBoard3Timer.Interval)}");
            leaderBoard3Timer.Start();
        }

        if (StaticConfig.LeaderBoards[3].Enabled)
        {
            System.Timers.Timer leaderBoard4Timer = new();
            leaderBoard4Timer.Elapsed += StaticLeaderBoards.LeaderBoard4.SendLeaderBoardOnTimer;
            // Interval is learned from config file in minutes
            leaderBoard4Timer.Interval = 60 * 1000 * StaticConfig.LeaderBoards[3].PeriodInMinutes;
            StaticLogger.LogInfo(
                $"Enabling LeaderBoard.4 timer with interval {Strings.HumanReadableMs(leaderBoard4Timer.Interval)}");
            leaderBoard4Timer.Start();
        }

        if (StaticConfig.LeaderBoards[4].Enabled)
        {
            System.Timers.Timer leaderBoard5Timer = new();
            leaderBoard5Timer.Elapsed += StaticLeaderBoards.LeaderBoard4.SendLeaderBoardOnTimer;
            // Interval is learned from config file in minutes
            leaderBoard5Timer.Interval = 60 * 1000 * StaticConfig.LeaderBoards[4].PeriodInMinutes;
            StaticLogger.LogInfo(
                $"Enabling LeaderBoard.5 timer with interval {Strings.HumanReadableMs(leaderBoard5Timer.Interval)}");
            leaderBoard5Timer.Start();
        }

        if (StaticConfig.ActivePlayersAnnouncement.Enabled)
        {
            System.Timers.Timer playerActivityTimer = new();
            playerActivityTimer.Elapsed += ActivePlayersAnnouncement.SendOnTimer;
            // Interval is learned from config file in minutes
            playerActivityTimer.Interval = 60 * 1000 * StaticConfig.ActivePlayersAnnouncement.PeriodInMinutes;
            StaticLogger.LogInfo(
                $"Enabling Player Activity announcement with interval {Strings.HumanReadableMs(playerActivityTimer.Interval)}");
            playerActivityTimer.Start();
        }

        var store = StaticDatabase.Activity;
        _observationStore = store;
        var collector = new ActivityCollector(store.Enqueue, message => StaticLogger.LogWarning(message), timeZoneId: StaticCombatConfig.TimeZoneId);
        _nativeSignals = new NativeSignalCollector(store.Enqueue, message => StaticLogger.LogWarning(message));
        NativeSignalPatches.Configure(_nativeSignals, EffectiveOptions);
        _activityRuntime = new ActivityRuntime(collector, EffectiveOptions,
            message => StaticLogger.LogWarning(message), store.CookingObservationEpoch,
            explorationPauseReason: store.ExplorationPauseReason);
        _manualReports = new ManualLeaderboardReports(StaticCombatConfig.TimeZoneId, StaticCombatConfig.TopCount,
            (world, start, end) => store.QueryAsync(world, start, end, EffectiveOptions()), FlushObservations,
            report => ManualReportDiagnostics.Dispatch(() => DiscordApi.SendRenderedMessage(Webhook.Event.LeaderboardsAll, report)),
            message => StaticLogger.LogWarning(message), LogLeaderboardReport);
        ActivityPatches.ManualCommand = RequestManualReport;
        _emoteTriggers = new EmoteReportTriggers(_manualTriggerConfig.Enabled, _manualTriggerConfig.Daily, _manualTriggerConfig.Weekly,
            message => StaticLogger.LogWarning(message));
        ActivityPatches.EmoteTriggers = _emoteTriggers;

        if (StaticCombatConfig.DailyEnabled || StaticCombatConfig.WeeklyEnabled)
        {
            _combatReports = new CombatReports(StaticCombatConfig.DailyEnabled, StaticCombatConfig.WeeklyEnabled,
                StaticCombatConfig.TimeZoneId, StaticCombatConfig.TopCount,
                (world, start, end) => store.QueryAsync(world, start, end, EffectiveOptions()),
                store.TryClaimReportAsync, FlushObservations,
                report => DiscordApi.SendRenderedMessage(Webhook.Event.LeaderboardsAll, report),
                message => StaticLogger.LogWarning(message), LogLeaderboardReport);
        }

        _harmony = Harmony.CreateAndPatchAll(typeof(DiscordConnectorPlugin).Assembly, ModGuid);
        ManualReportDiagnostics.Ready();
    }

    private static void LogLeaderboardReport(string message)
    {
        if (StaticConfig.DebugLeaderboardOperations) StaticLogger.LogInfo(message);
    }

    private void Update()
    {
        if (_stopping) return;
        DateTime nowUtc = DateTime.UtcNow;
        double monotonic = UnityEngine.Time.realtimeSinceStartupAsDouble;
        try { ManualReportDiagnostics.Poll(); }
        catch (Exception error) { LogStage("diagnostics", error); }
        UpdateObservationState(nowUtc, monotonic);
        UpdateReportState(nowUtc, monotonic, _observationWorld);
    }

    internal void UpdateObservationState(DateTime utc, double monotonic)
    {
        if (_stopping) return;
        _observationWorld = "";
        if (!double.IsNaN(monotonic) && !double.IsInfinity(monotonic) && monotonic >= _nextObservationMaintenance)
        {
            _nextObservationMaintenance = monotonic + 10;
            try { if (_lossMaintenance.IsCompleted) _lossMaintenance = _observationStore?.MaintainLoss(monotonic) ?? Task.CompletedTask; }
            catch (Exception error) { LogStage("loss-maintenance", error); }
            try { _ = _observationStore?.MaintainSignalRetention(utc, monotonic); }
            catch (Exception error) { LogStage("signal-retention", error); }
        }
        try
        {
            if (!StablePlayerResolver.World(out string world))
            {
                InvalidateObservationContext();
                return;
            }
            long server = ZNet.GetUID();
            if (_sessionWorldId != world || !ReferenceEquals(_sessionNet, ZNet.instance) ||
                !ReferenceEquals(_sessionWorld, ZNet.World) || !ReferenceEquals(_sessionZdos, ZDOMan.instance) || _sessionServer != server)
            {
                NativeSignalPatches.SetWorld("");
                _sessionNet = ZNet.instance; _sessionWorld = ZNet.World; _sessionZdos = ZDOMan.instance;
                _sessionWorldId = world; _sessionServer = server; _sessionEpoch = Guid.NewGuid().ToString("N");
                _nextObservationCoverage = 0;
                try { _activityRuntime?.ResetSession(world); }
                catch (Exception error) { LogStage("observation-session", error); }
            }
            NativeSignalPatches.SetSession(world, _sessionEpoch, ModGuid);
            _observationWorld = world;
        }
        catch (Exception error)
        {
            LogStage("world", error);
            InvalidateObservationContext();
            return;
        }
        try { _activityRuntime?.Update(utc, monotonic); }
        catch (Exception error) { LogStage("observation-maintenance", error); }
        if (monotonic >= _nextObservationCoverage)
        {
            _nextObservationCoverage = monotonic + 1;
            try { NativeSignalPatches.UpdateCoverage(utc); }
            catch (Exception error) { LogStage("native-coverage", error); }
        }
    }

    private void InvalidateObservationContext()
    {
        _observationWorld = ""; _nextObservationCoverage = 0;
        try { NativeSignalPatches.SetWorld(""); }
        catch (Exception error) { LogStage("native-invalidate", error); }
        try { _activityRuntime?.Suspend(); }
        catch (Exception error) { LogStage("observation-invalidate", error); }
    }

    internal void UpdateReportState(DateTime utc, double monotonic, string world)
    {
        if (_stopping || world.Length == 0) return;
        try { _emoteTriggers?.SetEnabled(_manualTriggerConfig.Enabled); _emoteTriggers?.SetWorld(world); }
        catch (Exception error) { LogStage("manual-context", error); }
        try { _manualReports?.Poll(monotonic, world); }
        catch (Exception error) { LogStage("manual-poll", error); }
        try { _combatReports?.Poll(utc, world); }
        catch (Exception error) { LogStage("automatic-poll", error); }
        if (_combatReports == null || utc < _nextCombatReportCheckUtc) return;
        try { _nextCombatReportCheckUtc = utc.AddMinutes(1); _combatReports.Tick(utc, world); }
        catch (Exception error) { LogStage("automatic-tick", error); }
    }

    private void LogStage(string stage, Exception error)
    {
        try
        {
            if (_stageFailures.TryTake(error, System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency, out long suppressed))
                StaticLogger.LogWarning($"Observation failure stage={stage} suppressed={suppressed} " + DatabaseDiagnostics.Describe(error));
        }
        catch { /* Logging must not prevent the next stage or shutdown sealing. */ }
    }

    private void RequestManualReport(ZRpc rpc, ActivityActor captured, string world, string text)
    {
        try
        {
            // Use the current transport socket and native platform-aware admin policy, never chat UserInfo/name.
            bool sameWorld = StablePlayerResolver.World(out string currentWorld) && currentWorld == world;
            bool samePeer = false;
            ActivityActor? current = null;
            if (sameWorld)
                samePeer = StablePlayerResolver.Transport(rpc, DateTime.UtcNow, out var peer, out current) && peer != null && current != null &&
                    ReferenceEquals(rpc.GetSocket(), peer.m_socket) &&
                    current.ConnectionToken == captured.ConnectionToken && current.PlayerId == captured.PlayerId;
            bool authorized = samePeer && ZNet.instance.IsAdmin(current!.PlayerId);
            ManualReportDiagnostics.Emit(ManualReportStage.Authorization, !sameWorld ? ManualReportReason.InvalidWorld :
                !samePeer ? ManualReportReason.InvalidIngress : authorized ? ManualReportReason.Passed : ManualReportReason.Denied);
            if (_manualReports == null) ManualReportDiagnostics.Emit(ManualReportStage.Callback, ManualReportReason.Disposed);
            _manualReports?.Request(text, authorized, DateTime.UtcNow, UnityEngine.Time.realtimeSinceStartupAsDouble, world);
        }
        catch (Exception error)
        {
            ManualReportDiagnostics.Emit(ManualReportStage.Authorization, ManualReportReason.Failed, error);
            StaticLogger.LogWarning($"Manual preview authorization failed ({error.GetType().Name}); no report requested.");
        }
    }

    private LeaderboardsOptions EffectiveOptions() => _effectiveOptions ??= _observedConfig.Snapshot(_activityConfig);

    private string[] FlushObservations(DateTime endUtc)
    {
        NativeSignalPatches.UpdateCoverage(DateTime.UtcNow);
        return _activityRuntime?.FlushThrough(endUtc) ?? Array.Empty<string>();
    }

    private void OnDestroy() => _ = BeginObservationShutdown();

    private void OnApplicationQuit()
    {
        if (_quitting) return;
        _quitting = true;
        try
        {
            if (!BeginObservationShutdown().Wait(TimeSpan.FromSeconds(5)))
                StaticLogger.LogWarning("Observation shutdown timed out after 5 seconds; pending writes may be lost on exit. The database remains owned by its drain worker.");
        }
        catch (Exception error) { LogStage("quit-drain", error); }
    }

    internal Task BeginObservationShutdown()
    {
        if (_observationShutdown != null) return _observationShutdown;
        _stopping = true;
        try
        {
            Cleanup("native-stop", NativeSignalPatches.Stop);
            Cleanup("native-dispose", () => _nativeSignals?.Dispose());
            _nativeSignals = null;
            ActivityPatches.EmoteTriggers = null; ActivityPatches.ManualCommand = null;
            Cleanup("emote-dispose", () => _emoteTriggers?.Dispose());
            _emoteTriggers = null;
            Cleanup("manual-dispose", () => _manualReports?.Dispose());
            _manualReports = null;
            Cleanup("automatic-dispose", () => _combatReports?.Dispose());
            _combatReports = null;
            Cleanup("observation-flush", () => _activityRuntime?.FlushThrough(DateTime.UtcNow));
            Cleanup("observation-dispose", () => _activityRuntime?.Dispose());
            _activityRuntime = null;
        }
        finally
        {
            if (!_quitting) Cleanup("unpatch", () => _harmony?.UnpatchSelf());
            ManualReportDiagnostics.Current = null;
            Task finalLoss = Task.CompletedTask;
            try
            {
                finalLoss = _observationStore?.SubmitLossBeforeSeal(UnityEngine.Time.realtimeSinceStartupAsDouble) ?? Task.CompletedTask;
            }
            catch (Exception error) { _cleanupFailed = true; LogStage("loss-final", error); }
            finally
            {
                // Sealing is synchronous; only pool continuations observe drain and acknowledgment bookkeeping.
                Task drain = StaticDatabase.BeginShutdown();
                _observationShutdown = Task.WhenAll(drain, _lossMaintenance, finalLoss).ContinueWith(completed =>
                {
                    if (completed.Exception != null) LogStage("shutdown-drain", completed.Exception.GetBaseException());
                    try
                    {
                        var loss = _observationStore?.Loss;
                        StaticLogger.LogWarning(FormattableString.Invariant($"Observation shutdown state=drained cleanupFailed={_cleanupFailed} drainFailed={completed.Status != TaskStatus.RanToCompletion} lossUnacknowledged={loss?.UnacknowledgedKeys ?? 0} lossIncomplete={loss?.Incomplete ?? false}; drain is not an all-writes-successful or exit-durability guarantee."));
                    }
                    catch { }
                }, System.Threading.CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            }
        }
        return _observationShutdown!;
    }

    private void Cleanup(string stage, Action cleanup)
    {
        try { cleanup(); }
        catch (Exception error) { _cleanupFailed = true; LogStage(stage, error); }
    }

    /// <summary>
    ///     Works in Awake()
    /// </summary>
    public static bool IsHeadless()
    {
        return SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;
    }
}
