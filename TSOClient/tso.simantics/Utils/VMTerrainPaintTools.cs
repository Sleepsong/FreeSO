using FSO.Content.Model;
using FSO.SimAntics.Model;
using Microsoft.Xna.Framework;
using System;

namespace FSO.SimAntics.Utils
{
    /// <summary>
    /// Terrain painting, as in The Sims 2, with the game's own terrain types: TERRAIN_PAINT paints a round brush of one
    /// type onto the ground (VMArchitectureTerrain.Paint), or takes the paint off again so the lot's own ground shows.
    /// </summary>
    public static class VMTerrainPaintTools
    {
        /// <summary>The terrain types there are to paint with (the game's, except water).</summary>
        public static readonly TerrainType[] PaintTypes =
        {
            TerrainType.GRASS,
            TerrainType.TS1DarkGrass,
            TerrainType.TS1AutumnGrass,
            TerrainType.SAND,
            TerrainType.ROCK,
            TerrainType.SNOW,
            TerrainType.TS1Cloud,
        };

        public static string Name(TerrainType type)
        {
            switch (type)
            {
                case TerrainType.GRASS: return "Grass";
                case TerrainType.TS1DarkGrass: return "Dark Grass";
                case TerrainType.TS1AutumnGrass: return "Autumn Grass";
                case TerrainType.SAND: return "Sand";
                case TerrainType.ROCK: return "Rock";
                case TerrainType.SNOW: return "Snow";
                case TerrainType.TS1Cloud: return "Cloud";
                default: return type.ToString();
            }
        }

        public const byte Erase = VMArchitectureTerrain.Unpainted;
        /// <summary>The biggest brush (radius in tiles).</summary>
        public const int MaxRadius = 6;

        /// <summary>Paints for real (or for a preview): the number of points changed, which is also the cost.</summary>
        public static int Paint(VMArchitecture arch, VMArchitectureCommand com)
        {
            var bounds = arch.DisableClip ? new Rectangle(0, 0, arch.Width, arch.Height) : arch.BuildableArea;
            bounds.X--; bounds.Y--; bounds.Width++; bounds.Height++;
            var changed = Apply(arch.Terrain, bounds, com);
            if (changed > 0) arch.SignalTerrainPaint();
            return changed;
        }

        /// <summary>
        /// Paints <paramref name="com"/>'s brush onto <paramref name="terrain"/>, within <paramref name="bounds"/> (in
        /// paint points, laid out like GrassState): the number of points changed.
        /// </summary>
        public static int Apply(VMArchitectureTerrain terrain, Rectangle bounds, VMArchitectureCommand com)
        {
            var type = (byte)Math.Min(255, (int)com.pattern);
            if (type != Erase && Array.IndexOf(PaintTypes, (TerrainType)type) == -1) return 0;
            if (type == Erase && terrain.Paint == null) return 0; //nothing painted to take off
            var radius = Math.Max(0, Math.Min(MaxRadius, com.x2));
            var soft = com.style != 0;

            var paint = (type == Erase) ? terrain.Paint : terrain.EnsurePaint();
            var limit = (radius + 0.5f) * (radius + 0.5f);
            int count = 0;
            for (int y = com.y - radius; y <= com.y + radius; y++)
            {
                for (int x = com.x - radius; x <= com.x + radius; x++)
                {
                    if (x < 0 || y < 0 || x >= terrain.Width || y >= terrain.Height || !bounds.Contains(x, y)) continue;
                    var dx = x - com.x;
                    var dy = y - com.y;
                    var d2 = dx * dx + dy * dy;
                    if (d2 > limit) continue;
                    //a soft brush paints only some of the points around its edge, so the paint feathers into what's there
                    if (soft && radius > 0 && d2 > (radius - 0.5f) * (radius - 0.5f) && (Hash(x, y) & 1) == 0) continue;
                    var off = y * terrain.Width + x;
                    if (paint[off] == type) continue;
                    paint[off] = type;
                    count++;
                }
            }
            if (type == Erase && !terrain.AnyPaint) terrain.Paint = null; //all off again: no layer
            return count;
        }

        /// <summary>A fixed pattern (the same on every machine) for the soft brush's edge.</summary>
        private static int Hash(int x, int y)
        {
            unchecked
            {
                var h = x * 374761393 + y * 668265263;
                h = (h ^ (h >> 13)) * 1274126177;
                return h ^ (h >> 16);
            }
        }
    }
}
