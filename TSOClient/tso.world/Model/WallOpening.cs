using FSO.Files.Formats.IFF.Chunks;
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
            if (frame == null) return null;
            frame.DecodeIfRequired();
            float top = 1, bottom = 0;
            int left = int.MaxValue, right = int.MinValue, firstWall = int.MaxValue, lastWall = int.MinValue;
            for (int x = 0; x < frame.Width; x++)
            {
                int wallTop = -1, wallBottom = -1;
                for (int y = 0; y < frame.Height; y++)
                {
                    if (frame.GetPixel(x, y).A == 0) continue;
                    if (wallTop < 0) wallTop = y;
                    wallBottom = y;
                }
                if (wallTop < 0 || wallBottom - wallTop < frame.Height / 4) continue; //(an edge of the wall, or nothing)
                firstWall = Math.Min(firstWall, x);
                lastWall = Math.Max(lastWall, x);
                for (int y = wallTop; y <= wallBottom; y++)
                {
                    if (frame.GetPixel(x, y).A != 0) continue;
                    var f = (y - wallTop) / (float)(wallBottom - wallTop);
                    top = Math.Min(top, f);
                    bottom = Math.Max(bottom, f);
                    left = Math.Min(left, x);
                    right = Math.Max(right, x);
                }
            }
            if (left > right || lastWall <= firstWall) return null;
            var width = (float)(lastWall - firstWall);
            return new WallOpening
            {
                Top = top,
                Bottom = bottom,
                Left = (left - firstWall) / width,
                Right = (right - firstWall) / width
            };
        }
    }
}
