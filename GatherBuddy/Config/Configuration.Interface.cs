using GatherBuddy.RebornUI;
using RebornMaterial;
using System.Numerics;

namespace GatherBuddy.Config;

public partial class Configuration : IM3Settings
{
    public Vector4 UiAccentColor { get; set; } = MaterialUI.DefaultAccent;
    public float UiTextScale { get; set; } = 1f;
    public float UiElementScale { get; set; } = 1f;
    public float UiPaddingScale { get; set; } = 1f;
    public float UiMotionScale { get; set; } = 1f;
    public bool UiCompactLayout { get; set; } = false;
}
