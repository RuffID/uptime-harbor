using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;

namespace UptimeHarbor.Application.Cluster.Onboarding;

/// <summary>Ограниченный строгий JSON; ошибки не сохраняют исходный текст и inner exception.</summary>
public static class StrictOnboardingJson
{
    private static readonly JsonSerializerOptions OPTIONS = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 24,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        Converters = { new UtcConverter() }
    };

    public static byte[] Encode<T>(T value)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, OPTIONS);
        Validate(bytes);
        using JsonDocument document = JsonDocument.Parse(bytes);
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            WriteCanonical(document.RootElement, writer);
        }

        return stream.ToArray();
    }

    public static T Decode<T>(ReadOnlySpan<byte> bytes)
    {
        Validate(bytes);
        try
        {
            return JsonSerializer.Deserialize<T>(bytes, OPTIONS) ?? throw new InvalidDataException("OnboardingDataMissing");
        }
        catch (JsonException)
        {
            throw new InvalidDataException("OnboardingDataInvalid");
        }
    }

    public static void Validate(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || bytes.Length > 262144)
        {
            throw new InvalidDataException("OnboardingDataLimit");
        }

        try
        {
            Utf8JsonReader reader = new(bytes, new JsonReaderOptions { MaxDepth = 24 });
            Stack<HashSet<string>> fields = new();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.StartObject)
                {
                    fields.Push(new HashSet<string>(StringComparer.Ordinal));
                }
                else if (reader.TokenType == JsonTokenType.EndObject)
                {
                    fields.Pop();
                }
                else if (reader.TokenType == JsonTokenType.PropertyName && !fields.Peek().Add(reader.GetString()!))
                {
                    throw new InvalidDataException("OnboardingDuplicateField");
                }
            }
        }
        catch (JsonException)
        {
            throw new InvalidDataException("OnboardingDataInvalid");
        }
    }

    private static void WriteCanonical(JsonElement element, Utf8JsonWriter writer)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (JsonProperty property in element.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                WriteCanonical(property.Value, writer);
            }

            writer.WriteEndObject();
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (JsonElement item in element.EnumerateArray())
            {
                WriteCanonical(item, writer);
            }

            writer.WriteEndArray();
        }
        else
        {
            element.WriteTo(writer);
        }
    }

    private class UtcConverter : JsonConverter<DateTimeOffset>
    {
        private const string FORMAT = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String || !DateTimeOffset.TryParseExact(reader.GetString(), FORMAT, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset value))
            {
                throw new JsonException();
            }

            return value;
        }

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
        {
            if (value.Offset != TimeSpan.Zero)
            {
                throw new InvalidDataException("OnboardingUtcRequired");
            }

            writer.WriteStringValue(value.ToString(FORMAT, CultureInfo.InvariantCulture));
        }
    }
}
