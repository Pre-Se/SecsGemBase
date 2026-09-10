using System.Text.Json;
using System.Text.Json.Serialization;
using TCPIPBaseLibrary.Interfaces;

namespace TCPIPBaseLibrary;

public class NetworkSettingsConverter : JsonConverter<INetworkSettings>
{
    public override INetworkSettings Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        throw new NotImplementedException();
    }

    public override void Write(Utf8JsonWriter writer, INetworkSettings value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        writer.WriteString("IpAddress", value.IpAddress);
        writer.WriteString("Port", value.Port.ToString());
        writer.WriteString("ConnectionMode", value.ConnectionMode.ToString());

        writer.WriteEndObject();
    }
}