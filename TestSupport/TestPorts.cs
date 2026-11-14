using System.Net;
using System.Net.Sockets;
using System.Reflection;

namespace TestSupport;

/// <summary>
/// Ports for socket tests that need a fixed port before anything listens on it (a passive TCPIPServerBase binds the port
/// from its settings, and a restarted connection binds it again). Linked into every test project.
/// </summary>
/// <remarks>
/// Until GitLab secsgem/secsgembase#61 the tests took a port from bind(0) and released it again. That port came from the
/// ephemeral range, where the system also picks the local port of outgoing connections and of other bind(0) calls, so a
/// parallel test process could take it in between, or a client could even connect to itself. The ports here lie below
/// the ephemeral ranges of Linux (32768–60999) and Windows (49152–65535).
/// <para>
/// `dotnet test` runs the test projects as parallel processes, so each project has its own slice. A probe alone would not
/// keep them apart: a passive server stops listening once it has accepted, and on Linux another process can then bind
/// the same port. Within a slice the process starts at a random offset and counts up; a port is skipped if it cannot be
/// bound right now (in use, or reserved: Windows excludes port ranges for Hyper-V and others).
/// </para>
/// </remarks>
internal static class TestPorts
{
    private const int First = 20000;
    private const int SliceSize = 3000;

    private static readonly int SliceStart = First + SliceSize * (Assembly.GetExecutingAssembly().GetName().Name switch
    {
        "TCPIPBaseLibrary.Tests" => 0,
        "SecsGemMessageHandling.Tests" => 1,
        "SecsGemScenarioEngine.Tests" => 2,
        _ => 3
    });

    private static int next = Random.Shared.Next(SliceSize);

    public static int Next()
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var port = SliceStart + (int)((uint)Interlocked.Increment(ref next) % SliceSize);
            try
            {
                using var probe = new TcpListener(IPAddress.Loopback, port);
                probe.Start();
                return port;
            }
            catch (SocketException)
            {
                // In use, or reserved.
            }
        }

        throw new InvalidOperationException("No free test port found");
    }
}
