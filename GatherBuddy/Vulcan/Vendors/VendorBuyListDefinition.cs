using System;
using System.Collections.Generic;

namespace GatherBuddy.Vulcan.Vendors;

public sealed class VendorBuyListDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<VendorBuyListEntry> Entries { get; set; } = new();
}
