using Microsoft.Extensions.DependencyInjection;
using TCPIPBaseLibrary.Interfaces;
using TCPIPBaseLibrary.TCPBase;

namespace TCPIPBaseLibrary;

/// <summary>
/// This class creates TCPIP Connection
/// </summary>
public class NetworkConnectionFactory(IServiceProvider provider)
    : INetworkConnectionFactory
{
    private IServiceProvider Provider { get; } = provider;

    /// <summary>
    /// Used to create a <see cref="ITCPIPBase"/> object
    /// </summary>
    /// <param name="handleMethod">this method gets called everytime the connection receives data</param>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
    /// <returns>The created <see cref="ITCPIPBase"/> object</returns>
    public ITCPIPBase CreateTCPIPConnection(ConnectionMode connectionMode)
    {
        ITCPIPBase tcpipBase = connectionMode switch
        {
            ConnectionMode.Active => Provider.GetRequiredService<TCPIPClientBase>(),
            ConnectionMode.Passive => Provider.GetRequiredService<TCPIPServerBase>(),
            _ => throw new ArgumentOutOfRangeException(
                nameof(connectionMode),
                connectionMode,
                "Invalid connection mode specified."
            )
        };
        return tcpipBase;
    }
}