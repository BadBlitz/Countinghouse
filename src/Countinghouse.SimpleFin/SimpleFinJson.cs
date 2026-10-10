using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Countinghouse.SimpleFin;

// SimpleFIN sends money as a JSON *string* ("-42.10") specifically to avoid float rounding, so
// binding straight to decimal fails. Numbers are accepted too because the spec's own examples
// aren't consistent about it and a stricter reader would break on a server that sends 0 instead
// of "0.00" — money is the one field where being lenient on input is cheaper than being wrong.
public sealed class SimpleFinDecimalConverter : JsonConverter<decimal>
{
    public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
            return reader.GetDecimal();

        var raw = reader.GetString();
        if (decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
            return value;

        throw new JsonException($"Expected a decimal amount but got '{raw}'.");
    }

    public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
}

public sealed class SimpleFinNullableDecimalConverter : JsonConverter<decimal?>
{
    private static readonly SimpleFinDecimalConverter Inner = new();

    public override decimal? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? null : Inner.Read(ref reader, typeof(decimal), options);

    public override void Write(Utf8JsonWriter writer, decimal? value, JsonSerializerOptions options)
    {
        if (value is null)
            writer.WriteNullValue();
        else
            Inner.Write(writer, value.Value, options);
    }
}

public static class SimpleFinJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        // A server that adds a field shouldn't break a sync.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };
}
