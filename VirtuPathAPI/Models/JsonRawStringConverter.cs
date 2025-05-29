using System;
using System.Text.Json;
using System.Text.Json.Serialization;

public class JsonRawStringConverter : JsonConverter<string>
{
    // We still read it back as a raw string
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.GetString() ?? "";

    // But on write, we parse that string and emit it *as* JSON
    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            writer.WriteNullValue();
            return;
        }

        using var doc = JsonDocument.Parse(value);
        doc.RootElement.WriteTo(writer);
    }
}
