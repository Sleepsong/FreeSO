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

        /// <summary>What a tile of each kind of foundation costs (see <see cref="FoundationKind"/>).</summary>
        public static int TilePrice(FoundationKind kind)
        {
            switch (kind)
            {
                case FoundationKind.LatticedDeck: return 8;
                case FoundationKind.ColumnDeck: return 6;
                default: return VMArchitecture.FoundationTilePrice;
            }
        }

        /// <summary>What selling back a tile of a kind of foundation gives (as for walls and floors, most of its price).</summary>
        public static int TileSellback(FoundationKind kind) => TilePrice(kind) * 7 / 10;

        /// <summary>
        /// What a set of foundation steps costs: a quarter of its staircase (they're its bottom quarter), or the plain
        /// steps' price.
        /// </summary>
        public static int StepsPrice(uint style)
        {
            if (style != 0)
            {
                try
                {
                    var item = FSO.Content.Content.Get()?.WorldCatalog?.GetItemByGUID(style);
                    if (item != null) return Math.Max(10, (int)item.Value.Price / 4);
                }
                catch (Exception) { }
            }
            return VMArchitecture.FoundationStepsPrice;
        }

        public static int StepsSellback(uint style) => StepsPrice(style) * 7 / 10;

        /// <summary>
        /// Raises every tile of <paramref name="rect"/> (x, y, and width and height less one: the tiles from corner to
        /// corner) to <paramref name="height"/> as a <paramref name="kind"/> of foundation, or removes foundations there
        /// for 0. A tile already at that height becomes that kind (a foundation to a deck, say). All or nothing.
        /// <paramref name="cost"/> is what it comes to: positive to pay, negative for what's sold back.
        /// </summary>
        public static int PlaceFoundation(VMArchitecture arch, Rectangle rect, short height, ushort pattern, FoundationKind kind, out int cost)
        {
            cost = 0;
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
                        if (map.Steps[i] != 0) continue;
                        if (raise == height)
                        {
                            //already that high: only its kind changes (nothing on it is in the way; but over a basement it stays a foundation)
                            if ((FoundationKind)map.Kind[i] == kind) continue;
                            if (arch.Basement?.IsDug(x, y) == true && kind != FoundationKind.Foundation) { arch.LastFailReason = FailBlocked; return 0; }
                            cost += TilePrice(kind) - TileSellback((FoundationKind)map.Kind[i]);
                            changes++;
                            continue;
                        }
                        //over a basement: a foundation (decks are open underneath), its floor going up with it (see below)
                        if (Blocked(arch, x, y) || HasWall(arch, x, y, x1, y1, x2, y2) || IsPool(arch, x, y)
                            || (arch.Basement?.IsDug(x, y) == true && kind != FoundationKind.Foundation)) { arch.LastFailReason = FailBlocked; return 0; }
                        cost += TilePrice(kind) - ((raise > 0) ? TileSellback((FoundationKind)map.Kind[i]) : 0);
                        changes++;
                    }
                    else
                    {
                        if (raise == 0) continue;
                        if (Blocked(arch, x, y) || HasWall(arch, x, y, x1, y1, x2, y2)) { arch.LastFailReason = FailBlocked; return 0; }
                        cost -= TileSellback((FoundationKind)map.Kind[i]);
                        changes++;
                    }
                }
            }
            if (changes == 0) return 0;
            //a basement under it has its floor a storey under its top: all of a basement's floor at one height (not partly under it)
            var basement = arch.Basement;
            if (basement != null && basement.Any && !VMBasementTools.FloorEven(arch, new Rectangle(x1, y1, x2 - x1, y2 - y1), basement.IsDug,
                (x, y) => (x >= x1 && x <= x2 && y >= y1 && y <= y2 && map.Steps[y * map.Width + x] == 0) ? height : map.RaiseOf(x, y)))
            {
                arch.LastFailReason = VMBasementTools.FailUneven;
                return 0;
            }

            for (int y = y1; y <= y2; y++)
            {
                for (int x = x1; x <= x2; x++)
                {
                    var i = y * map.Width + x;
                    if (map.Steps[i] != 0) continue;
                    if (height > 0)
                    {
                        if (map.Raise[i] == height && (FoundationKind)map.Kind[i] == kind) continue;
                        if (map.Raise[i] != height) map.SetAllSides(i, pattern); //(a new foundation; one changing kind keeps its paint)
                        map.Raise[i] = height;
                        map.Kind[i] = (byte)kind;
                    }
                    else
                    {
                        map.Raise[i] = 0;
                        map.Kind[i] = 0;
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
                        {
                            cost -= StepsSellback(map.StepStyle[y * map.Width + x]);
                            map.Steps[y * map.Width + x] = 0;
                            map.StepStyle[y * map.Width + x] = 0;
                        }
            }

            arch.SignalFoundationChange();
            return (height > 0) ? changes : -changes;
        }

        /// <summary>
        /// Puts steps on a ground tile, rising toward <paramref name="direction"/> (0-3, see
        /// <see cref="FoundationMap.Directions"/>; 255 for whichever side has a foundation), in a staircase's
        /// <paramref name="style"/> (its catalog GUID; 0 for the plain steps), or removes them. Steps already there take
        /// the new direction and style. <paramref name="cost"/> is what it comes to, positive to pay, negative sold back.
        /// </summary>
        public static int PlaceFoundationSteps(VMArchitecture arch, Point tile, ushort direction, bool place, uint style, out int cost)
        {
            cost = 0;
            var map = arch.Terrain.Foundations;
            if (map == null || !Buildable(arch, tile.X, tile.Y)) return 0;
            var i = tile.Y * map.Width + tile.X;

            if (!place)
            {
                if (map.Steps[i] == 0) return 0;
                if (Blocked(arch, tile.X, tile.Y)) { arch.LastFailReason = FailBlocked; return 0; }
                cost = -StepsSellback(map.StepStyle[i]);
                map.Steps[i] = 0;
                map.StepStyle[i] = 0;
                arch.SignalFoundationChange();
                return -1;
            }

            if (map.Raise[i] != 0) { arch.LastFailReason = FailBlocked; return 0; }
            int dir = (direction < 4) ? direction : FindFoundationBeside(map, tile.X, tile.Y);
            if (dir < 0) { arch.LastFailReason = FailNoFoundation; return 0; }
            var to = FoundationMap.Directions[dir];
            var topRaise = map.RaiseOf(tile.X + to.X, tile.Y + to.Y);
            if (topRaise <= 0) { arch.LastFailReason = FailNoFoundation; return 0; }
            if (map.Steps[i] == dir + 1 && map.StepStyle[i] == style) return 0;
            if (Blocked(arch, tile.X, tile.Y) || IsPool(arch, tile.X, tile.Y) || arch.Basement?.IsDug(tile.X, tile.Y) == true
                || arch.GetWall((short)tile.X, (short)tile.Y, 1).Segments != 0) { arch.LastFailReason = FailBlocked; return 0; }

            var wasSteps = map.Steps[i] != 0;
            //new steps cost theirs; steps changing style cost the new less the old's sellback; turning them is free
            if (!wasSteps) cost = StepsPrice(style);
            else if (map.StepStyle[i] != style) cost = StepsPrice(style) - StepsSellback(map.StepStyle[i]);
            map.Steps[i] = (byte)(dir + 1);
            map.StepStyle[i] = style;
            arch.SignalFoundationChange();
            return 1;
        }

        /// <summary>The side direction a paint command gives for every side of the foundation a tile is part of.</summary>
        public const int PaintWholeFoundation = 255;

        /// <summary>
        /// Paints a wall covering (0 for the foundation's own brick) on a foundation's side, as walls are painted: side
        /// <paramref name="dir"/> (see <see cref="FoundationMap.Directions"/>) of the tile, or with
        /// <see cref="PaintWholeFoundation"/> every showing side of the foundation the tile is part of (all the raised tiles
        /// joined to it). Only a foundation's sides that show can be painted, not a deck's. Returns how many sides changed.
        /// </summary>
        public static int PaintSides(VMArchitecture arch, Point tile, int dir, ushort pattern)
        {
            var map = arch.Terrain.Foundations;
            if (map == null || !map.InBounds(tile.X, tile.Y) || map.RaiseOf(tile.X, tile.Y) <= 0
                || map.KindOf(tile.X, tile.Y) != FoundationKind.Foundation) return 0; //(decks can't be painted)
            int changed = 0;
            if (dir != PaintWholeFoundation)
            {
                if (dir < 0 || dir > 3 || !map.SidePaintable(tile.X, tile.Y, dir)) return 0;
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
                        if (map.SidePaintable(t.X, t.Y, d))
                        {
                            var index = map.SideIndex(t.X, t.Y, d);
                            if (map.Sides[index] != pattern)
                            {
                                map.Sides[index] = pattern;
                                changed++;
                            }
                        }
                        var n = new Point(t.X + FoundationMap.Directions[d].X, t.Y + FoundationMap.Directions[d].Y);
                        if (!map.InBounds(n.X, n.Y) || map.RaiseOf(n.X, n.Y) <= 0 || map.KindOf(n.X, n.Y) != FoundationKind.Foundation
                            || seen[n.Y * map.Width + n.X]) continue;
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
