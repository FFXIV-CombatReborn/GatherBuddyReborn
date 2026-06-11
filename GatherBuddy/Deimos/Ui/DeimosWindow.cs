using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using GatherBuddy.Deimos.Data;

namespace GatherBuddy.Deimos.Ui;

/// standalone Deimos window (opened via /deimos), intentionally not linked from the GBR UI yet
public sealed class DeimosWindow : Window
{
    private readonly Deimos _owner;
    private string _search = "";
    private int    _zoneIdx = -1; // -1 = follow current zone
    private bool   _jobsFilter;   // limit table to configured jobs (or current job)

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
            MinimumSize = new Vector2(620, 420),
            MaximumSize = new Vector2(1400, 1200),
        };
    }

    public override void Draw()
    {
        DrawStatus();
        ImGui.Separator();
        DrawSettings();
        ImGui.Separator();
        DrawMissionTable();
    }

    private void DrawStatus()
    {
        var cfg = _owner.Config;

        if (_owner.Enabled)
        {
            if (ImGui.Button("Stop", new Vector2(80, 0)))
                _owner.Disable();
        }
        else
        {
            if (ImGui.Button("Start", new Vector2(80, 0)))
                _owner.Enable();
        }

        ImGui.SameLine();
        var zone = CosmicZone.InCosmicZone ? CosmicZone.Name(CosmicZone.Current) : "not in a cosmic zone";
        ImGui.TextUnformatted($"State: {_owner.State} | {zone} | Missions: {_owner.MissionsDone}");

        var missionId = Wks.CurrentMissionId;
        if (missionId != 0 && MissionData.TryGet(missionId, out var mission))
        {
            ImGui.SameLine();
            ImGui.TextUnformatted($"| Mission: {mission.Name}");
        }
    }

    private void DrawSettings()
    {
        var cfg   = _owner.Config;
        var dirty = false;

        // mode
        ImGui.SetNextItemWidth(180);
        if (ImGui.BeginCombo("Mode", cfg.Mode.ToString()))
        {
            foreach (var mode in new[] { DeimosMode.Standard, DeimosMode.MissionGold, DeimosMode.Leveling })
            {
                if (ImGui.Selectable(mode.ToString(), cfg.Mode == mode))
                {
                    cfg.Mode = mode;
                    dirty    = true;
                }
            }
            ImGui.EndCombo();
        }

        var skipExpert = cfg.SkipExpertCrafts;
        if (ImGui.Checkbox("Skip expert crafts", ref skipExpert))
        {
            cfg.SkipExpertCrafts = skipExpert;
            dirty = true;
        }

        ImGui.SameLine();
        var provisionals = cfg.IncludeProvisionals;
        if (ImGui.Checkbox("Provisionals", ref provisionals))
        {
            cfg.IncludeProvisionals = provisionals;
            dirty = true;
        }

        ImGui.SameLine();
        var criticals = cfg.IncludeCriticals;
        if (ImGui.Checkbox("Criticals", ref criticals))
        {
            cfg.IncludeCriticals = criticals;
            dirty = true;
        }

        ImGui.SameLine();
        var mastery = cfg.IncludeMastery;
        if (ImGui.Checkbox("Mastery", ref mastery))
        {
            cfg.IncludeMastery = mastery;
            dirty = true;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Include Auxesia tool-mastery (rank M) missions.");

        ImGui.SameLine();
        var reroll = cfg.AutoReroll;
        if (ImGui.Checkbox("Auto reroll", ref reroll))
        {
            cfg.AutoReroll = reroll;
            dirty = true;
        }

        if (cfg.AutoReroll)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(90);
            var max = cfg.MaxRerolls;
            if (ImGui.InputInt("Max##rerolls", ref max))
            {
                cfg.MaxRerolls = System.Math.Clamp(max, 1, 20);
                dirty = true;
            }
        }

        // job priority - checked jobs run in crafter order; none = current job only
        ImGui.TextUnformatted("Jobs:");
        foreach (var job in CosmicJobs.Crafters)
        {
            ImGui.SameLine();
            var on = cfg.JobPriority.Contains(job);
            if (ImGui.Checkbox(CosmicJobs.Name(job), ref on))
            {
                if (on)
                    cfg.JobPriority.Add(job);
                else
                    cfg.JobPriority.Remove(job);
                dirty = true;
            }
        }
        ImGui.SameLine();
        ImGui.TextDisabled("(none = current job)");

        // upkeep + stop conditions
        var repair = cfg.AutoRepair;
        if (ImGui.Checkbox("Self-repair", ref repair))
        {
            cfg.AutoRepair = repair;
            dirty = true;
        }

        if (cfg.AutoRepair)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(90);
            var threshold = cfg.RepairThreshold;
            if (ImGui.InputInt("%##repair", ref threshold))
            {
                cfg.RepairThreshold = System.Math.Clamp(threshold, 1, 99);
                dirty = true;
            }

            ImGui.SameLine();
            var vendor = cfg.RepairAtVendor;
            if (ImGui.Checkbox("At vendor", ref vendor))
            {
                cfg.RepairAtVendor = vendor;
                dirty = true;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Stellar Return to the hub and repair at the npc instead of using dark matter.");
        }

        ImGui.SameLine();
        var materia = cfg.AutoExtractMateria;
        if (ImGui.Checkbox("Extract materia", ref materia))
        {
            cfg.AutoExtractMateria = materia;
            dirty = true;
        }

        ImGui.SameLine();
        var gamba = cfg.AutoGamba;
        if (ImGui.Checkbox("Gamba", ref gamba))
        {
            cfg.AutoGamba = gamba;
            dirty = true;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Spend lunar credits on the Cosmic Fortune wheel between missions.");

        if (cfg.AutoGamba)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(90);
            var keep = cfg.GambaKeepLunarCredits;
            if (ImGui.InputInt("keep##gamba", ref keep))
            {
                cfg.GambaKeepLunarCredits = System.Math.Max(0, keep);
                dirty = true;
            }
        }

        ImGui.TextUnformatted("Stop after:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(90);
        var stopMissions = cfg.StopAfterMissions;
        if (ImGui.InputInt("missions##stop", ref stopMissions))
        {
            cfg.StopAfterMissions = System.Math.Max(0, stopMissions);
            dirty = true;
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(90);
        var stopLunar = cfg.StopAtLunarCredits;
        if (ImGui.InputInt("lunar##stop", ref stopLunar))
        {
            cfg.StopAtLunarCredits = System.Math.Max(0, stopLunar);
            dirty = true;
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(90);
        var stopCosmo = cfg.StopAtCosmoCredits;
        if (ImGui.InputInt("cosmo##stop", ref stopCosmo))
        {
            cfg.StopAtCosmoCredits = System.Math.Max(0, stopCosmo);
            dirty = true;
        }

        ImGui.SameLine();
        ImGui.TextDisabled("(0 = off)");

        if (dirty)
            cfg.Save();
    }

    private void DrawMissionTable()
    {
        var cfg = _owner.Config;

        // zone picker, defaulting to where we're standing
        var zoneId = _zoneIdx >= 0 ? Zones[_zoneIdx].Id
            : CosmicZone.InCosmicZone ? CosmicZone.Current : Zones[0].Id;

        ImGui.SetNextItemWidth(160);
        if (ImGui.BeginCombo("##zone", CosmicZone.Name(zoneId)))
        {
            for (var i = 0; i < Zones.Length; i++)
                if (ImGui.Selectable(Zones[i].Name, Zones[i].Id == zoneId))
                    _zoneIdx = i;
            ImGui.EndCombo();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(220);
        ImGui.InputTextWithHint("##search", "filter...", ref _search, 64);

        ImGui.SameLine();
        if (_jobsFilter)
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.2f, 0.5f, 0.2f, 1f));
        if (ImGui.Button("My jobs"))
            _jobsFilter = !_jobsFilter;
        if (_jobsFilter)
            ImGui.PopStyleColor();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Show only missions for the selected jobs (or your current job if none are selected).");

        ImGui.SameLine();
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

        if (!ImGui.BeginTable("##missions", 6,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Run", ImGuiTableColumnFlags.WidthFixed, 36);
        ImGui.TableSetupColumn("Id", ImGuiTableColumnFlags.WidthFixed, 48);
        ImGui.TableSetupColumn("Mission", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Rank", ImGuiTableColumnFlags.WidthFixed, 44);
        ImGui.TableSetupColumn("Research", ImGuiTableColumnFlags.WidthFixed, 130);
        ImGui.TableSetupColumn("Gold", ImGuiTableColumnFlags.WidthFixed, 44);
        ImGui.TableHeadersRow();

        var inZone = Wks.Available;
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
            ImGui.TextUnformatted(id.ToString());

            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{info.Name} ({string.Join("/", info.Jobs.Select(CosmicJobs.Name))})");

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(RankLabel(info.Rank));

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(string.Join(" ", info.RelicXpInfo.Select(x => $"{Roman(x.Key)}:{x.Value}")));

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(inZone && Wks.IsMissionGolded(id) ? "★" : "");
        }

        ImGui.EndTable();
    }

    // craft-only missions for the zone matching the text + job filters
    private System.Collections.Generic.IEnumerable<uint> VisibleMissions(uint zoneId)
    {
        var jobs = _jobsFilter ? JobFilterSet() : null;
        return MissionData.Missions
            .Where(kv => kv.Value.TerritoryId == zoneId && kv.Value.IsCraftOnly)
            .Where(kv => jobs == null || kv.Value.Jobs.Any(jobs.Contains))
            .Where(kv => _search.Length == 0
                || kv.Value.Name.Contains(_search, System.StringComparison.OrdinalIgnoreCase)
                || kv.Key.ToString().Contains(_search))
            .OrderBy(kv => kv.Value.Rank).ThenBy(kv => kv.Key)
            .Select(kv => kv.Key);
    }

    private System.Collections.Generic.HashSet<uint> JobFilterSet()
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
