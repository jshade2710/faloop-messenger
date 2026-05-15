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

    // Game fonts for nicer card typography. Static so windows/widgets can use
    // them without juggling a Plugin reference.
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

        FontTitle.Dispose();
        FontMedium.Dispose();
    }

    // True when the spawn windows should be suppressed this frame because the
    // user enabled "hide during combat" and is currently in combat.
    public static bool HiddenForCombat(Configuration cfg) =>
        cfg.HideDuringCombat && Condition[ConditionFlag.InCombat];

    private void OnCommand(string command, string args)        => MainWindow.Toggle();
    private void OnMiniCommand(string command, string args)    => MiniWindow.Toggle();
    private void OnCompactCommand(string command, string args) => CompactWindow.Toggle();
    public void ToggleMainUi()    => MainWindow.Toggle();
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
                    MiniWindow.IsOpen = true;
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
                    MiniWindow.IsOpen = false;

                _lastLiveSCount = live;
            }
            catch (System.Exception ex)
            {
                Log.Warning($"[Faloop] HandleSpawnsChanged failed: {ex.Message}");
            }
        });
    }

    // Print the spawn to the local chat log with a clickable map link.
    public static void PrintSpawnEcho(SpawnInfo spawn, string? extraPrefix = null)
    {
        try
        {
            var prefix = $"{extraPrefix}[Hunt {spawn.Rank}] {spawn.MobName} on {spawn.World} ";
            var coords = $" ({spawn.X:F1}, {spawn.Y:F1})";

            var builder = new SeStringBuilder().AddText(prefix);
            if (spawn.TerritoryId > 0 && spawn.MapId > 0)
                builder.Add(new MapLinkPayload(spawn.TerritoryId, spawn.MapId, spawn.X, spawn.Y));
            builder.AddText(coords);

            ChatGui.Print(builder.Build());
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
