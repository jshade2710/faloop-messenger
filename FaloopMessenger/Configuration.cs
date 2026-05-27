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

    // Per-rank toggles. S covers SS. A-ranks default off so the firehose
    // stays narrow on first run; users opt in when they want relic-book /
    // hunt-log notifications. B-ranks removed in v0.4.6 — most users never
    // turned them on, and the per-rank scope split below would have meant
    // three parallel scope blocks for vanishingly little benefit.
    public bool ShowSRanks { get; set; } = true;
    public bool ShowARanks { get; set; } = false;

    // Schema-only fields kept so old configs deserialise without losing
    // saved values; consumed by LoadSecrets() then nulled. Treated as
    // 'unset' when null in fresh configs.
    public bool? OnlySRanks { get; set; } = null;   // pre-v0.4.5
    public bool? ShowBRanks { get; set; } = null;   // pre-v0.4.6

    // ── Per-rank scope ────────────────────────────────────────────────
    //
    // S and A ranks each have an INDEPENDENT scope so you can (e.g.) track
    // S/SS across Aether AND get A-rank pings only from your home world.
    // Each scope is Region → DataCenter → Worlds[]. Region is UI-only
    // (derived from DC name); only DC + world whitelist persist.
    //
    // Empty/missing/"All" DC = no DC filter (the global firehose, scoped
    // by the worldlist if WorldFilterEnabled).
    public string    SDataCenter        { get; set; } = "Aether";
    public bool      SWorldFilterEnabled { get; set; } = false;
    public List<int> SWorldWhitelist     { get; set; } = new();

    public string    ADataCenter        { get; set; } = "Aether";
    public bool      AWorldFilterEnabled { get; set; } = false;
    public List<int> AWorldWhitelist     { get; set; } = new();

    // Pre-v0.4.6 had a single shared DC/world filter. Migrated to SDataCenter
    // / SWorldFilterEnabled / SWorldWhitelist in LoadSecrets(). A-rank
    // scope defaults to the same values so users who flip A on get the same
    // scope they had for S (changeable independently after).
    public string DataCenter { get; set; } = ""; // legacy, migrated then cleared

    // Pre-v0.4.6 single shared world filter (now split into S/A variants
    // above). Kept on the schema so old configs load — values copied into
    // SWorldFilterEnabled / SWorldWhitelist in LoadSecrets() and then
    // cleared. World IDs are int (not uint) so the settings UI can share
    // one picker helper for worlds and expansions; JSON shape unchanged.
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

    // Which spawn-list window auto-open should target: 0=Mini, 1=Compact,
    // 2=Main. Updated whenever a tracker window is observed open in Draw,
    // so it survives X-button closes AND plugin reloads — the user's last
    // preferred tracker is what auto-opens on the next spawn.
    public int LastTracker { get; set; } = 0;

    // Per-window persisted open state. Restored on plugin load so a user
    // who had the Compact tracker up gets it back immediately after a
    // plugin update — without having to wait for the next spawn for
    // ActiveTracker() to re-open it. Synced from the live windows on every
    // OnUpdate tick (cheap — just three bool reads and an equality check).
    public bool MainWindowOpen    { get; set; } = false;
    public bool MiniWindowOpen    { get; set; } = false;
    public bool CompactWindowOpen { get; set; } = false;

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

        // Pre-v0.4.5 had a single OnlySRanks bool. Migrate to the per-rank
        // pair if a saved value is still present. true → S only; false →
        // S + A (B was rolled in by the old "off" semantic but is now
        // dropped per v0.4.6 — see ShowBRanks below).
        var migrated = false;
        if (OnlySRanks.HasValue)
        {
            ShowSRanks = true;
            ShowARanks = !OnlySRanks.Value;
            OnlySRanks = null;
            migrated = true;
        }

        // Pre-v0.4.6 had a separate ShowBRanks toggle. We dropped B-rank
        // tracking entirely — clear any saved value so it can't resurrect
        // when round-tripped.
        if (ShowBRanks.HasValue)
        {
            ShowBRanks = null;
            migrated = true;
        }

        // Pre-v0.4.6 had a single DataCenter + WorldFilterEnabled +
        // WorldWhitelist set shared between all ranks. Now S and A each
        // own their own scope. Copy the old shared values into the S
        // scope (default DC for new installs is already "Aether" — the
        // condition below avoids overwriting an existing user's setting
        // with an empty migration). A scope inherits the same values so
        // turning A on doesn't suddenly drop spawns from outside scope.
        if (!string.IsNullOrEmpty(DataCenter))
        {
            SDataCenter = DataCenter;
            ADataCenter = DataCenter;
            DataCenter  = string.Empty;
            migrated = true;
        }
        if (WorldFilterEnabled || (WorldWhitelist?.Count ?? 0) > 0)
        {
            var src = WorldWhitelist ?? new List<int>();
            SWorldFilterEnabled = WorldFilterEnabled;
            SWorldWhitelist     = new List<int>(src);
            AWorldFilterEnabled = WorldFilterEnabled;
            AWorldWhitelist     = new List<int>(src);
            WorldFilterEnabled  = false;
            WorldWhitelist      = new List<int>();
            migrated = true;
        }

        // A migration write is needed if we recovered a legacy password that
        // isn't yet sealed, OR any filter migration occurred.
        return migrated || (hadLegacy && !string.IsNullOrEmpty(Password));
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
