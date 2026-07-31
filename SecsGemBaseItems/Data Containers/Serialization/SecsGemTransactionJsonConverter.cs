using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using SecsGemBaseItems.Enums;

namespace SecsGemBaseItems.Data_Containers.Serialization;

public class SecsGemTransactionJsonConverter : JsonConverter<SecsGemTransaction>
{
    private static readonly JsonSerializerOptions DefaultOptions = new()
    {
        Converters = { new SecsGemTransactionJsonConverter() },
        WriteIndented = false
    };

    public static string Serialize(SecsGemTransaction transaction)
        => JsonSerializer.Serialize(transaction, DefaultOptions);

    public static SecsGemTransaction? Deserialize(string json)
        => JsonSerializer.Deserialize<SecsGemTransaction>(json, DefaultOptions);

    public override SecsGemTransaction? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject) return null;

        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        var tx = new SecsGemTransaction
        {
            Name = root.GetProperty("Name").GetString() ?? "",
            Description = root.GetProperty("Description").GetString() ?? ""
        };

        if (root.TryGetProperty("PrimaryMessage", out var primary))
            tx.PrimaryMessage = ReadDataMessage(primary);
        if (root.TryGetProperty("ReplyMessage", out var reply))
            tx.ReplyMessage = ReadDataMessage(reply);

        return tx;
    }

    private static SecsGemDataMessage ReadDataMessage(JsonElement element)
    {
        var msg = new SecsGemDataMessage
        {
            Name = element.GetProperty("Name").GetString() ?? "",
            Description = element.GetProperty("Description").GetString() ?? "",
            Reply = element.GetProperty("Reply").GetBoolean(),
            Stream = element.GetProperty("Stream").GetByte(),
            Function = element.GetProperty("Function").GetByte()
        };

        if (element.TryGetProperty("IsPrimary", out var ip))
            msg.IsPrimary = ip.GetBoolean();

        if (element.TryGetProperty("Children", out var children))
        {
            foreach (var child in children.EnumerateArray())
            {
                var item = ReadSecsGemItem(child);
                item.SetParent(msg);
            }
        }

        return msg;
    }

    private static SecsGemItem ReadSecsGemItem(JsonElement element)
    {
        var formatType = Enum.Parse<SecsGemItemFormatType>(element.GetProperty("FormatType").GetString()!);
        var item = SecsGemItem.Create(formatType);
        item.Name = element.GetProperty("Name").GetString() ?? "";
        item.Description = element.GetProperty("Description").GetString() ?? "";

        if (element.TryGetProperty("Values", out var values))
        {
            var strings = new string[values.GetArrayLength()];
            var i = 0;
            foreach (var v in values.EnumerateArray())
                strings[i++] = v.GetString() ?? "";
            item.SetValuesFromStrings(strings);
        }

        if (element.TryGetProperty("Children", out var children))
        {
            foreach (var child in children.EnumerateArray())
            {
                var childItem = ReadSecsGemItem(child);
                childItem.SetParent(item);
            }
        }

        return item;
    }

    public override void Write(Utf8JsonWriter writer, SecsGemTransaction value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("Name", value.Name);
        writer.WriteString("Description", value.Description);

        writer.WritePropertyName("PrimaryMessage");
        WriteDataMessage(writer, value.PrimaryMessage);

        writer.WritePropertyName("ReplyMessage");
        WriteDataMessage(writer, value.ReplyMessage);

        writer.WriteEndObject();
    }

    private static void WriteDataMessage(Utf8JsonWriter writer, SecsGemDataMessage msg)
    {
        writer.WriteStartObject();
        writer.WriteString("Name", msg.Name);
        writer.WriteString("Description", msg.Description);
        writer.WriteBoolean("Reply", msg.Reply);
        writer.WriteNumber("Stream", msg.Stream);
        writer.WriteNumber("Function", msg.Function);
        writer.WriteBoolean("IsPrimary", msg.IsPrimary);

        writer.WritePropertyName("Children");
        writer.WriteStartArray();
        foreach (var iDataItem in msg.Children)
        {
            var child = (SecsGemItem)iDataItem;
            WriteSecsGemItem(writer, child);
        }

        writer.WriteEndArray();

        writer.WriteEndObject();
    }

    private static void WriteSecsGemItem(Utf8JsonWriter writer, SecsGemItem item)
    {
        writer.WriteStartObject();
        writer.WriteString("Name", item.Name);
        writer.WriteString("Description", item.Description);
        writer.WriteString("FormatType", item.FormatType.ToString());

        writer.WritePropertyName("Values");
        writer.WriteStartArray();
        foreach (var v in item.GetStringValues())
            writer.WriteStringValue(v);
        writer.WriteEndArray();

        writer.WritePropertyName("Children");
        writer.WriteStartArray();
        foreach (var iDataItem in item.Children)
        {
            var child = (SecsGemItem)iDataItem;
            WriteSecsGemItem(writer, child);
        }

        writer.WriteEndArray();

        writer.WriteEndObject();
    }
}
