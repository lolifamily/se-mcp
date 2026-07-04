using System;
using System.Runtime.CompilerServices;
using Keen.VRage.Library.Diagnostics;

namespace Shared.Logging;

// SE2 counterpart of SE1's PluginLogger. Same contract (LogFormatter + IPluginLogger),
// but routes to SE2's Keen.VRage.Library.Diagnostics.Log instead of VRage.Utils.MyLog.
// Shared by the SE2 client (and a future SE2 server), so it lives in Shared/Se2 —
// exactly symmetric to PluginLogger living in Shared/Se1.
public class Plugin2Logger(string pluginName) : LogFormatter($"{pluginName}: "), IPluginLogger{
    // SE2's Log exposes no per-level "enabled" query (unlike SE1's MyLog.LogEnabled).
    // Log.Default does its own internal filtering, so these are unconditionally true.
    public bool IsTraceEnabled => true;
    public bool IsDebugEnabled => true;
    public bool IsInfoEnabled => true;
    public bool IsWarningEnabled => true;
    public bool IsErrorEnabled => true;
    public bool IsCriticalEnabled => true;

    // Log.Default is a SingletonManager-backed instance that can be null before the
    // logging subsystem is up (or after teardown) — null-conditional guards each call.

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Trace(Exception ex, string message, params object[] data)
    {
        // SE2 has no Trace severity; using Debug instead (same as SE1's PluginLogger).
        Log.Default.WriteLine(LogSeverity.Debug, Format(ex, message, data));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Debug(Exception ex, string message, params object[] data)
    {
        Log.Default.WriteLine(LogSeverity.Debug, Format(ex, message, data));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Info(Exception ex, string message, params object[] data)
    {
        Log.Default.WriteLine(LogSeverity.Info, Format(ex, message, data));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Warning(Exception ex, string message, params object[] data)
    {
        Log.Default.WriteLine(LogSeverity.Warning, Format(ex, message, data));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Error(Exception ex, string message, params object[] data)
    {
        Log.Default.WriteLine(LogSeverity.Error, Format(ex, message, data));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Critical(Exception ex, string message, params object[] data)
    {
        Log.Default.WriteLine(LogSeverity.Critical, Format(ex, message, data));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Trace(string message, params object[] data)
    {
        Trace(null, message, data);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Debug(string message, params object[] data)
    {
        Debug(null, message, data);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Info(string message, params object[] data)
    {
        Info(null, message, data);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Warning(string message, params object[] data)
    {
        Warning(null, message, data);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Error(string message, params object[] data)
    {
        Error(null, message, data);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Critical(string message, params object[] data)
    {
        Critical(null, message, data);
    }
}
