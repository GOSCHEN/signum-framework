using System.Buffers;
using System.IO;
using System.Text.Json;

namespace Signum.Utilities;

public static class JsonExtensions
{
    public static T ToObject<T>(this JsonElement element, JsonSerializerOptions? options = null)
    {
        var bufferWriter = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(bufferWriter))
            element.WriteTo(writer);
        return JsonSerializer.Deserialize<T>(bufferWriter.WrittenSpan, options)!;
    }

    public static object ToObject(this JsonElement element, Type targetType, JsonSerializerOptions? options = null)
    {
        var bufferWriter = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(bufferWriter))
            element.WriteTo(writer);
        return JsonSerializer.Deserialize(bufferWriter.WrittenSpan, targetType, options)!;
    }

    public static void Assert(this ref Utf8JsonReader reader, JsonTokenType expected)
    {
        if (reader.TokenType != expected)
            throw new JsonException($"Expected '{expected}' but '{reader.TokenType}' found in '{reader.CurrentState}'");
    }

    public static object? GetLiteralValue(this ref Utf8JsonReader reader)
    {
        return reader.TokenType == JsonTokenType.Null ? null! :
               reader.TokenType == JsonTokenType.String ? reader.GetString() :
               reader.TokenType == JsonTokenType.Number ? reader.GetInt64() :
               reader.TokenType == JsonTokenType.True ? true :
               reader.TokenType == JsonTokenType.False ? false :
               throw new UnexpectedValueException(reader.TokenType);
    }

    /// <summary>
    /// Extracts a scalar value from a JSON text using a SQL Server JSON path like "$.a.b[0]".
    /// In the LINQ provider it translates to JSON_VALUE (SQL Server) or #&gt;&gt; (PostgreSQL).
    /// In-memory it uses lax semantics: a missing path or a non-scalar value returns null.
    /// </summary>
    public static string? JsonValue(this string? json, string path)
    {
        if (json == null)
            return null;

        var (strict, segments) = JsonPathParser.Parse(path);

        try
        {
            using var doc = JsonDocument.Parse(json);
            var current = doc.RootElement;
            foreach (var segment in segments)
            {
                if (segment.Index is int index)
                {
                    if (current.ValueKind != JsonValueKind.Array || index >= current.GetArrayLength())
                        return NotFound(strict, path);

                    current = current[index];
                }
                else
                {
                    if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment.Name!, out var prop))
                        return NotFound(strict, path);

                    current = prop;
                }
            }

            return current.ValueKind switch
            {
                JsonValueKind.String => current.GetString(),
                JsonValueKind.Number => current.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Null => null,
                _ => strict ? throw new InvalidOperationException($"JSON path '{path}' does not point to a scalar value") : null,
            };
        }
        catch (JsonException) when (!strict)
        {
            return null;
        }
    }

    static string? NotFound(bool strict, string path)
    {
        if (strict)
            throw new InvalidOperationException($"JSON path '{path}' not found");

        return null;
    }

    //Binary
    public static string ToJsonString(object obj, JsonSerializerOptions? options = null)
    {
        return JsonSerializer.Serialize(obj, obj.GetType(), options);
    }

    public static byte[] ToJsonBytes(object obj, JsonSerializerOptions? options = null)
    {
        using (MemoryStream ms = new MemoryStream())
        using (Utf8JsonWriter writer = new Utf8JsonWriter(ms))
        {
            JsonSerializer.Serialize(writer, obj, obj.GetType(), options);
            return ms.ToArray();
        }
    }

    public static void ToJsonFile(object graph, string fileName, JsonSerializerOptions? options = null)
    {
        using (FileStream fs = File.OpenWrite(fileName))
        using (Utf8JsonWriter writer = new Utf8JsonWriter(fs))
        {
            JsonSerializer.Serialize(writer, graph, graph.GetType(), options);
        }
    }

    public static T FromJsonBytes<T>(byte[] bytes, JsonSerializerOptions? options = null)
    {
        Utf8JsonReader reader = new Utf8JsonReader(bytes);
        var result = JsonSerializer.Deserialize<T>(ref reader, options);
        return result!;
    }

    public static T FromJsonFile<T>(string fileName, JsonSerializerOptions? options = null)
    {
        var bytes = File.ReadAllBytes(fileName);
        Utf8JsonReader reader = new Utf8JsonReader(bytes);
        var result = JsonSerializer.Deserialize<T>(ref reader, options);
        return result!;
    }

    public static T FromJsonString<T>(string json, JsonSerializerOptions? options = null)
    {
        var result = JsonSerializer.Deserialize<T>(json, options);
        return result!;
    }


    public static JsonElement? TryGetProperty(this JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var value))
            return value;

        return null;
    }
}

/// <summary>
/// Minimal parser for SQL Server JSON path expressions: [lax|strict] $ { .name | ."quoted name" | [index] }
/// </summary>
public static class JsonPathParser
{
    public readonly struct Segment
    {
        public readonly string? Name;
        public readonly int? Index;

        public Segment(string name) { Name = name; Index = null; }
        public Segment(int index) { Name = null; Index = index; }

        public override string ToString() => Index is int i ? $"[{i}]" : $".{Name}";
    }

    public static (bool strict, List<Segment> segments) Parse(string path)
    {
        if (path == null)
            throw new ArgumentNullException(nameof(path));

        var p = path.Trim();
        bool strict = false;
        if (p.StartsWith("strict ", StringComparison.OrdinalIgnoreCase))
        {
            strict = true;
            p = p.Substring("strict ".Length).TrimStart();
        }
        else if (p.StartsWith("lax ", StringComparison.OrdinalIgnoreCase))
        {
            p = p.Substring("lax ".Length).TrimStart();
        }

        if (p.Length == 0 || p[0] != '$')
            throw new FormatException($"Invalid JSON path '{path}': it should start with '$'");

        var segments = new List<Segment>();
        int i = 1;
        while (i < p.Length)
        {
            if (p[i] == '.')
            {
                i++;
                if (i < p.Length && p[i] == '"')
                {
                    int end = p.IndexOf('"', i + 1);
                    if (end == -1)
                        throw new FormatException($"Invalid JSON path '{path}': unterminated quoted name");

                    segments.Add(new Segment(p.Substring(i + 1, end - i - 1)));
                    i = end + 1;
                }
                else
                {
                    int start = i;
                    while (i < p.Length && p[i] != '.' && p[i] != '[')
                        i++;

                    if (start == i)
                        throw new FormatException($"Invalid JSON path '{path}': empty property name");

                    segments.Add(new Segment(p.Substring(start, i - start)));
                }
            }
            else if (p[i] == '[')
            {
                int end = p.IndexOf(']', i);
                if (end == -1)
                    throw new FormatException($"Invalid JSON path '{path}': unterminated array index");

                var inner = p.Substring(i + 1, end - i - 1).Trim();
                if (!int.TryParse(inner, out int index) || index < 0)
                    throw new FormatException($"Invalid JSON path '{path}': only non-negative array indexes are supported, not '{inner}'");

                segments.Add(new Segment(index));
                i = end + 1;
            }
            else
                throw new FormatException($"Invalid JSON path '{path}': unexpected character '{p[i]}' at position {i}");
        }

        return (strict, segments);
    }
}
