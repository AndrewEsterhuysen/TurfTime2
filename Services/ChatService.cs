using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Plugin.Firebase.Firestore;

namespace TurfTime2.Services;

/// <summary>
/// Team chat. Writes/reads use authenticated Firestore REST as primary
/// (Plugin.Firebase SetDataAsync often "succeeds" with empty documents on device —
/// same class of bug as roster). Snapshot listener is a change signal; empty SDK
/// Data falls back to REST.
/// </summary>
public sealed class ChatService : IChatService
{
    private const string FirebaseProjectId = "turf-timer";
    private static readonly HttpClient RestHttp = new();

    private readonly IFirebaseAuthService _auth;
    private readonly IFirebaseFirestore _db;

    public ChatService(IFirebaseAuthService auth, IFirebaseFirestore db)
    {
        _auth = auth;
        _db = db;
    }

    public async Task<IDisposable?> SubscribeAsync(
        string teamId,
        Action<IReadOnlyList<ChatMessage>> onMessages,
        Action<Exception>? onError = null)
    {
        if (string.IsNullOrWhiteSpace(teamId) || teamId.StartsWith("local_", StringComparison.Ordinal))
            return null;

        var uid = await _auth.EnsureSignedInAsync().ConfigureAwait(false);
        if (uid is null)
        {
            onError?.Invoke(new InvalidOperationException("Not signed in to Firebase"));
            return null;
        }

        // Initial REST load so the UI is correct even if the SDK listener Data is empty.
        try
        {
            var initial = await LoadMessagesViaRestAsync(teamId, uid).ConfigureAwait(false);
            onMessages(initial);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ChatService] Initial REST load: {ex.Message}");
        }

        try
        {
            var query = _db.GetCollection($"teams/{teamId}/messages")
                .OrderBy("timestamp", descending: false)
                .LimitedTo(100);

            return query.AddSnapshotListener<Dictionary<string, object>>(
                snapshot =>
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var list = ParseSdkSnapshot(snapshot, uid);
                            // Empty docs or empty-field docs → REST is source of truth.
                            var docCount = snapshot?.Documents?.Count() ?? 0;
                            var needsRest = list.Count == 0
                                || (docCount > 0 && list.All(m => string.IsNullOrWhiteSpace(m.Text)));

                            if (needsRest)
                            {
                                System.Diagnostics.Debug.WriteLine(
                                    $"[ChatService] Listener empty/partial Data — REST refresh team={teamId}");
                                list = await LoadMessagesViaRestAsync(teamId, uid).ConfigureAwait(false);
                            }

                            onMessages(list);
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine(
                                $"[ChatService] Subscribe callback: {ex.GetType().Name}: {ex.Message}");
                            onError?.Invoke(ex);
                        }
                    });
                },
                ex =>
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[ChatService] Subscribe error: {ex.Message}");
                    onError?.Invoke(ex);
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var list = await LoadMessagesViaRestAsync(teamId, uid).ConfigureAwait(false);
                            onMessages(list);
                        }
                        catch { /* ignore */ }
                    });
                });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ChatService] Subscribe: {ex.Message}");
            onError?.Invoke(ex);
            return null;
        }
    }

    public async Task SendAsync(string teamId, string text, string senderDisplayName)
    {
        if (string.IsNullOrWhiteSpace(teamId) || string.IsNullOrWhiteSpace(text))
            return;

        var uid = await _auth.EnsureSignedInAsync().ConfigureAwait(false);
        if (uid is null)
            throw new InvalidOperationException("Not signed in");

        var body = text.Length > 500 ? text[..500] : text;
        var sender = string.IsNullOrWhiteSpace(senderDisplayName)
            ? "Someone"
            : senderDisplayName.Trim();
        if (sender.Length > 40) sender = sender[..40];

        var now = DateTimeOffset.UtcNow;

        // PRIMARY: REST. SDK SetDataAsync often reports success while writing zero fields
        // (verified on Android: "SDK send OK" then empty docs → UI shows "Teammate").
        try
        {
            await SendViaRestAsync(teamId, uid, body, sender, now).ConfigureAwait(false);
            System.Diagnostics.Debug.WriteLine(
                $"[ChatService] ✅ REST send OK team={teamId} uid={uid[..Math.Min(8, uid.Length)]}…");
            return;
        }
        catch (Exception restEx)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[ChatService] REST send failed, trying SDK: {restEx.GetType().Name}: {restEx.Message}");
        }

        try
        {
            var collection = _db.GetCollection($"teams/{teamId}/messages");
            var doc = collection.CreateDocument();
            var payload = new Dictionary<object, object>
            {
                ["text"] = body,
                ["userId"] = uid,
                ["senderName"] = sender,
                ["timestamp"] = now
            };
            await doc.SetDataAsync(payload).ConfigureAwait(false);
            System.Diagnostics.Debug.WriteLine(
                $"[ChatService] ✅ SDK send OK team={teamId} (fallback)");
        }
        catch (Exception sdkEx)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[ChatService] ❌ SDK send failed: {sdkEx.GetType().Name}: {sdkEx.Message}");
            throw new InvalidOperationException(
                $"Could not send chat message: {sdkEx.Message}", sdkEx);
        }
    }

    private async Task SendViaRestAsync(
        string teamId,
        string uid,
        string text,
        string senderName,
        DateTimeOffset timestamp)
    {
        var idToken = await GetIdTokenAsync().ConfigureAwait(false);
        var url =
            $"https://firestore.googleapis.com/v1/projects/{FirebaseProjectId}/databases/(default)/documents/teams/{Uri.EscapeDataString(teamId)}/messages";

        var ts = timestamp.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
        var fields = new Dictionary<string, object>
        {
            ["text"] = new Dictionary<string, object> { ["stringValue"] = text },
            ["userId"] = new Dictionary<string, object> { ["stringValue"] = uid },
            ["senderName"] = new Dictionary<string, object> { ["stringValue"] = senderName },
            ["timestamp"] = new Dictionary<string, object> { ["timestampValue"] = ts }
        };

        var json = JsonSerializer.Serialize(new { fields });
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", idToken);

        using var resp = await RestHttp.SendAsync(req).ConfigureAwait(false);
        var respBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"REST chat {(int)resp.StatusCode}: {respBody[..Math.Min(240, respBody.Length)]}");
        }

        // Guard against silent empty writes (parity with roster).
        if (!respBody.Contains("\"text\"", StringComparison.Ordinal)
            || !respBody.Contains(text.Length > 40 ? text[..40] : text, StringComparison.Ordinal))
        {
            // Still accept if structure has stringValue for text (special chars may escape differently).
            using var doc = JsonDocument.Parse(respBody);
            if (!doc.RootElement.TryGetProperty("fields", out var f)
                || !f.TryGetProperty("text", out var t)
                || !t.TryGetProperty("stringValue", out _))
            {
                throw new InvalidOperationException("REST chat response missing text field");
            }
        }
    }

    private async Task<List<ChatMessage>> LoadMessagesViaRestAsync(string teamId, string uid)
    {
        var idToken = await GetIdTokenAsync().ConfigureAwait(false);
        // orderBy=timestamp (ascending). pageSize caps recent history.
        var url =
            $"https://firestore.googleapis.com/v1/projects/{FirebaseProjectId}/databases/(default)/documents/teams/{Uri.EscapeDataString(teamId)}/messages?pageSize=100&orderBy=timestamp";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", idToken);

        using var resp = await RestHttp.SendAsync(req).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            return [];
        if (!resp.IsSuccessStatusCode)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[ChatService] REST list {(int)resp.StatusCode}: {body[..Math.Min(160, body.Length)]}");
            return [];
        }

        var list = new List<ChatMessage>();
        using var json = JsonDocument.Parse(body);
        if (!json.RootElement.TryGetProperty("documents", out var documents))
            return list;

        foreach (var doc in documents.EnumerateArray())
        {
            if (!doc.TryGetProperty("fields", out var fields)) continue;
            var text = ReadRestString(fields, "text");
            if (string.IsNullOrWhiteSpace(text)) continue;

            var userId = ReadRestString(fields, "userId");
            var name = doc.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var id = name.Contains('/') ? name[(name.LastIndexOf('/') + 1)..] : name;

            list.Add(new ChatMessage
            {
                Id = id,
                Text = text,
                UserId = userId,
                SenderName = ReadRestString(fields, "senderName"),
                Timestamp = ReadRestTimestamp(fields, "timestamp"),
                IsMine = string.Equals(userId, uid, StringComparison.Ordinal)
            });
        }

        list.Sort((a, b) => Nullable.Compare(a.Timestamp, b.Timestamp));
        System.Diagnostics.Debug.WriteLine(
            $"[ChatService] REST list team={teamId} count={list.Count}");
        return list;
    }

    private static List<ChatMessage> ParseSdkSnapshot(object? snapshot, string uid)
    {
        var list = new List<ChatMessage>();
        if (snapshot is null) return list;

        // Avoid hard dependency on IQuerySnapshot generic variance; use dynamic docs.
        try
        {
            dynamic snap = snapshot;
            var documents = snap.Documents as System.Collections.IEnumerable;
            if (documents is null) return list;

            foreach (var raw in documents)
            {
                if (raw is null) continue;
                dynamic doc = raw;
                IDictionary<string, object>? data = null;
                try { data = doc.Data as IDictionary<string, object>; }
                catch { /* ignore */ }
                if (data is null || data.Count == 0) continue;

                var text = ReadString(data, "text");
                if (string.IsNullOrWhiteSpace(text)) continue;

                var userId = ReadString(data, "userId");
                string id = "";
                try { id = doc.Reference?.Id as string ?? ""; }
                catch { /* ignore */ }

                list.Add(new ChatMessage
                {
                    Id = id,
                    Text = text,
                    UserId = userId,
                    SenderName = ReadString(data, "senderName"),
                    Timestamp = ReadTimestamp(data, "timestamp"),
                    IsMine = string.Equals(userId, uid, StringComparison.Ordinal)
                });
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ChatService] ParseSdkSnapshot: {ex.Message}");
        }

        list.Sort((a, b) => Nullable.Compare(a.Timestamp, b.Timestamp));
        return list;
    }

    public async Task UpdateDisplayNameAsync(string teamId, string displayName)
    {
        var uid = await _auth.EnsureSignedInAsync().ConfigureAwait(false);
        if (uid is null || string.IsNullOrWhiteSpace(teamId) || string.IsNullOrWhiteSpace(displayName))
            return;

        var name = displayName.Trim();
        if (name.Length > 40) name = name[..40];

        try
        {
            await PatchMemberViaRestAsync(teamId, uid, new Dictionary<string, object>
            {
                ["displayName"] = new Dictionary<string, object> { ["stringValue"] = name }
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[ChatService] UpdateDisplayName REST failed, SDK: {ex.Message}");
            try
            {
                var doc = _db.GetDocument($"teams/{teamId}/members/{uid}");
                await doc.SetDataAsync(
                    new Dictionary<object, object> { ["displayName"] = name },
                    SetOptions.Merge()).ConfigureAwait(false);
            }
            catch (Exception sdkEx)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[ChatService] UpdateDisplayName SDK failed: {sdkEx.Message}");
            }
        }
    }

    public async Task<bool> RegisterFcmTokenAsync(string teamId, string fcmToken)
    {
        if (string.IsNullOrWhiteSpace(teamId) || string.IsNullOrWhiteSpace(fcmToken)
            || teamId.StartsWith("local_", StringComparison.Ordinal))
            return false;

        var uid = await _auth.EnsureSignedInAsync().ConfigureAwait(false);
        if (uid is null)
        {
            System.Diagnostics.Debug.WriteLine("[ChatService] RegisterFcmToken: not signed in");
            return false;
        }

        // Avoid FieldValue.ArrayUnion — native HashMap conversion fails on Android:
        // "Couldn't put object of type Plugin.Firebase.Firestore.FieldValue into HashMap"
        try
        {
            var tokens = await LoadFcmTokensViaRestAsync(teamId, uid).ConfigureAwait(false);
            if (!tokens.Contains(fcmToken, StringComparer.Ordinal))
                tokens.Add(fcmToken);

            // Cap stored tokens (old reinstalls can accumulate junk).
            if (tokens.Count > 10)
                tokens = tokens.Skip(tokens.Count - 10).ToList();

            var arrayValues = tokens.Select(t => (object)new Dictionary<string, object>
            {
                ["stringValue"] = t
            }).ToList();

            await PatchMemberViaRestAsync(teamId, uid, new Dictionary<string, object>
            {
                ["fcmTokens"] = new Dictionary<string, object>
                {
                    ["arrayValue"] = new Dictionary<string, object>
                    {
                        ["values"] = arrayValues
                    }
                },
                ["tokenUpdatedAt"] = new Dictionary<string, object>
                {
                    ["stringValue"] = DateTimeOffset.UtcNow.ToString("o")
                }
            }).ConfigureAwait(false);

            System.Diagnostics.Debug.WriteLine(
                $"[ChatService] ✅ FCM token registered via REST uid={uid[..Math.Min(8, uid.Length)]}… " +
                $"team={teamId} tokens={tokens.Count} latest={fcmToken[..Math.Min(16, fcmToken.Length)]}…");
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[ChatService] RegisterFcmToken FAILED team={teamId}: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private async Task<List<string>> LoadFcmTokensViaRestAsync(string teamId, string uid)
    {
        var list = new List<string>();
        try
        {
            var idToken = await GetIdTokenAsync().ConfigureAwait(false);
            var url =
                $"https://firestore.googleapis.com/v1/projects/{FirebaseProjectId}/databases/(default)/documents/teams/{Uri.EscapeDataString(teamId)}/members/{Uri.EscapeDataString(uid)}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", idToken);
            using var resp = await RestHttp.SendAsync(req).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return list;
            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var json = JsonDocument.Parse(body);
            if (!json.RootElement.TryGetProperty("fields", out var fields)) return list;
            if (!fields.TryGetProperty("fcmTokens", out var ft)) return list;
            if (!ft.TryGetProperty("arrayValue", out var av)) return list;
            if (!av.TryGetProperty("values", out var values)) return list;
            foreach (var item in values.EnumerateArray())
            {
                if (item.TryGetProperty("stringValue", out var sv))
                {
                    var s = sv.GetString();
                    if (!string.IsNullOrEmpty(s)) list.Add(s);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ChatService] LoadFcmTokens: {ex.Message}");
        }
        return list;
    }

    public async Task<IReadOnlyDictionary<string, string>> LoadMemberDisplayNamesAsync(string teamId)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(teamId)) return map;

        if (await _auth.EnsureSignedInAsync().ConfigureAwait(false) is null)
            return map;

        try
        {
            var snap = await _db.GetCollection($"teams/{teamId}/members")
                .GetDocumentsAsync<Dictionary<string, object>>()
                .ConfigureAwait(false);

            if (snap?.Documents == null) return map;

            foreach (var doc in snap.Documents)
            {
                var id = doc.Reference?.Id;
                if (string.IsNullOrEmpty(id) || doc.Data is null) continue;
                var name = ReadString(doc.Data, "displayName");
                if (!string.IsNullOrWhiteSpace(name))
                    map[id] = name.Trim();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ChatService] LoadMemberDisplayNames: {ex.Message}");
        }

        return map;
    }

    private async Task PatchMemberViaRestAsync(
        string teamId,
        string uid,
        Dictionary<string, object> fields)
    {
        var idToken = await GetIdTokenAsync().ConfigureAwait(false);
        var fieldPaths = string.Join("&",
            fields.Keys.Select(k => $"updateMask.fieldPaths={Uri.EscapeDataString(k)}"));
        var url =
            $"https://firestore.googleapis.com/v1/projects/{FirebaseProjectId}/databases/(default)/documents/teams/{Uri.EscapeDataString(teamId)}/members/{Uri.EscapeDataString(uid)}?{fieldPaths}";

        var json = JsonSerializer.Serialize(new { fields });
        using var req = new HttpRequestMessage(HttpMethod.Patch, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", idToken);

        using var resp = await RestHttp.SendAsync(req).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"REST member patch {(int)resp.StatusCode}: {body[..Math.Min(200, body.Length)]}");
        }
    }

    private async Task<string> GetIdTokenAsync()
    {
        var idToken = await _auth.GetIdTokenAsync(forceRefresh: false).ConfigureAwait(false);
        if (string.IsNullOrEmpty(idToken))
            idToken = await _auth.GetIdTokenAsync(forceRefresh: true).ConfigureAwait(false);
        if (string.IsNullOrEmpty(idToken))
            throw new InvalidOperationException("No Firebase id token");
        return idToken;
    }

    private static string ReadString(IDictionary<string, object>? d, string key)
    {
        if (d is null) return "";
        if (d.TryGetValue(key, out var v) && v != null)
            return v.ToString() ?? "";
        return "";
    }

    private static DateTimeOffset? ReadTimestamp(IDictionary<string, object>? d, string key)
    {
        if (d is null || !d.TryGetValue(key, out var v) || v is null)
            return null;

        switch (v)
        {
            case DateTimeOffset dto:
                return dto.ToUniversalTime();
            case DateTime dt:
                return new DateTimeOffset(
                    dt.Kind == DateTimeKind.Unspecified
                        ? DateTime.SpecifyKind(dt, DateTimeKind.Utc)
                        : dt.ToUniversalTime());
            case string s when DateTimeOffset.TryParse(s,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var p):
                return p.ToUniversalTime();
        }

        try
        {
            var t = v.GetType();
            foreach (var name in new[] { "ToDateTimeOffset", "ToDateTime", "ToDate" })
            {
                var m = t.GetMethod(name, Type.EmptyTypes);
                if (m is null) continue;
                var result = m.Invoke(v, null);
                switch (result)
                {
                    case DateTimeOffset dto:
                        return dto.ToUniversalTime();
                    case DateTime dt:
                        return new DateTimeOffset(
                            dt.Kind == DateTimeKind.Unspecified
                                ? DateTime.SpecifyKind(dt, DateTimeKind.Utc)
                                : dt.ToUniversalTime());
                }
            }

            var secProp = t.GetProperty("Seconds") ?? t.GetProperty("seconds");
            if (secProp?.GetValue(v) is long secL)
                return DateTimeOffset.FromUnixTimeSeconds(secL);
            if (secProp?.GetValue(v) is int secI)
                return DateTimeOffset.FromUnixTimeSeconds(secI);
        }
        catch { /* ignore */ }

        return null;
    }

    private static string ReadRestString(JsonElement fields, string name)
    {
        if (!fields.TryGetProperty(name, out var f)) return "";
        if (f.TryGetProperty("stringValue", out var sv))
            return sv.GetString() ?? "";
        return "";
    }

    private static DateTimeOffset? ReadRestTimestamp(JsonElement fields, string name)
    {
        if (!fields.TryGetProperty(name, out var f)) return null;
        if (f.TryGetProperty("timestampValue", out var tv)
            && DateTimeOffset.TryParse(tv.GetString(),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var dto))
            return dto.ToUniversalTime();
        return null;
    }
}
