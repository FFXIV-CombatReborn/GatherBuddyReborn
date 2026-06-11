using GatherBuddy.Crafting;

namespace GatherBuddy.Deimos.Scheduling;

/// between-mission upkeep: self-repair (dark matter) and materia extraction,
/// reusing GBR's existing TaskResult-based crafting tasks
internal sealed class MaintenanceRunner
{
    private enum Step
    {
        RepairOpen,
        RepairRun,
        RepairClose,
        Materia,
        Done,
    }

    private Step _step = Step.Done;

    public void Begin()
    {
        _step = Step.RepairOpen;
        CraftingTasks.ResetRepairState();
    }

    /// tick each frame; true when upkeep is finished
    public bool Update(DeimosConfig config)
    {
        switch (_step)
        {
            case Step.RepairOpen:
                if (!config.AutoRepair
                 || config.RepairAtVendor // hub trip handles it
                 || !RepairManager.NeedsRepair(config.RepairThreshold)
                 || !RepairManager.CanRepairAny(config.RepairThreshold))
                {
                    _step = Step.Materia;
                    return false;
                }

                switch (CraftingTasks.TaskOpenRepairWindow())
                {
                    case CraftingTasks.TaskResult.Done:  _step = Step.RepairRun; break;
                    case CraftingTasks.TaskResult.Abort: _step = Step.Materia; break;
                }
                return false;

            case Step.RepairRun:
                if (CraftingTasks.TaskExecuteRepair(isSelfRepair: true) != CraftingTasks.TaskResult.Retry)
                    _step = Step.RepairClose;
                return false;

            case Step.RepairClose:
                if (CraftingTasks.TaskCloseRepairWindow() != CraftingTasks.TaskResult.Retry)
                {
                    DeimosLog.Info("Gear repaired.");
                    _step = Step.Materia;
                }
                return false;

            case Step.Materia:
                if (!config.AutoExtractMateria
                 || !MateriaManager.IsExtractionUnlocked()
                 || !MateriaManager.IsSpiritbondReadyAny()
                 || !MateriaManager.HasFreeInventorySlots())
                {
                    _step = Step.Done;
                    return true;
                }

                if (CraftingTasks.TaskExtractAllMateria() != CraftingTasks.TaskResult.Retry)
                {
                    DeimosLog.Info("Materia extracted.");
                    _step = Step.Done;
                    return true;
                }
                return false;

            default:
                return true;
        }
    }
}
