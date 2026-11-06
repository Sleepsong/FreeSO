using FSO.LotView.Model;
using FSO.LotView.Utils;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;

namespace FSO.LotView.Components.Geometry
{
    /// <summary>
    /// The visible parts of foundations (see <see cref="FoundationMap"/>) with one wall covering: their sides down to the
    /// ground, the tops of tiles with no floor laid on them, and their steps. Drawn with the ground floor by
    /// <see cref="_3DFloorGeometry"/>, in 2D and 3D alike, like the pools' models. Positions are in world units
    /// (3 per tile), heights without the lot's base altitude (the floor drawing takes that off).
    /// </summary>
    public class Modelled3DFoundation : Modelled3DFloor
    {
        /// <summary>The wall pattern on the sides (0 for plain).</summary>
        public ushort WallPattern;
        private Texture2D Texture;

        private const float StoreyHeight = 2.95f;

        public Modelled3DFoundation(Blueprint bp, ushort pattern) : base(bp)
        {
            WallPattern = pattern;
        }

        /// <summary>
        /// Builds the geometry for every foundation tile and steps tile with this group's pattern. A tile with floor
        /// laid on it gets no top here (the floor is drawn raised instead).
        /// </summary>
        protected override void GenerateGeometry()
        {
            var verts = new List<TerrainParallaxVertex>();
            var inds = new List<int>();
            var map = Bp.Foundations;
            if (map != null && map.Any)
            {
                var floors = Bp.Floors[0];
                for (int y = 0; y < map.Height; y++)
                {
                    for (int x = 0; x < map.Width; x++)
                    {
                        var i = y * map.Width + x;
                        if (map.Pattern[i] != WallPattern) continue;
                        if (map.Raise[i] > 0) AppendFoundationTile(verts, inds, map, x, y, floors[i].Pattern == 0);
                        else if (map.Steps[i] != 0) AppendSteps(verts, inds, map, x, y);
                    }
                }
            }
            Vertices = verts.ToArray();
            Indices = inds.ToArray();
        }

        public static bool HasGeometry(FoundationMap map, int x, int y)
        {
            var i = y * map.Width + x;
            return map.Raise[i] > 0 || (map.Steps[i] != 0 && map.StepTop(x, y) > 0);
        }

        /// <summary>Ground height (without foundations or the lot's base altitude) at a terrain vertex.</summary>
        private float GroundAtVertex(int x, int y)
        {
            var alt = Bp.Altitude;
            if (alt == null) return 0;
            x = Math.Max(0, Math.Min(Bp.Width - 1, x));
            y = Math.Max(0, Math.Min(Bp.Height - 1, y));
            return alt[y * Bp.Width + x] * Bp.TerrainFactor;
        }

        private void AppendFoundationTile(List<TerrainParallaxVertex> verts, List<int> inds, FoundationMap map, int x, int y, bool top)
        {
            var tf = Bp.TerrainFactor;
            var raise = map.RaiseOf(x, y) * tf;

            if (top)
            {
                // a bare foundation: its own top, flat at the foundation's height over the ground's corners
                var c00 = new Vector3(x, GroundAtVertex(x, y) + raise, y);
                var c10 = new Vector3(x + 1, GroundAtVertex(x + 1, y) + raise, y);
                var c11 = new Vector3(x + 1, GroundAtVertex(x + 1, y + 1) + raise, y + 1);
                var c01 = new Vector3(x, GroundAtVertex(x, y + 1) + raise, y + 1);
                AddQuad(verts, inds, c00, c10, c11, c01, Vector3.Up,
                    new Vector2(0, 0.75f), new Vector2(1, 0.75f), new Vector2(1, 0.95f), new Vector2(0, 0.95f));
            }

            for (int dir = 0; dir < 4; dir++)
            {
                var drop = map.SideDrop(x, y, dir);
                if (drop <= 0) continue;
                var d = FoundationMap.Directions[dir];
                // steps leading up here cover this side
                if (map.StepDirection(x + d.X, y + d.Y) == (dir + 2) % 4) continue;

                // the edge's two ends, going clockwise round the tile
                Point a, b;
                switch (dir)
                {
                    case 0: a = new Point(x, y); b = new Point(x + 1, y); break;
                    case 1: a = new Point(x + 1, y); b = new Point(x + 1, y + 1); break;
                    case 2: a = new Point(x + 1, y + 1); b = new Point(x, y + 1); break;
                    default: a = new Point(x, y + 1); b = new Point(x, y); break;
                }
                var bottom = (map.RaiseOf(x, y) - drop) * tf;
                var ga = GroundAtVertex(a.X, a.Y);
                var gb = GroundAtVertex(b.X, b.Y);
                var normal = new Vector3(d.X, 0, d.Y);
                float ua = (d.Y != 0) ? a.X : a.Y, ub = (d.Y != 0) ? b.X : b.Y;
                AddQuad(verts, inds,
                    new Vector3(a.X, ga + bottom, a.Y), new Vector3(b.X, gb + bottom, b.Y),
                    new Vector3(b.X, gb + raise, b.Y), new Vector3(a.X, ga + raise, a.Y), normal,
                    new Vector2(ua, WallV(bottom)), new Vector2(ub, WallV(bottom)), new Vector2(ub, WallV(raise)), new Vector2(ua, WallV(raise)));
            }
        }

        /// <summary>A flight of steps from the ground up to the foundation it faces: three treads and four risers.</summary>
        private void AppendSteps(List<TerrainParallaxVertex> verts, List<int> inds, FoundationMap map, int x, int y)
        {
            var dir = map.StepDirection(x, y);
            var topUnits = map.StepTop(x, y);
            if (dir < 0 || topUnits <= 0) return;
            var top = topUnits * Bp.TerrainFactor;
            var ground = (GroundAtVertex(x, y) + GroundAtVertex(x + 1, y) + GroundAtVertex(x, y + 1) + GroundAtVertex(x + 1, y + 1)) / 4;
            var rise = new Vector3(FoundationMap.Directions[dir].X, 0, FoundationMap.Directions[dir].Y);

            // local (u along the rise from foot to head, w across, h up) to world (tile units)
            Func<float, float, float, Vector3> at = (u, w, h) =>
            {
                float px, py;
                switch (dir)
                {
                    case 0: px = x + w; py = y + 1 - u; break;
                    case 1: px = x + u; py = y + w; break;
                    case 2: px = x + w; py = y + u; break;
                    default: px = x + 1 - u; py = y + w; break;
                }
                return new Vector3(px, ground + h, py);
            };
            var across = at(0, 1, 0) - at(0, 0, 0);

            int treads = FoundationMap.StepCount - 1;
            float riser = top / FoundationMap.StepCount;
            for (int k = 0; k < treads; k++)
            {
                float u0 = k / (float)treads, u1 = (k + 1) / (float)treads;
                float h0 = k * riser, h1 = (k + 1) * riser;
                // riser
                AddQuad(verts, inds, at(u0, 0, h0), at(u0, 1, h0), at(u0, 1, h1), at(u0, 0, h1), -rise,
                    new Vector2(0, WallV(h0)), new Vector2(1, WallV(h0)), new Vector2(1, WallV(h1)), new Vector2(0, WallV(h1)));
                // tread
                AddQuad(verts, inds, at(u0, 0, h1), at(u0, 1, h1), at(u1, 1, h1), at(u1, 0, h1), Vector3.Up,
                    new Vector2(0, 0.75f + u0 * 0.2f), new Vector2(1, 0.75f + u0 * 0.2f), new Vector2(1, 0.75f + u1 * 0.2f), new Vector2(0, 0.75f + u1 * 0.2f));
                // the two sides of this step, down to the ground
                AddQuad(verts, inds, at(u0, 0, 0), at(u1, 0, 0), at(u1, 0, h1), at(u0, 0, h1), -across,
                    new Vector2(u0, WallV(0)), new Vector2(u1, WallV(0)), new Vector2(u1, WallV(h1)), new Vector2(u0, WallV(h1)));
                AddQuad(verts, inds, at(u0, 1, 0), at(u1, 1, 0), at(u1, 1, h1), at(u0, 1, h1), across,
                    new Vector2(u0, WallV(0)), new Vector2(u1, WallV(0)), new Vector2(u1, WallV(h1)), new Vector2(u0, WallV(h1)));
            }
            // the last riser, up the foundation's edge
            float hl = treads * riser;
            AddQuad(verts, inds, at(1, 0, hl), at(1, 1, hl), at(1, 1, top), at(1, 0, top), -rise,
                new Vector2(0, WallV(hl)), new Vector2(1, WallV(hl)), new Vector2(1, WallV(top)), new Vector2(0, WallV(top)));
        }

        /// <summary>
        /// The wall texture's v for a height above the ground: the bottom of a storey's wall is at the bottom of the
        /// texture, as FreeSO's 3D walls map it.
        /// </summary>
        private static float WallV(float height)
        {
            return 0.995f - (height / StoreyHeight) * 0.96f;
        }

        /// <summary>
        /// A quad (corners in order round it, tile units) seen from both sides, so it shows whichever way the camera
        /// culls.
        /// </summary>
        private static void AddQuad(List<TerrainParallaxVertex> verts, List<int> inds, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, Vector3 normal,
            Vector2 t0, Vector2 t1, Vector2 t2, Vector2 t3)
        {
            var baseI = verts.Count;
            var one = Vector4.One;
            verts.Add(new TerrainParallaxVertex(p0 * 3f, one, t0, 0, normal));
            verts.Add(new TerrainParallaxVertex(p1 * 3f, one, t1, 0, normal));
            verts.Add(new TerrainParallaxVertex(p2 * 3f, one, t2, 0, normal));
            verts.Add(new TerrainParallaxVertex(p3 * 3f, one, t3, 0, normal));
            inds.Add(baseI); inds.Add(baseI + 1); inds.Add(baseI + 2);
            inds.Add(baseI); inds.Add(baseI + 2); inds.Add(baseI + 3);
            inds.Add(baseI); inds.Add(baseI + 2); inds.Add(baseI + 1);
            inds.Add(baseI); inds.Add(baseI + 3); inds.Add(baseI + 2);
        }

        /// <summary>Finds this group's wall covering texture (needs the world's sprite cache), before drawing.</summary>
        public void ResolveTexture(GraphicsDevice gd, WorldState state)
        {
            if (Texture != null && !Texture.IsDisposed) return;
            Texture = null;
            if (WallPattern != 0 && state != null)
            {
                try
                {
                    var wall = Content.Content.Get().WorldWalls.Get(WallPattern);
                    var frame = wall?.Near?.Frames;
                    if (frame != null && frame.Count > 2) Texture = state._2D.GetTexture(frame[2]);
                }
                catch (Exception)
                {
                    Texture = null;
                }
            }
            if (Texture == null) Texture = Common.Utils.TextureGenerator.GetPxWhite(gd);
        }

        public override void PrepareGPU(GraphicsDevice gd)
        {
            GPUData?.Dispose();
            VertGPUData?.Dispose();
            GPUData = null;
            VertGPUData = null;
            GenerateGeometry();
            PrimitiveCount = Indices.Length / 3;
            if (PrimitiveCount == 0) return;

            GPUData = new IndexBuffer(gd, IndexElementSize.ThirtyTwoBits, Indices.Length, BufferUsage.None);
            GPUData.SetData(Indices);
            VertGPUData = new VertexBuffer(gd, typeof(TerrainParallaxVertex), Vertices.Length, BufferUsage.None);
            VertGPUData.SetData(Vertices);
        }

        public override Texture2D GetTexture(GraphicsDevice gd)
        {
            return Texture ?? Common.Utils.TextureGenerator.GetPxWhite(gd);
        }

        public override void Dispose()
        {
            GPUData?.Dispose();
            VertGPUData?.Dispose();
        }
    }
}
