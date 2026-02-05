using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Component.GUI;
using ValueType = FFXIVClientStructs.FFXIV.Component.GUI.ValueType;

namespace GatherBuddy.Automation;

public unsafe class AtkReader
{
    protected readonly AtkUnitBase* addon;
    protected readonly int beginOffset;

    public AtkReader(IntPtr ptr, int beginOffset = 0)
    {
        this.addon = (AtkUnitBase*)ptr;
        this.beginOffset = beginOffset;
    }

    public AtkReader(AtkUnitBase* addon, int beginOffset = 0)
    {
        this.addon = addon;
        this.beginOffset = beginOffset;
    }

    public bool IsNull => addon == null || addon->AtkValuesCount == 0;

    protected uint? ReadUInt(int index)
    {
        var actualIndex = beginOffset + index;
        if (addon == null || actualIndex < 0 || actualIndex >= addon->AtkValuesCount)
            return null;

        var value = addon->AtkValues[actualIndex];
        return value.Type switch
        {
            ValueType.UInt => value.UInt,
            ValueType.Int => (uint)value.Int,
            _ => null
        };
    }

    protected bool? ReadBool(int index)
    {
        var actualIndex = beginOffset + index;
        if (addon == null || actualIndex < 0 || actualIndex >= addon->AtkValuesCount)
            return null;

        var value = addon->AtkValues[actualIndex];
        return value.Type switch
        {
            ValueType.Bool => value.Bool,
            ValueType.Int => value.Int != 0,
            ValueType.UInt => value.UInt != 0,
            _ => null
        };
    }

    protected SeString ReadSeString(int index)
    {
        var actualIndex = beginOffset + index;
        if (addon == null || actualIndex < 0 || actualIndex >= addon->AtkValuesCount)
            return SeString.Empty;

        var value = addon->AtkValues[actualIndex];
        if (value.Type == ValueType.String && value.String != null)
        {
            var ptr = (byte*)value.String;
            return SeString.Parse(ptr);
        }

        return SeString.Empty;
    }

    protected IEnumerable<T> Loop<T>(int startOffset, int stride, int count) where T : class
    {
        return Enumerable.Range(0, count)
            .Select(i =>
            {
                var offset = startOffset + i * stride;
                // Null should never occur in current usage (GatheringReader with 8 ItemSlotReaders).
                // Fail-fast with NotImplementedException rather than silently skip if behavior changes,
                // as handling null is not implemented.
                return Activator.CreateInstance(typeof(T), (IntPtr)addon, offset) as T ?? throw new NotImplementedException();
            });
    }
}
