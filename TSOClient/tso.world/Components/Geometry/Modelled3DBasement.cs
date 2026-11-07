using FSO.LotView.Model;
using FSO.LotView.RC;
using FSO.LotView.Utils;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System.Collections.Generic;
using System.Linq;

namespace FSO.LotView.Components.Geometry
{
    /// <summary>
    /// The earth's sides round the basement (see <see cref="BasementMap"/>) with one wall covering: wherever a dug out
    /// tile meets earth, a side from the basement's floor up to the ground, facing into the basement. Drawn with the
    /// basement's floor by <see cref="_3DFloorGeometry"/> (a storey below the ground floor), like the foundations' sides.
    /// Positions are in world units (3 per tile), heights from the basement floor, without the lot's base altitude.
    /// </summary>
    public class Modelled3DBasement : Modelled3DFoundation
    {
        public Modelled3DBasement(Blueprint bp, ushort pattern) : base(bp, pattern)
        {
        }

        /// <summary>How high a side is where the walls are cut away: as high as a cut wall (see WallComponentRC).</summary>
        public const float CutHeight = 0.12f;

        private bool[] Cut; //the cutaway it was made with: null for none (whole sides)
        private WorldRotation CutRotation;

        /// <summary>
        /// Makes the sides again if the cutaway they were made with has changed: while the basement is shown, sides come down
        /// where the walls do (so the near ones don't hide it), and are whole otherwise (pass null). Each view that draws them
        /// (the lot, a live picture) passes its own.
        /// </summary>
        public void UseCut(GraphicsDevice gd, bool[] cut, WorldRotation rotation)
        {
            if (cut != null && !cut.Any(x => x)) cut = null; //(nothing cut: whole, as with none)
            if (cut == null && Cut == null && GPUData != null) return;
            if (cut != null && Cut != null && rotation == CutRotation && cut.Length == Cut.Length && cut.SequenceEqual(Cut)) return;
            Cut = (bool[])cut?.Clone();
            CutRotation = rotation;
            PrepareGPU(gd);
        }

        private float HeightAt(Point p)
        {
            return SideHeight(Bp.Width, Cut, CutRotation, p);
        }

        /// <summary>
        /// A side's height at one end (a tile corner, tile units) with a cutaway (null for none): cut down if the point a
        /// little toward the camera is on a cut tile, as walls check (see WallComponentRC).
        /// </summary>
        public static float SideHeight(int width, bool[] cut, WorldRotation rotation, Point p)
        {
            if (cut == null) return StoreyHeight;
            var xz = WallComponentRC.CutCheckDir[(int)rotation] * 0.66f;
            if (float.IsNaN(xz.X)) xz = Vector2.Zero;
            var x = (int)(p.X + xz.X);
            var y = (int)(p.Y + xz.Y);
            var index = y * width + x;
            if (x < 0 || y < 0 || x >= width || index >= cut.Length) return StoreyHeight;
            return cut[index] ? StoreyHeight * CutHeight : StoreyHeight;
        }

        /// <summary>Whether a tile has any earth side: it's dug out and next to earth.</summary>
        public static bool HasGeometry(BasementMap map, int x, int y)
        {
            for (int dir = 0; dir < 4; dir++)
                if (map.EarthSide(x, y, dir)) return true;
            return false;
        }

        /// <summary>The texture's v up a side: a wall covering (0 for the foundations' brick), as walls map it.</summary>
        private float SideV(float height)
        {
            return WallV(height);
        }

        protected override void GenerateGeometry()
        {
            var verts = new List<TerrainParallaxVertex>();
            var inds = new List<int>();
            var map = Bp.Basement;
            if (map != null && map.Any)
            {
                for (int y = 0; y < map.Height; y++)
                {
                    for (int x = 0; x < map.Width; x++)
                    {
                        var i = y * map.Width + x;
                        if (map.Dug[i] == 0 || map.Pattern[i] != WallPattern) continue;
                        for (int dir = 0; dir < 4; dir++)
                        {
                            if (!map.EarthSide(x, y, dir)) continue;
                            var d = BasementMap.Directions[dir];
                            // the edge's two ends, going clockwise round the tile
                            Point a, b;
                            switch (dir)
                            {
                                case 0: a = new Point(x, y); b = new Point(x + 1, y); break;
                                case 1: a = new Point(x + 1, y); b = new Point(x + 1, y + 1); break;
                                case 2: a = new Point(x + 1, y + 1); b = new Point(x, y + 1); break;
                                default: a = new Point(x, y + 1); b = new Point(x, y); break;
                            }
                            var ga = GroundAtVertex(a.X, a.Y);
                            var gb = GroundAtVertex(b.X, b.Y);
                            var ha = HeightAt(a);
                            var hb = HeightAt(b);
                            var normal = new Vector3(-d.X, 0, -d.Y); //into the basement
                            float ua = (d.Y != 0) ? a.X : a.Y, ub = (d.Y != 0) ? b.X : b.Y;
                            AddQuad(verts, inds,
                                new Vector3(a.X, ga, a.Y), new Vector3(b.X, gb, b.Y),
                                new Vector3(b.X, gb + hb, b.Y), new Vector3(a.X, ga + ha, a.Y), normal,
                                new Vector2(ua, SideV(0)), new Vector2(ub, SideV(0)), new Vector2(ub, SideV(hb)), new Vector2(ua, SideV(ha)));
                        }
                    }
                }
            }
            Vertices = verts.ToArray();
            Indices = inds.ToArray();
        }
    }
}
