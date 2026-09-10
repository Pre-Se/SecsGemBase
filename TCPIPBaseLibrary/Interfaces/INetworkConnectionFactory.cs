namespace TCPIPBaseLibrary.Interfaces;

public interface INetworkConnectionFactory
{
    /// <summary>
    /// Used to create a connection object
    /// </summary>
    /// <param name="connectionMode"> this decides what kind of connector will be returned</param>
    /// <returns>the connection object</returns>
    ITCPIPBase CreateTCPIPConnection(ConnectionMode connectionMode);
}