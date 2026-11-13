using ImGuiNET;
using System.Numerics;

public sealed partial class MainWindow
{
    private const float SnapFloatStep = 0.01f;
    private const string SnapFloatFormat = "%.2f";

    private readonly record struct SnapSettingsLayout(
        float LabelWidth,
        float MiddleLabelWidth,
        float LastLabelWidth,
        float RangeInputWidth,
        float StrengthInputWidth,
        float ExtraInputWidth);

    private float DrawSnapSettingsSection(HomeLayoutMetrics metrics, ImGuiStylePtr topPanelStyle)
    {
        ImGui.TableNextRow();
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("吸附参数设定");
        ImGui.TableSetColumnIndex(1);

        var selectedModelSize = _onnxTopSelectedModelIndex >= 0 && _onnxTopSelectedModelIndex < _onnxModels.Count
            ? Math.Max(1, _onnxModels[_onnxTopSelectedModelIndex].InputHeight)
            : 1;
        var displayHeightLimit = GetDisplayHeightOrWindowHeight();
        var snapOuterRangeMax = Math.Max(selectedModelSize, displayHeightLimit);
        NormalizeSnapSettings(selectedModelSize, snapOuterRangeMax);
        var layout = BuildSnapSettingsLayout(topPanelStyle);

        var tableStartX = ImGui.GetCursorScreenPos().X;
        var rightmostItemX = tableStartX;
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() - topPanelStyle.CellPadding.Y);
        if (ImGui.BeginTable("##SnapSettingsGrid", 6, ImGuiTableFlags.SizingFixedFit))
        {
            SetupSnapSettingsGridColumns(layout);
            DrawSnapRangeRow(layout, selectedModelSize, snapOuterRangeMax);
            DrawSnapStrengthRow(layout);
            rightmostItemX = ImGui.GetItemRectMax().X;
            DrawSnapExtraRow(layout);

            ImGui.EndTable();
        }

        DrawSnapInterpolationTypeRow(
            "##SnapCenterInterpolationRow",
            "##SnapInnerInterpolationTypeCombo",
            "中心衰减差值",
            "目标在核心区内由中心向外移动时的吸附强度过渡曲线。\nEase-In：前段变化慢、后段变化快；Ease-Out 相反。",
            SnapConfigCatalog.InnerInterpolationTypeKey,
            () => _homeViewState.SnapInnerInterpolationTypeIndex,
            value => _homeViewState.SnapInnerInterpolationTypeIndex = value,
            metrics.ReserveWidth,
            layout.LabelWidth);
        DrawSnapInterpolationTypeRow(
            "##SnapDistanceInterpolationRow",
            "##SnapDistanceInterpolationTypeCombo",
            "距离衰减差值",
            "目标框越小（距离越远）时吸附强度衰减的曲线。\nEase-In：先缓降、接近最小时快速衰减；Ease-Out 相反。",
            SnapConfigCatalog.DistanceInterpolationTypeKey,
            () => _homeViewState.SnapDistanceInterpolationTypeIndex,
            value => _homeViewState.SnapDistanceInterpolationTypeIndex = value,
            metrics.ReserveWidth,
            layout.LabelWidth);

        var fallbackWidth = layout.LabelWidth
                            + layout.MiddleLabelWidth
                            + layout.LastLabelWidth
                            + layout.StrengthInputWidth * 3f;
        var measuredWidth = rightmostItemX - tableStartX;
        if (_snapSettingsWidth <= 0f && measuredWidth >= fallbackWidth)
        {
            _snapSettingsWidth = measuredWidth;
        }

        return _snapSettingsWidth > 0f ? _snapSettingsWidth : fallbackWidth;
    }

    private void NormalizeSnapSettings(int selectedModelSize, int snapOuterRangeMax)
    {
        _homeViewState.ApplySnapConfig(
            SnapConfigCatalog.Normalized(_homeViewState.ToSnapSettings(), selectedModelSize, snapOuterRangeMax));
    }

    private static SnapSettingsLayout BuildSnapSettingsLayout(ImGuiStylePtr topPanelStyle)
    {
        var rangeInputWidth = ImGui.CalcTextSize("0000").X + topPanelStyle.FramePadding.X * 2f;
        var strengthInputWidth = rangeInputWidth + ImGui.GetFrameHeight() * 2f + topPanelStyle.ItemInnerSpacing.X * 2f;
        var labelWidth = MeasureMaxTextWidth(
                             "吸附范围",
                             "腰射强度系数",
                             "强度爬升时间",
                             "中心衰减差值",
                             "距离衰减差值")
                         + topPanelStyle.CellPadding.X * 2f;
        var middleLabelWidth = MeasureMaxTextWidth(
                                   "外围强度",
                                   "起始强度")
                               + topPanelStyle.CellPadding.X * 2f;
        var lastLabelWidth = MeasureMaxTextWidth("无限远强度", "吸附高度") + topPanelStyle.CellPadding.X * 2f;
        return new SnapSettingsLayout(
            labelWidth,
            middleLabelWidth,
            lastLabelWidth,
            rangeInputWidth,
            strengthInputWidth,
            strengthInputWidth);
    }

    private static float MeasureMaxTextWidth(params string[] texts)
    {
        var maxWidth = 0f;
        for (var i = 0; i < texts.Length; i++)
        {
            maxWidth = MathF.Max(maxWidth, ImGui.CalcTextSize(texts[i]).X);
        }

        return maxWidth;
    }

    private static void SetupSnapSettingsGridColumns(in SnapSettingsLayout layout)
    {
        ImGui.TableSetupColumn("##SnapLabelCol0", ImGuiTableColumnFlags.WidthFixed, layout.LabelWidth);
        ImGui.TableSetupColumn("##SnapInputCol0", ImGuiTableColumnFlags.WidthFixed, layout.StrengthInputWidth);
        ImGui.TableSetupColumn("##SnapLabelCol1", ImGuiTableColumnFlags.WidthFixed, layout.MiddleLabelWidth);
        ImGui.TableSetupColumn("##SnapInputCol1", ImGuiTableColumnFlags.WidthFixed, layout.StrengthInputWidth);
        ImGui.TableSetupColumn("##SnapLabelCol2", ImGuiTableColumnFlags.WidthFixed, layout.LastLabelWidth);
        ImGui.TableSetupColumn("##SnapInputCol2", ImGuiTableColumnFlags.WidthFixed, layout.StrengthInputWidth);
    }

    private void DrawSnapRangeRow(in SnapSettingsLayout layout, int selectedModelSize, int snapOuterRangeMax)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("吸附范围");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("吸附范围的直径，目标进入该范围即开始吸附。\n单位：像素，上限取决于模型尺寸与显示高度。");
        }

        ImGui.TableSetColumnIndex(1);
        ImGui.SetNextItemWidth(layout.RangeInputWidth);
        var snapOuterRange = _homeViewState.SnapOuterRange;
        if (ImGui.InputInt("##SnapOuterRange", ref snapOuterRange, 0, 0))
        {
            _homeViewState.SnapOuterRange = Math.Clamp(snapOuterRange, selectedModelSize, snapOuterRangeMax);
            TryWriteIntToCurrentConfig(SnapConfigCatalog.OuterRangeKey, _homeViewState.SnapOuterRange);
            PushAimAssistConfig();
            SyncSmartCoreVisionPipeline();
        }

        ImGui.TableSetColumnIndex(2);
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("外围强度");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("瞄准时，目标位于核心区外、吸附范围内的吸附强度。\n核心区直径为目标框宽度的 3 倍。");
        }

        ImGui.TableSetColumnIndex(3);
        ImGui.SetNextItemWidth(layout.StrengthInputWidth);
        DrawClampedConfigFloatInput(
            "##SnapOuterStrength",
            SnapConfigCatalog.OuterStrengthKey,
            _homeViewState.SnapOuterStrength,
            value => _homeViewState.SnapOuterStrength = value,
            0f,
            1f);

        ImGui.TableSetColumnIndex(4);
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("无限远强度");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("目标越小（3W 越接近 0）吸附强度越低，最低降到该强度。\n3W 达到外圈直径时按 100% 计算。");
        }

        ImGui.TableSetColumnIndex(5);
        ImGui.SetNextItemWidth(layout.StrengthInputWidth);
        DrawClampedConfigFloatInput(
            "##SnapFarStrengthFactor",
            SnapConfigCatalog.FarStrengthFactorKey,
            _homeViewState.SnapFarStrengthFactor,
            value => _homeViewState.SnapFarStrengthFactor = value,
            0f,
            1f);
    }

    private void DrawSnapStrengthRow(in SnapSettingsLayout layout)
    {
        ImGui.TableNextRow();
        ImGui.TableNextRow();

        ImGui.TableSetColumnIndex(0);
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("腰射强度系数");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("未按瞄准键（腰射）时对吸附强度的缩放系数。\n1 表示与内圈强度一致，0 表示腰射不吸附。");
        }

        ImGui.TableSetColumnIndex(1);
        ImGui.SetNextItemWidth(layout.StrengthInputWidth);
        DrawClampedConfigFloatInput(
            "##SnapHipfireStrengthFactor",
            SnapConfigCatalog.HipfireStrengthFactorKey,
            _homeViewState.SnapHipfireStrengthFactor,
            value => _homeViewState.SnapHipfireStrengthFactor = value,
            0f,
            1f);

        ImGui.TableSetColumnIndex(2);
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("起始强度");
        ImGui.TableSetColumnIndex(3);
        ImGui.SetNextItemWidth(layout.StrengthInputWidth);
        DrawClampedConfigFloatInput(
            "##SnapStartStrength",
            SnapConfigCatalog.StartStrengthKey,
            _homeViewState.SnapStartStrength,
            value => _homeViewState.SnapStartStrength = value,
            0f,
            1f);

        ImGui.TableSetColumnIndex(4);
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("吸附高度");
        ImGui.TableSetColumnIndex(5);
        ImGui.SetNextItemWidth(layout.StrengthInputWidth);
        DrawClampedConfigFloatInput(
            "##SnapHeight",
            SnapConfigCatalog.HeightKey,
            _homeViewState.SnapHeight,
            value => _homeViewState.SnapHeight = value,
            0f,
            1f);
    }

    private void DrawSnapExtraRow(in SnapSettingsLayout layout)
    {
        ImGui.TableNextRow();
        ImGui.TableNextRow();

        ImGui.TableSetColumnIndex(0);
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("强度爬升时间");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("吸附强度从起始强度爬升到目标强度所需的时间（秒）。\n0 表示瞬间到达目标强度，数值越大爬升越平缓。");
        }

        ImGui.TableSetColumnIndex(1);
        ImGui.SetNextItemWidth(layout.StrengthInputWidth);
        DrawClampedConfigFloatInput(
            "##SnapStrengthRampTime",
            SnapConfigCatalog.StrengthRampTimeKey,
            _homeViewState.SnapStrengthRampTime,
            value => _homeViewState.SnapStrengthRampTime = value,
            0f,
            1f,
            0.1f,
            "%.1f");
    }

    private void DrawSnapInterpolationTypeRow(
        string tableId,
        string comboId,
        string label,
        string tooltip,
        string configKey,
        Func<int> getInterpolationTypeIndex,
        Action<int> setInterpolationTypeIndex,
        float reserveWidth,
        float labelWidth)
    {
        if (!ImGui.BeginTable(tableId, 2, ImGuiTableFlags.SizingStretchProp))
        {
            return;
        }

        ImGui.TableSetupColumn("##SnapInterpolationLabel", ImGuiTableColumnFlags.WidthFixed, labelWidth);
        ImGui.TableSetupColumn("##SnapInterpolationInput", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableNextRow();

        var interpolationIndex = Math.Clamp(
            getInterpolationTypeIndex(),
            0,
            AimAssistOptionCatalog.SnapInterpolationTypeOptions.Length - 1);
        setInterpolationTypeIndex(interpolationIndex);

        ImGui.TableSetColumnIndex(0);
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(label);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(tooltip);
        }

        ImGui.TableSetColumnIndex(1);
        var interpolationComboWidth = MathF.Max(90f, ImGui.GetContentRegionAvail().X - reserveWidth);
        var interpolationChanged = DrawConfigBoundCombo(
            comboId,
            AimAssistOptionCatalog.SnapInterpolationTypeOptions,
            ref interpolationIndex,
            interpolationComboWidth,
            false);
        setInterpolationTypeIndex(interpolationIndex);
        if (interpolationChanged)
        {
            TryWriteStringToCurrentConfig(configKey, AimAssistOptionCatalog.SnapInterpolationTypeOptions[interpolationIndex]);
            PushAimAssistConfig();
        }

        ImGui.EndTable();
    }

    private void DrawClampedConfigFloatInput(
        string controlId,
        string configKey,
        float currentValue,
        Action<float> setValue,
        float min,
        float max,
        float step = SnapFloatStep,
        string format = SnapFloatFormat)
    {
        var editedValue = currentValue;
        if (!ImGui.InputFloat(controlId, ref editedValue, step, step, format))
        {
            return;
        }

        var clampedValue = Math.Clamp(editedValue, min, max);
        setValue(clampedValue);
        TryWriteFloatToCurrentConfig(configKey, clampedValue);
        PushAimAssistConfig();
    }

    private void DrawSnapCurveSection(ImGuiStylePtr topPanelStyle, float snapSettingsWidth)
    {
        ImGui.TableNextRow();
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        // ImGui.SetCursorPosY(ImGui.GetCursorPosY() - topPanelStyle.CellPadding.Y);
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("吸附曲线预览");
        ImGui.TableSetColumnIndex(1);
        // ImGui.SetCursorPosY(ImGui.GetCursorPosY() - topPanelStyle.CellPadding.Y);
        DrawSnapCurvePreview(snapSettingsWidth);
    }
}
