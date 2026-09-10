using System.ComponentModel;
using SecsGemHelperClasses.Copy;

namespace TCPIPBaseLibrary.Interfaces;

public interface INetworkSettings : INotifyPropertyChanged, INotifyDataErrorInfo, ICopy<INetworkSettings>
{
    /// <inheritdoc cref="EvenCoolerFastSim.Services.NetworkSettings.ipAddress"/>
    string IpAddress { get; set; }

    /// <inheritdoc cref="EvenCoolerFastSim.Services.NetworkSettings.port"/>
    ushort Port { get; set; }

    /// <inheritdoc cref="EvenCoolerFastSim.Services.NetworkSettings.connectionMode"/>
    ConnectionMode ConnectionMode { get; set; }
}