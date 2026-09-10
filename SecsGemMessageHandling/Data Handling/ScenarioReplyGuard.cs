using System;
using System.Collections.Generic;

namespace SecsGemMessageHandling.Data_Handling;

/// <summary>
/// While a scenario run is active, records which Stream/Function messages that scenario answers
/// (via its Receive nodes) so the library auto-reply (<see cref="SpecialCasesHandling"/>) stands
/// down for those — otherwise the reply gets sent twice.
/// </summary>
public sealed class ScenarioReplyGuard
{
    private readonly object gate = new();
    private readonly HashSet<(byte Stream, byte Function)> claimed = [];
    private int activeRuns;

    /// <summary>Marks a scenario run as active and records the S/F its Receive nodes handle. Dispose to end.</summary>
    public IDisposable BeginRun(IEnumerable<(byte Stream, byte Function)> handledStreamFunctions)
    {
        lock (gate)
        {
            activeRuns++;
            foreach (var sf in handledStreamFunctions)
                claimed.Add(sf);
        }
        return new RunScope(this);
    }

    /// <summary>True when a running scenario is answering this Stream/Function itself.</summary>
    public bool Handles(byte stream, byte function)
    {
        lock (gate)
            return activeRuns > 0 && claimed.Contains((stream, function));
    }

    private void EndRun()
    {
        lock (gate)
        {
            if (--activeRuns <= 0)
            {
                activeRuns = 0;
                claimed.Clear();
            }
        }
    }

    private sealed class RunScope(ScenarioReplyGuard owner) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (System.Threading.Interlocked.Exchange(ref disposed, 1) == 0)
                owner.EndRun();
        }
    }
}
