using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace DynamicIsland.Core;

public class DiscordMember
{
    public string Id = "";
    public string Name = "";
    public string? AvatarUrl;
    public bool Speaking;
    public bool Muted;
    public bool Deafened;
}

public class DiscordVoiceState
{
    public bool InChannel;
    public string ChannelName = "";
    public string GuildName = "";
    public List<DiscordMember> Members = new();
}

/// <summary>Tracks Discord voice-channel state through official local RPC: the current
/// channel, server, members, and active speakers. This requires the `rpc.voice.read`
/// scope and the user's own Discord Developer Portal application credentials.</summary>
public class DiscordVoiceService : IDisposable
{
    private static readonly string TokenPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DynamicIsland", "discord_token.json");

    private readonly SettingsService _settings;
    private readonly HttpClient _http = new();
    private DiscordRpcClient? _rpc;
    private string? _accessToken, _refreshToken;
    private string? _currentChannelId, _currentGuildId;
    private readonly Dictionary<string, DiscordMember> _members = new();
    private readonly HashSet<string> _speakingNow = new();
    private readonly System.Windows.Threading.DispatcherTimer _reconnectTimer =
        new() { Interval = TimeSpan.FromSeconds(12) };
    private bool _connecting;

    public DiscordVoiceState State { get; } = new();
    public event Action? Changed;

    public bool IsAuthenticated { get; private set; }
    public string? AuthenticatedUserName { get; private set; }
    public string? AuthenticatedUserId { get; private set; }
    public event Action? StatusChanged;

    /// <summary>Raised when another member newly joins the voice channel.</summary>
    public event Action<DiscordMember>? MemberJoined;

    public DiscordVoiceService(SettingsService settings)
    {
        _settings = settings;
        LoadTokens();
        _reconnectTimer.Tick += async (_, _) => await TryConnectAsync();
    }

    public async Task InitAsync()
    {
        await TryConnectAsync();
        _reconnectTimer.Start();
    }

    /// <summary>Retries the connection after Client ID and Secret have been configured.</summary>
    public async Task RetryAsync() => await TryConnectAsync();

    /// <summary>Returns the current user's member state, including mic/headphone status.</summary>
    public DiscordMember? SelfMember =>
        AuthenticatedUserId != null ? State.Members.FirstOrDefault(m => m.Id == AuthenticatedUserId) : null;

    private void LoadTokens()
    {
        try
        {
            if (File.Exists(TokenPath))
            {
                var j = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(TokenPath));
                if (j != null)
                {
                    j.TryGetValue("access_token", out _accessToken);
                    j.TryGetValue("refresh_token", out _refreshToken);
                }
            }
        }
        catch { }
    }

    private void SaveTokens()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(TokenPath)!);
            File.WriteAllText(TokenPath,
                JsonSerializer.Serialize(new { access_token = _accessToken, refresh_token = _refreshToken }));
        }
        catch { }
    }

    private async Task TryConnectAsync()
    {
        if (_connecting || IsAuthenticated) return;
        var s = _settings.Current;
        if (string.IsNullOrWhiteSpace(s.DiscordClientId) || string.IsNullOrWhiteSpace(s.DiscordClientSecret)) return;

        _connecting = true;
        try
        {
            _rpc = new DiscordRpcClient();
            _rpc.Dispatch += OnDispatch;
            _rpc.Disconnected += OnDisconnected;

            if (!await _rpc.ConnectAsync(s.DiscordClientId))
            {
                AppLog.Write("Discord istemcisine bağlanılamadı (Discord açık değil mi?).");
                _rpc = null;
                return;
            }

            if (!await AuthenticateAsync())
            {
                AppLog.Write("Kayıtlı token ile giriş başarısız, yeni yetkilendirme isteniyor.");
                if (!await AuthorizeFlowAsync() || !await AuthenticateAsync())
                {
                    AppLog.Write("Yetkilendirme başarısız oldu.");
                    _rpc.Dispose();
                    _rpc = null;
                    return;
                }
            }

            AppLog.Write($"Bağlandı: {AuthenticatedUserName} ({AuthenticatedUserId})");
            await _rpc.RequestAsync("SUBSCRIBE", new { }, "VOICE_CHANNEL_SELECT");

            var sel = await _rpc.RequestAsync("GET_SELECTED_VOICE_CHANNEL");
            if (sel.ValueKind == JsonValueKind.Object && sel.TryGetProperty("id", out _))
                await OnChannelSelectedAsync(sel);
            else
                AppLog.Write("Şu an bir ses kanalında değilsin.");
        }
        catch (Exception ex)
        {
            AppLog.Write("TryConnectAsync hatası: " + ex.Message);
            _rpc?.Dispose();
            _rpc = null;
        }
        finally { _connecting = false; }
    }

    private void OnDisconnected()
    {
        AppLog.Write("Discord bağlantısı koptu.");
        IsAuthenticated = false;
        AuthenticatedUserId = null;
        AuthenticatedUserName = null;
        _rpc = null;
        State.InChannel = false;
        State.Members.Clear();
        Changed?.Invoke();
        StatusChanged?.Invoke();
    }

    /// <summary>First tries the stored access token, then silently refreshes it when it
    /// has expired so the user does not need to authorize the app again.</summary>
    private async Task<bool> AuthenticateAsync()
    {
        if (await TryAuthenticateWithTokenAsync()) return true;

        if (!string.IsNullOrEmpty(_refreshToken))
        {
            AppLog.Write("access_token geçersiz, refresh_token ile yenileniyor.");
            if (await RefreshAccessTokenAsync() && await TryAuthenticateWithTokenAsync())
                return true;
        }
        return false;
    }

    private async Task<bool> TryAuthenticateWithTokenAsync()
    {
        if (string.IsNullOrEmpty(_accessToken) || _rpc == null) return false;
        try
        {
            var res = await _rpc.RequestAsync("AUTHENTICATE", new { access_token = _accessToken });
            if (res.ValueKind != JsonValueKind.Object) return false;

            IsAuthenticated = true;
            if (res.TryGetProperty("user", out var u))
            {
                AuthenticatedUserName = u.TryGetProperty("username", out var un) ? un.GetString() : null;
                AuthenticatedUserId = u.TryGetProperty("id", out var uid) ? uid.GetString() : null;
            }
            StatusChanged?.Invoke();
            return true;
        }
        catch { return false; }
    }

    private async Task<bool> RefreshAccessTokenAsync()
    {
        var s = _settings.Current;
        try
        {
            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = s.DiscordClientId,
                ["client_secret"] = s.DiscordClientSecret,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = _refreshToken!
            });
            var res = await _http.PostAsync("https://discord.com/api/oauth2/token", form);
            if (!res.IsSuccessStatusCode)
            {
                AppLog.Write($"Token yenileme başarısız: HTTP {(int)res.StatusCode}");
                return false;
            }

            var content = await res.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(content);
            _accessToken = doc.RootElement.GetProperty("access_token").GetString();
            _refreshToken = doc.RootElement.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : _refreshToken;
            SaveTokens();
            return !string.IsNullOrEmpty(_accessToken);
        }
        catch (Exception ex)
        {
            AppLog.Write("RefreshAccessTokenAsync hatası: " + ex.Message);
            return false;
        }
    }

    private async Task<bool> AuthorizeFlowAsync()
    {
        var s = _settings.Current;
        JsonElement res;
        try
        {
            // On first use, this opens Discord's authorization prompt for the user.
            res = await _rpc!.RequestAsync("AUTHORIZE", new
            {
                client_id = s.DiscordClientId,
                scopes = new[] { "rpc", "rpc.voice.read" }
            });
        }
        catch (Exception ex) { AppLog.Write("AUTHORIZE isteği hatası: " + ex.Message); return false; }

        if (res.ValueKind != JsonValueKind.Object || !res.TryGetProperty("code", out var codeEl))
        {
            AppLog.Write("AUTHORIZE yanıtında code yok (kullanıcı reddetmiş olabilir).");
            return false;
        }
        var code = codeEl.GetString();
        if (string.IsNullOrEmpty(code)) return false;

        try
        {
            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = s.DiscordClientId,
                ["client_secret"] = s.DiscordClientSecret,
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = "http://127.0.0.1"
            });
            var tokenRes = await _http.PostAsync("https://discord.com/api/oauth2/token", form);
            if (!tokenRes.IsSuccessStatusCode)
            {
                AppLog.Write($"Kod-token değişimi başarısız: HTTP {(int)tokenRes.StatusCode}");
                return false;
            }

            var content = await tokenRes.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(content);
            _accessToken = doc.RootElement.GetProperty("access_token").GetString();
            _refreshToken = doc.RootElement.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
            SaveTokens();
            return !string.IsNullOrEmpty(_accessToken);
        }
        catch (Exception ex) { AppLog.Write("Token değişimi hatası: " + ex.Message); return false; }
    }

    private void OnDispatch(string evt, JsonElement data)
    {
        switch (evt)
        {
            case "VOICE_CHANNEL_SELECT":
                _ = OnChannelSelectedAsync(data);
                break;
            case "VOICE_STATE_CREATE":
                UpsertMember(data);
                RaiseChanged();
                var joinedId = ExtractUserId(data);
                if (joinedId != null && joinedId != AuthenticatedUserId && _members.TryGetValue(joinedId, out var joined))
                    MemberJoined?.Invoke(joined);
                break;
            case "VOICE_STATE_UPDATE":
                UpsertMember(data);
                RaiseChanged();
                break;
            case "VOICE_STATE_DELETE":
                if (data.TryGetProperty("user", out var u) && u.TryGetProperty("id", out var uid))
                {
                    var id = uid.GetString() ?? "";
                    _members.Remove(id);
                    _speakingNow.Remove(id);
                    RaiseChanged();
                }
                break;
            case "SPEAKING_START":
                if (data.TryGetProperty("user_id", out var sid))
                {
                    var id = sid.GetString() ?? "";
                    _speakingNow.Add(id);
                    if (_members.TryGetValue(id, out var m)) m.Speaking = true;
                    RaiseChanged();
                }
                break;
            case "SPEAKING_STOP":
                if (data.TryGetProperty("user_id", out var eid))
                {
                    var id = eid.GetString() ?? "";
                    _speakingNow.Remove(id);
                    if (_members.TryGetValue(id, out var m)) m.Speaking = false;
                    RaiseChanged();
                }
                break;
        }
    }

    private async Task OnChannelSelectedAsync(JsonElement data)
    {
        var channelId = data.TryGetProperty("channel_id", out var cid) && cid.ValueKind == JsonValueKind.String
            ? cid.GetString() : null;
        var guildId = data.TryGetProperty("guild_id", out var gid) && gid.ValueKind == JsonValueKind.String
            ? gid.GetString() : null;

        if (_currentChannelId != null)
        {
            try
            {
                foreach (var evt in new[] { "VOICE_STATE_CREATE", "VOICE_STATE_UPDATE", "VOICE_STATE_DELETE", "SPEAKING_START", "SPEAKING_STOP" })
                    await _rpc!.RequestAsync("UNSUBSCRIBE", new { channel_id = _currentChannelId }, evt);
            }
            catch { }
        }

        _members.Clear();
        _speakingNow.Clear();
        _currentChannelId = channelId;
        _currentGuildId = guildId;

        if (channelId == null)
        {
            AppLog.Write("Ses kanalından çıkıldı.");
            State.InChannel = false;
            State.ChannelName = "";
            State.GuildName = "";
            State.Members = new();
            Changed?.Invoke();
            return;
        }

        try
        {
            var ch = await _rpc!.RequestAsync("GET_CHANNEL", new { channel_id = channelId });
            State.InChannel = true;
            State.ChannelName = ch.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "";

            if (ch.TryGetProperty("voice_states", out var vs) && vs.ValueKind == JsonValueKind.Array)
                foreach (var v in vs.EnumerateArray())
                    UpsertMember(v);

            AppLog.Write($"Kanal seçildi: {State.ChannelName} ({_members.Count} üye)");

            foreach (var evt in new[] { "VOICE_STATE_CREATE", "VOICE_STATE_UPDATE", "VOICE_STATE_DELETE", "SPEAKING_START", "SPEAKING_STOP" })
                await _rpc.RequestAsync("SUBSCRIBE", new { channel_id = channelId }, evt);

            RaiseChanged();

            // Guild name is optional; channel information can still be displayed if this fails.
            if (guildId != null)
            {
                try
                {
                    var guild = await _rpc.RequestAsync("GET_GUILD", new { guild_id = guildId });
                    State.GuildName = guild.TryGetProperty("name", out var gn) ? gn.GetString() ?? "" : "";
                    Changed?.Invoke();
                }
                catch (Exception ex) { AppLog.Write("GET_GUILD hatası: " + ex.Message); }
            }
            else
            {
                State.GuildName = ""; // DM or group voice call.
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("GET_CHANNEL hatası: " + ex.Message);
        }
    }

    private static string? ExtractUserId(JsonElement voiceStateOrUser)
    {
        var userEl = voiceStateOrUser.TryGetProperty("user", out var u) ? u : voiceStateOrUser;
        return userEl.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
    }

    private void UpsertMember(JsonElement voiceStateOrUser)
    {
        // GET_CHANNEL voice_states and the related events share the { user, voice_state, ... } shape.
        var userEl = voiceStateOrUser.TryGetProperty("user", out var u) ? u : voiceStateOrUser;
        if (!userEl.TryGetProperty("id", out var idEl)) return;
        var id = idEl.GetString();
        if (string.IsNullOrEmpty(id)) return;

        if (!_members.TryGetValue(id, out var member))
        {
            member = new DiscordMember { Id = id };
            _members[id] = member;
        }

        if (userEl.TryGetProperty("username", out var un)) member.Name = un.GetString() ?? member.Name;
        var avatar = userEl.TryGetProperty("avatar", out var av) && av.ValueKind == JsonValueKind.String ? av.GetString() : null;
        member.AvatarUrl = avatar != null ? $"https://cdn.discordapp.com/avatars/{id}/{avatar}.png?size=80" : null;

        if (voiceStateOrUser.TryGetProperty("voice_state", out var vstate))
        {
            member.Muted = (vstate.TryGetProperty("mute", out var mu) && mu.GetBoolean())
                        || (vstate.TryGetProperty("self_mute", out var sm) && sm.GetBoolean());
            member.Deafened = (vstate.TryGetProperty("deaf", out var de) && de.GetBoolean())
                        || (vstate.TryGetProperty("self_deaf", out var sd) && sd.GetBoolean());
        }
        member.Speaking = _speakingNow.Contains(id);
    }

    private void RaiseChanged()
    {
        State.Members = _members.Values.OrderByDescending(m => m.Speaking).ThenBy(m => m.Name).ToList();
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _reconnectTimer.Stop();
        _rpc?.Dispose();
        _rpc = null;
        _http.Dispose();
    }
}
