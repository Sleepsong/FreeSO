using FSO.Files.Formats.IFF.Chunks;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;

namespace FSO.LotView.Model
{
    /// <summary>
    /// Where a window cuts its wall, from its wall style's mask (walls up, near: where the mask's see-through, the wall is cut):
    /// fractions of the wall, down from its top (Top, Bottom) and along it (Left, Right). For basement windows (only high ones
    /// fit above the ground outside), and the foundation's side over a basement wall's window (cut to show it).
    /// </summary>
    public struct WallOpening
    {
        public float Top, Bottom, Left, Right;

        /// <summary>The cells of <see cref="Open"/>: along the wall, and up it.</summary>
        public const int GridU = 32, GridK = 96;

        /// <summary>
        /// The hole's shape, a cell at a time: <see cref="GridU"/> along the wall (as its mask is laid on it: see
        /// <see cref="MaskCoord"/>) by <see cref="GridK"/> up it from its foot, row by row from the foot. Null if not known.
        /// </summary>
        public bool[] Open;

        /// <summary>Whether a cell's in the hole (<paramref name="i"/> along, <paramref name="j"/> up; outside the grid isn't).</summary>
        public bool IsOpen(int i, int j) => Open != null && i >= 0 && j >= 0 && i < GridU && j < GridK && Open[j * GridU + i];

        /// <summary>
        /// Where on a wall style's mask (0 to 1 across and down its frame) a point of the wall is: <paramref name="u"/> along it,
        /// <paramref name="k"/> up it (0 at its foot, 1 at its top). As the walls' shader samples it (RCObject.fx, psWallRC): the
        /// mask is a top right wall's, slanted, so it's skewed up the wall.
        /// </summary>
        public static Vector2 MaskCoord(float u, float k)
        {
            var v = (k * 0.970f) * -(1 - 0.1185f) + (1 - u) * 0.1185f - 0.117f;
            return new Vector2(u, v - (float)Math.Floor(v));
        }

        private static Dictionary<ushort, WallOpening?> ByStyle = new Dictionary<ushort, WallOpening?>();

        /// <summary>A wall style's opening (null if it has none, or it can't be told). Cached.</summary>
        public static WallOpening? OfStyle(ushort style)
        {
            lock (ByStyle)
            {
                if (ByStyle.TryGetValue(style, out var known)) return known;
            }
            WallOpening? result = null;
            try
            {
                var spr = Content.Content.Get()?.WorldWalls?.GetWallStyle(style)?.WallsUpNear;
                if (spr != null && spr.Frames != null && spr.Frames.Count > 0) result = Measure(spr.Frames[0]);
            }
            catch (Exception) { }
            lock (ByStyle) ByStyle[style] = result;
            return result;
        }

        /// <summary>
        /// A mask's opening: per column, the wall is where the mask's opaque, and the hole is the see-through between its top and
        /// bottom. Null if there's none.
        /// </summary>
        public static WallOpening? Measure(SPRFrame frame)
        {
            var opaque = frame?.GetOpaque(); //(a wall style's pixels go once on the GPU: where it's opaque is kept)
            if (opaque == null || opaque.Length < frame.Width * frame.Height) return null;
            float top = 1, bottom = 0;
            int left = int.MaxValue, right = int.MinValue, firstWall = int.MaxValue, lastWall = int.MinValue;
            for (int x = 0; x < frame.Width; x++)
            {
                int wallTop = -1, wallBottom = -1;
                for (int y = 0; y < frame.Height; y++)
                {
                    if (!opaque[y * frame.Width + x]) continue;
                    if (wallTop < 0) wallTop = y;
                    wallBottom = y;
                }
                if (wallTop < 0 || wallBottom - wallTop < frame.Height / 4) continue; //(an edge of the wall, or nothing)
                firstWall = Math.Min(firstWall, x);
                lastWall = Math.Max(lastWall, x);
                for (int y = wallTop; y <= wallBottom; y++)
                {
                    if (opaque[y * frame.Width + x]) continue;
                    var f = (y - wallTop) / (float)(wallBottom - wallTop);
                    top = Math.Min(top, f);
                    bottom = Math.Max(bottom, f);
                    left = Math.Min(left, x);
                    right = Math.Max(right, x);
                }
            }
            if (left > right || lastWall <= firstWall) return null;
            var width = (float)(lastWall - firstWall);
            var result = new WallOpening
            {
                Top = top,
                Bottom = bottom,
                Left = (left - firstWall) / width,
                Right = (right - firstWall) / width
            };
            result.Open = SampleGrid(frame, opaque, result);
            return result;
        }

        /// <summary>
        /// The hole's cells (see <see cref="Open"/>): where the mask's see-through, as it's laid on the wall. Only round the
        /// opening found (a little either way): the mask's edges, slanted, can read as see-through along the wall's top and foot.
        /// </summary>
        private static bool[] SampleGrid(SPRFrame frame, System.Collections.BitArray opaque, WallOpening box)
        {
            int w = frame.Width, h = frame.Height;
            if (w <= 0 || h <= 0) return null;
            var grid = new bool[GridU * GridK];
            float kLow = 1 - box.Bottom - 0.04f, kHigh = 1 - box.Top + 0.04f; //(the opening, up the wall)
            for (int j = 0; j < GridK; j++)
            {
                var k = (j + 0.5f) / GridK;
                if (k < kLow || k > kHigh || k < 0.02f || k > 0.98f) continue;
                for (int i = 0; i < GridU; i++)
                {
                    var u = (i + 0.5f) / GridU;
                    if (u < 0.02f || u > 0.98f) continue;
                    var c = MaskCoord(u, k);
                    int px = Math.Min(w - 1, Math.Max(0, (int)(c.X * w)));
                    int py = Math.Min(h - 1, Math.Max(0, (int)(c.Y * h)));
                    grid[j * GridU + i] = !opaque[py * w + px];
                }
            }
            return grid;
        }
    }
}
