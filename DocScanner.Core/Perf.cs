using System.Diagnostics;

namespace DocScanner.Core;

/// <summary>
/// Stage timings for tuning on a real phone. The app sends them to logcat (tag DocScanPerf, see MauiProgram), so they can
/// be read passively with <c>adb logcat -s DocScanPerf</c> while someone uses the app; tests leave <see cref="Sink"/> null
/// and the calls cost next to nothing.
/// </summary>
public static class Perf
{
    /// <summary>Where lines go; null = timings are off.</summary>
    public static Action<string>? Sink { get; set; }

    public static bool Enabled => Sink != null;

    /// <summary>Times the scope: <c>using (Perf.Measure("proxy")) { ... }</c> logs "proxy 123 ms".</summary>
    public static Scope Measure(string what) => new(what, Sink != null ? Stopwatch.GetTimestamp() : 0);

    public static void Log(string line) => Sink?.Invoke(line);

    public readonly struct Scope(string what, long start) : IDisposable
    {
        public void Dispose()
        {
            if (start == 0 || Sink is not { } sink) return;
            sink($"{what} {Stopwatch.GetElapsedTime(start).TotalMilliseconds:0} ms");
        }
    }
}
