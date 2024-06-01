using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using DailyRoutines.Infos;
using DailyRoutines.Managers;
using Dalamud.Hooking;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Utility.Signatures;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using ImGuiNET;
using CharacterStruct = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;
using GameObjectStruct = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace DailyRoutines.Modules;

[ModuleDescription("CustomizeGameObjectTitle", "CustomizeGameObjectDescription", ModuleCategories.系统)]
public unsafe class CustomizeGameObject : DailyModuleBase
{
    private struct ChibiTarget
    {
        public CustomizeType Type;
        public string Value;
        public float Scale;
    }

    private class CustomizePreset : IComparable<CustomizePreset>, IEquatable<CustomizePreset>
    {
        public string Note { get; set; } = string.Empty;
        public CustomizeType Type { get; set; }
        public string Value { get; set; } = string.Empty;
        public float Scale { get; set; }
        public bool ScaleVFX { get; set; }
        public bool Enabled { get; set; }

        public int CompareTo(CustomizePreset? other)
        {
            if (other == null) return 1;

            var typeComparison = Type.CompareTo(other.Type);
            if (typeComparison != 0) return typeComparison;

            var valueComparison = string.Compare(Value, other.Value, StringComparison.Ordinal);
            if (valueComparison != 0) return valueComparison;

            return 0;
        }

        public override bool Equals(object? obj)
        {
            if (obj is CustomizePreset other) return Equals(other);
            return false;
        }

        public bool Equals(CustomizePreset? other)
        {
            if (other == null) return false;

            return Type == other.Type &&
                   string.Equals(Value, other.Value, StringComparison.Ordinal);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(Type, Value);
        }

        public static bool operator ==(CustomizePreset left, CustomizePreset right)
        {
            return Equals(left, right);
        }

        public static bool operator !=(CustomizePreset left, CustomizePreset right)
        {
            return !Equals(left, right);
        }
    }

    private enum CustomizeType
    {
        Name,
        ModelCharaID,
        ModelSkeletonID,
        DataID,
        ObjectID
    }

    public override string? Author { get; set; } = "HSS";

    private class Config : ModuleConfiguration
    {
        public readonly List<CustomizePreset> CustomizePresets = [];
    }

    private delegate byte IsTargetableDelegate(GameObjectStruct* gameObj);

    [Signature("40 53 48 83 EC 20 F3 0F 10 89 ?? ?? ?? ?? 0F 57 C0 0F 2E C8 48 8B D9 7A 0A",
               DetourName = nameof(IsTargetableDetour))]
    private static Hook<IsTargetableDelegate>? IsTargetableHook;

    private static Config ModuleConfig = null!;

    private static CustomizeType TypeInput = CustomizeType.Name;
    private static string NoteInput = string.Empty;
    private static float ScaleInput = 1f;
    private static string ValueInput = string.Empty;
    private static bool ScaleVFXInput;

    private static CustomizeType TypeEditInput = CustomizeType.Name;
    private static string NoteEditInput = string.Empty;
    private static float ScaleEditInput = 1f;
    private static string ValueEditInput = string.Empty;
    private static bool ScaleVFXEditInput;

    private static readonly Dictionary<nint, (CustomizePreset Preset, float Scale)> CustomizeHistory = [];

    public override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();

        Service.Hook.InitializeFromAttributes(this);
        IsTargetableHook?.Enable();

        Service.ClientState.TerritoryChanged += OnZoneChanged;
    }

    public override void ConfigUI()
    {
        TargetInfoPreviewUI();

        var tableSize = ImGui.GetContentRegionAvail() with { Y = 0 };
        if (ImGui.BeginTable("###ConfigTable", 7, ImGuiTableFlags.Borders, tableSize))
        {
            ImGui.TableSetupColumn("启用", ImGuiTableColumnFlags.WidthFixed, Styles.CheckboxSize.X);
            ImGui.TableSetupColumn("备注", ImGuiTableColumnFlags.None, 40);
            ImGui.TableSetupColumn("模式", ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize("ModelSkeletonID").X);
            ImGui.TableSetupColumn("值", ImGuiTableColumnFlags.None, 30);
            ImGui.TableSetupColumn("缩放比例", ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize("99.99").X);
            ImGui.TableSetupColumn("缩放特效", ImGuiTableColumnFlags.WidthFixed, Styles.CheckboxSize.X);
            ImGui.TableSetupColumn("操作", ImGuiTableColumnFlags.WidthFixed,
                                   (4 * Styles.CheckboxSize.X) + (4 * ImGui.GetStyle().ItemSpacing.X));

            ImGui.TableNextRow(ImGuiTableRowFlags.Headers);

            ImGui.TableNextColumn();
            if (ImGuiOm.SelectableIconCentered("AddNewPreset", FontAwesomeIcon.Plus))
                ImGui.OpenPopup("AddNewPresetPopup");

            if (ImGui.BeginPopup("AddNewPresetPopup"))
            {
                CustomizePresetEditorUI(ref TypeInput, ref ValueInput, ref ScaleInput, ref ScaleVFXInput,
                                        ref NoteInput);

                ImGui.GetWindowDrawList().AddRect(ImGui.GetItemRectMin() - ImGuiHelpers.ScaledVector2(2f),
                                                  ImGui.GetItemRectMax() + ImGuiHelpers.ScaledVector2(2f),
                                                  ImGui.ColorConvertFloat4ToU32(ImGuiColors.DalamudWhite), 2f,
                                                  ImDrawFlags.RoundCornersAll, 1f);

                ImGuiHelpers.ScaledDummy(1f);

                if (ImGuiOm.ButtonSelectable(Service.Lang.GetText("Add")))
                {
                    if (ScaleInput > 0 && !string.IsNullOrWhiteSpace(ValueInput))
                    {
                        ModuleConfig.CustomizePresets.Add(
                            new CustomizePreset
                            {
                                Enabled = true,
                                Scale = ScaleInput,
                                Type = TypeInput,
                                Value = ValueInput,
                                ScaleVFX = ScaleVFXInput,
                                Note = NoteInput
                            });
                        SaveConfig(ModuleConfig);
                        ImGui.CloseCurrentPopup();
                    }
                }

                ImGui.EndPopup();
            }

            ImGui.TableNextColumn();
            ImGuiOm.Text(Service.Lang.GetText("Note"));

            ImGui.TableNextColumn();
            ImGuiOm.Text(Service.Lang.GetText("CustomizeGameObject-CustomizeType"));

            ImGui.TableNextColumn();
            ImGuiOm.Text(Service.Lang.GetText("Value"));

            ImGui.TableNextColumn();
            ImGuiOm.Text(Service.Lang.GetText("CustomizeGameObject-Scale"));

            ImGui.TableNextColumn();
            ImGui.Dummy(new(32f));
            ImGuiOm.TooltipHover(Service.Lang.GetText("CustomizeGameObject-ScaleVFX"));

            ImGui.TableNextColumn();
            ImGuiOm.Text(Service.Lang.GetText("Operation"));

            var array = ModuleConfig.CustomizePresets.ToArray();
            for (var i = 0; i < ModuleConfig.CustomizePresets.Count; i++)
            {
                var preset = array[i];

                ImGui.PushID($"Preset_{i}");

                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                var isEnabled = preset.Enabled;
                if (ImGui.Checkbox("###IsEnabled", ref isEnabled))
                {
                    ModuleConfig.CustomizePresets[i].Enabled = isEnabled;
                    SaveConfig(ModuleConfig);

                    RemovePresetHistory(preset);
                }

                ImGui.TableNextColumn();
                ImGuiOm.Text(preset.Note);

                ImGui.TableNextColumn();
                ImGuiOm.Text(preset.Type.ToString());

                ImGui.TableNextColumn();
                ImGuiOm.Text(preset.Value);

                ImGui.TableNextColumn();
                ImGuiOm.Text(preset.Scale.ToString(CultureInfo.InvariantCulture));

                ImGui.TableNextColumn();
                var isScaleVFX = preset.ScaleVFX;
                if (ImGui.Checkbox("###IsScaleVFX", ref isScaleVFX))
                {
                    ModuleConfig.CustomizePresets[i].ScaleVFX = isScaleVFX;
                    SaveConfig(ModuleConfig);

                    RemovePresetHistory(preset);
                }

                ImGui.TableNextColumn();
                if (ImGuiOm.ButtonIcon($"EditPreset_{i}", FontAwesomeIcon.Edit))
                    ImGui.OpenPopup($"EditNewPresetPopup_{i}");

                if (ImGui.BeginPopup($"EditNewPresetPopup_{i}"))
                {
                    if (ImGui.IsWindowAppearing())
                    {
                        TypeEditInput = preset.Type;
                        NoteEditInput = preset.Note;
                        ScaleEditInput = preset.Scale;
                        ValueEditInput = preset.Value;
                        ScaleVFXEditInput = preset.ScaleVFX;
                    }

                    if (CustomizePresetEditorUI(ref TypeEditInput, ref ValueEditInput, ref ScaleEditInput,
                                                ref ScaleVFXEditInput, ref NoteEditInput))
                    {
                        ModuleConfig.CustomizePresets[i].Type = TypeEditInput;
                        ModuleConfig.CustomizePresets[i].Value = ValueEditInput;
                        ModuleConfig.CustomizePresets[i].Scale = ScaleEditInput;
                        ModuleConfig.CustomizePresets[i].ScaleVFX = ScaleVFXEditInput;
                        ModuleConfig.CustomizePresets[i].Note = NoteEditInput;
                        SaveConfig(ModuleConfig);

                        RemovePresetHistory(preset);
                    }


                    ImGui.EndPopup();
                }


                ImGui.SameLine();
                if (ImGuiOm.ButtonIcon($"DeletePreset_{i}", FontAwesomeIcon.TrashAlt,
                                       Service.Lang.GetText("CustomizeGameObject-HoldCtrlToDelete")) &&
                    ImGui.IsKeyDown(ImGuiKey.LeftCtrl))
                {
                    var keysToRemove = CustomizeHistory
                                       .Where(x => x.Value.Preset == preset)
                                       .Select(x => x.Key)
                                       .ToList();

                    foreach (var key in keysToRemove)
                    {
                        ResetCustomizeFromHistory(key);
                        CustomizeHistory.Remove(key);
                    }

                    ModuleConfig.CustomizePresets.Remove(preset);
                    SaveConfig(ModuleConfig);
                }

                ImGui.SameLine();
                if (ImGuiOm.ButtonIcon($"ExportPreset_{i}", FontAwesomeIcon.FileExport,
                                       Service.Lang.GetText("ExportToClipboard")))
                    ExportToClipboard(preset);

                ImGui.SameLine();
                if (ImGuiOm.ButtonIcon($"ImportPreset_{i}", FontAwesomeIcon.FileImport,
                                       Service.Lang.GetText("ImportFromClipboard")))
                {
                    var presetImport = ImportFromClipboard<CustomizePreset>();

                    if (presetImport != null && !ModuleConfig.CustomizePresets.Contains(presetImport))
                    {
                        ModuleConfig.CustomizePresets.Add(presetImport);
                        SaveConfig(ModuleConfig);
                        array = [.. ModuleConfig.CustomizePresets];
                    }
                }

                ImGui.PopID();
            }


            ImGui.EndTable();
        }
    }

    private static bool CustomizePresetEditorUI
    (
        ref CustomizeType typeInput, ref string valueInput, ref float scaleInput, ref bool scaleVFXInput,
        ref string noteInput)
    {
        var state = false;

        if (ImGui.BeginTable("CustomizeTable", 2, ImGuiTableFlags.None))
        {
            ImGui.TableSetupColumn("Label", ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize("真得五个字").X);
            ImGui.TableSetupColumn("Input", ImGuiTableColumnFlags.WidthFixed, 300f * ImGuiHelpers.GlobalScale);

            // 类型
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.Text($"{Service.Lang.GetText("CustomizeGameObject-CustomizeType")}:");

            ImGui.TableNextColumn();
            if (ImGui.BeginCombo("###CustomizeTypeSelectCombo", typeInput.ToString()))
            {
                foreach (var mode in Enum.GetValues<CustomizeType>())
                    if (ImGui.Selectable(mode.ToString(), mode == typeInput))
                    {
                        typeInput = mode;
                        state = true;
                    }

                ImGui.EndCombo();
            }

            // 值
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.Text($"{Service.Lang.GetText("Value")}:");

            ImGui.TableNextColumn();
            ImGui.InputText("###CustomizeValueInput", ref valueInput, 128);
            if (ImGui.IsItemDeactivatedAfterEdit()) state = true;
            ImGuiOm.TooltipHover(valueInput);

            // 缩放
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.Text($"{Service.Lang.GetText("CustomizeGameObject-Scale")}:");

            ImGui.TableNextColumn();
            ImGui.SliderFloat("###CustomizeScaleSilder", ref scaleInput, 0.1f, 10f, "%.1f");
            if (ImGui.IsItemDeactivatedAfterEdit()) state = true;

            ImGui.SameLine();
            if (ImGui.Checkbox(Service.Lang.GetText("CustomizeGameObject-ScaleVFX"), ref scaleVFXInput)) state = true;

            // 备注
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.Text("备注:");

            ImGui.TableNextColumn();
            ImGui.InputText("###CustomizeNoteInput", ref noteInput, 128);
            if (ImGui.IsItemDeactivatedAfterEdit()) state = true;
            ImGuiOm.TooltipHover(noteInput);

            ImGui.EndTable();
        }

        return state;
    }

    private static void TargetInfoPreviewUI()
    {
        var currentTarget = TargetSystem.Instance()->Target;
        if (currentTarget == null)
        {
            ImGui.Text(Service.Lang.GetText("CustomizeGameObject-NoTaretNotice"));
            return;
        }

        if (!currentTarget->IsCharacter()) return;

        var tableSize1 = ImGui.GetContentRegionAvail() with { Y = 0 };
        if (ImGui.BeginTable("TargetInfoPreviewTable", 2, ImGuiTableFlags.BordersInner, tableSize1))
        {
            ImGui.TableSetupColumn("Lable", ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize("Model Skeleton ID:").X);
            ImGui.TableSetupColumn("Input", ImGuiTableColumnFlags.WidthStretch, 50);

            // Target Name
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.Text($"{Service.Lang.GetText("Name")}:");

            ImGui.TableNextColumn();
            var targetName = Marshal.PtrToStringUTF8((nint)currentTarget->Name);
            ImGui.SetNextItemWidth(-1f);
            ImGui.InputText("###TargetNamePreview", ref targetName, 128, ImGuiInputTextFlags.ReadOnly);

            // Data ID
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.Text("Data ID:");

            ImGui.TableNextColumn();
            var targetDataID = currentTarget->DataID.ToString();
            ImGui.SetNextItemWidth(-1f);
            ImGui.InputText("###TargetDataIDPreview", ref targetDataID, 128, ImGuiInputTextFlags.ReadOnly);

            // Object ID
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.Text("Object ID:");

            ImGui.TableNextColumn();
            var targetObjectID = currentTarget->ObjectID.ToString();
            ImGui.SetNextItemWidth(-1f);
            ImGui.InputText("###TargetObjectIDPreview", ref targetObjectID, 128, ImGuiInputTextFlags.ReadOnly);

            // ModelChara ID
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.Text("Model Chara ID:");

            ImGui.TableNextColumn();
            var targetModelCharaID = ((CharacterStruct*)currentTarget)->CharacterData.ModelCharaId.ToString();
            ImGui.SetNextItemWidth(-1f);
            ImGui.InputText("###TargetModelCharaIDPreview", ref targetModelCharaID, 128, ImGuiInputTextFlags.ReadOnly);

            // ModelChara ID
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.Text("Model Skeleton ID:");

            ImGui.TableNextColumn();
            var targetSkeletonID = ((CharacterStruct*)currentTarget)->CharacterData.ModelSkeletonId.ToString();
            ImGui.SetNextItemWidth(-1f);
            ImGui.InputText("###TargetModelSkeletonIDPreview", ref targetSkeletonID, 128, ImGuiInputTextFlags.ReadOnly);

            ImGui.EndTable();
        }
    }

    private static byte IsTargetableDetour(GameObjectStruct* pTarget)
    {
        var isTargetable = IsTargetableHook.Original(pTarget);

        if (ModuleConfig.CustomizePresets.Count == 0 || !pTarget->IsCharacter()) return isTargetable;
        if (!EzThrottler.Throttle($"CustomizeGameObjectScale_{(nint)pTarget}", 1000)) return isTargetable;

        var name = Marshal.PtrToStringUTF8((nint)pTarget->Name);
        var dataID = pTarget->DataID;
        var objectID = pTarget->ObjectID;
        var charaData = ((CharacterStruct*)pTarget)->CharacterData;
        var modelCharaID = charaData.ModelCharaId;
        var modelSkeletonID = charaData.ModelSkeletonId;

        foreach (var preset in ModuleConfig.CustomizePresets)
        {
            if (!preset.Enabled) continue;

            var isNeedToReScale = false;
            switch (preset.Type)
            {
                case CustomizeType.Name:
                    if (name.Equals(preset.Value)) isNeedToReScale = true;
                    break;
                case CustomizeType.DataID:
                    if (dataID.ToString() == preset.Value) isNeedToReScale = true;
                    break;
                case CustomizeType.ObjectID:
                    if (objectID.ToString() == preset.Value) isNeedToReScale = true;
                    break;
                case CustomizeType.ModelCharaID:
                    if (modelCharaID.ToString() == preset.Value)
                    {
                        isNeedToReScale = true;
                        charaData.ModelScale = preset.Scale;
                    }

                    break;
                case CustomizeType.ModelSkeletonID:
                    if (modelSkeletonID.ToString() == preset.Value)
                    {
                        isNeedToReScale = true;
                        charaData.ModelScale = preset.Scale;
                    }

                    break;
            }

            if (isNeedToReScale &&
                (pTarget->Scale != preset.Scale || (preset.ScaleVFX && pTarget->VfxScale != preset.Scale)))
            {
                CustomizeHistory.TryAdd((nint)pTarget, (preset, pTarget->Scale));

                pTarget->Scale = preset.Scale;
                if (preset.ScaleVFX) pTarget->VfxScale = preset.Scale;
                pTarget->DisableDraw();
                pTarget->EnableDraw();
            }
        }

        return isTargetable;
    }

    private static void OnZoneChanged(ushort zone)
    {
        CustomizeHistory.Clear();
    }

    private static void RemovePresetHistory(CustomizePreset? preset)
    {
        var keysToRemove = CustomizeHistory
                           .Where(x => x.Value.Preset == preset)
                           .Select(x => x.Key)
                           .ToList();

        foreach (var key in keysToRemove)
        {
            ResetCustomizeFromHistory(key);
            CustomizeHistory.Remove(key);
        }
    }

    private static void ResetCustomizeFromHistory(nint address)
    {
        if (CustomizeHistory.Count == 0) return;

        if (!CustomizeHistory.TryGetValue(address, out var data)) return;

        var gameObj = (GameObjectStruct*)address;
        if (gameObj == null || !gameObj->IsReadyToDraw()) return;

        gameObj->Scale = data.Scale;
        gameObj->VfxScale = data.Scale;
        gameObj->DisableDraw();
        gameObj->EnableDraw();
    }

    private static void ResetAllCustomizeFromHistory()
    {
        if (CustomizeHistory.Count == 0) return;

        foreach (var (objectPtr, data) in CustomizeHistory)
        {
            var gameObj = (GameObjectStruct*)objectPtr;
            if (gameObj == null || !gameObj->IsReadyToDraw()) continue;

            gameObj->Scale = data.Scale;
            gameObj->VfxScale = data.Scale;
            gameObj->DisableDraw();
            gameObj->EnableDraw();
        }
    }

    public override void Uninit()
    {
        Service.ClientState.TerritoryChanged -= OnZoneChanged;

        if (Service.ClientState.LocalPlayer != null)
            ResetAllCustomizeFromHistory();
        CustomizeHistory.Clear();

        base.Uninit();
    }
}