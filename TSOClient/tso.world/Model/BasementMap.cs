using System;

namespace FSO.LotView.Model
{
    /// <summary>
    /// Basements, as in The Sims 2: level 0, a storey under the ground floor. The lot has it everywhere (see
    /// <see cref="LevelArray{T}"/>), but it's solid earth except the tiles dug out here: only those can have walls, floors,
    /// things and Sims on level 0, the ground isn't drawn over them (the ground floor above has a floor instead), and
    /// where a dug tile meets earth, the earth's side is drawn, covered in <see cref="Pattern"/> (a wall pattern ID).
    /// The simulation owns this (it's saved at the end of a save, with level 0) and the world draws from the same arrays.
    /// </summary>
    public class BasementMap
    {
        /// <summary>The neighbour each side faces: 0 is -y, 1 is +x, 2 is +y, 3 is -x (as foundations' steps).</summary>
        public static readonly Microsoft.Xna.Framework.Point[] Directions = FoundationMap.Directions;

        public int Width;
        public int Height;

        /// <summary>Per tile: 1 if dug out (part of the basement), 0 for earth.</summary>
        public byte[] Dug;
        /// <summary>Per dug tile: the wall covering (a wall pattern ID) on the earth's sides around it.</summary>
        public ushort[] Pattern;

        /// <summary>True if any tile is dug out (kept up to date by <see cref="Refresh"/>).</summary>
        public bool Any { get; private set; }

        public BasementMap(int width, int height)
        {
            Width = width;
            Height = height;
            Dug = new byte[width * height];
            Pattern = new ushort[width * height];
        }

        public BasementMap Clone()
        {
            var result = new BasementMap(Width, Height);
            result.CopyFrom(this);
            return result;
        }

        public void CopyFrom(BasementMap other)
        {
            if (other.Width != Width || other.Height != Height)
            {
                Width = other.Width;
                Height = other.Height;
                Dug = new byte[Width * Height];
                Pattern = new ushort[Width * Height];
            }
            Array.Copy(other.Dug, Dug, Dug.Length);
            Array.Copy(other.Pattern, Pattern, Pattern.Length);
            Any = other.Any;
        }

        /// <summary>Call after changing the arrays.</summary>
        public void Refresh()
        {
            var any = false;
            for (int i = 0; i < Dug.Length && !any; i++) any = Dug[i] != 0;
            Any = any;
        }

        public bool InBounds(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;

        /// <summary>Whether the tile is dug out: part of the basement (false off the lot).</summary>
        public bool IsDug(int x, int y) => InBounds(x, y) && Dug[y * Width + x] != 0;

        /// <summary>
        /// Whether a wall segment on level 0 has the basement beside it: a top left segment (along -x) or top right one
        /// (along -y) of a tile borders that tile and the one across; diagonals only their own tile.
        /// </summary>
        public bool SegmentDug(int x, int y, WallSegments segment)
        {
            if (IsDug(x, y)) return true;
            if ((segment & WallSegments.TopLeft) != 0 && IsDug(x - 1, y)) return true;
            if ((segment & WallSegments.TopRight) != 0 && IsDug(x, y - 1)) return true;
            return false;
        }

        /// <summary>Whether a dug tile's side toward <paramref name="dir"/> (see <see cref="Directions"/>) is the earth's.</summary>
        public bool EarthSide(int x, int y, int dir)
        {
            if (!IsDug(x, y)) return false;
            var d = Directions[dir];
            return !IsDug(x + d.X, y + d.Y);
        }
    }
}
