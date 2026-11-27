using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Plugin.Firebase.Firestore;

namespace TurfTime2.Services.WinFirestore;

/// <summary>
/// Windows-only <see cref="IFirebaseFirestore"/> implemented on the Firestore REST API. Every request carries
/// the signed-in user's Firebase ID token, so <c>firestore.rules</c> apply exactly as on iOS/Android (no admin
/// credentials, no Google.Cloud.Firestore). Registered only for the Windows target, so the existing services
/// that talk to Plugin.Firebase's interfaces run unchanged.
///
/// Supported: document get/set/merge/update/delete, collection + collection-group queries (where/order/limit/
/// cursors), batches, and polling "listeners". Not supported: transactions (phone-only nominations) and
/// offline persistence.
/// </summary>
public sealed class WindowsFirebaseFirestore : IFirebaseFirestore
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(8);
    private readonly IFirebaseAuthService _auth;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public WindowsFirebaseFirestore(IFirebaseAuthService auth) { _auth = auth; }

    public FirestoreSettings Settings { get; set; } = new();

    public IDocumentReference GetDocument(string documentPath) => new WinDocRef(this, Norm(documentPath));
    public ICollectionReference GetCollection(string collectionPath) => new WinCollectionRef(this, Norm(collectionPath));
    public IQuery GetCollectionGroup(string collectionId) => new WinQuery(this, "", collectionId, allDescendants: true);
    public IWriteBatch CreateBatch() => new WinBatch(this);

    public Task<TResult> RunTransactionAsync<TResult>(Func<ITransaction, TResult> updateFunc)
        => throw new NotSupportedException("Firestore transactions are not supported in the Windows app.");

    public Task WaitForPendingWritesAsync() => Task.CompletedTask;
    public Task DisableNetworkAsync() => Task.CompletedTask;
    public Task EnableNetworkAsync() => Task.CompletedTask;
    public Task ClearPersistenceAsync() => Task.CompletedTask;
    public Task TerminateAsync() => Task.CompletedTask;
    public void Restart() { }
    public void UseEmulator(string host, int port) => throw new NotSupportedException();
    public void Dispose() { }

    internal static string Norm(string p) => p.Trim('/');

    // ── HTTP ──────────────────────────────────────────────────────────────
    /// <summary>Sends a request as the signed-in user. Returns null on 404 when <paramref name="allow404"/>.</summary>
    internal async Task<JsonDocument?> SendAsync(HttpMethod method, string url, object? body = null, bool allow404 = false)
    {
        for (var attempt = 0; ; attempt++)
        {
            var token = await _auth.GetIdTokenAsync(forceRefresh: attempt > 0).ConfigureAwait(false);
            if (string.IsNullOrEmpty(token))
                throw new WindowsFirestoreException(401, "UNAUTHENTICATED", "Could not sign in to Firebase. Check your internet connection.");

            using var req = new HttpRequestMessage(method, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.TryAddWithoutValidation("X-Ios-Bundle-Identifier", WindowsFirebaseConfig.IosBundleId);
            if (body is not null)
                req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            using var resp = await Http.SendAsync(req).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (resp.IsSuccessStatusCode) return JsonDocument.Parse(text);
            if (resp.StatusCode == HttpStatusCode.NotFound && allow404) return null;
            if (resp.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) continue;
            throw ToException(resp.StatusCode, text);
        }
    }

    private static WindowsFirestoreException ToException(HttpStatusCode code, string text)
    {
        var status = code.ToString().ToUpperInvariant();
        var msg = text.Length <= 300 ? text : text[..300];
        try
        {
            using var d = JsonDocument.Parse(text);
            var root = d.RootElement.ValueKind == JsonValueKind.Array && d.RootElement.GetArrayLength() > 0 ? d.RootElement[0] : d.RootElement;
            if (root.TryGetProperty("error", out var e))
            {
                if (e.TryGetProperty("status", out var s) && s.GetString() is { Length: > 0 } st) status = st;
                if (e.TryGetProperty("message", out var m)) msg = m.GetString() ?? msg;
            }
        }
        catch { /* keep raw text */ }
        return new WindowsFirestoreException((int)code, status, msg);
    }

    internal async Task CommitAsync(IEnumerable<FsJson.WriteSpec> writes)
    {
        var list = writes.Select(w => (object)w.ToJson()).ToList();
        if (list.Count == 0) return;
        using var _ = await SendAsync(HttpMethod.Post, $"{WindowsFirebaseConfig.DocumentsBaseUrl}:commit",
            new Dictionary<string, object> { ["writes"] = list }).ConfigureAwait(false);
    }

    internal async Task<(Dictionary<string, object>? Data, string Raw)> GetDocAsync(string path)
    {
        using var doc = await SendAsync(HttpMethod.Get, FsJson.Url(path), allow404: true).ConfigureAwait(false);
        if (doc is null) return (null, "");
        return (FsJson.ReadDocument(doc.RootElement).Data, doc.RootElement.GetRawText());
    }

    internal async Task<(List<(string Path, Dictionary<string, object> Data)> Docs, string Raw)> RunQueryAsync(WinQuery q)
    {
        var parentUrl = FsJson.Url(q.ParentPath) + ":runQuery";
        using var resp = await SendAsync(HttpMethod.Post, parentUrl, new Dictionary<string, object> { ["structuredQuery"] = q.ToStructuredQuery() }).ConfigureAwait(false);
        var docs = new List<(string, Dictionary<string, object>)>();
        foreach (var item in resp!.RootElement.EnumerateArray())
            if (item.TryGetProperty("document", out var d)) docs.Add(FsJson.ReadDocument(d));
        if (q.LimitToLast) docs.Reverse();
        return (docs, resp.RootElement.GetRawText());
    }

    internal static IDisposable Poll(Func<Task<string>> fetchAndDeliver, Action<Exception>? onError)
    {
        var cts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            string? last = null;
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    var sig = await fetchAndDeliver().ConfigureAwait(false);
                    last = sig;
                }
                catch (Exception ex) when (!cts.IsCancellationRequested) { onError?.Invoke(ex); }
                try { await Task.Delay(PollInterval, cts.Token).ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
            _ = last;
        });
        return new Cancel(cts);
    }

    private sealed class Cancel(CancellationTokenSource cts) : IDisposable { public void Dispose() { try { cts.Cancel(); } catch { } } }
}

internal sealed class WinMetadata : ISnapshotMetadata
{
    public bool HasPendingWrites => false;
    public bool IsFromCache => false;
}

internal sealed class WinDocSnapshot<T> : IDocumentSnapshot<T>
{
    public WinDocSnapshot(T? data, IDocumentReference reference) { Data = data!; Reference = reference; }
    public T Data { get; }
    public ISnapshotMetadata Metadata { get; } = new WinMetadata();
    public IDocumentReference Reference { get; }
}

internal sealed class WinQuerySnapshot<T> : IQuerySnapshot<T>
{
    private readonly List<IDocumentSnapshot<T>> _docs;
    public WinQuerySnapshot(List<IDocumentSnapshot<T>> docs, IQuery query) { _docs = docs; Query = query; }
    public IEnumerable<IDocumentSnapshot<T>> Documents => _docs;
    public ISnapshotMetadata Metadata { get; } = new WinMetadata();
    public IEnumerable<DocumentChange<T>> DocumentChanges => GetDocumentChanges(false);
    public IQuery Query { get; }
    public bool IsEmpty => _docs.Count == 0;
    public int Count => _docs.Count;
    public IEnumerable<DocumentChange<T>> GetDocumentChanges(bool includeMetadataChanges)
        => _docs.Select((d, i) => new DocumentChange<T>(d, DocumentChangeType.Added, i, -1));
}

internal sealed class WinDocRef : IDocumentReference
{
    private readonly WindowsFirebaseFirestore _fs;
    public WinDocRef(WindowsFirebaseFirestore fs, string path) { _fs = fs; Path = path; }
    public string Path { get; }
    public string Id => Path[(Path.LastIndexOf('/') + 1)..];
    public ICollectionReference Parent => new WinCollectionRef(_fs, Path[..Path.LastIndexOf('/')]);

    public Task SetDataAsync(object data, SetOptions? options = null)
        => _fs.CommitAsync([FsJson.Set(Path, FsJson.ToMap(data), options)]);
    public Task SetDataAsync(Dictionary<object, object> data, SetOptions? options = null)
        => _fs.CommitAsync([FsJson.Set(Path, FsJson.ToMap(data), options)]);
    public Task SetDataAsync(params (object, object)[] data)
        => _fs.CommitAsync([FsJson.Set(Path, FsJson.ToMap(data), null)]);
    public Task SetDataAsync(SetOptions options, params (object, object)[] data)
        => _fs.CommitAsync([FsJson.Set(Path, FsJson.ToMap(data), options)]);
    public Task UpdateDataAsync(Dictionary<object, object> data)
        => _fs.CommitAsync([FsJson.Update(Path, FsJson.ToMap(data))]);
    public Task UpdateDataAsync(params (string, object)[] data)
        => _fs.CommitAsync([FsJson.Update(Path, data.ToDictionary(t => t.Item1, t => (object?)t.Item2))]);
    public Task DeleteDocumentAsync() => _fs.CommitAsync([FsJson.Delete(Path)]);

    public async Task<IDocumentSnapshot<T>> GetDocumentSnapshotAsync<T>(Source source = Source.Default)
    {
        var (data, _) = await _fs.GetDocAsync(Path).ConfigureAwait(false);
        return new WinDocSnapshot<T>(data is null ? default : FsJson.ConvertData<T>(data, Id), this);
    }

    public IDisposable AddSnapshotListener<T>(Action<IDocumentSnapshot<T>> onChanged, Action<Exception>? onError = null, bool includeMetaDataChanges = false)
    {
        string? last = null;
        return WindowsFirebaseFirestore.Poll(async () =>
        {
            var (data, raw) = await _fs.GetDocAsync(Path).ConfigureAwait(false);
            if (raw != last)
            {
                last = raw;
                onChanged(new WinDocSnapshot<T>(data is null ? default : FsJson.ConvertData<T>(data, Id), this));
            }
            return raw;
        }, onError);
    }

    public ICollectionReference GetCollection(string collectionPath) => new WinCollectionRef(_fs, $"{Path}/{WindowsFirebaseFirestore.Norm(collectionPath)}");
}

internal sealed class WinCollectionRef : WinQuery, ICollectionReference
{
    public WinCollectionRef(WindowsFirebaseFirestore fs, string collectionPath)
        : base(fs, collectionPath.Contains('/') ? collectionPath[..collectionPath.LastIndexOf('/')] : "",
               collectionPath[(collectionPath.LastIndexOf('/') + 1)..], allDescendants: false)
    {
        CollectionPath = collectionPath;
    }

    public string CollectionPath { get; }
    public IDocumentReference Parent => string.IsNullOrEmpty(ParentPath) ? null! : new WinDocRef(Fs, ParentPath);
    public IDocumentReference GetDocument(string documentPath) => new WinDocRef(Fs, $"{CollectionPath}/{WindowsFirebaseFirestore.Norm(documentPath)}");
    public IDocumentReference CreateDocument() => GetDocument(NewId());
    public async Task<IDocumentReference> AddDocumentAsync(object data)
    {
        var doc = CreateDocument();
        await doc.SetDataAsync(data).ConfigureAwait(false);
        return doc;
    }

    private static string NewId()
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        var sb = new StringBuilder(20);
        for (var i = 0; i < 20; i++) sb.Append(chars[Random.Shared.Next(chars.Length)]);
        return sb.ToString();
    }
}

internal class WinQuery : IQuery
{
    internal readonly WindowsFirebaseFirestore Fs;
    internal readonly string ParentPath;      // "" = database root, else "teams/abc" (a document)
    internal readonly string CollectionId;
    internal readonly bool AllDescendants;
    private readonly List<Dictionary<string, object>> _filters = new();
    private readonly List<(string Field, bool Desc)> _orders = new();
    private int? _limit;
    private (object[] Values, bool Before)? _startAt, _endAt;
    internal bool LimitToLast;

    public WinQuery(WindowsFirebaseFirestore fs, string parentPath, string collectionId, bool allDescendants)
    { Fs = fs; ParentPath = parentPath; CollectionId = collectionId; AllDescendants = allDescendants; }

    private WinQuery Clone()
    {
        var q = new WinQuery(Fs, ParentPath, CollectionId, AllDescendants) { LimitToLast = LimitToLast, _limit = _limit, _startAt = _startAt, _endAt = _endAt };
        q._filters.AddRange(_filters);
        q._orders.AddRange(_orders);
        return q;
    }

    private IQuery Where(string field, string op, object? value)
    {
        var q = Clone();
        q._filters.Add(new Dictionary<string, object>
        {
            ["fieldFilter"] = new Dictionary<string, object>
            {
                ["field"] = new Dictionary<string, object> { ["fieldPath"] = field },
                ["op"] = op,
                ["value"] = value is null ? FsJson.Encode(null) : EncodeFilterValue(field, value),
            }
        });
        return q;
    }

    private object EncodeFilterValue(string field, object value)
    {
        if (field == "__name__" && value is string id)
            return new Dictionary<string, object> { ["referenceValue"] = FsJson.FullName(AllDescendants ? id : $"{(string.IsNullOrEmpty(ParentPath) ? "" : ParentPath + "/")}{CollectionId}/{id}") };
        return FsJson.Encode(value);
    }

    private static string Name(FieldPath p) => p.IsDocumentId ? "__name__" : FsJson.JoinPath(p.Fields);

    private IQuery WhereList(string field, string op, object[] values)
    {
        var q = Clone();
        q._filters.Add(new Dictionary<string, object>
        {
            ["fieldFilter"] = new Dictionary<string, object>
            {
                ["field"] = new Dictionary<string, object> { ["fieldPath"] = field },
                ["op"] = op,
                ["value"] = FsJson.Encode(values.ToList()),
            }
        });
        return q;
    }

    public IQuery WhereEqualsTo(string field, object value) => Where(FsJson.JoinPath(field.Split('.')), "EQUAL", value);
    public IQuery WhereEqualsTo(FieldPath path, object value) => Where(Name(path), "EQUAL", value);
    public IQuery WhereGreaterThan(string field, object value) => Where(FsJson.JoinPath(field.Split('.')), "GREATER_THAN", value);
    public IQuery WhereGreaterThan(FieldPath path, object value) => Where(Name(path), "GREATER_THAN", value);
    public IQuery WhereLessThan(string field, object value) => Where(FsJson.JoinPath(field.Split('.')), "LESS_THAN", value);
    public IQuery WhereLessThan(FieldPath path, object value) => Where(Name(path), "LESS_THAN", value);
    public IQuery WhereGreaterThanOrEqualsTo(string field, object value) => Where(FsJson.JoinPath(field.Split('.')), "GREATER_THAN_OR_EQUAL", value);
    public IQuery WhereGreaterThanOrEqualsTo(FieldPath path, object value) => Where(Name(path), "GREATER_THAN_OR_EQUAL", value);
    public IQuery WhereLessThanOrEqualsTo(string field, object value) => Where(FsJson.JoinPath(field.Split('.')), "LESS_THAN_OR_EQUAL", value);
    public IQuery WhereLessThanOrEqualsTo(FieldPath path, object value) => Where(Name(path), "LESS_THAN_OR_EQUAL", value);
    public IQuery WhereArrayContains(string field, object value) => Where(FsJson.JoinPath(field.Split('.')), "ARRAY_CONTAINS", value);
    public IQuery WhereArrayContains(FieldPath path, object value) => Where(Name(path), "ARRAY_CONTAINS", value);
    public IQuery WhereArrayContainsAny(string field, object[] values) => WhereList(FsJson.JoinPath(field.Split('.')), "ARRAY_CONTAINS_ANY", values);
    public IQuery WhereArrayContainsAny(FieldPath path, object[] values) => WhereList(Name(path), "ARRAY_CONTAINS_ANY", values);
    public IQuery WhereFieldIn(string field, object[] values) => WhereList(FsJson.JoinPath(field.Split('.')), "IN", values);
    public IQuery WhereFieldIn(FieldPath path, object[] values) => WhereList(Name(path), "IN", values);

    public IQuery OrderBy(string field, bool descending = false)
    { var q = Clone(); q._orders.Add((FsJson.JoinPath(field.Split('.')), descending)); return q; }
    public IQuery OrderBy(FieldPath path, bool descending = false)
    { var q = Clone(); q._orders.Add((Name(path), descending)); return q; }

    public IQuery StartingAt(params object[] fieldValues) { var q = Clone(); q._startAt = (fieldValues, true); return q; }
    public IQuery StartingAfter(params object[] fieldValues) { var q = Clone(); q._startAt = (fieldValues, false); return q; }
    public IQuery EndingAt(params object[] fieldValues) { var q = Clone(); q._endAt = (fieldValues, false); return q; }
    public IQuery EndingBefore(params object[] fieldValues) { var q = Clone(); q._endAt = (fieldValues, true); return q; }
    public IQuery StartingAt(IDocumentSnapshot snapshot) => throw new NotSupportedException();
    public IQuery StartingAfter(IDocumentSnapshot snapshot) => throw new NotSupportedException();
    public IQuery EndingAt(IDocumentSnapshot snapshot) => throw new NotSupportedException();
    public IQuery EndingBefore(IDocumentSnapshot snapshot) => throw new NotSupportedException();

    public IQuery LimitedTo(int limit) { var q = Clone(); q._limit = limit; q.LimitToLast = false; return q; }
    public IQuery LimitedToLast(int limit) { var q = Clone(); q._limit = limit; q.LimitToLast = true; return q; }

    internal Dictionary<string, object> ToStructuredQuery()
    {
        var sq = new Dictionary<string, object>
        {
            ["from"] = new[] { new Dictionary<string, object> { ["collectionId"] = CollectionId, ["allDescendants"] = AllDescendants } }
        };
        if (_filters.Count == 1) sq["where"] = _filters[0];
        else if (_filters.Count > 1)
            sq["where"] = new Dictionary<string, object> { ["compositeFilter"] = new Dictionary<string, object> { ["op"] = "AND", ["filters"] = _filters } };

        var orders = _orders.Select(o => (o.Field, Desc: LimitToLast ? !o.Desc : o.Desc)).ToList();
        if (orders.Count > 0)
            sq["orderBy"] = orders.Select(o => new Dictionary<string, object>
            {
                ["field"] = new Dictionary<string, object> { ["fieldPath"] = o.Field },
                ["direction"] = o.Desc ? "DESCENDING" : "ASCENDING"
            }).ToList();
        if (_startAt is { } s) sq[LimitToLast ? "endAt" : "startAt"] = Cursor(s.Values, s.Before, flip: LimitToLast);
        if (_endAt is { } e) sq[LimitToLast ? "startAt" : "endAt"] = Cursor(e.Values, e.Before, flip: LimitToLast);
        if (_limit is int l) sq["limit"] = l;
        return sq;
    }

    private static Dictionary<string, object> Cursor(object[] values, bool before, bool flip)
        => new() { ["values"] = values.Select(FsJson.Encode).ToList(), ["before"] = flip ? !before : before };

    public async Task<IQuerySnapshot<T>> GetDocumentsAsync<T>(Source source = Source.Default)
    {
        var (docs, _) = await Fs.RunQueryAsync(this).ConfigureAwait(false);
        return BuildSnapshot<T>(docs);
    }

    private IQuerySnapshot<T> BuildSnapshot<T>(List<(string Path, Dictionary<string, object> Data)> docs)
    {
        var list = new List<IDocumentSnapshot<T>>();
        foreach (var (path, data) in docs)
        {
            var reference = new WinDocRef(Fs, path);
            list.Add(new WinDocSnapshot<T>(FsJson.ConvertData<T>(data, reference.Id), reference));
        }
        return new WinQuerySnapshot<T>(list, this);
    }

    public IDisposable AddSnapshotListener<T>(Action<IQuerySnapshot<T>> onChanged, Action<Exception>? onError = null, bool includeMetaDataChanges = false)
    {
        string? last = null;
        return WindowsFirebaseFirestore.Poll(async () =>
        {
            var (docs, raw) = await Fs.RunQueryAsync(this).ConfigureAwait(false);
            if (raw != last) { last = raw; onChanged(BuildSnapshot<T>(docs)); }
            return raw;
        }, onError);
    }
}

internal sealed class WinBatch : IWriteBatch
{
    private readonly WindowsFirebaseFirestore _fs;
    private readonly List<FsJson.WriteSpec> _writes = new();
    public WinBatch(WindowsFirebaseFirestore fs) { _fs = fs; }

    private IWriteBatch Add(FsJson.WriteSpec w) { _writes.Add(w); return this; }
    public IWriteBatch SetData(IDocumentReference document, object data, SetOptions? options = null) => Add(FsJson.Set(document.Path, FsJson.ToMap(data), options));
    public IWriteBatch SetData(IDocumentReference document, Dictionary<object, object> data, SetOptions? options = null) => Add(FsJson.Set(document.Path, FsJson.ToMap(data), options));
    public IWriteBatch SetData(IDocumentReference document, params (object, object)[] data) => Add(FsJson.Set(document.Path, FsJson.ToMap(data), null));
    public IWriteBatch SetData(IDocumentReference document, SetOptions options, params (object, object)[] data) => Add(FsJson.Set(document.Path, FsJson.ToMap(data), options));
    public IWriteBatch UpdateData(IDocumentReference document, Dictionary<object, object> data) => Add(FsJson.Update(document.Path, FsJson.ToMap(data)));
    public IWriteBatch UpdateData(IDocumentReference document, params (string, object)[] data) => Add(FsJson.Update(document.Path, data.ToDictionary(t => t.Item1, t => (object?)t.Item2)));
    public IWriteBatch DeleteDocument(IDocumentReference document) => Add(FsJson.Delete(document.Path));
    public Task CommitAsync() => _fs.CommitAsync(_writes);
    public void CommitLocal() => throw new NotSupportedException();
}
