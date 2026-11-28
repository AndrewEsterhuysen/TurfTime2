using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Plugin.Firebase.Firestore;

namespace TurfTime2.Services.WinFirestore;

/// <summary>Error from the Firestore REST API (status like PERMISSION_DENIED is kept in the message).</summary>
public sealed class WindowsFirestoreException : Exception
{
    public int HttpStatus { get; }
    public string Status { get; }
    public WindowsFirestoreException(int httpStatus, string status, string message)
        : base($"{status}: {message}") { HttpStatus = httpStatus; Status = status; }
}

/// <summary>Firestore REST value encoding/decoding and write construction.</summary>
internal static class FsJson
{
    private static readonly Regex SimpleSegment = new("^[a-zA-Z_][a-zA-Z_0-9]*$", RegexOptions.Compiled);

    public static string FullName(string docPath) => $"{WindowsFirebaseConfig.DocumentsRoot}/{docPath}";

    public static string Url(string path) =>
        string.IsNullOrEmpty(path)
            ? WindowsFirebaseConfig.DocumentsBaseUrl
            : $"{WindowsFirebaseConfig.DocumentsBaseUrl}/{string.Join('/', path.Split('/').Select(Uri.EscapeDataString))}";

    public static string QuoteSegment(string s) =>
        SimpleSegment.IsMatch(s) ? s : "`" + s.Replace("\\", "\\\\").Replace("`", "\\`") + "`";

    public static string JoinPath(IEnumerable<string> segments) => string.Join('.', segments.Select(QuoteSegment));

    // ── Encode ────────────────────────────────────────────────────────────
    public static Dictionary<string, object> Encode(object? v)
    {
        switch (v)
        {
            case null: return new() { ["nullValue"] = JsonSerializer.SerializeToElement<object?>(null) };
            case bool b: return new() { ["booleanValue"] = b };
            case string s: return new() { ["stringValue"] = s };
            case byte[] bytes: return new() { ["bytesValue"] = Convert.ToBase64String(bytes) };
            case sbyte or byte or short or ushort or int or uint or long:
                return new() { ["integerValue"] = Convert.ToInt64(v, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) };
            case ulong ul: return new() { ["integerValue"] = ((long)ul).ToString(CultureInfo.InvariantCulture) };
            case float f: return new() { ["doubleValue"] = (double)f };
            case double d: return new() { ["doubleValue"] = d };
            case decimal m: return new() { ["doubleValue"] = (double)m };
            case DateTimeOffset dto: return new() { ["timestampValue"] = dto.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture) };
            case DateTime dt: return new() { ["timestampValue"] = dt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture) };
            case GeoPoint gp: return new() { ["geoPointValue"] = new Dictionary<string, object> { ["latitude"] = gp.Latitude, ["longitude"] = gp.Longitude } };
            case Enum e: return new() { ["stringValue"] = e.ToString() };
            case IFirestoreObject fo: return new() { ["mapValue"] = new Dictionary<string, object> { ["fields"] = EncodeMap(ObjectToMap(fo)) } };
            case IDictionary dict:
                {
                    var m = new Dictionary<string, object?>();
                    foreach (DictionaryEntry de in dict) m[de.Key?.ToString() ?? ""] = de.Value;
                    return new() { ["mapValue"] = new Dictionary<string, object> { ["fields"] = EncodeMap(m) } };
                }
            case IEnumerable seq:
                {
                    var list = new List<object>();
                    foreach (var item in seq) list.Add(Encode(item));
                    return new() { ["arrayValue"] = new Dictionary<string, object> { ["values"] = list } };
                }
            default:
                throw new NotSupportedException($"Cannot store a value of type {v.GetType().Name} in Firestore.");
        }
    }

    public static Dictionary<string, object> EncodeMap(IDictionary<string, object?> map)
    {
        var o = new Dictionary<string, object>();
        foreach (var kv in map) o[kv.Key] = Encode(kv.Value);
        return o;
    }

    // ── Decode ────────────────────────────────────────────────────────────
    public static object? Decode(JsonElement v)
    {
        if (v.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in v.EnumerateObject())
        {
            switch (p.Name)
            {
                case "nullValue": return null;
                case "booleanValue": return p.Value.GetBoolean();
                case "integerValue": return long.Parse(p.Value.GetString()!, CultureInfo.InvariantCulture);
                case "doubleValue":
                    return p.Value.ValueKind == JsonValueKind.String
                        ? double.Parse(p.Value.GetString()!.Replace("Infinity", "∞").Replace("∞", "Infinity"), CultureInfo.InvariantCulture)
                        : p.Value.GetDouble();
                case "stringValue": return p.Value.GetString();
                case "timestampValue":
                    return DateTimeOffset.TryParse(p.Value.GetString(), CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto) ? dto : null;
                case "bytesValue": return Convert.FromBase64String(p.Value.GetString()!);
                case "referenceValue": return p.Value.GetString();
                case "geoPointValue":
                    return new GeoPoint(p.Value.TryGetProperty("latitude", out var la) ? la.GetDouble() : 0,
                                        p.Value.TryGetProperty("longitude", out var lo) ? lo.GetDouble() : 0);
                case "arrayValue":
                    {
                        var list = new List<object>();
                        if (p.Value.TryGetProperty("values", out var vals))
                            foreach (var item in vals.EnumerateArray()) list.Add(Decode(item)!);
                        return list;
                    }
                case "mapValue":
                    return p.Value.TryGetProperty("fields", out var f) ? DecodeFields(f) : new Dictionary<string, object>();
            }
        }
        return null;
    }

    public static Dictionary<string, object> DecodeFields(JsonElement fields)
    {
        var d = new Dictionary<string, object>();
        if (fields.ValueKind != JsonValueKind.Object) return d;
        foreach (var p in fields.EnumerateObject()) d[p.Name] = Decode(p.Value)!;
        return d;
    }

    /// <summary>Document resource → (relative path, fields).</summary>
    public static (string Path, Dictionary<string, object> Data) ReadDocument(JsonElement doc)
    {
        var name = doc.GetProperty("name").GetString() ?? "";
        const string marker = "/documents/";
        var i = name.IndexOf(marker, StringComparison.Ordinal);
        var path = i >= 0 ? name[(i + marker.Length)..] : name;
        var data = doc.TryGetProperty("fields", out var f) ? DecodeFields(f) : new Dictionary<string, object>();
        return (path, data);
    }

    // ── POCO mapping (IFirestoreObject with [FirestoreProperty]) ──────────
    public static Dictionary<string, object?> ObjectToMap(object obj)
    {
        var map = new Dictionary<string, object?>();
        foreach (var p in obj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var attr = p.GetCustomAttribute<FirestorePropertyAttribute>();
            if (attr is not null) { map[attr.PropertyName] = p.GetValue(obj); continue; }
            var ts = p.GetCustomAttribute<FirestoreServerTimestampAttribute>();
            if (ts is not null) map[ts.PropertyName] = FieldValue.ServerTimestamp();
        }
        return map;
    }

    public static T? ConvertData<T>(Dictionary<string, object> map, string id)
    {
        var t = typeof(T);
        if (t.IsAssignableFrom(typeof(Dictionary<string, object>))) return (T)(object)map;
        if (t == typeof(Dictionary<object, object>))
        {
            var o = new Dictionary<object, object>();
            foreach (var kv in map) o[kv.Key] = kv.Value;
            return (T)(object)o;
        }
        var inst = Activator.CreateInstance(t)!;
        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!p.CanWrite) continue;
            if (p.GetCustomAttribute<FirestoreDocumentIdAttribute>() is not null) { p.SetValue(inst, id); continue; }
            var attr = p.GetCustomAttribute<FirestorePropertyAttribute>();
            if (attr is null || !map.TryGetValue(attr.PropertyName, out var raw)) continue;
            p.SetValue(inst, ConvertValue(raw, p.PropertyType));
        }
        return (T)inst;
    }

    private static object? ConvertValue(object? v, Type target)
    {
        if (v is null) return target.IsValueType && Nullable.GetUnderlyingType(target) is null ? Activator.CreateInstance(target) : null;
        var t = Nullable.GetUnderlyingType(target) ?? target;
        if (t.IsInstanceOfType(v)) return v;
        if (t == typeof(string)) return Convert.ToString(v, CultureInfo.InvariantCulture);
        if (t == typeof(DateTimeOffset) && v is DateTime dt) return new DateTimeOffset(dt);
        if (t == typeof(DateTime) && v is DateTimeOffset dto) return dto.UtcDateTime;
        try { return Convert.ChangeType(v, t, CultureInfo.InvariantCulture); } catch { return t.IsValueType ? Activator.CreateInstance(t) : null; }
    }

    // ── Write construction ────────────────────────────────────────────────
    public static Dictionary<string, object?> ToMap(object data)
    {
        switch (data)
        {
            case IFirestoreObject fo: return ObjectToMap(fo);
            case IDictionary d:
                {
                    var m = new Dictionary<string, object?>();
                    foreach (DictionaryEntry de in d) m[de.Key?.ToString() ?? ""] = de.Value;
                    return m;
                }
            default: throw new NotSupportedException($"Cannot write a {data.GetType().Name} as a document.");
        }
    }

    public static Dictionary<string, object?> ToMap((object, object)[] pairs)
    {
        var m = new Dictionary<string, object?>();
        foreach (var (k, v) in pairs) m[k?.ToString() ?? ""] = v;
        return m;
    }

    public static Dictionary<string, object?> ToMap(Dictionary<object, object> d)
    {
        var m = new Dictionary<string, object?>();
        foreach (var kv in d) m[kv.Key?.ToString() ?? ""] = kv.Value;
        return m;
    }

    /// <summary>One document write, rendered as a Firestore <c>Write</c> for <c>documents:commit</c>.</summary>
    public sealed class WriteSpec
    {
        public string Path = "";
        public bool IsDelete;
        public bool? MustExist;
        public Dictionary<string, object>? Fields;
        public List<string>? Mask;
        public List<object> Transforms = new();

        public Dictionary<string, object> ToJson()
        {
            var w = new Dictionary<string, object>();
            if (IsDelete) { w["delete"] = FullName(Path); return w; }
            w["update"] = new Dictionary<string, object> { ["name"] = FullName(Path), ["fields"] = Fields ?? new() };
            if (Mask is not null) w["updateMask"] = new Dictionary<string, object> { ["fieldPaths"] = Mask };
            if (Transforms.Count > 0) w["updateTransforms"] = Transforms;
            if (MustExist is bool me) w["currentDocument"] = new Dictionary<string, object> { ["exists"] = me };
            return w;
        }
    }

    public static WriteSpec Delete(string path) => new() { Path = path, IsDelete = true };

    /// <summary><c>SetDataAsync</c>: full replace, or merge (deep, like the native SDKs) with <see cref="SetOptions"/>.</summary>
    public static WriteSpec Set(string path, Dictionary<string, object?> data, SetOptions? options)
    {
        var spec = new WriteSpec { Path = path };
        var deletes = new List<string>();
        var clean = Strip(data, new List<string>(), spec.Transforms, deletes);
        spec.Fields = EncodeMap(clean);
        if (options is null) return spec;

        spec.Mask = new List<string>();
        switch (options.Type)
        {
            case SetOptions.TypeMerge:
                Leaves(clean, new List<string>(), spec.Mask);
                spec.Mask.AddRange(deletes);
                break;
            case SetOptions.TypeMergeFields:
                foreach (var f in options.Fields ?? new List<string>()) spec.Mask.Add(JoinPath(f.Split('.')));
                break;
            case SetOptions.TypeMergeFieldPaths:
                foreach (var segs in options.FieldPaths ?? new List<IList<string>>()) spec.Mask.Add(JoinPath(segs));
                break;
        }
        return spec;
    }

    /// <summary><c>UpdateDataAsync</c>: keys are field paths (dots = nesting); document must exist.</summary>
    public static WriteSpec Update(string path, Dictionary<string, object?> data)
    {
        var spec = new WriteSpec { Path = path, MustExist = true, Mask = new List<string>() };
        var fields = new Dictionary<string, object?>();
        foreach (var (key, value) in data)
        {
            var segs = key.Split('.');
            var p = JoinPath(segs);
            if (value is FieldValue fv)
            {
                if (fv.Type == FieldValueType.Delete) spec.Mask.Add(p);
                else spec.Transforms.Add(Transform(p, fv));
                continue;
            }
            spec.Mask.Add(p);
            var cursor = fields;
            for (var i = 0; i < segs.Length - 1; i++)
            {
                if (!cursor.TryGetValue(segs[i], out var next) || next is not Dictionary<string, object?> nd)
                {
                    nd = new Dictionary<string, object?>();
                    cursor[segs[i]] = nd;
                }
                cursor = nd;
            }
            cursor[segs[^1]] = value;
        }
        spec.Fields = EncodeMap(fields);
        return spec;
    }

    private static Dictionary<string, object?> Strip(IDictionary<string, object?> map, List<string> prefix,
        List<object> transforms, List<string> deletes)
    {
        var clean = new Dictionary<string, object?>();
        foreach (var (k, v) in map)
        {
            var here = new List<string>(prefix) { k };
            switch (v)
            {
                case FieldValue fv:
                    if (fv.Type == FieldValueType.Delete) deletes.Add(JoinPath(here));
                    else transforms.Add(Transform(JoinPath(here), fv));
                    break;
                case IDictionary d when d is not IFirestoreObject:
                    var inner = new Dictionary<string, object?>();
                    foreach (DictionaryEntry de in d) inner[de.Key?.ToString() ?? ""] = de.Value;
                    clean[k] = Strip(inner, here, transforms, deletes);
                    break;
                default:
                    clean[k] = v;
                    break;
            }
        }
        return clean;
    }

    private static void Leaves(IDictionary<string, object?> map, List<string> prefix, List<string> mask)
    {
        foreach (var (k, v) in map)
        {
            var here = new List<string>(prefix) { k };
            if (v is IDictionary<string, object?> nested && nested.Count > 0) Leaves(nested, here, mask);
            else mask.Add(JoinPath(here));
        }
    }

    private static object Transform(string fieldPath, FieldValue fv)
    {
        var t = new Dictionary<string, object> { ["fieldPath"] = fieldPath };
        switch (fv.Type)
        {
            case FieldValueType.ServerTimestamp: t["setToServerValue"] = "REQUEST_TIME"; break;
            case FieldValueType.ArrayUnion:
                t["appendMissingElements"] = new Dictionary<string, object> { ["values"] = (fv.Elements ?? []).Select(Encode).ToList() }; break;
            case FieldValueType.ArrayRemove:
                t["removeAllFromArray"] = new Dictionary<string, object> { ["values"] = (fv.Elements ?? []).Select(Encode).ToList() }; break;
            case FieldValueType.IntegerIncrement when fv.IncrementValue == Math.Floor(fv.IncrementValue):
                t["increment"] = Encode((long)fv.IncrementValue); break;
            default:
                t["increment"] = Encode(fv.IncrementValue); break;
        }
        return t;
    }
}
