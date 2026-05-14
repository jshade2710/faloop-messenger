# Faloop Messenger

A Dalamud plugin for FFXIV that connects to [Faloop](https://faloop.app) — the real-time S-rank hunt tracking site — and surfaces spawn alerts directly in-game. Echoes to chat, plays a sound, shows you exactly where the mob is on a zoomed map preview, and lets you teleport to the closest aetheryte in one click via [Lifestream](https://github.com/NightmareXIV/Lifestream).

Built for the Aether DC by default, but configurable to any data center.

---

## Features

- **Live S-rank tracker** — connects to Faloop's WebSocket feed; receives spawns the same instant Faloop's website does.
- **Card UI** with rank badge, mob name, world chip, age, reporter, route hint, and a zoomed-in map preview centered on the spawn point.
- **Three windows, your choice of density:**
  - `/faloop` — full standard view (status bar + full cards + recently-killed footer)
  - `/faloopmini` — slim auto-popping companion (only live spawns)
  - `/faloopcompact` — high-density 64 px cards (4–5 per window)
- **Auto-echo + sound** on every new S-rank spawn (configurable `<se.1>`–`<se.16>` sound).
- **One-click actions** on each card:
  - **Party** — opens Party Finder with Hunt category and "S Rank" description preset
  - **Ping** — posts the spawn to echo chat with a clickable in-game map link
  - **TP** — uses Lifestream to switch worlds (if needed) then teleport to the closest aetheryte
  - **Click the thumbnail** — drops your in-game flag at the spawn
- **Smart routing** via Faloop's own POI graph — 938 spawn points have precomputed best gateways including cross-zone walks (e.g. *"→ Idyllshire · walk to The Dravanian Hinterlands"*).
- **Server-clock sync** so the "X seconds ago" timer matches Faloop's website exactly.
- **Mini window auto-pops** when an S-rank spawns and auto-closes when the last one dies.

---

## Install

This plugin isn't in the official Dalamud repository — it ships through this
repo as a custom plugin source. Add the URL below to Dalamud and the plugin
will appear in the regular plugin installer.

1. In FFXIV, open **Dalamud → ⚙ Settings → Experimental** tab.
2. Under **Custom Plugin Repositories**, paste:
   ```
   https://raw.githubusercontent.com/jshade2710/faloop-messenger/main/pluginmaster.json
   ```
3. Click **Save & Close**.
4. Open the plugin installer (`/xlplugins`), search for **Faloop Messenger**, and install.
5. Updates ship through the same channel — Dalamud will notify you whenever a new release is tagged.

### Build from source (optional)

If you want to build it yourself:
```
dotnet build FaloopMessenger/FaloopMessenger.csproj -c Release
```
The output is `FaloopMessenger/bin/Release/FaloopMessenger/latest.zip` — drop it
into `%appdata%\XIVLauncher\devPlugins\FaloopMessenger\` (creating the folder)
to side-load.

---

## Setup

1. **Open settings:** Type `/faloop` in chat, then click the **Settings** button.
2. **Enter your Faloop credentials.** Anonymous WebSocket sessions appear to connect but receive no live events, so a free [faloop.app](https://faloop.app) account is recommended.
3. **Pick your data center.** Defaults to Aether.
4. **(Optional)** Configure auto-echo, sound effect, ping channel, and mini-window behavior.
5. Click **Apply & Reconnect**.

You'll see `Connected` in green at the top of the main window when it's working.

---

## Commands

| Command | Effect |
|---|---|
| `/faloop` | Toggle the main tracker window |
| `/faloopmini` | Toggle the slim companion (also auto-opens on new S-rank) |
| `/faloopcompact` | Toggle the high-density compact window |

---

## Required / Optional Plugins

- **[Lifestream](https://github.com/NightmareXIV/Lifestream)** *(optional but recommended)*: needed for the TP button to work. Without it, the TP button does nothing.

---

## Settings Reference

### Faloop Account
- **Username / Password** — your faloop.app credentials. Stored in the Dalamud plugin config as plaintext (same as Lifestream/most other plugins store their tokens). Needed to receive live events.

### Connection
- **Socket URL** *(advanced)* — defaults to `wss://faloop.app/comms/socket.io/?EIO=4&transport=websocket`. Don't change unless Faloop moves their endpoint.

### Filters
- **Data center** — `Aether` (default) or `All`. Spawns from other DCs are dropped before they hit the tracker.
- **Show S-ranks only** — drops A and B rank events.
- **Max entries kept** — cap on the spawn list.

### Auto-notify on spawn
- **Echo new spawns to chat** — prints `[Hunt S] Senmurv on Gilgamesh <map-link> (X.X, Y.Y)` to your chat log on every new spawn.
- **Play sound effect on new spawn** — plays one of FFXIV's `<se.1>`–`<se.16>` chat sounds.

### Mini window (/faloopmini)
- **Auto-open the mini window when an S-rank spawns** — pop the window automatically.
- **Auto-close when no S-ranks are live** — hide the window once the last live S-rank is gone.

### Ping channel
- **Echo / Party / Alliance / Say / Yell / Shout / Free Company** — the channel the Ping button posts to. Only Echo is fully functional right now (the others require a chat-send hook that isn't implemented yet).

---

## Troubleshooting

**Status says Disconnected and there's an error in parentheses.**
Open Settings → re-enter your credentials → Apply & Reconnect. If you see "Login failed", verify your username/password on faloop.app.

**Status is Connected but no spawns appear.**
Wait — S-ranks don't spawn constantly. If no spawn arrives in 30+ minutes, enable Verbose logging in Dalamud settings and search `/xllog` for `[Faloop]`. If you see `event=` lines, events *are* arriving but being filtered out; check your DC and rank settings. If you see *no* `event=` lines, the WebSocket is connected but Faloop isn't pushing — usually means your account/session is anonymous.

**TP button does nothing.**
Confirm Lifestream is installed and enabled. Try typing `/li help` to verify Lifestream's command is registered.

**The age timer is several seconds off from Faloop's site.**
Your PC clock and Faloop's server clock are slightly out of sync (NTP drift). The plugin auto-corrects this within a few seconds of connecting; the time-sync is also refreshed every 30 minutes while connected. Restart the plugin if the offset still feels wrong.

**Spawn shows wrong category (e.g., Behemoth as S-rank).**
Faloop classifies certain FATE bosses (Behemoth, Odin, Ixion, etc.) under the same event type as hunts. The plugin filters FATEs out, but if a misclassification slips through, use the × on the card to dismiss it.

---

## Credits

- **[Faloop](https://faloop.app)** for the hunt data and route graph (mob/world/zone tables and POI routing extracted from their public bundle).
- **[Lifestream](https://github.com/NightmareXIV/Lifestream)** by NightmareXIV for the teleport infrastructure (`/li` command + `Lifestream.IsBusy` IPC).
- **[SlashNephy/Divination](https://github.com/SlashNephy/Divination)** for the FaloopIntegration plugin source which informed the WebSocket handshake and authentication flow.
