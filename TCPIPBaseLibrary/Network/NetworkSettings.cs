using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using TCPIPBaseLibrary.Interfaces;

namespace TCPIPBaseLibrary.Network;

[JsonConverter(typeof(NetworkSettingsConverter))]
public partial class NetworkSettings : ObservableValidator, INetworkSettings
{
    public const string Section = nameof(NetworkSettings);

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [CustomValidation(typeof(NetworkSettings), nameof(ValidateIpAddressResult))]
    private string ipAddress = "127.0.0.1";
    [ObservableProperty]
    private ushort port = 5000;
    [ObservableProperty]
    [NotifyDataErrorInfo]
    [CustomValidation(typeof(NetworkSettings), nameof(ValidateIpAddressResult))]
    private ConnectionMode connectionMode = ConnectionMode.Active;



    public static ValidationResult? ValidateIpAddressResult(string _, ValidationContext context)
    {
        string errorString = "The IP Address is not a valid one";
        NetworkSettings instance = (NetworkSettings)context.ObjectInstance;
        if (instance is { ConnectionMode: ConnectionMode.Passive, IpAddress: "" })
        {
            return ValidationResult.Success;
        }
        string[] splitValues = instance.IpAddress.Split('.');
        if (splitValues.Length != 4)
        {
            return new(errorString);
        }
        return IPAddress.TryParse((string?)instance.IpAddress, out var _) ? ValidationResult.Success : new(errorString);
    }

    public void CopyFrom(INetworkSettings source)
    {
        IpAddress = source.IpAddress;
        Port = source.Port;
        ConnectionMode = source.ConnectionMode;
    }

    public INetworkSettings GetCopySource()
    {
        return this;
    }
}