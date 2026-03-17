namespace GatherBuddy.Vulcan;

public interface IActionExecutor
{
    bool CanExecuteAction(VulcanSkill action, CraftState craft, StepState step, string outReason = "");
    System.Threading.Tasks.Task<bool> TryExecuteActionAsync(VulcanSkill action);
}

public static class ActionExecutor
{
    private static IActionExecutor? _instance;

    public static void SetExecutor(IActionExecutor executor)
    {
        _instance = executor;
    }

    public static bool CanExecute(VulcanSkill action, CraftState craft, StepState step)
    {
        if (!Simulator.CanUseAction(craft, step, action))
            return false;

        if (_instance == null)
            return true;

        return _instance.CanExecuteAction(action, craft, step);
    }

    public static async System.Threading.Tasks.Task<bool> TryExecuteAsync(VulcanSkill action)
    {
        if (_instance == null)
        {
            return false;
        }

        return await _instance.TryExecuteActionAsync(action);
    }

    public static void ClearExecutor()
    {
        _instance = null;
    }
}
