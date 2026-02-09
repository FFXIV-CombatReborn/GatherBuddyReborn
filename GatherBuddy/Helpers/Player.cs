using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Statuses;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace GatherBuddy.Helpers;

public static unsafe class Player
{
    public static IPlayerCharacter? Object => Dalamud.Objects.LocalPlayer;
    [MemberNotNullWhen(true, nameof(Object))]
    public static bool Available => Dalamud.Objects.LocalPlayer != null;
    
    public static IReadOnlyCollection<IStatus> Status => Dalamud.Objects.LocalPlayer?.StatusList ?? (IReadOnlyCollection<IStatus>)[];
    public static int Level => Dalamud.Objects.LocalPlayer?.Level ?? 0;
    public static uint Job => Dalamud.Objects.LocalPlayer?.ClassJob.RowId ?? 0;
    
    public static Vector3 Position => Available ? Object!.Position : Vector3.Zero;
    public static float Rotation => Available ? Object!.Rotation : 0;
    public static string? CurrentWorld => Dalamud.Objects.LocalPlayer?.CurrentWorld.ValueNullable?.Name.ToString();
    public static uint Territory => Dalamud.ClientState.TerritoryType;
    public static bool Interactable => Available && Object!.IsTargetable;
    
    public static float AnimationLock => *(float*)((nint)ActionManager.Instance() + 8);
    public static bool IsAnimationLocked => AnimationLock > 0;
}
