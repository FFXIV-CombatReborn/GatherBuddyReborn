using System.Collections.Generic;
using System.Numerics;

namespace GatherBuddy.Deimos.Data;

/// red-alert site coords + which option to pick at the hub teleporter npc, keyed by mission MarkerId
internal static class CriticalSites
{
    public readonly record struct Site(Vector3 Spot, int NpcChoice);

    public static bool TryGet(uint markerId, out Site site) => Sites.TryGetValue(markerId, out site);

    private static readonly Dictionary<uint, Site> Sites = new()
    {
        // Sinus: astromagnetic storms
        [40] = new(new(176.24f, 9.40f, 560.07f), 0),
        [41] = new(new(-91.58f, 19.32f, -241.99f), 1),
        [38] = new(new(-72.76f, 51.00f, 768.64f), 0),
        [39] = new(new(-464.50f, 37.89f, -69.89f), 1),
        // Sinus: meteor showers
        [42] = new(new(-219.93f, 24.16f, 209.98f), 0),
        [43] = new(new(34.86f, 34.38f, -349.75f), 1),
        [44] = new(new(845.90f, -58.44f, -390.45f), 0),
        [45] = new(new(497.36f, -115.32f, -845.65f), 1),
        // Sinus: sporing mist
        [48] = new(new(539.43f, 36.38f, 49.89f), 0),
        [49] = new(new(654.32f, 52.00f, 100.13f), 1),
        [46] = new(new(379.62f, 51.39f, 704.14f), 0),
        [47] = new(new(99.66f, 18.11f, -209.80f), 1),

        // Phaenna: thunderstorms
        [83] = new(new(417.57f, 52.00f, -445.41f), 0),
        [84] = new(new(432.76f, 54.13f, -169.80f), 1),
        [85] = new(new(169.79f, 41.00f, -210.79f), 0),
        [86] = new(new(-615.30f, 8.26f, -515.45f), 1),
        // Phaenna: annealing winds
        [87] = new(new(239.77f, 133.83f, -704.44f), 0),
        [88] = new(new(-506.27f, -8.42f, -751.29f), 1),
        [90] = new(new(410.29f, 18.90f, 25.14f), 1),
        [89] = new(new(10.10f, 7.98f, 339.70f), 0),
        // Phaenna: glass rain
        [91] = new(new(407.15f, -229.45f, 224.76f), 0),
        [92] = new(new(544.42f, -251.07f, 634.55f), 1),
        [93] = new(new(148.96f, -9.99f, 487.46f), 0),
        [94] = new(new(-488.32f, 25.05f, 35.65f), 1),

        // Oizys: gravitational anomaly
        [101] = new(new(77.05f, -58.69f, -475.25f), 0),
        [102] = new(new(-189.71f, -0.07f, -61.36f), 1),
        // Oizys: gale force
        [105] = new(new(584.29f, -60.42f, -429.47f), 0),
        [106] = new(new(125.37f, 0.64f, -68.72f), 1),
        [107] = new(new(-669.31f, -88.50f, -453.18f), 0),
        [108] = new(new(-127.96f, 0.27f, -50.00f), 1),
        // Oizys: bubble bloom
        [103] = new(new(-572.10f, 22.70f, 156.11f), 0),
        [104] = new(new(-369.71f, 104.98f, 876.68f), 1),

        // Auxesia: auroral flare
        [185] = new(new(-37.54f, 185.10f, 352.42f), 0),
        [186] = new(new(-660.01f, 184.99f, 292.33f), 1),
        [187] = new(new(13.88f, 165.01f, 124.97f), 0),
        [188] = new(new(300.00f, 165.10f, 13.52f), 1),
        // Auxesia: floracane
        [189] = new(new(-367.00f, 146.96f, 426.96f), 0),
        [190] = new(new(-235.59f, 145.15f, -504.55f), 1),
        [191] = new(new(739.87f, 184.28f, 514.36f), 0),
        [192] = new(new(-353.96f, 165.10f, 226.09f), 1),
    };
}
