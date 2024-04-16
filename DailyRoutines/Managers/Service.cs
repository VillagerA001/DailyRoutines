using System.Linq;
using DailyRoutines.Infos;
using Dalamud.Game;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.ClientState.Objects;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace DailyRoutines.Managers;

public class Service
{
    internal static void Initialize(DalamudPluginInterface pluginInterface)
    {
        Config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Config.Initialize(pluginInterface);
        pluginInterface.Create<Service>();

        InitLanguage();
        CheckConflictKeyValidation();
        AddonManager.Init();
        PresetData = new();
        WinToast.Init();
        Waymarks = new();
        Font = new();
        PayloadText = new();
    }

    private static void InitLanguage()
    {
        var playerLang = Config.SelectedLanguage;
        if (string.IsNullOrEmpty(playerLang))
        {
            playerLang = ClientState.ClientLanguage.ToString();
            if (LanguageManager.LanguageNames.All(x => x.Language != playerLang))
            {
                playerLang = "English";
            }
            Config.SelectedLanguage = playerLang;
            Config.Save();
        }

        Lang = new LanguageManager(playerLang);
    }

    private static void CheckConflictKeyValidation()
    {
        var validKeys = KeyState.GetValidVirtualKeys();
        if (!validKeys.Contains(Config.ConflictKey))
        {
            Config.ConflictKey = VirtualKey.SHIFT;
            Config.Save();
        }
    }

    internal static void Uninit()
    {
        AddonManager.Uninit();
        Waymarks.Uninit();
        Config.Uninit();
        WinToast.Dispose();
        LuminaCache.ClearCache();
    }

    [PluginService] public static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] public static IAddonEventManager AddonEvent { get; private set; } = null!;
    [PluginService] public static IAetheryteList AetheryteList { get; private set; } = null!;
    [PluginService] public static IBuddyList BuddyList { get; private set; } = null!;
    [PluginService] public static IChatGui Chat { get; private set; } = null!;
    [PluginService] public static IClientState ClientState { get; private set; } = null!;
    [PluginService] public static ICommandManager Command { get; set; } = null!;
    [PluginService] public static ICondition Condition { get; private set; } = null!;
    [PluginService] public static IContextMenu ContextMenu { get; private set; } = null!;
    /// <summary>
    /// 如非必要请使用 LuminaCache, 而不是直接使用 IDataManager 来获取数据
    /// Please use LuminaCache instead of obtaining data from IDataManager directly
    /// </summary>
    [PluginService] public static IDataManager Data { get; private set; } = null!;
    [PluginService] public static IDtrBar DtrBar { get; private set; } = null!;
    [PluginService] public static IDutyState DutyState { get; private set; } = null!;
    [PluginService] public static IFateTable Fate { get; private set; } = null!;
    [PluginService] public static IFlyTextGui FlyText { get; private set; } = null!;
    [PluginService] public static IFramework Framework { get; private set; } = null!;
    [PluginService] public static IGameConfig GameConfig { get; private set; } = null!;
    [PluginService] public static IGameGui Gui { get; private set; } = null!;
    [PluginService] public static IGameInteropProvider Hook { get; private set; } = null!;
    [PluginService] public static IGameInventory Inventory { get; private set; } = null!;
    [PluginService] public static IGameLifecycle Lifecycle { get; private set; } = null!;
    [PluginService] public static IGameNetwork Network { get; private set; } = null!;
    [PluginService] public static IGamepadState Gamepad { get; private set; } = null!;
    [PluginService] public static IJobGauges JobGauges { get; private set; } = null!;
    [PluginService] public static IKeyState KeyState { get; private set; } = null!;
    [PluginService] public static ILibcFunction LibcFunction { get; private set; } = null!;
    [PluginService] public static INotificationManager DalamudNotice { get; private set; } = null!;
    [PluginService] public static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] public static IPartyFinderGui PartyFinder { get; private set; } = null!;
    [PluginService] public static IPartyList PartyList { get; private set; } = null!;
    [PluginService] public static IPluginLog Log { get; private set; } = null!;
    [PluginService] public static ITargetManager Target { get; private set; } = null!;
    [PluginService] public static ITextureProvider Texture { get; private set; } = null!;
    [PluginService] public static ITitleScreenMenu TitleScreenMenu { get; private set; } = null!;
    [PluginService] public static IToastGui Toast { get; private set; } = null!;

    public static SigScanner SigScanner { get; private set; } = new();
    public static FontManager Font { get; private set; } = null!;
    public static PresetData PresetData { get; set; } = null!;
    public static FieldMarkerManager Waymarks { get; set; } = null!;
    public static LanguageManager Lang { get; set; } = null!;
    public static Configuration Config { get; set; } = null!;
    public static PayloadText PayloadText { get; set; } = null!;
}
