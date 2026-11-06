using FSO.LotView.Model;
using FSO.SimAntics.Entities;
using Microsoft.Xna.Framework;

namespace FSO.SimAntics.Utils
{
    /// <summary>
    /// Digging out and filling in the basement (level 0; see <see cref="BasementMap"/>), for the build mode command.
    /// Results are counts for pricing: positive for tiles dug, negative for tiles filled. All or nothing.
    /// </summary>
    public static class VMBasementTools
    {
        /// <summary>LastFailReason when something is in the way (furniture on bare ground, a pool).</summary>
        public const int FailBlocked = 20;
        /// <summary>LastFailReason when filling in a part of the basement with something (or someone) in it.</summary>
        public const int FailOccupied = 21;

        /// <summary>
        /// Digs out every tile of <paramref name="rect"/> (x, y, and width and height less one: the tiles from corner to
        /// corner), or fills them in. A dug tile gets <paramref name="floor"/> as its floor, and so does the tile above it
        /// if it has none (the basement's ceiling); the earth's sides round it get <paramref name="covering"/> (a wall
        /// pattern). Filling in takes the basement's floors there away, and its walls that are left with earth on both
        /// sides; the ground floor above keeps its floor.
        /// </summary>
        public static int Dig(VMArchitecture arch, Rectangle rect, bool dig, ushort floor, ushort covering)
        {
            var map = arch.Terrain.Basement;
            if (map == null) return 0;
            int x1 = rect.X, y1 = rect.Y, x2 = rect.X + rect.Width, y2 = rect.Y + rect.Height;
            if (x2 < x1) { var t = x1; x1 = x2; x2 = t; }
            if (y2 < y1) { var t = y1; y1 = y2; y2 = t; }
            if (floor == 0 || floor > 65533) floor = 1;

            //check every tile first
            int changes = 0;
            for (int y = y1; y <= y2; y++)
            {
                for (int x = x1; x <= x2; x++)
                {
                    if (!Buildable(arch, x, y)) { arch.LastFailReason = FailBlocked; return 0; }
                    var dug = map.IsDug(x, y);
                    if (dig)
                    {
                        if (dug) continue;
                        var above = arch.GetFloor((short)x, (short)y, 1).Pattern;
                        //no pool over a basement; things on bare ground may need it (plants), so clear them first; and not
                        //under a foundation or its steps (yet: the earth's sides would have to follow its height)
                        var foundations = arch.Terrain.Foundations;
                        var underFoundation = foundations != null && (foundations.RaiseOf(x, y) != 0 || foundations.StepDirection(x, y) >= 0);
                        if (above > 65533 || (above == 0 && HasObjects(arch, x, y, 1)) || underFoundation) { arch.LastFailReason = FailBlocked; return 0; }
                        changes++;
                    }
                    else
                    {
                        if (!dug) continue;
                        if (HasObjects(arch, x, y, 0, true)) { arch.LastFailReason = FailOccupied; return 0; }
                        changes++;
                    }
                }
            }
            if (changes == 0) return 0;

            var size = arch.Width;
            for (int y = y1; y <= y2; y++)
            {
                for (int x = x1; x <= x2; x++)
                {
                    var i = y * size + x;
                    if (dig)
                    {
                        if (map.Dug[i] != 0) continue;
                        map.Dug[i] = 1;
                        map.Pattern[i] = covering;
                        arch.Floors[-1][i] = new FloorTile { Pattern = floor };
                        if (arch.Floors[0][i].Pattern == 0) arch.Floors[0][i] = new FloorTile { Pattern = floor };
                    }
                    else
                    {
                        if (map.Dug[i] == 0) continue;
                        map.Dug[i] = 0;
                        map.Pattern[i] = 0;
                        arch.Floors[-1][i] = new FloorTile();
                    }
                }
            }

            if (!dig)
            {
                //walls left with earth on both sides go (the filled tiles' own, and the edges with the tiles round them)
                for (int y = y1; y <= y2 + 1; y++)
                    for (int x = x1; x <= x2 + 1; x++)
                    {
                        if (!map.InBounds(x, y)) continue;
                        var wall = arch.GetWall((short)x, (short)y, 0);
                        if (wall.Segments == 0) continue;
                        var keep = wall.Segments;
                        if ((keep & WallSegments.TopLeft) != 0 && !map.IsDug(x, y) && !map.IsDug(x - 1, y)) keep &= ~WallSegments.TopLeft;
                        if ((keep & WallSegments.TopRight) != 0 && !map.IsDug(x, y) && !map.IsDug(x, y - 1)) keep &= ~WallSegments.TopRight;
                        if ((keep & WallSegments.AnyDiag) != 0 && !map.IsDug(x, y)) keep &= ~WallSegments.AnyDiag;
                        if (keep == wall.Segments) continue;
                        if (keep == 0) wall = new WallTile();
                        else wall.Segments = keep;
                        arch.SetWall((short)x, (short)y, 0, wall);
                    }
            }

            arch.SignalBasementChange();
            return dig ? changes : -changes;
        }

        private static bool Buildable(VMArchitecture arch, int x, int y)
        {
            if (x < 1 || y < 1 || x >= arch.Width - 1 || y >= arch.Height - 1) return false;
            return !arch.OutsideClip((short)x, (short)y, 1);
        }

        /// <summary>Things on the tile at a level: furniture, and with <paramref name="avatars"/>, Sims too.</summary>
        private static bool HasObjects(VMArchitecture arch, int x, int y, sbyte level, bool avatars = false)
        {
            var objs = arch.Context.ObjectQueries.GetObjectsAt(LotTilePos.FromBigTile((short)x, (short)y, level));
            if (objs == null) return false;
            foreach (var obj in objs)
            {
                if (obj is VMGameObject || avatars) return true;
            }
            return false;
        }
    }
}
