using FSO.Files.Formats.IFF.Chunks;
using FSO.LotView.Model;
using FSO.SimAntics.Entities;
using FSO.SimAntics.Model;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;

namespace FSO.SimAntics.Utils
{
    /// <summary>
    /// Windows in the basement (level 0). Between two of its rooms, any window, as on any floor. On its edge against the earth,
    /// only where a foundation lifts the basement above the ground outside (the top of its wall is then above ground, as in
    /// The Sims 3), and only windows high enough in the wall to fit in that part: the Privacy Window, and any other whose
    /// opening is in the top of the wall (found from the hole it cuts in the wall, its wall style). Those let daylight in.
    /// </summary>
    public static class VMBasementWindows
    {
        /// <summary>A storey's height, and a foundation's height units, in tiles (as Blueprint.TerrainFactor).</summary>
        public const float StoreyTiles = 2.95f, HeightToTiles = 3 / 160f;
        /// <summary>How far below the ground outside a window's opening may reach (a fraction of the wall): its sill.</summary>
        public const float Slack = 0.12f;

        /// <summary>Placement errors of our own (not in the game's table: the frontend has the words).</summary>
        public const VMPlacementError NeedsFoundation = (VMPlacementError)200, TooLow = (VMPlacementError)201;

        public static bool IsWindow(VMEntity entity)
        {
            return ((VMEntityFlags2)entity.GetValue(VMStackObjectVariable.FlagField2) & VMEntityFlags2.ArchitectualWindow) > 0;
        }

        /// <summary>A window or door: it's in a wall, and stands as high as the wall does.</summary>
        public static bool InWall(VMEntity entity)
        {
            return ((VMEntityFlags2)entity.GetValue(VMStackObjectVariable.FlagField2) & (VMEntityFlags2.ArchitectualWindow | VMEntityFlags2.ArchitectualDoor)) > 0;
        }

        /// <summary>The tile across a tile's side (0 top right: -y, 1 bottom right: +x, 2 bottom left: +y, 3 top left: -x).</summary>
        public static Point Across(int x, int y, int side)
        {
            switch (side & 3)
            {
                case 0: return new Point(x, y - 1);
                case 1: return new Point(x + 1, y);
                case 2: return new Point(x, y + 1);
                default: return new Point(x - 1, y);
            }
        }

        /// <summary>The sides of its tile a window at <paramref name="direction"/> attaches to (see <see cref="Across"/>).</summary>
        public static IEnumerable<int> WallSides(VMEntity window, Direction direction)
        {
            var flags = window.GetValue(VMStackObjectVariable.WallPlacementFlags);
            var off = DirectionOff(direction);
            for (int k = 0; k < 4; k++)
                if ((flags & (1 << k)) != 0) yield return (off + k) % 4;
        }

        private static int DirectionOff(Direction dir)
        {
            switch (dir)
            {
                case Direction.EAST: return 1;
                case Direction.SOUTH: return 2;
                case Direction.WEST: return 3;
                default: return 0;
            }
        }

        /// <summary>
        /// On a basement's edge, how much of its wall is above the ground outside (a fraction of the storey, from the top): the
        /// dug tile's foundation above the other's. 0 if the wall isn't on its edge, or none of it is above ground.
        /// </summary>
        public static float AboveGround(VMArchitecture arch, int x, int y, int side)
        {
            var map = arch.Basement;
            if (map == null || !map.Any) return 0;
            var other = Across(x, y, side);
            bool here = map.IsDug(x, y), there = map.IsDug(other.X, other.Y);
            if (here == there) return 0;
            var dug = here ? new Point(x, y) : other;
            var outside = here ? other : new Point(x, y);
            var fnd = arch.Terrain.Foundations;
            if (fnd == null) return 0;
            var raise = fnd.RaiseOf(dug.X, dug.Y) - fnd.RaiseOf(outside.X, outside.Y);
            return Math.Max(0, raise * HeightToTiles / StoreyTiles);
        }

        /// <summary>
        /// Whether a window can go at <paramref name="pos"/> facing <paramref name="direction"/>: anywhere off level 0; between
        /// two of the basement's rooms; and on its edge, only a high window where a foundation lifts it above the ground.
        /// </summary>
        public static VMPlacementError Check(VMEntity window, LotTilePos pos, Direction direction, VMContext context)
        {
            if (pos.Level != 0 || !IsWindow(window)) return VMPlacementError.Success;
            var arch = context.Architecture;
            var map = arch.Basement;
            if (map == null) return VMPlacementError.Success;
            foreach (var side in WallSides(window, direction))
            {
                var other = Across(pos.TileX, pos.TileY, side);
                if (map.IsDug(pos.TileX, pos.TileY) == map.IsDug(other.X, other.Y)) continue; //(between two of its rooms)
                var above = AboveGround(arch, pos.TileX, pos.TileY, side);
                if (above <= 0) return NeedsFoundation;
                if (!Fits(window, above)) return TooLow;
            }
            return VMPlacementError.Success;
        }

        /// <summary>
        /// Whether a basement window lets daylight in: one on its edge that's allowed there (see <see cref="Check"/>). A window
        /// between two of its rooms (or one put in before the rule) doesn't.
        /// </summary>
        public static bool LetsDaylightIn(VMEntity window, VMContext context)
        {
            if (window.Position.Level != 0 || !IsWindow(window)) return false;
            var arch = context.Architecture;
            foreach (var side in WallSides(window, window.Direction))
            {
                var above = AboveGround(arch, window.Position.TileX, window.Position.TileY, side);
                if (above > 0 && Fits(window, above)) return true;
            }
            return false;
        }

        /// <summary>Whether a window's opening is high enough to be above ground with <paramref name="above"/> of its wall above it.</summary>
        public static bool Fits(VMEntity window, float above)
        {
            var depth = OpeningDepth(window, out var named);
            return named || depth <= above + Slack;
        }

        private static Dictionary<uint, Tuple<float, bool>> Openings = new Dictionary<uint, Tuple<float, bool>>();

        /// <summary>
        /// How far down from the top of the wall a window's opening goes (a fraction of the wall's height; 1 if it can't be
        /// told), from the hole its wall style cuts. <paramref name="named"/>: it's the Privacy Window (by name), which always fits.
        /// </summary>
        public static float OpeningDepth(VMEntity window, out bool named)
        {
            var obj = window.Object;
            var guid = obj.OBJ.GUID;
            lock (Openings)
            {
                if (Openings.TryGetValue(guid, out var known))
                {
                    named = known.Item2;
                    return known.Item1;
                }
            }
            var name = Name(window);
            named = name.IndexOf("privacy", StringComparison.OrdinalIgnoreCase) >= 0;
            float depth = 1;
            try
            {
                depth = MeasureOpening(Mask(window));
            }
            catch (Exception) { }
            lock (Openings) Openings[guid] = new Tuple<float, bool>(depth, named);
            Log(name + " (" + guid.ToString("X8") + "): opening down to " + (int)Math.Round(depth * 100) + "% of the wall"
                + (named ? ", the Privacy Window: a basement window" : (depth <= 0.25f + Slack ? ": a basement window (under a standard foundation)" : "")));
            return depth;
        }

        private static string Name(VMEntity window)
        {
            var obj = window.Object;
            try
            {
                var ctss = obj.Resource.Get<CTSS>(obj.OBJ.CatalogStringsID);
                var catalog = ctss?.GetString(0);
                if (!string.IsNullOrEmpty(catalog)) return catalog + " / " + obj.OBJ.ChunkLabel;
            }
            catch (Exception) { }
            return obj.OBJ.ChunkLabel ?? "";
        }

        /// <summary>The window's wall style's mask (walls up, near): where it's see-through, the wall is cut.</summary>
        private static SPRFrame Mask(VMEntity window)
        {
            var obj = window.Object;
            SPR spr = null;
            var style = obj.OBJ.WallStyle;
            if (style > 21 && style < 256) spr = obj.Resource.Get<SPR>((ushort)(obj.OBJ.WallStyleSpriteID + 2)); //(not loaded yet: in the object)
            else spr = Content.Content.Get().WorldWalls.GetWallStyle(style)?.WallsUpNear;
            if (spr == null || spr.Frames == null || spr.Frames.Count == 0) return null;
            return spr.Frames[0];
        }

        /// <summary>The deepest a wall mask's hole goes, as a fraction of the wall down from its top (1 if there's none: see <see cref="WallOpening"/>).</summary>
        public static float MeasureOpening(SPRFrame frame)
        {
            return WallOpening.Measure(frame)?.Bottom ?? 1;
        }

        private static bool? Logs;
        /// <summary>-logs: basement-windows.log, what each window was found to be.</summary>
        private static void Log(string line)
        {
            if (Logs == null)
            {
                Logs = false;
                foreach (var part in (FSO.Common.FSOEnvironment.Args ?? "").Split(' '))
                    if ((part.StartsWith("-") || part.StartsWith("/")) && part.TrimStart('-', '/').Equals("logs", StringComparison.OrdinalIgnoreCase)) Logs = true;
            }
            if (Logs != true) return;
            try
            {
                var path = System.IO.Path.Combine(FSO.Common.FSOEnvironment.UserDir ?? "", "basement-windows.log");
                System.IO.File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + line + Environment.NewLine);
            }
            catch (Exception) { }
        }
    }
}
