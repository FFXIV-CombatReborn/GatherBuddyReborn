using GatherBuddy.Helpers;
using System.Numerics;

namespace GatherBuddy.CustomInfo
{
    public static class VectorExtensions
    {
        public static float DistanceToPlayer(this Vector3 vector)
        {
            var distance = Vector3.Distance(vector, Player.Position);
            return distance;
        }
    }
}
