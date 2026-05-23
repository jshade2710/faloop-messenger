using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using FaloopMessenger.Windows;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace FaloopMessenger;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager        CommandManager  { get; private set; } = null!;
    [PluginService] internal static IClientState           ClientState     { get; private set; } = null!;
    [PluginService] internal static IDataManager           DataManager     { get; private set; } = null!;
    [PluginService] internal static IChatGui               ChatGui         { get; private set; } = null!;
    [PluginService] internal static IGameGui               GameGui         { get; private set; } = null!;
    [PluginService] internal static IPluginLog             Log             { get; private set; } = null!;
    [PluginService] internal static ITextureProvider       TextureProvider { get; private set; } = null!;
    [PluginService] internal static IFramework             Framework       { get; private set; } = null!;
    [PluginService] internal static ICondition             Condition       { get; private set; } = null!;

    private const string CommandName        = "/faloop";
    private const string MiniCommandName    = "/faloopmini";
    private const string CompactCommandName = "/faloopcompact";

    public Configuration      Configuration { get; init; }
    public FaloopSocketClient Client        { get; init; }

    // Static mirror so the static card renderer can read settings without a
    // Plugin reference (consistent with the static services above).
    internal static Configuration Config { get; private set; } = null!;

    // Game fonts for nicer card typography. Static so windows/widgets can use
    // them without juggling a Plugin reference.
    internal static IFontHandle FontWorld  { get; private set; } = null!;   // ~28px AXIS (card title)
    internal static IFontHandle FontTitle  { get; private set; } = null!;   // ~22px AXIS
    internal static IFontHandle FontMedium { get; private set; } = null!;   // ~16px AXIS

    public readonly WindowSystem WindowSystem = new("FaloopMessenger");
    internal MainWindow      MainWindow    { get; init; }
    internal SpawnListWindow MiniWindow    { get; init; }
    internal SpawnListWindow CompactWindow { get; init; }
    private  ConfigWindow    ConfigWindow  { get; init; }

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Config        = Configuration;

        // Decrypt the password blob and one-time-migrate any pre-0.2 plaintext
        // password into the encrypted-at-rest form.
        if (Configuration.LoadSecrets())
        {
            Configuration.Save();
            Log.Information("[Faloop] Migrated plaintext password to encrypted storage.");
        }

        // Migrate stale URLs from earlier plugin versions. Faloop's Socket.IO server
        // lives at /comms/socket.io, not /socket.io.
        if (!Configuration.SocketUrl.Contains("/comms/socket.io", System.StringComparison.OrdinalIgnoreCase))
        {
            Configuration.SocketUrl = new Configuration().SocketUrl;
            Configuration.Save();
        }

        Client = new FaloopSocketClient(Configuration);
        Client.OnNewSpawn += HandleNewSpawn;
        Client.OnUpdate   += HandleSpawnsChanged;

        // Build game-font handles. NewGameFontHandle is non-blocking — the first
        // few frames may render in the default font until the atlas is rebuilt.
        FontWorld  = PluginInterface.UiBuilder.FontAtlas.NewGameFontHandle(new GameFontStyle(GameFontFamily.Axis, 28f));
        FontTitle  = PluginInterface.UiBuilder.FontAtlas.NewGameFontHandle(new GameFontStyle(GameFontFamily.Axis, 22f));
        FontMedium = PluginInterface.UiBuilder.FontAtlas.NewGameFontHandle(new GameFontStyle(GameFontFamily.Axis, 16f));

        MainWindow    = new MainWindow(this);
        MiniWindow    = new SpawnListWindow(this,
            "Faloop · Mini##faloopmini",
            compact: false,
            defaultSize: new System.Numerics.Vector2(620, 170),
            minSize:     new System.Numerics.Vector2(560, 160));
        CompactWindow = new SpawnListWindow(this,
            "Faloop · Compact##faloopcompact",
            compact: true,
            defaultSize: new System.Numerics.Vector2(560, 200),
            minSize:     new System.Numerics.Vector2(500, 100));
        ConfigWindow  = new ConfigWindow(this);

        WindowSystem.AddWindow(MainWindow);
        WindowSystem.AddWindow(MiniWindow);
        WindowSystem.AddWindow(CompactWindow);
        WindowSystem.AddWindow(ConfigWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the Faloop S-rank tracker (/faloop)"
        });
        CommandManager.AddHandler(MiniCommandName, new CommandInfo(OnMiniCommand)
        {
            HelpMessage = "Open the slim S-rank tracker (/faloopmini)"
        });
        CommandManager.AddHandler(CompactCommandName, new CommandInfo(OnCompactCommand)
        {
            HelpMessage = "Open the compact S-rank tracker (/faloopcompact)"
        });

        PluginInterface.UiBuilder.Draw         += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi   += ToggleMainUi;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;

        Client.Connect();
        Log.Information("[Faloop] Messenger loaded.");

        // Confirm the embedded data resource loaded with the expected
        // magnitudes (cheap guard against a broken JSON edit).
        try { Log.Information($"[Faloop] Data loaded: {FaloopData.IntegritySummary()}"); }
        catch (System.Exception ex) { Log.Error(ex, "[Faloop] Embedded data failed to load"); }

        // One-shot: audit which Faloop zones can resolve an aetheryte. Findings
        // go to the Dalamud log so we know which territories need overrides.
        try { Windows.TeleportRoutine.AuditAetherytes(); }
        catch (System.Exception ex) { Log.Warning($"[Faloop] Aetheryte audit failed: {ex.Message}"); }
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw         -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi   -= ToggleMainUi;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;

        WindowSystem.RemoveAllWindows();
        MainWindow.Dispose();
        MiniWindow.Dispose();
        CompactWindow.Dispose();
        ConfigWindow.Dispose();

        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler(MiniCommandName);
        CommandManager.RemoveHandler(CompactCommandName);
        Client.OnNewSpawn -= HandleNewSpawn;
        Client.OnUpdate   -= HandleSpawnsChanged;
        Client.Dispose();

        FontWorld.Dispose();
        FontTitle.Dispose();
        FontMedium.Dispose();
    }

    // True when the spawn windows should be suppressed this frame because the
    // user enabled "hide in instances" and is currently bound by a duty
    // (dungeon / trial / raid / deep dungeon / variant dungeon). Combat in the
    // open world (e.g. fighting the S-rank itself) does NOT hide the tracker.
    public static bool HiddenInInstance(Configuration cfg) =>
        cfg.HideInInstance && (
            Condition[ConditionFlag.BoundByDuty]   ||
            Condition[ConditionFlag.BoundByDuty56] ||
            Condition[ConditionFlag.BoundByDuty95] ||
            Condition[ConditionFlag.InDeepDungeon]);

    // Which tracker window the user most recently opened. The auto open/close
    // on spawn targets THIS one — so if you live in /faloopcompact, the
    // compact window is what pops and hides, not always the mini.
    private enum Tracker { Main, Mini, Compact }
    private Tracker _lastTracker = Tracker.Mini;

    private Dalamud.Interface.Windowing.Window ActiveTracker() => _lastTracker switch
    {
        Tracker.Main    => MainWindow,
        Tracker.Compact => CompactWindow,
        _               => MiniWindow,
    };

    private void OnCommand(string command, string args)
    {
        MainWindow.Toggle();
        if (MainWindow.IsOpen) _lastTracker = Tracker.Main;
    }

    private void OnMiniCommand(string command, string args)
    {
        MiniWindow.Toggle();
        if (MiniWindow.IsOpen) _lastTracker = Tracker.Mini;
    }

    private void OnCompactCommand(string command, string args)
    {
        CompactWindow.Toggle();
        if (CompactWindow.IsOpen) _lastTracker = Tracker.Compact;
    }

    public void ToggleMainUi()
    {
        MainWindow.Toggle();
        if (MainWindow.IsOpen) _lastTracker = Tracker.Main;
    }

    public void ToggleConfigUi()  => ConfigWindow.Toggle();

    // Tracks the previous live-S-rank count so we only auto-close on the
    // ">0 → 0" transition, not every redraw.
    private int _lastLiveSCount;

    // Auto-notify when a new spawn arrives from Faloop.
    //
    // IMPORTANT: this fires from the WebSocket background thread. ChatGui,
    // game native calls (PlayChatSound) and ImGui window state are NOT
    // thread-safe — touching them off the framework thread can corrupt game
    // memory and crash FFXIV. Everything here is marshaled onto the framework
    // thread via RunOnFrameworkThread.
    private void HandleNewSpawn(SpawnInfo spawn)
    {
        Framework.RunOnFrameworkThread(() =>
        {
            try
            {
                if (Configuration.AutoEchoOnSpawn)
                    PrintSpawnEcho(spawn);

                if (Configuration.AutoSoundOnSpawn)
                    PlayChatSound(Configuration.SoundEffect);

                if (Configuration.AutoOpenMiniOnSpawn && spawn.Rank == HuntRank.S)
                    ActiveTracker().IsOpen = true;
            }
            catch (System.Exception ex)
            {
                Log.Warning($"[Faloop] HandleNewSpawn failed: {ex.Message}");
            }
        });
    }

    // Fires after every spawn list change (add, mark-dead, remove). Auto-closes
    // the mini window once the last live S-rank is gone. Also fires off the
    // background thread — marshaled like HandleNewSpawn.
    private void HandleSpawnsChanged()
    {
        Framework.RunOnFrameworkThread(() =>
        {
            try
            {
                var live = 0;
                foreach (var s in Client.GetSnapshot())
                    if (!s.IsDead && s.Rank == HuntRank.S) live++;

                if (Configuration.AutoCloseMiniWhenIdle && _lastLiveSCount > 0 && live == 0)
                    ActiveTracker().IsOpen = false;

                _lastLiveSCount = live;
            }
            catch (System.Exception ex)
            {
                Log.Warning($"[Faloop] HandleSpawnsChanged failed: {ex.Message}");
            }
        });
    }

    // Print the spawn to the local chat log. Every reported point becomes its
    // own independently-clickable map flag on a single line — each
    // MapLinkPayload is closed with a LinkTerminator so clicking flag #3
    // plants exactly that point (without the terminator the whole rest of the
    // line would belong to the first link). SS marks show "SS".
    public static void PrintSpawnEcho(SpawnInfo spawn, string? extraPrefix = null)
    {
        try
        {
            var rank   = spawn.IsSS ? "SS" : spawn.Rank.ToString();
            var inst   = spawn.ZoneInstance > 0 ? $" i{spawn.ZoneInstance}" : string.Empty;
            var prefix = $"{extraPrefix}[Hunt {rank}] {spawn.MobName} on {spawn.World}{inst}";

            var sb        = new SeStringBuilder().AddText(prefix);
            var hasLink   = spawn.TerritoryId > 0 && spawn.MapId > 0;
            var hasCoords = spawn.X > 0 && spawn.Y > 0;

            if (hasLink && spawn.Points.Count > 0)
            {
                var multi = spawn.Points.Count > 1;
                for (var i = 0; i < spawn.Points.Count; i++)
                {
                    var p = spawn.Points[i];
                    sb.AddText("  ");
                    sb.Add(new MapLinkPayload(spawn.TerritoryId, spawn.MapId, p.MapX, p.MapY));
                    sb.AddText(multi
                        ? $"#{i + 1} ({p.MapX:F1}, {p.MapY:F1})"
                        : $"({p.MapX:F1}, {p.MapY:F1})");
                    sb.Add(RawPayload.LinkTerminator);
                }
            }
            else if (hasLink && hasCoords)
            {
                sb.AddText("  ");
                sb.Add(new MapLinkPayload(spawn.TerritoryId, spawn.MapId, spawn.X, spawn.Y));
                sb.AddText($"({spawn.X:F1}, {spawn.Y:F1})");
                sb.Add(RawPayload.LinkTerminator);
            }
            else if (hasCoords)
            {
                sb.AddText($"  ({spawn.X:F1}, {spawn.Y:F1})");
            }
            // else: location not yet known — print mob/world only rather than
            // a misleading "(0.0, 0.0)" / a clickable flag at the map origin.

            ChatGui.Print(sb.Build());
        }
        catch (System.Exception ex)
        {
            Log.Warning($"[Faloop] Echo print failed: {ex.Message}");
        }
    }

    // Play one of FFXIV's <se.1>..<se.16> chat sound effects.
    public static void PlayChatSound(uint id)
    {
        if (id < 1 || id > 16) return;
        try
        {
            UIGlobals.PlayChatSoundEffect(id);
        }
        catch (System.Exception ex)
        {
            Log.Warning($"[Faloop] Sound play failed: {ex.Message}");
        }
    }
}
