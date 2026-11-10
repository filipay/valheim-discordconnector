using System;
using System.Threading.Tasks;

using BepInEx.Logging;

namespace DiscordConnector.Common;

public sealed class VdcLogger
{
    private static ManualLogSource s_logger = null!; // set in constructor
    private bool _logDebugMessages;

    public VdcLogger(ManualLogSource logger, string basePath)
    {
        s_logger = logger;
        // Keep basePath for caller compatibility; BepInEx owns all future log output.
        s_logger.LogInfo("Logger initialized.");
    }

    public void SetLogLevel(bool logDebugMessages)
    {
        this._logDebugMessages = logDebugMessages;
    }

    public Task LogDebugAsync(string message) => Invoke(LogDebug, message);
    public Task LogInfoAsync(string message) => Invoke(LogInfo, message);
    public Task LogWarningAsync(string message) => Invoke(LogWarning, message);
    public Task LogErrorAsync(string message) => Invoke(LogError, message);
    public Task LogFatalAsync(string message) => Invoke(LogFatal, message);

    private static Task Invoke(Action<string> log, string message)
    {
        try
        {
            log(message);
            return Task.CompletedTask;
        }
        catch (Exception error)
        {
            return Task.FromException(error);
        }
    }

    public void LogDebug(string message)
    {
        if (_logDebugMessages)
        {
            s_logger.LogInfo(message);
        }
    }

    public void LogInfo(string message)
    {
        s_logger.LogInfo(message);
    }

    public void LogWarning(string message)
    {
        s_logger.LogWarning(message);
    }

    public void LogError(string message)
    {
        s_logger.LogError(message);
    }

    public void LogFatal(string message)
    {
        s_logger.LogFatal(message);
    }
}
