using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ChatSpace.Core;

/// JSON conventions of docs/PROTOCOL.md: camelCase keys, snake_case enum values, nulls omitted.
public static class Json
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.AllowNamedFloatingPointLiterals,
            PropertyNameCaseInsensitive = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        o.Converters.Add(new SnakeEnumConverterFactory());
        o.Converters.Add(new LenientIntConverter());
        return o;
    }

    public static T? Decode<T>(this JsonNode? node)
    {
        if (node == null) return default;
        return node.Deserialize<T>(Options);
    }

    /// Like Swift's `try? x.decode(T.self)`.
    public static T? TryDecode<T>(this JsonNode? node)
    {
        try { return node.Decode<T>(); } catch { return default; }
    }

    public static JsonNode? From<T>(T value) => JsonSerializer.SerializeToNode(value, Options);

    public static string Encode<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Parse<T>(string s)
    {
        try { return JsonSerializer.Deserialize<T>(s, Options); } catch { return default; }
    }

    public static string? Str(this JsonNode? n, string key) =>
        n is JsonObject o && o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static bool? Bool(this JsonNode? n, string key) =>
        n is JsonObject o && o[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    public static double? Num(this JsonNode? n, string key)
    {
        if (n is not JsonObject o || o[key] is not JsonValue v) return null;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<int>(out var i)) return i;
        return null;
    }

    public static JsonNode? Get(this JsonNode? n, string key) => n is JsonObject o ? o[key] : null;

    public static string SnakeCase(string pascal)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < pascal.Length; i++)
        {
            var c = pascal[i];
            if (char.IsUpper(c))
            {
                if (i > 0) sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }
}

/// Enum members `LoggedOut` ⇄ `"logged_out"`. Unknown values decode to a member named `Unsupported` when the enum has one.
public sealed class SnakeEnumConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type t) => t.IsEnum;

    public override JsonConverter CreateConverter(Type t, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(SnakeEnumConverter<>).MakeGenericType(t))!;

    private sealed class SnakeEnumConverter<T> : JsonConverter<T> where T : struct, Enum
    {
        private static readonly Dictionary<string, T> ByName = Enum.GetValues<T>().ToDictionary(v => Json.SnakeCase(v.ToString()), v => v);
        private static readonly Dictionary<T, string> ByValue = ByName.ToDictionary(p => p.Value, p => p.Key);
        private static readonly T? Fallback = Enum.TryParse<T>("Unsupported", out var u) ? u : null;

        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var s = reader.TokenType == JsonTokenType.String ? reader.GetString() ?? "" : "";
            if (ByName.TryGetValue(s, out var v)) return v;
            if (Fallback is { } f) return f;
            throw new JsonException($"Unknown {typeof(T).Name} '{s}'");
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) => writer.WriteStringValue(ByValue[value]);
    }
}

/// Bridges written in JavaScript send counts as floats ("3.0"); Swift's JSONDecoder accepts those for Int.
public sealed class LenientIntConverter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
            return reader.TryGetInt32(out var i) ? i : (int)reader.GetDouble();
        if (reader.TokenType == JsonTokenType.String && double.TryParse(reader.GetString(), System.Globalization.CultureInfo.InvariantCulture, out var d))
            return (int)d;
        throw new JsonException("Expected number");
    }

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
}
