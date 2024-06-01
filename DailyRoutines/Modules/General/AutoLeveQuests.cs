using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using ClickLib;
using DailyRoutines.Helpers;
using DailyRoutines.Infos;
using DailyRoutines.Managers;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Hooking;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Utility.Signatures;
using ECommons.Automation;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Application.Network.WorkDefinitions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using ImGuiNET;
using Lumina.Excel.GeneratedSheets;

namespace DailyRoutines.Modules;

[PrecedingModule([typeof(AutoRequestItemSubmit), typeof(AutoTalkSkip), typeof(AutoQuestComplete)])]
[ModuleDescription("AutoLeveQuestsTitle", "AutoLeveQuestsDescription", ModuleCategories.一般)]
public unsafe class AutoLeveQuests : DailyModuleBase
{
    private static AtkUnitBase* SelectString     => (AtkUnitBase*)Service.Gui.GetAddonByName("SelectString");
    private static AtkUnitBase* SelectIconString => (AtkUnitBase*)Service.Gui.GetAddonByName("SelectIconString");
    private static AtkUnitBase* GuildLeve        => (AtkUnitBase*)Service.Gui.GetAddonByName("GuildLeve");

    private delegate void GameObjectRotateDelegate(GameObject* obj, float value);
    [Signature("E8 ?? ?? ?? ?? 83 FE 4F", DetourName = nameof(GameObjectRotateDetour))]
    private static Hook<GameObjectRotateDelegate>? GameObjectRotateHook;

    [Signature("88 05 ?? ?? ?? ?? 0F B7 41 06", ScanType = ScanType.StaticAddress)]
    private static byte LeveAllowances;

    private static Dictionary<uint, Leve> LeveQuests = [];

    private static Leve? SelectedLeve;
    private static GameObject* LeveMete;
    private static GameObject* LeveReceiver;
    private static string SearchString = string.Empty;

    private static byte LeveAllowancesDisplay;
    private static float RandomRotation;

    public override void Init()
    {
        Service.Hook.InitializeFromAttributes(this);
        Service.ClientState.TerritoryChanged += OnZoneChanged;
        TaskManager ??= new TaskManager { AbortOnTimeout = true, TimeLimitMS = 30000, ShowDebug = false };

        RandomRotation = new Random().Next(0, 360);
    }

    public override void ConfigUI()
    {
        var childSize = new Vector2(420f, 300f);
        ImGuiOm.DisableZoneWithHelp(() =>
        {
            if (ImGui.BeginChild("LeveSelectChild", childSize, true))
            {
                if (LeveQuests.Count == 0)
                {
                    if (ImGuiOm.SelectableTextCentered(Service.Lang.GetText("AutoLeveQuests-GetAreaLeveData")))
                        GetMapLeveQuests();
                }
                else
                {
                    ImGui.SetNextItemWidth(-1f);
                    ImGui.InputTextWithHint("##AutoLeveQuests-SearchLeveQuest", Service.Lang.GetText("PleaseSearch"), ref SearchString, 128);
                }

                ImGui.Separator();

                foreach (var leve in LeveQuests)
                {
                    var leveName = leve.Value.Name.RawString;
                    var jobName = leve.Value.ClassJobCategory.Value.Name.RawString;
                    var leveID = leve.Value.RowId.ToString();

                    if (!string.IsNullOrWhiteSpace(SearchString) &&
                        !leveName.Contains(SearchString) && !jobName.Contains(SearchString) &&
                        !leveID.Contains(SearchString))
                        continue;

                    if (ImGui.Selectable($"{jobName}{leveName[leveName.IndexOf('：')..]} ({leveID})", SelectedLeve == leve.Value))
                        SelectedLeve = leve.Value;
                }

                ImGui.EndChild();
            }
        }, [new(TaskManager.IsBusy, Service.Lang.GetText("AutoLeveQuests-DisableHelp-Delivering"))], Service.Lang.GetText("DisableZoneHeader"));

        ImGui.SameLine();
        ImGui.BeginGroup();
        var childSize1 = ImGuiHelpers.ScaledVector2(350f, 60f);
        ImGuiOm.DisableZoneWithHelp(() =>
        {
            if (ImGui.BeginChild("NPCSelectChild", childSize1, true))
            {
                if (ImGui.Button(Service.Lang.GetText("AutoLeveQuests-ObtainLevemete")))
                    LeveMete = TargetSystem.Instance()->Target;

                ImGui.SameLine();
                ImGui.Text(LeveMete == null
                               ? Service.Lang.GetText("AutoLeveQuests-ObtainHelp")
                               : $"{Marshal.PtrToStringUTF8((nint)LeveMete->Name)} ({LeveMete->DataID})");

                ImGui.Separator();
                ImGuiHelpers.ScaledDummy(1f);

                if (ImGui.Button(Service.Lang.GetText("AutoLeveQuests-ObtainLeveClient")))
                    LeveReceiver = TargetSystem.Instance()->Target;

                ImGui.SameLine();
                ImGui.Text(LeveReceiver == null
                               ? Service.Lang.GetText("AutoLeveQuests-ObtainHelp")
                               : $"{Marshal.PtrToStringUTF8((nint)LeveReceiver->Name)} ({LeveReceiver->DataID})");
                ImGui.EndChild();
            }
        }, [new(TaskManager.IsBusy, Service.Lang.GetText("AutoLeveQuests-DisableHelp-Delivering"))], Service.Lang.GetText("DisableZoneHeader"));

        var childSize2 = ImGuiHelpers.ScaledVector2(350f, 90f);
        if (ImGui.BeginChild("OperationChild", childSize2, true))
        {
            ImGuiOm.DisableZoneWithHelp(() =>
            {
                if (ImGuiOm.ButtonIconWithTextVertical(FontAwesomeIcon.Play, Service.Lang.GetText("Start"),
                                                       ImGuiHelpers.ScaledVector2(167f, 45f)))
                {
                    Service.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "SelectYesno", AlwaysYes);
                    Service.ExecuteCommandManager.Register(OnPreExecuteCommand);
                    GameObjectRotateHook?.Enable();
                    EnqueueARound();
                }
            }, [
                new(LeveMete == null, Service.Lang.GetText("AutoLeveQuests-DisableHelp-NullMete")),
                new(LeveReceiver == null, Service.Lang.GetText("AutoLeveQuests-DisableHelp-NullReceiver")),
                new(LeveMete == LeveReceiver, Service.Lang.GetText("AutoLeveQuests-DisableHelp-SameNPC")),
                new(SelectedLeve == null, Service.Lang.GetText("AutoLeveQuests-DisableHelp-NullLeve")),
                new(TaskManager.IsBusy, Service.Lang.GetText("AutoLeveQuests-DisableHelp-Delivering")),
            ], Service.Lang.GetText("DisableZoneHeader"));

            ImGui.SameLine();
            if (ImGuiOm.ButtonIconWithTextVertical(FontAwesomeIcon.Pause, Service.Lang.GetText("Stop"), ImGuiHelpers.ScaledVector2(167f, 45f)))
            {
                TaskManager.Abort();
                Service.AddonLifecycle.UnregisterListener(AlwaysYes);
                GameObjectRotateHook?.Disable();
                Service.ExecuteCommandManager.Unregister(OnPreExecuteCommand);
            }

            if (EzThrottler.Throttle("AutoLeveQuests-GetLeveAllowancesDisplay", 1000))
                LeveAllowancesDisplay = *(byte*)Service.SigScanner.GetStaticAddressFromSig("88 05 ?? ?? ?? ?? 0F B7 41 06");

            var text = Service.Lang.GetText("AutoLeveQuests-CurrentLeveAllowances", LeveAllowancesDisplay);
            PresetFont.Axis14.Push();
            ImGui.SetWindowFontScale(1.5f);
            ImGuiHelpers.CenterCursorForText(text);
            ImGui.Text(text);
            ImGui.SetWindowFontScale(1f);
            PresetFont.Axis14.Pop();

            ImGui.EndChild();
        }

        ImGui.EndGroup();
    }

    public void EnqueueARound()
    {
        if (SelectedLeve == null || LeveAllowances <= 0)
        {
            TaskManager.Abort();
            Service.AddonLifecycle.UnregisterListener(AlwaysYes);
            GameObjectRotateHook?.Disable();
            Service.ExecuteCommandManager.Unregister(OnPreExecuteCommand);

            NotifyHelper.ToastInfo(Service.Lang.GetText("AutoLeveQuests-CompleteHelp"));
            return;
        }

        // 与理符发行人交互
        TaskManager.Enqueue(InteractWithMete);
        // 点击对应理符类别
        TaskManager.Enqueue(ClickLeveGenre);
        // 接取对应理符任务
        TaskManager.Enqueue(AcceptLeveQuest);
        // 退出理符任务界面
        TaskManager.Enqueue(ExitLeveInterface);
        // 与理符委托人交互
        TaskManager.Enqueue(InteractWithReceiver);
        // 检查是否有多个理符待提交
        TaskManager.Enqueue(CheckIfMultipleLevesToSubmit);
    }

    private static bool? InteractWithMete()
    {
        var continueToSubmit = LuminaCache.GetRow<CraftLeveClient>(1).Text.RawString;
        if (SelectString != null && IsAddonAndNodesReady(SelectString) &&
            TryScanSelectStringText(SelectString, continueToSubmit, out _))
        {
            ClickHelper.SelectString(continueToSubmit);
            return false;
        }

        if (IsOccupied()) return false;
        TargetSystem.Instance()->Target = LeveMete;
        TargetSystem.Instance()->InteractWithObject(LeveMete);
        return true;
    }

    private static bool? ClickLeveGenre()
    {
        if (SelectString == null || !IsAddonAndNodesReady(SelectString)) return false;

        var fieldcraft = LuminaCache.GetRow<Addon>(595).Text.RawString;
        var tradecraft = LuminaCache.GetRow<Addon>(596).Text.RawString;
        return ClickHelper.SelectString(SelectedLeve.ClassJobCategory.Row is 19 ? fieldcraft : tradecraft);
    }

    private static bool? AcceptLeveQuest()
    {
        if (GuildLeve == null || !IsAddonAndNodesReady(GuildLeve)) return false;
        
        AgentHelper.SendEvent(AgentId.LeveQuest, 0, 3, SelectedLeve.RowId);

        return QuestManager.Instance()->LeveQuestsSpan.ToArray().Select(x => (LeveWork?)x)
                                                      .FirstOrDefault(
                                                          x => x.HasValue && x.Value.LeveId == SelectedLeve.RowId) != null;
    }

    private static bool? ExitLeveInterface()
    {
        if (GuildLeve != null && IsAddonAndNodesReady(GuildLeve))
        {
            GuildLeve->FireCloseCallback();
            GuildLeve->Close(true);

            return false;
        }

        var cancel = LuminaCache.GetRow<Addon>(581).Text.RawString;
        if (SelectString != null && IsAddonAndNodesReady(SelectString))
            return ClickHelper.SelectString(cancel);

        return false;
    }

    private static bool? InteractWithReceiver()
    {
        if (IsOccupied()) return false;
        TargetSystem.Instance()->Target = LeveReceiver;
        TargetSystem.Instance()->InteractWithObject(LeveReceiver);
        return true;
    }

    private bool? CheckIfMultipleLevesToSubmit()
    {
        var levesSpan = QuestManager.Instance()->LeveQuestsSpan;
        var qualifiedCount = 0;

        // 判断是否为当前地图的理符
        foreach (var leve in levesSpan)
            if (LeveQuests.ContainsKey(leve.LeveId))
                qualifiedCount++;

        if (qualifiedCount > 1)
        {
            if (SelectIconString != null && IsAddonAndNodesReady(SelectIconString))
            {
                if (!ClickHelper.SelectIconString(SelectedLeve.Name.RawString)) return false;

                EnqueueARound();
                return true;
            }

            return false;
        }

        EnqueueARound();
        return true;
    }

    private static void GetMapLeveQuests()
    {
        var currentZonePlaceNameID = LuminaCache.GetRow<TerritoryType>(Service.ClientState.TerritoryType).PlaceName.Row;

        if (Service.ClientState.TerritoryType == 0 || currentZonePlaceNameID == 0)
        {
            NotifyHelper.NotificationError(Service.Lang.GetText("AutoLeveQuests-GetMapLevesFailed"));
            return;
        }

        LeveQuests = LuminaCache.Get<Leve>()
                                .Where(x => x.PlaceNameIssued.Row == currentZonePlaceNameID &&
                                            !string.IsNullOrEmpty(x.Name.RawString) &&
                                            x.ClassJobCategory.Row is (>= 9 and <= 16) or 19)
                                .ToDictionary(x => x.RowId, x => x);
    }

    private void OnPreExecuteCommand
        (ref bool isPrevented, ref int command, ref int param1, ref int param2, ref int param3, ref int param4)
    {
        if (command != 3 || !TaskManager.IsBusy) return;

        param1 = int.MinValue / 4;
    }

    private static void GameObjectRotateDetour(GameObject* obj, float value)
    {
        if ((nint)obj != Service.ClientState.LocalPlayer.Address)
            GameObjectRotateHook.Original(obj, value);

        GameObjectRotateHook.Original(obj, RandomRotation);
    }

    private static void OnZoneChanged(ushort zone)
    {
        LeveQuests.Clear();
        SelectedLeve = null;
        LeveMete = null;
        LeveReceiver = null;
    }

    private void AlwaysYes(AddonEvent type, AddonArgs args)
    {
        if (!TaskManager.IsBusy) return;
        Click.SendClick("select_yes");
    }

    public override void Uninit()
    {
        Service.ClientState.TerritoryChanged -= OnZoneChanged;
        Service.AddonLifecycle.UnregisterListener(AlwaysYes);
        Service.ExecuteCommandManager.Unregister(OnPreExecuteCommand);
        LeveMete = null;
        LeveReceiver = null;
        LeveQuests.Clear();

        base.Uninit();
    }
}
