using System.Text.Json;
using System.Text.Json.Serialization;
using PCCExecutive.Domain;

namespace PCCExecutive.Infrastructure;

internal static class DurabilityJson
{
    public static JsonSerializerOptions CreateOptions(bool writeIndented = false)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = writeIndented
        };
        options.Converters.Add(new StableIdJsonConverterFactory());
        options.Converters.Add(new WorkerSlotIdJsonConverter());
        options.Converters.Add(new ManagerEstimateJsonConverter());
        options.Converters.Add(new VerifiedCompletionJsonConverter());
        options.Converters.Add(new ReadOnlySetJsonConverterFactory());
        options.Converters.Add(new ReadOnlyListJsonConverterFactory());
        return options;
    }

    private sealed class StableIdJsonConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert) =>
            typeToConvert.IsValueType && typeof(IStableId).IsAssignableFrom(typeToConvert);

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
            (JsonConverter)Activator.CreateInstance(typeof(StableIdJsonConverter<>).MakeGenericType(typeToConvert))!;
    }

    private sealed class StableIdJsonConverter<T> : JsonConverter<T> where T : struct, IStableId
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            Guid value;
            if (reader.TokenType == JsonTokenType.String)
            {
                value = reader.GetGuid();
            }
            else
            {
                using var document = JsonDocument.ParseValue(ref reader);
                var root = document.RootElement;
                if (!TryGetProperty(root, "value", out var valueElement))
                    throw new JsonException($"{typeof(T).Name} is missing value.");
                value = valueElement.ValueKind == JsonValueKind.String
                    ? Guid.Parse(valueElement.GetString()!)
                    : valueElement.GetGuid();
            }

            return (T)Activator.CreateInstance(typeof(T), new object[] { value })!;
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("value", value.Value);
            writer.WriteEndObject();
        }
    }

    private sealed class WorkerSlotIdJsonConverter : JsonConverter<WorkerSlotId>
    {
        public override WorkerSlotId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            int value;
            if (reader.TokenType == JsonTokenType.Number)
            {
                value = reader.GetInt32();
            }
            else
            {
                using var document = JsonDocument.ParseValue(ref reader);
                if (!TryGetProperty(document.RootElement, "value", out var valueElement))
                    throw new JsonException("WorkerSlotId is missing value.");
                value = valueElement.GetInt32();
            }
            return new WorkerSlotId(value);
        }

        public override void Write(Utf8JsonWriter writer, WorkerSlotId value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteNumber("value", value.Value);
            writer.WriteEndObject();
        }
    }

    private sealed class ManagerEstimateJsonConverter : JsonConverter<ManagerEstimate>
    {
        public override ManagerEstimate Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            new(ReadDecimal(ref reader, "percent"));

        public override void Write(Utf8JsonWriter writer, ManagerEstimate value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteNumber("percent", value.Percent);
            writer.WriteEndObject();
        }
    }

    private sealed class VerifiedCompletionJsonConverter : JsonConverter<VerifiedCompletion>
    {
        public override VerifiedCompletion Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            new(ReadDecimal(ref reader, "percent"));

        public override void Write(Utf8JsonWriter writer, VerifiedCompletion value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteNumber("percent", value.Percent);
            writer.WriteEndObject();
        }
    }

    private sealed class ReadOnlySetJsonConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert) =>
            typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(IReadOnlySet<>);

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
            (JsonConverter)Activator.CreateInstance(typeof(ReadOnlySetJsonConverter<>).MakeGenericType(typeToConvert.GetGenericArguments()[0]))!;
    }

    private sealed class ReadOnlySetJsonConverter<T> : JsonConverter<IReadOnlySet<T>>
    {
        public override IReadOnlySet<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            JsonSerializer.Deserialize<HashSet<T>>(ref reader, options) ?? new HashSet<T>();

        public override void Write(Utf8JsonWriter writer, IReadOnlySet<T> value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value.ToArray(), options);
    }

    private sealed class ReadOnlyListJsonConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert) =>
            typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(IReadOnlyList<>);

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
            (JsonConverter)Activator.CreateInstance(typeof(ReadOnlyListJsonConverter<>).MakeGenericType(typeToConvert.GetGenericArguments()[0]))!;
    }

    private sealed class ReadOnlyListJsonConverter<T> : JsonConverter<IReadOnlyList<T>>
    {
        public override IReadOnlyList<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            JsonSerializer.Deserialize<T[]>(ref reader, options) ?? Array.Empty<T>();

        public override void Write(Utf8JsonWriter writer, IReadOnlyList<T> value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value.ToArray(), options);
    }

    private static decimal ReadDecimal(ref Utf8JsonReader reader, string propertyName)
    {
        if (reader.TokenType == JsonTokenType.Number) return reader.GetDecimal();
        using var document = JsonDocument.ParseValue(ref reader);
        if (!TryGetProperty(document.RootElement, propertyName, out var element))
            throw new JsonException($"Missing {propertyName}.");
        return element.GetDecimal();
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.TryGetProperty(name, out value)) return true;
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }
}
