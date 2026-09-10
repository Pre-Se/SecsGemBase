using Microsoft.Extensions.Logging;
using SecsGemHelperClasses;
using System.Net.Sockets;
using TCPIPBaseLibrary.Interfaces;

namespace TCPIPBaseLibrary.TCPBase;
internal static class TCPIPExtensions
{
    internal static async Task ReceiveDataLoopAsync(this ITCPIPBase tcpipBase, NetworkStream networkStream,
        ILogger<ITCPIPBase> logger, CancellationToken cancellation)
    {
        var lengthBuffer = new Memory<byte>(new byte[4]);
        var errorMessage = "Connection was reset by remote machine";

        try
        {
            //loop until the cancellation token is cancelled
            while (cancellation is { IsCancellationRequested: false })
            {
                // Read message length
                if (!await ReadExactAsync(networkStream, lengthBuffer,
                        cancellation))
                {
                    logger.LogError("{error}", errorMessage);
                    break;
                }

                var messageLength = BitConverter.ToUInt32(MessageHandlingHelpers.Reverse(lengthBuffer.ToArray()));

                // Read the actual message
                var messageBuffer = new Memory<byte>(new byte[messageLength]);
                if (!await ReadExactAsync(networkStream, messageBuffer,
                        cancellation))
                {
                    logger.LogError("{error}", errorMessage);
                    break;
                }

                tcpipBase.OnDataReceived.Publish(MessageHandlingHelpers.Combine(lengthBuffer.ToArray(), messageBuffer.ToArray()));
            }
        }
        catch (IOException ex) when (ex.InnerException is SocketException)
        {
            logger.LogError("{errorMessage}: {exception}", errorMessage, ex.Message);
        }
    }

    internal static async Task<bool> ReadExactAsync(NetworkStream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[totalRead..], cancellationToken);
            if (read == 0)
                return false;
            totalRead += read;
        }
        return true;
    }
}
