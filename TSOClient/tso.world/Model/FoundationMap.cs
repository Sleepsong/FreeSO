using Microsoft.Xna.Framework;
using System;

namespace FSO.LotView.Model
{
    /// <summary>
    /// Foundations, as in The Sims 2: tiles of the ground floor raised a few steps above the ground, with everything on
    /// them (floors, walls, objects, Sims, and the floors above). Each tile has its own raise, in terrain height units
    /// (see <see cref="Blueprint.TerrainFactor"/>), 0 for none. Steps are tiles on the ground in front of a foundation,
    /// rising onto it: Sims walk up them, and can only get on or off a foundation there (see <see cref="EdgeBlocked"/>).
    /// The simulation owns this (it's saved with the lot's terrain) and the world draws from the same arrays.
    /// </summary>
    public class FoundationMap
    {
        /// <summary>A foundation's height: a quarter of a storey (a storey is 2.95 / <see cref="Blueprint.TerrainFactor"/>).</summary>
        public const short DefaultHeight = 40;
        /// <summary>How many risers a flight of foundation steps has.</summary>
        public const int StepCount = 4;

        /// <summary>The neighbour each step direction rises toward: 0 is -y, 1 is +x, 2 is +y, 3 is -x.</summary>
        public static readonly Point[] Directions = { new Point(0, -1), new Point(1, 0), new Point(0, 1), new Point(-1, 0) };

        public int Width;
        public int Height;

        /// <summary>Per tile: how far the tile is raised, 0 for no foundation.</summary>
        public short[] Raise;
        /// <summary>Per tile: 0 for no steps, else 1 + the direction (see <see cref="Directions"/>) the steps rise toward.</summary>
        public byte[] Steps;
        /// <summary>
        /// Per side of each tile, four a tile (<see cref="SideIndex"/>): the wall covering (a wall pattern ID) painted on
        /// that side of the foundation, 0 for plain concrete. Caps, tops and steps are always concrete.
        /// </summary>
        public ushort[] Sides;

        /// <summary>True if any tile has a foundation or steps (kept up to date by <see cref="Refresh"/>).</summary>
        public bool Any { get; private set; }

        public FoundationMap(int width, int height)
        {
            Width = width;
            Height = height;
            Raise = new short[width * height];
            Steps = new byte[width * height];
            Sides = new ushort[width * height * 4];
        }

        public FoundationMap Clone()
        {
            var result = new FoundationMap(Width, Height);
            result.CopyFrom(this);
            return result;
        }

        public void CopyFrom(FoundationMap other)
        {
            if (other.Width != Width || other.Height != Height)
            {
                Width = other.Width;
                Height = other.Height;
                Raise = new short[Width * Height];
                Steps = new byte[Width * Height];
                Sides = new ushort[Width * Height * 4];
            }
            Array.Copy(other.Raise, Raise, Raise.Length);
            Array.Copy(other.Steps, Steps, Steps.Length);
            Array.Copy(other.Sides, Sides, Sides.Length);
            Any = other.Any;
        }

        /// <summary>Call after changing the arrays.</summary>
        public void Refresh()
        {
            var any = false;
            for (int i = 0; i < Raise.Length && !any; i++) any = Raise[i] != 0 || Steps[i] != 0;
            Any = any;
        }

        public bool InBounds(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;

        /// <summary>Where a tile's side (a direction, see <see cref="Directions"/>) is in <see cref="Sides"/>.</summary>
        public int SideIndex(int x, int y, int dir) => (y * Width + x) * 4 + dir;

        /// <summary>The covering on a tile's side (0 for plain, or off the lot).</summary>
        public ushort SideOf(int x, int y, int dir) => InBounds(x, y) ? Sides[SideIndex(x, y, dir)] : (ushort)0;

        /// <summary>Puts one covering on all four sides of a tile.</summary>
        public void SetAllSides(int index, ushort pattern)
        {
            for (int dir = 0; dir < 4; dir++) Sides[index * 4 + dir] = pattern;
        }

        /// <summary>The tile's foundation raise (0 off the lot, on the ground, or on steps).</summary>
        public short RaiseOf(int x, int y) => InBounds(x, y) ? Raise[y * Width + x] : (short)0;

        /// <summary>The direction (0-3, see <see cref="Directions"/>) the steps on this tile rise toward, or -1.</summary>
        public int StepDirection(int x, int y) => InBounds(x, y) ? Steps[y * Width + x] - 1 : -1;

        /// <summary>How high the steps on this tile go: the raise of the foundation they lead onto (0 if none).</summary>
        public short StepTop(int x, int y)
        {
            var dir = StepDirection(x, y);
            if (dir < 0) return 0;
            var to = Directions[dir];
            return RaiseOf(x + to.X, y + to.Y);
        }

        /// <summary>
        /// The raise under a point (tile units): the tile's foundation, or partway up its steps (from the ground at their
        /// foot to the foundation at their head), or 0.
        /// </summary>
        public float RaiseAt(float px, float py)
        {
            int x = (int)Math.Floor(px), y = (int)Math.Floor(py);
            if (!InBounds(x, y)) return 0;
            var raise = Raise[y * Width + x];
            if (raise != 0) return raise;
            var dir = StepDirection(x, y);
            if (dir < 0) return 0;
            var top = StepTop(x, y);
            float fx = px - x, fy = py - y;
            float t;
            switch (dir)
            {
                case 0: t = 1 - fy; break;
                case 1: t = fx; break;
                case 2: t = fy; break;
                default: t = 1 - fx; break;
            }
            return top * Math.Max(0, Math.Min(1, t));
        }

        /// <summary>
        /// The highest foundation touching a small square around a point (tile units). Things that stand on a tile edge
        /// or corner, like walls, sit on the higher side.
        /// </summary>
        public short RaiseNear(float px, float py, float radius)
        {
            int x1 = (int)Math.Floor(px - radius), x2 = (int)Math.Floor(px + radius);
            int y1 = (int)Math.Floor(py - radius), y2 = (int)Math.Floor(py + radius);
            short best = 0;
            for (int y = y1; y <= y2; y++)
                for (int x = x1; x <= x2; x++)
                {
                    var r = RaiseOf(x, y);
                    if (r > best) best = r;
                }
            return best;
        }

        /// <summary>
        /// The raise for a tile's walls (its top left and top right edges, and diagonals across it): the higher of the
        /// tile and the tile across each edge it has a wall on.
        /// </summary>
        public short WallRaise(int x, int y, WallSegments segments)
        {
            var raise = RaiseOf(x, y);
            if ((segments & WallSegments.TopLeft) != 0) raise = Math.Max(raise, RaiseOf(x - 1, y));
            if ((segments & WallSegments.TopRight) != 0) raise = Math.Max(raise, RaiseOf(x, y - 1));
            if ((segments & WallSegments.BottomRight) != 0) raise = Math.Max(raise, RaiseOf(x + 1, y));
            if ((segments & WallSegments.BottomLeft) != 0) raise = Math.Max(raise, RaiseOf(x, y + 1));
            return raise;
        }

        /// <summary>
        /// Whether Sims can't step from tile (x, y) to its neighbour in direction <paramref name="dir"/>: true where the
        /// two are at different heights (a foundation's edge), except from steps onto the foundation they lead up to.
        /// </summary>
        public bool EdgeBlocked(int x, int y, int dir)
        {
            var d = Directions[dir];
            int nx = x + d.X, ny = y + d.Y;
            if (!InBounds(x, y) || !InBounds(nx, ny)) return false;
            if (StepDirection(x, y) == dir && RaiseOf(nx, ny) > 0) return false;
            if (StepDirection(nx, ny) == (dir + 2) % 4 && RaiseOf(x, y) > 0) return false;
            return RaiseOf(x, y) != RaiseOf(nx, ny);
        }

        /// <summary>
        /// Where a foundation's side is: tile (x, y) is raised higher than its neighbour in direction <paramref name="dir"/>
        /// (or that neighbour is off the lot). Returns how far the side drops (0 for none).
        /// </summary>
        public short SideDrop(int x, int y, int dir)
        {
            var raise = RaiseOf(x, y);
            if (raise <= 0) return 0;
            var d = Directions[dir];
            int nx = x + d.X, ny = y + d.Y;
            var other = RaiseOf(nx, ny);
            if (other >= raise) return 0;
            return (short)(raise - other);
        }

        /// <summary>
        /// Whether a foundation tile's side shows (and can be painted, and has a cap on top): it drops to lower ground or
        /// a lower foundation, and no steps lead up to it.
        /// </summary>
        public bool SideShows(int x, int y, int dir)
        {
            if (SideDrop(x, y, dir) <= 0) return false;
            var d = Directions[dir];
            return StepDirection(x + d.X, y + d.Y) != (dir + 2) % 4;
        }
    }
}
