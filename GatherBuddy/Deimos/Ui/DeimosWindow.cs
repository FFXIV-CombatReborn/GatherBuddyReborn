using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using ElliLib.Raii;
using GatherBuddy.Deimos.Data;

namespace GatherBuddy.Deimos.Ui;

/// standalone Deimos window (opened via /deimos), intentionally not linked from the GBR UI yet
public sealed class DeimosWindow : Window
{
    private readonly Deimos _owner;
    private string _search = "";
    private int    _zoneIdx = -1; // -1 = follow current zone
    private bool   _jobsFilter;   // limit table to configured jobs (or current job)
    private bool   _dirty;        // any config change this frame -> one Save() at the end

    // lunar theme, pushed window-wide in PreDraw / popped in PostDraw
    private readonly ImRaii.Style _themeStyle = new();
    private readonly ImRaii.Color _themeColor = new();

    // theme palette (0xAABBGGRR)
    private const uint Indigo       = 0xFF2A1114;
    private const uint Panel        = 0xFF36161A;
    private const uint BorderCol    = 0xFF6A3339;
    private const uint FrameBg      = 0xFF441F24;
    private const uint FrameBgHover = 0xFF58282E;
    private const uint FrameBgAct   = 0xFF703139;
    private const uint HeaderCol    = 0xFF50242A;
    private const uint HeaderHover  = 0xFF632C34;
    private const uint Moonlight    = 0xFFF6E3E7;
    private const uint TextDim      = 0xFFBD9199;
    private const uint Gold         = 0xFF4EA9C8; // primary action / active
    private const uint Teal         = 0xFFC0B63F; // research + "include" toggles
    private const uint ScrollGrab   = 0xFF68343A;
    private const uint Danger       = 0xFF3A3AB0; // stop run
    private const uint TextDark     = 0xFF1A0A0D;
    private const uint TextLight    = 0xFFF2EEF5;

    private static readonly (uint Id, string Name)[] Zones =
    [
        (CosmicZone.SinusArdorum, "Sinus Ardorum"),
        (CosmicZone.Phaenna, "Phaenna"),
        (CosmicZone.Oizys, "Oizys"),
        (CosmicZone.Auxesia, "Auxesia"),
    ];

    public DeimosWindow(Deimos owner) : base("Deimos - Cosmic Exploration###DeimosWindow")
    {
        _owner = owner;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(720, 460),
            MaximumSize = new Vector2(1600, 1400),
        };
    }

    private static float S(float v) => v * ImGuiHelpers.GlobalScale;

    public override void PreDraw()
    {
        _themeStyle
            .Push(ImGuiStyleVar.WindowRounding, 7f)
            .Push(ImGuiStyleVar.FrameRounding,  4f)
            .Push(ImGuiStyleVar.ChildRounding,  4f)
            .Push(ImGuiStyleVar.FramePadding,   new Vector2(S(7), S(4)))
            .Push(ImGuiStyleVar.ItemSpacing,    new Vector2(S(8), S(6)))
            .Push(ImGuiStyleVar.WindowPadding,  new Vector2(S(11), S(11)));

        _themeColor
            .Push(ImGuiCol.WindowBg,       Indigo)
            .Push(ImGuiCol.ChildBg,        Panel)
            .Push(ImGuiCol.Border,         BorderCol)
            .Push(ImGuiCol.FrameBg,        FrameBg)
            .Push(ImGuiCol.FrameBgHovered, FrameBgHover)
            .Push(ImGuiCol.FrameBgActive,  FrameBgAct)
            .Push(ImGuiCol.Header,         HeaderCol)
            .Push(ImGuiCol.HeaderHovered,  HeaderHover)
            .Push(ImGuiCol.Text,           Moonlight)
            .Push(ImGuiCol.TextDisabled,   TextDim)
            .Push(ImGuiCol.CheckMark,      Gold)
            .Push(ImGuiCol.Button,         FrameBg)
            .Push(ImGuiCol.ButtonHovered,  FrameBgHover)
            .Push(ImGuiCol.ButtonActive,   FrameBgAct)
            .Push(ImGuiCol.ScrollbarGrab,  ScrollGrab);
    }

    public override void PostDraw()
    {
        _themeColor.Dispose();
        _themeStyle.Dispose();
    }

    public override void Draw()
    {
        var cfg = _owner.Config;
        _dirty = false;

        using (ImRaii.Child("rail", new Vector2(S(280), 0), false))
        {
            DrawStatusOrb(cfg);
            DrawStrategyPanel(cfg);
            DrawInclusionsPanel(cfg);
            DrawJobRotationPanel(cfg);
            DrawUpkeepPanel(cfg);
            DrawStopPanel(cfg);
        }

        ImGui.SameLine();

        using (ImRaii.Child("board", new Vector2(0, 0), false))
            DrawMissionBoard(cfg);

        if (_dirty)
            cfg.Save();
    }

    // ---- left rail ----------------------------------------------------------

    private void DrawStatusOrb(DeimosConfig cfg)
    {
        using var child = ImRaii.Child("##status", new Vector2(0, S(112)), true);

        DrawMoonDial(cfg, S(82));

        ImGui.SameLine(0, S(10));
        using (ImRaii.Group())
        {
            ImGui.TextDisabled("State");
            ImGui.SameLine(0, S(6));
            ImGui.TextUnformatted(_owner.State.ToString());

            DrawDot(Teal);
            ImGui.SameLine(0, S(6));
            ImGui.TextUnformatted(CosmicZone.InCosmicZone ? CosmicZone.Name(CosmicZone.Current) : "not in a cosmic zone");

            var missionId = Wks.CurrentMissionId;
            if (missionId != 0 && MissionData.TryGet(missionId, out var mission))
                ImGui.TextDisabled(mission.Name);
            else
                ImGui.TextDisabled($"{_owner.MissionsDone} missions done");

            var btn = new Vector2(-1, S(28));
            if (_owner.Enabled)
            {
                if (PrimaryButton(FontAwesomeIcon.Stop, "Stop run", btn, Danger, TextLight))
                    _owner.Disable();
            }
            else
            {
                if (PrimaryButton(FontAwesomeIcon.Play, "Start run", btn, Gold, TextDark))
                    _owner.Enable();
            }
        }
    }

    private void DrawStrategyPanel(DeimosConfig cfg)
    {
        using var group = ImRaii.FramedGroup("STRATEGY", new Vector2(-1, 0), "", BorderCol, TextDim);

        ImGui.SetNextItemWidth(-1);
        using (var combo = ImRaii.Combo("##mode", cfg.Mode.ToString()))
        {
            if (combo)
                foreach (var mode in new[] { DeimosMode.Standard, DeimosMode.MissionGold, DeimosMode.Leveling })
                    if (ImGui.Selectable(mode.ToString(), cfg.Mode == mode))
                    {
                        cfg.Mode = mode;
                        _dirty   = true;
                    }
        }

        var skipExpert = cfg.SkipExpertCrafts;
        if (ImGui.Checkbox("Skip expert crafts", ref skipExpert))
        {
            cfg.SkipExpertCrafts = skipExpert;
            _dirty = true;
        }

        var reroll = cfg.AutoReroll;
        if (ImGui.Checkbox("Auto reroll", ref reroll))
        {
            cfg.AutoReroll = reroll;
            _dirty = true;
        }

        if (cfg.AutoReroll)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(S(60));
            var max = cfg.MaxRerolls;
            if (ImGui.InputInt("max##rerolls", ref max, 0))
            {
                cfg.MaxRerolls = Math.Clamp(max, 1, 20);
                _dirty = true;
            }
        }
    }

    private void DrawInclusionsPanel(DeimosConfig cfg)
    {
        using var group = ImRaii.FramedGroup("BOARD INCLUSIONS", new Vector2(-1, 0), "", BorderCol, TextDim);
        using var check = ImRaii.PushColor(ImGuiCol.CheckMark, Teal);

        var provisionals = cfg.IncludeProvisionals;
        if (ImGui.Checkbox("Provisionals", ref provisionals))
        {
            cfg.IncludeProvisionals = provisionals;
            _dirty = true;
        }

        var criticals = cfg.IncludeCriticals;
        if (ImGui.Checkbox("Criticals", ref criticals))
        {
            cfg.IncludeCriticals = criticals;
            _dirty = true;
        }

        var mastery = cfg.IncludeMastery;
        if (ImGui.Checkbox("Mastery", ref mastery))
        {
            cfg.IncludeMastery = mastery;
            _dirty = true;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Include Auxesia tool-mastery (rank M) missions.");
    }

    private void DrawJobRotationPanel(DeimosConfig cfg)
    {
        using var group = ImRaii.FramedGroup("JOB ROTATION", new Vector2(-1, 0), "", BorderCol, TextDim);

        var count = cfg.JobPriority.Count(CosmicJobs.IsCrafter);
        ImGui.TextDisabled(count == 0 ? "current job only" : $"{count} selected, in order");

        var size = S(26);
        var avail = ImGui.GetContentRegionAvail().X;
        var perRow = Math.Max(1, (int)(avail / (size + S(4))));
        for (var i = 0; i < CosmicJobs.Crafters.Count; i++)
        {
            if (i % perRow != 0)
                ImGui.SameLine(0, S(4));
            DrawJobToggle(cfg, CosmicJobs.Crafters[i], size);
        }
    }

    private void DrawUpkeepPanel(DeimosConfig cfg)
    {
        using var group = ImRaii.FramedGroup("UPKEEP", new Vector2(-1, 0), "", BorderCol, TextDim);

        var repair = cfg.AutoRepair;
        if (ImGui.Checkbox("Self-repair", ref repair))
        {
            cfg.AutoRepair = repair;
            _dirty = true;
        }

        if (cfg.AutoRepair)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(S(60));
            var threshold = cfg.RepairThreshold;
            if (ImGui.InputInt("%##repair", ref threshold, 0))
            {
                cfg.RepairThreshold = Math.Clamp(threshold, 1, 99);
                _dirty = true;
            }

            var vendor = cfg.RepairAtVendor;
            if (ImGui.Checkbox("At vendor", ref vendor))
            {
                cfg.RepairAtVendor = vendor;
                _dirty = true;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Stellar Return to the hub and repair at the npc instead of using dark matter.");
        }

        var materia = cfg.AutoExtractMateria;
        if (ImGui.Checkbox("Extract materia", ref materia))
        {
            cfg.AutoExtractMateria = materia;
            _dirty = true;
        }

        var gamba = cfg.AutoGamba;
        if (ImGui.Checkbox("Gamba", ref gamba))
        {
            cfg.AutoGamba = gamba;
            _dirty = true;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Spend lunar credits on the Cosmic Fortune wheel between missions.");

        if (cfg.AutoGamba)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(S(60));
            var keep = cfg.GambaKeepLunarCredits;
            if (ImGui.InputInt("keep##gamba", ref keep, 0))
            {
                cfg.GambaKeepLunarCredits = Math.Max(0, keep);
                _dirty = true;
            }
        }
    }

    private void DrawStopPanel(DeimosConfig cfg)
    {
        using var group = ImRaii.FramedGroup("STOP", new Vector2(-1, 0), "", BorderCol, TextDim);

        StopInput("missions", "##stopmissions", cfg.StopAfterMissions, v => cfg.StopAfterMissions = v);
        StopInput("lunar",    "##stoplunar",    cfg.StopAtLunarCredits, v => cfg.StopAtLunarCredits = v);
        StopInput("cosmo",    "##stopcosmo",    cfg.StopAtCosmoCredits, v => cfg.StopAtCosmoCredits = v);
        ImGui.TextDisabled("0 = off");
    }

    private void StopInput(string label, string id, int value, Action<int> set)
    {
        ImGui.SetNextItemWidth(S(60));
        var v = value;
        if (ImGui.InputInt(id, ref v, 0))
        {
            set(Math.Max(0, v));
            _dirty = true;
        }
        ImGui.SameLine(0, S(6));
        ImGui.TextUnformatted(label);
    }

    // ---- right board --------------------------------------------------------

    private void DrawMissionBoard(DeimosConfig cfg)
    {
        var zoneId = _zoneIdx >= 0 ? Zones[_zoneIdx].Id
            : CosmicZone.InCosmicZone ? CosmicZone.Current : Zones[0].Id;

        // toolbar
        ImGui.SetNextItemWidth(S(160));
        using (var combo = ImRaii.Combo("##zone", CosmicZone.Name(zoneId)))
            if (combo)
                for (var i = 0; i < Zones.Length; i++)
                    if (ImGui.Selectable(Zones[i].Name, Zones[i].Id == zoneId))
                        _zoneIdx = i;

        ImGui.SameLine();
        ImGui.SetNextItemWidth(-(ImGui.GetFrameHeight() + S(8)));
        ImGui.InputTextWithHint("##search", "filter...", ref _search, 64);

        ImGui.SameLine();
        if (IconButton(FontAwesomeIcon.User, "myjobs", _jobsFilter,
                "Show only missions for the selected jobs (or your current job if none are selected)."))
            _jobsFilter = !_jobsFilter;

        // table sized to leave room for the action row beneath it
        var tableHeight = ImGui.GetContentRegionAvail().Y - ImGui.GetFrameHeightWithSpacing();
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY;
        using (var table = ImRaii.Table("##missions", 7, flags, new Vector2(0, tableHeight)))
        {
            if (table)
            {
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableSetupColumn("Run", ImGuiTableColumnFlags.WidthFixed, S(28));
                ImGui.TableSetupColumn("Id", ImGuiTableColumnFlags.WidthFixed, S(42));
                ImGui.TableSetupColumn("Mission", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Jobs", ImGuiTableColumnFlags.WidthFixed, S(68));
                ImGui.TableSetupColumn("Rank", ImGuiTableColumnFlags.WidthFixed, S(40));
                ImGui.TableSetupColumn("Research", ImGuiTableColumnFlags.WidthFixed, S(120));
                ImGui.TableSetupColumn("Gold", ImGuiTableColumnFlags.WidthFixed, S(34));
                ImGui.TableHeadersRow();

                var inZone   = Wks.Available;
                var iconSize = ImGui.GetTextLineHeight();
                foreach (var id in VisibleMissions(zoneId))
                {
                    var info = MissionData.Get(id)!;
                    ImGui.TableNextRow();

                    ImGui.TableNextColumn();
                    var enabled = cfg.EnabledMissions.Count == 0 || cfg.EnabledMissions.Contains(id);
                    if (ImGui.Checkbox($"##run{id}", ref enabled))
                    {
                        if (enabled)
                        {
                            cfg.EnabledMissions.Add(id);
                        }
                        else
                        {
                            EnsureExplicitEnabledSet(cfg);
                            cfg.EnabledMissions.Remove(id);
                        }
                        cfg.Save();
                    }

                    ImGui.TableNextColumn();
                    ImGui.TextDisabled(id.ToString());

                    ImGui.TableNextColumn();
                    if (enabled)
                        ImGui.TextUnformatted(info.Name);
                    else
                        ImGui.TextDisabled(info.Name);

                    ImGui.TableNextColumn();
                    DrawJobIcons(info.Jobs, iconSize);

                    ImGui.TableNextColumn();
                    DrawRankBadge(info.Rank);

                    ImGui.TableNextColumn();
                    DrawResearch(info.RelicXpInfo);

                    ImGui.TableNextColumn();
                    var golded = inZone && Wks.IsMissionGolded(id);
                    IconText(FontAwesomeIcon.Star, golded ? Gold : 0xFF3A2B2F);
                }
            }
        }

        // action row
        if (ImGui.Button("Enable shown"))
        {
            foreach (var id in VisibleMissions(zoneId))
                cfg.EnabledMissions.Add(id);
            cfg.Save();
        }

        ImGui.SameLine();
        if (ImGui.Button("Disable shown"))
        {
            EnsureExplicitEnabledSet(cfg);
            foreach (var id in VisibleMissions(zoneId))
                cfg.EnabledMissions.Remove(id);
            cfg.Save();
        }

        ImGui.SameLine();
        if (ImGui.Button("Reset (all on)"))
        {
            cfg.EnabledMissions.Clear();
            cfg.Save();
        }
    }

    // ---- moon status dial ---------------------------------------------------

    private void DrawMoonDial(DeimosConfig cfg, float size)
    {
        var dl      = ImGui.GetWindowDrawList();
        var topLeft = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Vector2(size, size));

        var c = topLeft + new Vector2(size / 2f, size / 2f);
        var r = size * 0.30f;

        var target   = cfg.StopAfterMissions > 0 ? cfg.StopAfterMissions : 12;
        var progress = Math.Clamp(_owner.MissionsDone / (float)Math.Max(1, target), 0f, 1f);
        var running  = _owner.Enabled;
        var t        = (float)ImGui.GetTime();

        // 1. glow (running only)
        if (running)
        {
            var pulse = 0.5f + 0.5f * MathF.Sin(t * 2.4f);
            for (var i = 3; i >= 1; i--)
            {
                var a    = (uint)(0.05f * pulse / i * 255f);
                var glow = (Gold & 0x00FFFFFFu) | (a << 24);
                dl.AddCircleFilled(c, r * (1.18f + 0.16f * i), glow, 48);
            }
        }

        // 2. body (layered to fake lit relief)
        (float dx, float dy, float rr, uint col)[] body =
        [
            (0f,    0f,    1.00f, 0xFF5F4146),
            (-.10f, -.10f, .92f,  0xFF6F565B),
            (-.18f, -.18f, .78f,  0xFF91787D),
            (-.24f, -.24f, .60f,  0xFFBAA2A7),
            (-.28f, -.28f, .40f,  0xFFDCC9CD),
            (-.30f, -.30f, .22f,  0xFFF0E3E6),
        ];
        foreach (var (dx, dy, rr, col) in body)
            dl.AddCircleFilled(c + new Vector2(dx, dy) * r, rr * r, col, 48);

        // 3. craters
        (float dx, float dy, float rr)[] craters = [(.30f, .22f, .16f), (-.05f, .40f, .12f), (.42f, -.18f, .09f)];
        foreach (var (dx, dy, rr) in craters)
            dl.AddCircleFilled(c + new Vector2(dx, dy) * r, rr * r, 0x61362428, 24);

        // 4. track ring
        var lw = size * 0.055f;
        dl.AddCircle(c, r * 1.32f, 0xFF50252A, 64, lw);

        // 5. progress arc
        var a0 = -MathF.PI / 2f;
        var a1 = a0 + MathF.PI * 2f * progress;
        dl.PathArcTo(c, r * 1.32f, a0, a1);
        dl.PathStroke(Gold, ImDrawFlags.None, lw);

        // 6. head dot (running only)
        if (running)
        {
            var dir = new Vector2(MathF.Cos(a1), MathF.Sin(a1));
            dl.AddCircleFilled(c + dir * r * 1.32f, lw * 0.62f, 0xFF84D7F0, 16);
        }

        // center label
        var big      = _owner.MissionsDone.ToString();
        var small    = $"/ {target}";
        var bigSize  = ImGui.CalcTextSize(big);
        var smallSz  = ImGui.CalcTextSize(small);
        dl.AddText(new Vector2(c.X - bigSize.X / 2f, c.Y - bigSize.Y * 0.7f), Moonlight, big);
        dl.AddText(new Vector2(c.X - smallSz.X / 2f, c.Y + smallSz.Y * 0.1f), TextDim, small);
    }

    // ---- token rendering ----------------------------------------------------

    private static void DrawJobIcons(List<uint> jobs, float size)
    {
        for (var i = 0; i < jobs.Count; i++)
        {
            if (i > 0)
                ImGui.SameLine(0, S(2));
            DrawJobIcon(jobs[i], size);
        }
    }

    private static void DrawJobIcon(uint job, float size)
    {
        var tex = Icons.DefaultStorage.TextureProvider.GetFromGameIcon(new GameIconLookup(62100u + job));
        if (tex.TryGetWrap(out var wrap, out _))
            ImGui.Image(wrap.Handle, new Vector2(size));
        else
            ImGui.Dummy(new Vector2(size));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(CosmicJobs.Name(job));
    }

    private void DrawJobToggle(DeimosConfig cfg, uint job, float size)
    {
        var on  = cfg.JobPriority.Contains(job);
        var p   = ImGui.GetCursorScreenPos();
        var tex = Icons.DefaultStorage.TextureProvider.GetFromGameIcon(new GameIconLookup(62100u + job));
        var tint = on ? Vector4.One : new Vector4(0.45f, 0.45f, 0.45f, 1f);
        if (tex.TryGetWrap(out var wrap, out _))
            ImGui.Image(wrap.Handle, new Vector2(size), Vector2.Zero, Vector2.One, tint);
        else
            ImGui.Dummy(new Vector2(size));

        if (ImGui.IsItemClicked())
        {
            if (on)
                cfg.JobPriority.Remove(job);
            else
                cfg.JobPriority.Add(job);
            _dirty = true;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(CosmicJobs.Name(job));

        if (on)
            ImGui.GetWindowDrawList().AddRect(p, p + new Vector2(size), Gold, S(3), ImDrawFlags.None, S(1.5f));
    }

    private static void DrawRankBadge(uint rank)
    {
        var bg = RankColor(rank);
        var fg = rank is 2 or 3 or 4 ? TextLight : TextDark; // C/B/A light, rest dark
        var dl = ImGui.GetWindowDrawList();
        var text = RankLabel(rank);
        var sz   = ImGui.CalcTextSize(text);
        var pad  = new Vector2(S(5), S(2));
        var p    = ImGui.GetCursorScreenPos();
        var max  = p + sz + pad * 2;
        dl.AddRectFilled(p, max, bg, S(3));
        if (rank == 6) // mastery glow
            dl.AddRect(p - Vector2.One, max + Vector2.One, (Teal & 0x00FFFFFFu) | 0x80000000u, S(3), ImDrawFlags.None, S(1.5f));
        dl.AddText(p + pad, fg, text);
        ImGui.Dummy(sz + pad * 2);
    }

    private static void DrawResearch(Dictionary<int, int> info)
    {
        var first = true;
        foreach (var kv in info)
        {
            if (!first)
                ImGui.SameLine(0, S(6));
            first = false;
            ImGui.TextDisabled(Roman(kv.Key));
            ImGui.SameLine(0, S(4));
            using (ImRaii.PushColor(ImGuiCol.Text, Teal))
                ImGui.TextUnformatted(kv.Value.ToString());
        }
    }

    private static void DrawDot(uint color)
    {
        var r = S(4);
        var p = ImGui.GetCursorScreenPos();
        var c = p + new Vector2(r, ImGui.GetTextLineHeight() / 2f);
        ImGui.GetWindowDrawList().AddCircleFilled(c, r, color, 12);
        ImGui.Dummy(new Vector2(r * 2, ImGui.GetTextLineHeight()));
    }

    private static void IconText(FontAwesomeIcon icon, uint color)
    {
        using var font = ImRaii.PushFont(UiBuilder.IconFont);
        using var col  = ImRaii.PushColor(ImGuiCol.Text, color);
        ImGui.TextUnformatted(icon.ToIconString());
    }

    private static bool IconButton(FontAwesomeIcon icon, string id, bool active, string tooltip)
    {
        bool clicked;
        using (ImRaii.PushColor(ImGuiCol.Button, Gold, active)
                   .Push(ImGuiCol.ButtonHovered, Brighten(Gold, 1.1f), active)
                   .Push(ImGuiCol.Text, TextDark, active))
        using (ImRaii.PushFont(UiBuilder.IconFont))
            clicked = ImGui.Button($"{icon.ToIconString()}##{id}");

        if (tooltip.Length > 0 && ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);
        return clicked;
    }

    // full-width primary button with a centered icon + label (icon font + default font)
    private static bool PrimaryButton(FontAwesomeIcon icon, string text, Vector2 size, uint bg, uint fg)
    {
        using var col = ImRaii.PushColor(ImGuiCol.Button, bg)
            .Push(ImGuiCol.ButtonHovered, Brighten(bg, 1.15f))
            .Push(ImGuiCol.ButtonActive, Brighten(bg, 0.9f));

        var clicked  = ImGui.Button($"##{text}", size);
        var min      = ImGui.GetItemRectMin();
        var rectSize = ImGui.GetItemRectSize();
        var dl       = ImGui.GetWindowDrawList();

        string  iconStr;
        Vector2 iconSize;
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            iconStr  = icon.ToIconString();
            iconSize = ImGui.CalcTextSize(iconStr);
        }
        var textSize = ImGui.CalcTextSize(text);
        var gap      = S(6);
        var originX  = min.X + (rectSize.X - (iconSize.X + gap + textSize.X)) / 2f;

        using (ImRaii.PushFont(UiBuilder.IconFont))
            dl.AddText(new Vector2(originX, min.Y + (rectSize.Y - iconSize.Y) / 2f), fg, iconStr);
        dl.AddText(new Vector2(originX + iconSize.X + gap, min.Y + (rectSize.Y - textSize.Y) / 2f), fg, text);
        return clicked;
    }

    private static uint Brighten(uint abgr, float f)
    {
        var a = (abgr >> 24) & 0xFF;
        var b = (uint)Math.Min(255f, ((abgr >> 16) & 0xFF) * f);
        var g = (uint)Math.Min(255f, ((abgr >> 8) & 0xFF) * f);
        var r = (uint)Math.Min(255f, (abgr & 0xFF) * f);
        return (a << 24) | (b << 16) | (g << 8) | r;
    }

    private static uint RankColor(uint rank) => rank switch
    {
        1 => 0xFF80726B, // D  grey
        2 => 0xFF6BAE5F, // C  green
        3 => 0xFFD68F5A, // B  blue
        4 => 0xFFD67C9A, // A  purple
        5 => 0xFF4EA9C8, // EX gold
        6 => 0xFFC0B63F, // M  teal
        _ => 0xFF80726B,
    };

    // ---- data helpers (unchanged) -------------------------------------------

    // craft-only missions for the zone matching the text + job filters
    private IEnumerable<uint> VisibleMissions(uint zoneId)
    {
        var jobs = _jobsFilter ? JobFilterSet() : null;
        return MissionData.Missions
            .Where(kv => kv.Value.TerritoryId == zoneId && kv.Value.IsCraftOnly)
            .Where(kv => jobs == null || kv.Value.Jobs.Any(jobs.Contains))
            .Where(kv => _search.Length == 0
                || kv.Value.Name.Contains(_search, StringComparison.OrdinalIgnoreCase)
                || kv.Key.ToString().Contains(_search))
            .OrderBy(kv => kv.Value.Rank).ThenBy(kv => kv.Key)
            .Select(kv => kv.Key);
    }

    private HashSet<uint> JobFilterSet()
    {
        var set = _owner.Config.JobPriority.Where(CosmicJobs.IsCrafter).ToHashSet();
        if (set.Count == 0)
        {
            var current = Dalamud.Objects.LocalPlayer?.ClassJob.RowId ?? 0;
            if (current != 0)
                set.Add(current);
        }
        return set;
    }

    // "empty set = all on", so make it explicit before removing entries
    private static void EnsureExplicitEnabledSet(DeimosConfig cfg)
    {
        if (cfg.EnabledMissions.Count > 0)
            return;
        foreach (var (id, info) in MissionData.Missions)
            if (info.IsCraftOnly)
                cfg.EnabledMissions.Add(id);
    }

    private static string RankLabel(uint rank) => rank switch
    {
        1 => "D",
        2 => "C",
        3 => "B",
        4 => "A",
        5 => "EX",
        6 => "M",
        _ => rank.ToString(),
    };

    private static string Roman(int type) => type switch
    {
        1 => "I",
        2 => "II",
        3 => "III",
        4 => "IV",
        5 => "V",
        6 => "VI",
        _ => type.ToString(),
    };
}
