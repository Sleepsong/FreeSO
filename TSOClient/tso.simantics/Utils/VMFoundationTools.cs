using FSO.LotView.Model;
using FSO.SimAntics.Entities;
using Microsoft.Xna.Framework;
using System;

namespace FSO.SimAntics.Utils
{
    /// <summary>
    /// Building and removing foundations and their steps (see <see cref="FoundationMap"/>), for the build mode commands.
    /// As in The Sims 2, a foundation goes on empty ground: no furniture on it and no walls on or around its tiles (build
    /// those after). Results are counts for pricing: positive for tiles built, negative for tiles removed.
    /// </summary>
    public static class VMFoundationTools
    {
        /// <summary>LastFailReason when something is in the way.</summary>
        public const int FailBlocked = 10;
        /// <summary>LastFailReason when steps have no foundation to lead up to.</summary>
        public const int FailNoFoundation = 11;

        /// <summary>
        /// Raises every tile of <paramref name="rect"/> (x, y, and width and height less one: the tiles from corner to
        /// corner) to <paramref name="height"/>, or removes foundations there for 0. All or nothing.
        /// </summary>
        public static int PlaceFoundation(VMArchitecture arch, Rectangle rect, short height, ushort pattern)
        {
            var map = arch.Terrain.Foundations;
            if (map == null || height < 0) return 0;
            int x1 = rect.X, y1 = rect.Y, x2 = rect.X + rect.Width, y2 = rect.Y + rect.Height;
            if (x2 < x1) { var t = x1; x1 = x2; x2 = t; }
            if (y2 < y1) { var t = y1; y1 = y2; y2 = t; }

            //check every tile first
            int changes = 0;
            for (int y = y1; y <= y2; y++)
            {
                for (int x = x1; x <= x2; x++)
                {
                    if (!Buildable(arch, x, y)) { arch.LastFailReason = FailBlocked; return 0; }
                    var i = y * map.Width + x;
                    var raise = map.Raise[i];
                    if (height > 0)
                    {
                        if (raise == height || map.Steps[i] != 0) continue;
                        if (Blocked(arch, x, y) || HasWall(arch, x, y, x1, y1, x2, y2) || IsPool(arch, x, y)
                            || arch.Basement?.IsDug(x, y) == true) { arch.LastFailReason = FailBlocked; return 0; } //(not over a basement, yet)
                        changes++;
                    }
                    else
                    {
                        if (raise == 0) continue;
                        if (Blocked(arch, x, y) || HasWall(arch, x, y, x1, y1, x2, y2)) { arch.LastFailReason = FailBlocked; return 0; }
                        changes++;
                    }
                }
            }
            if (changes == 0) return 0;

            for (int y = y1; y <= y2; y++)
            {
                for (int x = x1; x <= x2; x++)
                {
                    var i = y * map.Width + x;
                    if (map.Steps[i] != 0) continue;
                    if (height > 0)
                    {
                        if (map.Raise[i] == height) continue;
                        map.Raise[i] = height;
                        map.SetAllSides(i, pattern);
                    }
                    else
                    {
                        map.Raise[i] = 0;
                        map.SetAllSides(i, 0);
                    }
                }
            }

            if (height == 0)
            {
                //steps left leading up to nothing go too
                for (int y = y1 - 1; y <= y2 + 1; y++)
                    for (int x = x1 - 1; x <= x2 + 1; x++)
                        if (map.InBounds(x, y) && map.Steps[y * map.Width + x] != 0 && map.StepTop(x, y) == 0)
                            map.Steps[y * map.Width + x] = 0;
            }

            arch.SignalFoundationChange();
            return (height > 0) ? changes : -changes;
        }

        /// <summary>
        /// Puts steps on a ground tile, rising toward <paramref name="direction"/> (0-3, see
        /// <see cref="FoundationMap.Directions"/>; 255 for whichever side has a foundation), or removes them.
        /// </summary>
        public static int PlaceFoundationSteps(VMArchitecture arch, Point tile, ushort direction, bool place)
        {
            var map = arch.Terrain.Foundations;
            if (map == null || !Buildable(arch, tile.X, tile.Y)) return 0;
            var i = tile.Y * map.Width + tile.X;

            if (!place)
            {
                if (map.Steps[i] == 0) return 0;
                if (Blocked(arch, tile.X, tile.Y)) { arch.LastFailReason = FailBlocked; return 0; }
                map.Steps[i] = 0;
                arch.SignalFoundationChange();
                return -1;
            }

            if (map.Raise[i] != 0) { arch.LastFailReason = FailBlocked; return 0; }
            int dir = (direction < 4) ? direction : FindFoundationBeside(map, tile.X, tile.Y);
            if (dir < 0) { arch.LastFailReason = FailNoFoundation; return 0; }
            var to = FoundationMap.Directions[dir];
            var topRaise = map.RaiseOf(tile.X + to.X, tile.Y + to.Y);
            if (topRaise <= 0) { arch.LastFailReason = FailNoFoundation; return 0; }
            if (map.Steps[i] == dir + 1) return 0;
            if (Blocked(arch, tile.X, tile.Y) || IsPool(arch, tile.X, tile.Y) || arch.Basement?.IsDug(tile.X, tile.Y) == true
                || arch.GetWall((short)tile.X, (short)tile.Y, 1).Segments != 0) { arch.LastFailReason = FailBlocked; return 0; }

            var wasSteps = map.Steps[i] != 0;
            map.Steps[i] = (byte)(dir + 1);
            arch.SignalFoundationChange();
            return wasSteps ? 0 : 1;
        }

        /// <summary>The side direction a paint command gives for every side of the foundation a tile is part of.</summary>
        public const int PaintWholeFoundation = 255;

        /// <summary>
        /// Paints a wall covering (0 for plain concrete) on a foundation's side, as walls are painted: side
        /// <paramref name="dir"/> (see <see cref="FoundationMap.Directions"/>) of the tile, or with
        /// <see cref="PaintWholeFoundation"/> every showing side of the foundation the tile is part of (all the raised tiles
        /// joined to it). Only sides that show can be painted. Returns how many sides changed.
        /// </summary>
        public static int PaintSides(VMArchitecture arch, Point tile, int dir, ushort pattern)
        {
            var map = arch.Terrain.Foundations;
            if (map == null || !map.InBounds(tile.X, tile.Y) || map.RaiseOf(tile.X, tile.Y) <= 0) return 0;
            int changed = 0;
            if (dir != PaintWholeFoundation)
            {
                if (dir < 0 || dir > 3 || !map.SideShows(tile.X, tile.Y, dir)) return 0;
                var index = map.SideIndex(tile.X, tile.Y, dir);
                if (map.Sides[index] == pattern) return 0;
                map.Sides[index] = pattern;
                changed = 1;
            }
            else
            {
                //every raised tile joined to this one (through raised neighbours), and each of their showing sides
                var seen = new bool[map.Width * map.Height];
                var todo = new System.Collections.Generic.Stack<Point>();
                todo.Push(tile);
                seen[tile.Y * map.Width + tile.X] = true;
                while (todo.Count > 0)
                {
                    var t = todo.Pop();
                    for (int d = 0; d < 4; d++)
                    {
                        if (map.SideShows(t.X, t.Y, d))
                        {
                            var index = map.SideIndex(t.X, t.Y, d);
                            if (map.Sides[index] != pattern)
                            {
                                map.Sides[index] = pattern;
                                changed++;
                            }
                        }
                        var n = new Point(t.X + FoundationMap.Directions[d].X, t.Y + FoundationMap.Directions[d].Y);
                        if (!map.InBounds(n.X, n.Y) || map.RaiseOf(n.X, n.Y) <= 0 || seen[n.Y * map.Width + n.X]) continue;
                        seen[n.Y * map.Width + n.X] = true;
                        todo.Push(n);
                    }
                }
            }
            if (changed > 0) arch.SignalFoundationChange();
            return changed;
        }

        /// <summary>The direction from a tile to a foundation beside it (the highest one), or -1.</summary>
        public static int FindFoundationBeside(FoundationMap map, int x, int y)
        {
            int best = -1;
            short bestRaise = 0;
            for (int dir = 0; dir < 4; dir++)
            {
                var d = FoundationMap.Directions[dir];
                var r = map.RaiseOf(x + d.X, y + d.Y);
                if (r > bestRaise) { bestRaise = r; best = dir; }
            }
            return best;
        }

        private static bool Buildable(VMArchitecture arch, int x, int y)
        {
            if (x < 1 || y < 1 || x >= arch.Width - 1 || y >= arch.Height - 1) return false;
            return !arch.OutsideClip((short)x, (short)y, 1);
        }

        /// <summary>Furniture on the tile (Sims don't count: they step up with it).</summary>
        private static bool Blocked(VMArchitecture arch, int x, int y)
        {
            var objs = arch.Context.ObjectQueries.GetObjectsAt(LotTilePos.FromBigTile((short)x, (short)y, 1));
            if (objs == null) return false;
            foreach (var obj in objs)
            {
                if (obj is VMGameObject) return true;
            }
            return false;
        }

        /// <summary>
        /// A wall on the tile, or on the far edges of the rectangle (those belong to the tiles beyond it). Walls go on a
        /// foundation after it's built.
        /// </summary>
        private static bool HasWall(VMArchitecture arch, int x, int y, int x1, int y1, int x2, int y2)
        {
            if (arch.GetWall((short)x, (short)y, 1).Segments != 0) return true;
            if (x == x2 && (arch.GetWall((short)(x + 1), (short)y, 1).Segments & WallSegments.TopLeft) != 0) return true;
            if (y == y2 && (arch.GetWall((short)x, (short)(y + 1), 1).Segments & WallSegments.TopRight) != 0) return true;
            return false;
        }

        private static bool IsPool(VMArchitecture arch, int x, int y)
        {
            return arch.GetFloor((short)x, (short)y, 1).Pattern >= 65504;
        }
    }
}
