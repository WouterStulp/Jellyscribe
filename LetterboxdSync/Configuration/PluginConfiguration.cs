using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Xml.Serialization;
using LetterboxdSync.Security;
using MediaBrowser.Model.Plugins;

namespace LetterboxdSync.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public List<Account> Accounts { get; set; } = new List<Account>();

    /// <summary>
    /// Kill switch for <see cref="SidebarScriptStartupFilter"/>, the request-time injection that
    /// adds the sidebar link without the File Transformation plugin. No dashboard control: set it
    /// in the plugin's XML config if the injection ever conflicts with another plugin.
    /// </summary>
    public bool DisableSidebarScriptMiddleware { get; set; }

    /// <summary>
    /// Serializd (TV) account links, one or more per Jellyfin user. Independent of
    /// <see cref="Accounts"/> (Letterboxd/film); a user can link either, both, or neither.
    /// </summary>
    public List<SerializdAccount> SerializdAccounts { get; set; } = new List<SerializdAccount>();

    /// <summary>
    /// Base URL of the Seerr instance, e.g. "http://192.168.1.122:5055" or "https://requests.example.com".
    /// Trailing slash is stripped at use time.
    /// </summary>
    public string? JellyseerrUrl { get; set; }

    /// <summary>
    /// Seerr API key (Settings → General → API Key in Seerr).
    /// </summary>
    [XmlIgnore]
    [JsonIgnore]
    public string? JellyseerrApiKey { get; set; }

    /// <summary>Write-only JSON form of <see cref="JellyseerrApiKey"/>. See <see cref="Account.LetterboxdPasswordInput"/>.</summary>
    [XmlIgnore]
    [JsonPropertyName("JellyseerrApiKey")]
    public string? JellyseerrApiKeyInput
    {
        internal get => JellyseerrApiKey;
        set => JellyseerrApiKey = value;
    }

    [XmlIgnore]
    public bool HasJellyseerrApiKey => !string.IsNullOrEmpty(JellyseerrApiKey);

    /// <summary>Encrypted on-disk form of <see cref="JellyseerrApiKey"/>. See <see cref="Configuration.Account.LetterboxdPasswordProtected"/> for why this is JsonIgnore'd.</summary>
    [XmlElement("JellyseerrApiKey")]
    [JsonIgnore]
    public string? JellyseerrApiKeyProtected
    {
        get => SecretProtector.Protect(JellyseerrApiKey);
        set => JellyseerrApiKey = SecretProtector.Unprotect(value);
    }

    /// <summary>
    /// Approve the requests this plugin creates in Seerr, so they actually reach Radarr/Sonarr.
    /// <para>
    /// Seerr decides auto-approval from the permissions of the user a request is attributed to,
    /// not from the API key used to create it. A request attributed to a Seerr user without
    /// "Auto-Approve" is therefore created as PENDING and never handed to Radarr, even though the
    /// API key belongs to an admin. That is issue #110: requests show up in Seerr but nothing
    /// downloads. With this on, the plugin follows a PENDING request with
    /// POST /api/v1/request/{id}/approve using the admin API key, but only when that Seerr user
    /// holds Admin, Manage Requests, Auto-Approve, or the media type's Auto-Approve permission.
    /// Everyone else's requests stay in Seerr's approval queue, as Seerr itself intends.
    /// </para>
    /// <para>
    /// Defaults to true: it never approves beyond what the user's Seerr permissions allow.
    /// Turn it off to keep every plugin-created request in Seerr's manual approval queue.
    /// </para>
    /// </summary>
    public bool AutoApproveJellyseerrRequests { get; set; } = true;

    /// <summary>
    /// Full ntfy topic URL (e.g. "https://ntfy.sh/mytopic") that receives "sync needs attention"
    /// alerts. Empty disables notifications.
    /// </summary>
    public string? NtfyUrl { get; set; }

    /// <summary>Optional ntfy access token, sent as a bearer token.</summary>
    [XmlIgnore]
    [JsonIgnore]
    public string? NtfyToken { get; set; }

    /// <summary>Write-only JSON form of <see cref="NtfyToken"/>. See <see cref="Account.LetterboxdPasswordInput"/>.</summary>
    [XmlIgnore]
    [JsonPropertyName("NtfyToken")]
    public string? NtfyTokenInput
    {
        internal get => NtfyToken;
        set => NtfyToken = value;
    }

    [XmlIgnore]
    public bool HasNtfyToken => !string.IsNullOrEmpty(NtfyToken);

    /// <summary>Write-only: true on a config POST drops the stored token, which an empty value would keep.</summary>
    [XmlIgnore]
    public bool ClearNtfyToken { internal get; set; }

    /// <summary>Encrypted on-disk form of <see cref="NtfyToken"/>, same scheme as <see cref="JellyseerrApiKeyProtected"/>.</summary>
    [XmlElement("NtfyToken")]
    [JsonIgnore]
    public string? NtfyTokenProtected
    {
        get => SecretProtector.Protect(NtfyToken);
        set => NtfyToken = SecretProtector.Unprotect(value);
    }
}
