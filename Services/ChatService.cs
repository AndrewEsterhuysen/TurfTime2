using Plugin.Firebase.Firestore;

namespace TurfTime2.Services;

/// <summary>Plugin.Firebase Firestore chat — replaces WebView Firebase JS.</summary>
public sealed class ChatService : IChatService
{
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

        try
        {
            var query = _db.GetCollection($"teams/{teamId}/messages")
                .OrderBy("timestamp", descending: false)
                .LimitedTo(100);

            return query.AddSnapshotListener<Dictionary<object, object>>(
                snapshot =>
                {
                    try
                    {
                        var list = new List<ChatMessage>();
                        if (snapshot?.Documents == null)
                        {
                            onMessages(list);
                            return;
                        }

                        foreach (var doc in snapshot.Documents)
                        {
                            var data = doc.Data;
                            if (data is null) continue;
                            var userId = ReadString(data, "userId");
                            list.Add(new ChatMessage
                            {
                                Id = doc.Reference?.Id ?? "",
                                Text = ReadString(data, "text"),
                                UserId = userId,
                                SenderName = ReadString(data, "senderName"),
                                Timestamp = ReadTimestamp(data, "timestamp"),
                                IsMine = string.Equals(userId, uid, StringComparison.Ordinal)
                            });
                        }

                        onMessages(list);
                    }
                    catch (Exception ex)
                    {
                        onError?.Invoke(ex);
                    }
                },
                ex => onError?.Invoke(ex));
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

        var payload = new Dictionary<object, object>
        {
            ["text"] = text.Length > 500 ? text[..500] : text,
            ["userId"] = uid,
            ["senderName"] = string.IsNullOrWhiteSpace(senderDisplayName) ? "Someone" : senderDisplayName.Trim(),
            ["timestamp"] = FieldValue.ServerTimestamp()
        };

        await _db.GetCollection($"teams/{teamId}/messages")
            .AddDocumentAsync(payload)
            .ConfigureAwait(false);
    }

    public async Task UpdateDisplayNameAsync(string teamId, string displayName)
    {
        var uid = await _auth.EnsureSignedInAsync().ConfigureAwait(false);
        if (uid is null || string.IsNullOrWhiteSpace(teamId) || string.IsNullOrWhiteSpace(displayName))
            return;

        var name = displayName.Trim();
        if (name.Length > 40) name = name[..40];

        var doc = _db.GetDocument($"teams/{teamId}/members/{uid}");
        await doc.SetDataAsync(
            new Dictionary<object, object>
            {
                ["displayName"] = name,
                ["role"] = "member"
            },
            SetOptions.Merge()).ConfigureAwait(false);
    }

    public async Task<bool> RegisterFcmTokenAsync(string teamId, string fcmToken)
    {
        if (string.IsNullOrWhiteSpace(teamId) || string.IsNullOrWhiteSpace(fcmToken)
            || teamId.StartsWith("local_", StringComparison.Ordinal))
            return false;

        var uid = await _auth.EnsureSignedInAsync().ConfigureAwait(false);
        if (uid is null) return false;

        try
        {
            var doc = _db.GetDocument($"teams/{teamId}/members/{uid}");
            await doc.SetDataAsync(
                new Dictionary<object, object>
                {
                    ["fcmTokens"] = FieldValue.ArrayUnion(fcmToken),
                    ["tokenUpdatedAt"] = DateTimeOffset.UtcNow.ToString("o"),
                    ["role"] = "member"
                },
                SetOptions.Merge()).ConfigureAwait(false);

            System.Diagnostics.Debug.WriteLine(
                $"[ChatService] ✅ FCM token registered for {uid[..Math.Min(8, uid.Length)]}… team={teamId}");
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ChatService] RegisterFcmToken: {ex.Message}");
            return false;
        }
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
                .GetDocumentsAsync<Dictionary<object, object>>()
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

    private static string ReadString(IDictionary<object, object> d, string key)
    {
        if (d.TryGetValue(key, out var v) && v != null)
            return v.ToString() ?? "";
        foreach (var kv in d)
        {
            if (kv.Key?.ToString() == key)
                return kv.Value?.ToString() ?? "";
        }
        return "";
    }

    private static DateTimeOffset? ReadTimestamp(IDictionary<object, object> d, string key)
    {
        object? v = null;
        if (d.TryGetValue(key, out var direct)) v = direct;
        else
        {
            foreach (var kv in d)
            {
                if (kv.Key?.ToString() == key) { v = kv.Value; break; }
            }
        }

        return v switch
        {
            DateTimeOffset dto => dto,
            DateTime dt => new DateTimeOffset(dt.ToUniversalTime()),
            string s when DateTimeOffset.TryParse(s, out var p) => p,
            _ => null
        };
    }
}
