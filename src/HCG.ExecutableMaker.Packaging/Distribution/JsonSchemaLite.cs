using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace HCG.ExecutableMaker.Packaging.Distribution;

/// <summary>
/// A small JSON Schema (draft 2020-12) evaluator for the keywords HCG's interchange schemas use: <c>type</c> (one or a
/// list, including <c>null</c> and <c>integer</c>), <c>const</c>, <c>enum</c>, <c>required</c>, <c>properties</c>,
/// <c>additionalProperties</c>, <c>items</c>, <c>pattern</c>, <c>minimum</c>/<c>maximum</c>, <c>minLength</c>/<c>maxLength</c>,
/// <c>minItems</c>/<c>maxItems</c>. Annotations (<c>title</c>, <c>description</c>, <c>examples</c>, <c>$id</c>, <c>$schema</c>,
/// <c>format</c>, <c>default</c>) are ignored. Any other keyword is reported as unsupported rather than skipped, so a schema
/// that starts using something new is noticed instead of being half-checked.
/// </summary>
public static class JsonSchemaLite
{
    private static readonly HashSet<string> Annotations = ["$schema", "$id", "$comment", "title", "description", "examples", "format", "default", "deprecated", "readOnly", "writeOnly"];
    private static readonly HashSet<string> Supported = ["type", "const", "enum", "required", "properties", "additionalProperties", "items", "pattern",
        "minimum", "maximum", "minLength", "maxLength", "minItems", "maxItems"];

    /// <summary>Every problem, each with its JSON path (e.g. <c>$.artifact.sha256</c>); empty when the instance is valid.</summary>
    public static IReadOnlyList<string> Validate(JsonNode schema, JsonNode? instance)
    {
        var problems = new List<string>();
        Check(schema, instance, "$", problems);
        return problems;
    }

    /// <summary>Keywords in the schema this evaluator does not implement (empty when it can check the whole schema).</summary>
    public static IReadOnlyList<string> UnsupportedKeywords(JsonNode schema)
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        void Walk(JsonNode? s, string path)
        {
            if (s is not JsonObject o) return;
            foreach (var (k, v) in o)
            {
                if (!Annotations.Contains(k) && !Supported.Contains(k)) found.Add($"{path}: {k}");
                if (k == "properties" && v is JsonObject props) foreach (var (pk, pv) in props) Walk(pv, $"{path}.properties.{pk}");
                if (k is "items" or "additionalProperties") Walk(v, $"{path}.{k}");
            }
        }
        Walk(schema, "$");
        return found.ToList();
    }

    private static void Check(JsonNode? schema, JsonNode? node, string path, List<string> problems)
    {
        if (schema is JsonValue b && b.TryGetValue<bool>(out var allow)) { if (!allow) problems.Add($"{path}: not allowed"); return; }
        if (schema is not JsonObject s) return;
        foreach (var (k, _) in s)
            if (!Annotations.Contains(k) && !Supported.Contains(k)) problems.Add($"{path}: the schema uses '{k}', which this checker does not implement");

        if (s["type"] is { } type)
        {
            var allowed = type is JsonArray a ? a.Select(t => t!.GetValue<string>()).ToList() : [type.GetValue<string>()];
            if (!allowed.Any(t => IsType(node, t)))
            {
                problems.Add($"{path}: expected {string.Join(" or ", allowed)}, found {TypeName(node)}");
                return;   // the other keywords would only repeat this
            }
        }
        if (s.TryGetPropertyValue("const", out var c) && !JsonNode.DeepEquals(c, node))
            problems.Add($"{path}: must be {c?.ToJsonString() ?? "null"}, found {node?.ToJsonString() ?? "null"}");
        if (s["enum"] is JsonArray e && !e.Any(x => JsonNode.DeepEquals(x, node)))
            problems.Add($"{path}: must be one of {string.Join(", ", e.Select(x => x?.ToJsonString() ?? "null"))}, found {node?.ToJsonString() ?? "null"}");

        if (node is JsonObject obj)
        {
            if (s["required"] is JsonArray req)
                foreach (var r in req.Select(x => x!.GetValue<string>()))
                    if (!obj.ContainsKey(r)) problems.Add($"{path}: '{r}' is required");
            var props = s["properties"] as JsonObject;
            foreach (var (k, v) in obj)
            {
                if (props is not null && props.TryGetPropertyValue(k, out var ps)) Check(ps, v, $"{path}.{k}", problems);
                else if (s.TryGetPropertyValue("additionalProperties", out var ap)) Check(ap, v, $"{path}.{k}", problems);
            }
        }
        if (node is JsonArray arr)
        {
            if (s["items"] is { } items) for (var i = 0; i < arr.Count; i++) Check(items, arr[i], $"{path}[{i}]", problems);
            if (s["minItems"] is { } mi && arr.Count < mi.GetValue<int>()) problems.Add($"{path}: at least {mi} items");
            if (s["maxItems"] is { } ma && arr.Count > ma.GetValue<int>()) problems.Add($"{path}: at most {ma} items");
        }
        if (node is JsonValue val && val.TryGetValue<string>(out var str))
        {
            if (s["pattern"] is { } pat && !Regex.IsMatch(str, pat.GetValue<string>(), RegexOptions.None, TimeSpan.FromSeconds(1)))
                problems.Add($"{path}: \"{(str.Length > 80 ? str[..80] + "…" : str)}\" does not match {pat.GetValue<string>()}");
            if (s["minLength"] is { } mnl && str.Length < mnl.GetValue<int>()) problems.Add($"{path}: shorter than {mnl}");
            if (s["maxLength"] is { } mxl && str.Length > mxl.GetValue<int>()) problems.Add($"{path}: longer than {mxl}");
        }
        if (node is JsonValue num && Number(num) is { } d)
        {
            if (s["minimum"] is { } min && d < min.GetValue<double>()) problems.Add($"{path}: below {min}");
            if (s["maximum"] is { } max && d > max.GetValue<double>()) problems.Add($"{path}: above {max}");
        }
    }

    private static bool IsType(JsonNode? n, string t) => t switch
    {
        "null" => n is null,
        "object" => n is JsonObject,
        "array" => n is JsonArray,
        "string" => n is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.String,
        "boolean" => n is JsonValue v && v.GetValueKind() is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False,
        "number" => n is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number,
        "integer" => n is JsonValue v && Number(v) is { } x && Math.Floor(x) == x,
        _ => false,
    };

    /// <summary>The value of a JSON number, whether parsed (JsonElement) or created in code (int, long, double, decimal…).</summary>
    private static double? Number(JsonValue v)
    {
        if (v.GetValueKind() != System.Text.Json.JsonValueKind.Number) return null;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<decimal>(out var m)) return (double)m;
        if (v.TryGetValue<float>(out var f)) return f;
        if (v.TryGetValue<ulong>(out var u)) return u;
        return double.TryParse(v.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var p) ? p : null;
    }

    private static string TypeName(JsonNode? n) => n switch
    {
        null => "null",
        JsonObject => "object",
        JsonArray => "array",
        JsonValue v => v.GetValueKind() switch
        {
            System.Text.Json.JsonValueKind.String => "string",
            System.Text.Json.JsonValueKind.Number => "number",
            System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False => "boolean",
            _ => "value",
        },
        _ => "value",
    };
}
