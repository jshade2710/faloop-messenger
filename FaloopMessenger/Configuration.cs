using Dalamud.Configuration;
using System;
using System.Collections.Generic;

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
    public string Password { get; set; } = string.Empty;

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
    public bool        WorldFilterEnabled { get; set; } = false;
    public List<uint>  WorldWhitelist     { get; set; } = new();

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

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
