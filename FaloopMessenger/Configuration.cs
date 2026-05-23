using Dalamud.Configuration;
using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FaloopMessenger;

// Only Echo is fully functional — the others would require hooking the
// game's chat-send function and aren't implemented yet. Add back here when
// implemented; the UI reads this list as the source of truth.
public enum PingChannel { Echo }

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    // Faloop account credentials (optional — anonymous sessions appear to
    // connect to the WebSocket but receive no live events).
    public string Username { get; set; } = string.Empty;

    // T-1: the password is NEVER serialized in cleartext. The in-memory value
    // is [JsonIgnore]; only the DPAPI-encrypted blob is persisted. Migration
    // of a pre-0.2 plaintext "Password" key is handled via the extension-data
    // catch-all below.
    [JsonIgnore]
    public string Password { get; set; } = string.Empty;

    // DPAPI (CurrentUser) encrypted password blob — this is what hits disk.
    public string ProtectedPassword { get; set; } = string.Empty;

    // Newtonsoft dumps any unknown JSON keys here. We use it solely to pick up
    // a legacy plaintext "Password" written by pre-0.2 versions, then remove
    // the key so it is never written back.
    [JsonExtensionData]
    private IDictionary<string, JToken> _legacy = new Dictionary<string, JToken>();

    // Cached session ID from the most recent successful refresh — used so we
    // don't need to do a fresh anonymous handshake on every plugin start.
    public string StoredSessionId { get; set; } = string.Empty;

    // Connection — /comms/socket.io is the correct Socket.IO path on faloop.app
    public string SocketUrl { get; set; } = "wss://faloop.app/comms/socket.io/?EIO=4&transport=websocket";

    // Filtering
    public bool   OnlySRanks { get; set; } = true;
    public string DataCenter { get; set; } = "Aether"; // "" / "All" = no filter

    // Per-world filter (subset of the data center). Off by default so existing
    // users keep getting the whole DC. When enabled, only spawns on a world in
    // WorldWhitelist (Lumina World row IDs) notify — everything else is dropped.
    // World row IDs stored as int (not uint) so the settings UI can drive the
    // world and expansion pickers through one shared helper. JSON on disk is
    // identical (plain integers), so existing saved configs load unchanged.
    public bool        WorldFilterEnabled { get; set; } = false;
    public List<int>   WorldWhitelist     { get; set; } = new();

    // Per-expansion filter (e.g. "only Dawntrail"). Off by default. When on,
    // only spawns whose zone belongs to an expansion in ExpansionWhitelist
    // (stored as (int)Expansion) notify. Spawns with an unknown territory are
    // never dropped by this filter.
    public bool       ExpansionFilterEnabled { get; set; } = false;
    public List<int>  ExpansionWhitelist     { get; set; } = new();

    // Display / chat
    public int         MaxEntries     { get; set; } = 50;
    public PingChannel PingChannel    { get; set; } = PingChannel.Echo;
    public bool        HideInInstance { get; set; } = false;

    // UI scale for the spawn cards — multiplies every fixed pixel constant
    // (font sizes, card height, badges, buttons, paddings, gaps, marker
    // sizes), so the card grows/shrinks proportionally. 1.0 = design size.
    // Clamped to 0.8 – 1.5 in the renderer.
    public float UiScale { get; set; } = 1.0f;

    // Hunt-train pull timer: how many real-time minutes after a spawn is
    // reported before it's customary to pull. Shown as a countdown on the
    // card, flipping to "PULL" when elapsed. 0 disables the timer.
    public int PullTimerMinutes { get; set; } = 3;

    // Auto-notify when a spawn report arrives
    public bool AutoEchoOnSpawn  { get; set; } = true;
    public bool AutoSoundOnSpawn { get; set; } = true;
    public uint SoundEffect      { get; set; } = 1;   // FFXIV <se.1>..<se.16>

    // Mini window auto-show / auto-hide
    public bool AutoOpenMiniOnSpawn   { get; set; } = true;
    public bool AutoCloseMiniWhenIdle { get; set; } = true;

    // Decrypt the stored blob into the in-memory Password, and migrate a
    // pre-0.2 plaintext "Password" key if one is present. Call once right
    // after the config is loaded. Returns true if a migration write is needed.
    public bool LoadSecrets()
    {
        Password = Dpapi.Unprotect(ProtectedPassword);

        if (string.IsNullOrEmpty(Password) &&
            _legacy.TryGetValue("Password", out var legacy) &&
            legacy.Type == JTokenType.String)
        {
            Password = legacy.Value<string>() ?? string.Empty;
        }

        // Drop the legacy key unconditionally so it can never be written back
        // as plaintext (JsonExtensionData round-trips otherwise).
        var hadLegacy = _legacy.Remove("Password");

        // A migration write is needed if we recovered a legacy password that
        // isn't yet sealed.
        return hadLegacy && !string.IsNullOrEmpty(Password);
    }

    // Seal the password before handing the object to Dalamud's serializer so
    // plaintext never reaches disk.
    public void Save()
    {
        try { ProtectedPassword = Dpapi.Protect(Password ?? string.Empty); }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[Faloop] Password encryption failed: {ex.Message}");
        }
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
