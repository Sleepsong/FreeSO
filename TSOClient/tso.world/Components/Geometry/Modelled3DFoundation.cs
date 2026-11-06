using FSO.Content;
using FSO.LotView.Model;
using FSO.LotView.Utils;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FSO.LotView.Components.Geometry
{
    /// <summary>
    /// The visible parts of foundations (see <see cref="FoundationMap"/>), as in The Sims 2: concrete, with a cap round
    /// the top edge that stands out a little past the walls built on it, concrete steps, and a concrete top where no
    /// floor is laid. The sides under the cap are concrete too, or a wall covering painted on them (side by side, as on
    /// walls). One group per side covering: the plain group (<see cref="WallPattern"/> 0) has the plain sides and every
    /// foundation's cap, bare top and steps; a covered group only has the sides painted with its covering. Drawn with the
    /// ground floor by <see cref="_3DFloorGeometry"/>, in 2D and 3D alike, like the pools' models. Positions are in
    /// world units (3 per tile), heights without the lot's base altitude (the floor drawing takes that off).
    /// </summary>
    public class Modelled3DFoundation : Modelled3DFloor
    {
        /// <summary>The wall covering on this group's sides (0 for plain concrete).</summary>
        public ushort WallPattern;
        private Texture2D Texture;

        protected const float StoreyHeight = 2.95f;
        /// <summary>How tall the cap round a foundation's top edge is (tile units).</summary>
        public const float CapHeight = 0.12f;
        /// <summary>How far the cap stands out from the sides: just past the face of a wall built on the edge.</summary>
        public const float CapOverhang = 0.1f;

        public Modelled3DFoundation(Blueprint bp, ushort pattern) : base(bp)
        {
            WallPattern = pattern;
        }

        /// <summary>
        /// Builds the geometry for every foundation tile and steps tile in this group. A tile with floor laid on it gets
        /// no top here (the floor is drawn raised instead).
        /// </summary>
        protected override void GenerateGeometry()
        {
            var verts = new List<TerrainParallaxVertex>();
            var inds = new List<int>();
            var map = Bp.Foundations;
            if (map != null && map.Any)
            {
                var floors = Bp.Floors[0];
                var plain = WallPattern == 0;
                for (int y = 0; y < map.Height; y++)
                {
                    for (int x = 0; x < map.Width; x++)
                    {
                        var i = y * map.Width + x;
                        if (map.Raise[i] > 0)
                        {
                            AppendSides(verts, inds, map, x, y);
                            if (plain) AppendTopAndCap(verts, inds, map, x, y, floors[i].Pattern == 0);
                        }
                        else if (plain && map.Steps[i] != 0) AppendSteps(verts, inds, map, x, y);
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

        /// <summary>
        /// The groups the foundations need: the plain one (caps, tops and steps) whenever there are any, and one per
        /// other covering on their sides.
        /// </summary>
        public static HashSet<ushort> Groups(FoundationMap map)
        {
            var result = new HashSet<ushort>();
            if (map == null || !map.Any) return result;
            for (int y = 0; y < map.Height; y++)
            {
                for (int x = 0; x < map.Width; x++)
                {
                    if (!HasGeometry(map, x, y)) continue;
                    result.Add(0);
                    for (int dir = 0; dir < 4; dir++)
                        if (map.SideShows(x, y, dir)) result.Add(map.SideOf(x, y, dir));
                }
            }
            return result;
        }

        /// <summary>Ground height (without foundations or the lot's base altitude) at a terrain vertex.</summary>
        protected float GroundAtVertex(int x, int y)
        {
            var alt = Bp.Altitude;
            if (alt == null) return 0;
            x = Math.Max(0, Math.Min(Bp.Width - 1, x));
            y = Math.Max(0, Math.Min(Bp.Height - 1, y));
            return alt[y * Bp.Width + x] * Bp.TerrainFactor;
        }

        /// <summary>
        /// Whether a foundation tile's side shows, with a cap on top: it drops to lower ground or a lower foundation, and
        /// no steps lead up to it.
        /// </summary>
        public static bool Capped(FoundationMap map, int x, int y, int dir) => map.SideShows(x, y, dir);

        /// <summary>How the outline round the foundations goes on at one end of a capped side.</summary>
        public enum CapEnd
        {
            /// <summary>Turns outward, round this tile: the cap runs on round the corner.</summary>
            Convex,
            /// <summary>Goes straight on along the next tile's side.</summary>
            Straight,
            /// <summary>Turns inward, round the tile beside: the cap meets the next one's face.</summary>
            Concave,
            /// <summary>Stops (at steps, or the lot's edge): the cap gets an end.</summary>
            Free
        }

        /// <summary>
        /// The end of a capped side at its corner <paramref name="b"/> (the end going clockwise round the tile, see
        /// <see cref="EdgeEnds"/>) or the other.
        /// </summary>
        public static CapEnd EndOf(FoundationMap map, int x, int y, int dir, bool b)
        {
            var d = FoundationMap.Directions[dir];
            var e = FoundationMap.Directions[(dir + 1) % 4]; //along the side, toward b
            if (!b) e = new Point(-e.X, -e.Y);
            int turn = b ? 1 : 3;
            if (Capped(map, x, y, (dir + turn) % 4)) return CapEnd.Convex;
            if (Capped(map, x + e.X, y + e.Y, dir)) return CapEnd.Straight;
            if (Capped(map, x + e.X + d.X, y + e.Y + d.Y, (dir + 4 - turn) % 4)) return CapEnd.Concave;
            return CapEnd.Free;
        }

        /// <summary>A tile's side's two ends (corner vertices), going clockwise round the tile.</summary>
        public static void EdgeEnds(int x, int y, int dir, out Point a, out Point b)
        {
            switch (dir)
            {
                case 0: a = new Point(x, y); b = new Point(x + 1, y); break;
                case 1: a = new Point(x + 1, y); b = new Point(x + 1, y + 1); break;
                case 2: a = new Point(x + 1, y + 1); b = new Point(x, y + 1); break;
                default: a = new Point(x, y + 1); b = new Point(x, y); break;
            }
        }

        /// <summary>
        /// A foundation tile's sides painted with this group's covering, from the ground (or the lower foundation beside) up
        /// to the cap.
        /// </summary>
        private void AppendSides(List<TerrainParallaxVertex> verts, List<int> inds, FoundationMap map, int x, int y)
        {
            var tf = Bp.TerrainFactor;
            var raise = map.RaiseOf(x, y) * tf;
            var sideTop = raise - CapHeight;
            for (int dir = 0; dir < 4; dir++)
            {
                if (!Capped(map, x, y, dir) || map.SideOf(x, y, dir) != WallPattern) continue;
                var bottom = (map.RaiseOf(x, y) - map.SideDrop(x, y, dir)) * tf;
                if (sideTop <= bottom) continue;
                var d = FoundationMap.Directions[dir];
                EdgeEnds(x, y, dir, out var a, out var b);
                var ga = GroundAtVertex(a.X, a.Y);
                var gb = GroundAtVertex(b.X, b.Y);
                float ua = (d.Y != 0) ? a.X : a.Y, ub = (d.Y != 0) ? b.X : b.Y;
                AddQuad(verts, inds,
                    new Vector3(a.X, ga + bottom, a.Y), new Vector3(b.X, gb + bottom, b.Y),
                    new Vector3(b.X, gb + sideTop, b.Y), new Vector3(a.X, ga + sideTop, a.Y), new Vector3(d.X, 0, d.Y),
                    new Vector2(ua, SideV(bottom, sideTop)), new Vector2(ub, SideV(bottom, sideTop)),
                    new Vector2(ub, SideV(sideTop, sideTop)), new Vector2(ua, SideV(sideTop, sideTop)));
            }
        }

        /// <summary>
        /// The texture's v on a foundation's side: concrete at the floor's scale, or a wall covering's middle (where it
        /// has no skirting or trim), its top under the cap.
        /// </summary>
        private float SideV(float height, float sideTop)
        {
            if (WallPattern == 0) return -height;
            return 0.45f + ((sideTop - height) / StoreyHeight) * 0.96f;
        }

        /// <summary>A foundation tile's concrete: its top if nothing is laid on it, and the cap on its showing sides.</summary>
        private void AppendTopAndCap(List<TerrainParallaxVertex> verts, List<int> inds, FoundationMap map, int x, int y, bool top)
        {
            var raise = map.RaiseOf(x, y) * Bp.TerrainFactor;
            var capBottom = raise - CapHeight;

            if (top)
            {
                // a bare foundation: its own top, flat at the foundation's height over the ground's corners
                AddQuad(verts, inds,
                    new Vector3(x, GroundAtVertex(x, y) + raise, y), new Vector3(x + 1, GroundAtVertex(x + 1, y) + raise, y),
                    new Vector3(x + 1, GroundAtVertex(x + 1, y + 1) + raise, y + 1), new Vector3(x, GroundAtVertex(x, y + 1) + raise, y + 1),
                    Vector3.Up, new Vector2(x, y), new Vector2(x + 1, y), new Vector2(x + 1, y + 1), new Vector2(x, y + 1));
            }

            for (int dir = 0; dir < 4; dir++)
            {
                if (!Capped(map, x, y, dir)) continue;
                var d = FoundationMap.Directions[dir];
                var e = FoundationMap.Directions[(dir + 1) % 4];
                EdgeEnds(x, y, dir, out var a, out var b);
                var ga = GroundAtVertex(a.X, a.Y);
                var gb = GroundAtVertex(b.X, b.Y);
                var endA = EndOf(map, x, y, dir, false);
                var endB = EndOf(map, x, y, dir, true);

                var along = new Vector2(e.X, e.Y);
                var outward = new Vector2(d.X, d.Y) * CapOverhang;
                var pa = new Vector2(a.X, a.Y);
                var pb = new Vector2(b.X, b.Y);

                // the cap's face: round a corner it runs on to meet the next face; at an inside corner it stops at it
                var faceA = pa - along * EndShift(endA);
                var faceB = pb + along * EndShift(endB);
                Func<Vector2, float, float, Vector3> at = (p, ground, h) => new Vector3(p.X, ground + h, p.Y);
                Func<Vector2, float> u = (p) => (d.Y != 0) ? p.X : p.Y;
                AddQuad(verts, inds,
                    at(faceA + outward, ga, capBottom), at(faceB + outward, gb, capBottom),
                    at(faceB + outward, gb, raise), at(faceA + outward, ga, raise), new Vector3(d.X, 0, d.Y),
                    new Vector2(u(faceA), -capBottom), new Vector2(u(faceB), -capBottom), new Vector2(u(faceB), -raise), new Vector2(u(faceA), -raise));

                // its top and underside: the corner square outside a convex corner goes with the side before it
                // (clockwise), and the one inside a concave corner with the side after it, so none are drawn twice
                var stripA = pa + ((endA == CapEnd.Concave) ? along * CapOverhang : Vector2.Zero);
                var stripB = pb + ((endB == CapEnd.Convex) ? along * CapOverhang : Vector2.Zero);
                foreach (var h in new[] { raise, capBottom })
                {
                    AddQuad(verts, inds,
                        at(stripA, ga, h), at(stripB, gb, h), at(stripB + outward, gb, h), at(stripA + outward, ga, h),
                        (h == raise) ? Vector3.Up : Vector3.Down,
                        stripA, stripB, stripB + outward, stripA + outward);
                }

                // where the cap stops (at steps), its end
                if (endA == CapEnd.Free) AppendCapEnd(verts, inds, pa, outward, ga, capBottom, raise, -along);
                if (endB == CapEnd.Free) AppendCapEnd(verts, inds, pb, outward, gb, capBottom, raise, along);
            }
        }

        /// <summary>How much further a cap's face goes past its side's end: round a corner, or short of the face it meets.</summary>
        private static float EndShift(CapEnd end)
        {
            switch (end)
            {
                case CapEnd.Convex: return CapOverhang;
                case CapEnd.Concave: return -CapOverhang;
                default: return 0;
            }
        }

        private static void AppendCapEnd(List<TerrainParallaxVertex> verts, List<int> inds, Vector2 p, Vector2 outward, float ground, float bottom, float top, Vector2 facing)
        {
            var q = p + outward;
            var w = outward.Length();
            AddQuad(verts, inds,
                new Vector3(p.X, ground + bottom, p.Y), new Vector3(q.X, ground + bottom, q.Y),
                new Vector3(q.X, ground + top, q.Y), new Vector3(p.X, ground + top, p.Y), new Vector3(facing.X, 0, facing.Y),
                new Vector2(0, -bottom), new Vector2(w, -bottom), new Vector2(w, -top), new Vector2(0, -top));
        }

        /// <summary>A flight of concrete steps from the ground up to the foundation it faces: three treads and four risers.</summary>
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
            // concrete at the floor's scale: the treads by where they are on the lot, the risers and sides by height
            Func<Vector3, Vector2> flat = (p) => new Vector2(p.X, p.Z);
            var across = at(0, 1, 0) - at(0, 0, 0);

            int treads = FoundationMap.StepCount - 1;
            float riser = top / FoundationMap.StepCount;
            for (int k = 0; k < treads; k++)
            {
                float u0 = k / (float)treads, u1 = (k + 1) / (float)treads;
                float h0 = k * riser, h1 = (k + 1) * riser;
                // riser
                AddQuad(verts, inds, at(u0, 0, h0), at(u0, 1, h0), at(u0, 1, h1), at(u0, 0, h1), -rise,
                    new Vector2(0, -h0), new Vector2(1, -h0), new Vector2(1, -h1), new Vector2(0, -h1));
                // tread
                var t0 = at(u0, 0, h1); var t1 = at(u0, 1, h1); var t2 = at(u1, 1, h1); var t3 = at(u1, 0, h1);
                AddQuad(verts, inds, t0, t1, t2, t3, Vector3.Up, flat(t0), flat(t1), flat(t2), flat(t3));
                // the two sides of this step, down to the ground
                AddQuad(verts, inds, at(u0, 0, 0), at(u1, 0, 0), at(u1, 0, h1), at(u0, 0, h1), -across,
                    new Vector2(u0, 0), new Vector2(u1, 0), new Vector2(u1, -h1), new Vector2(u0, -h1));
                AddQuad(verts, inds, at(u0, 1, 0), at(u1, 1, 0), at(u1, 1, h1), at(u0, 1, h1), across,
                    new Vector2(u0, 0), new Vector2(u1, 0), new Vector2(u1, -h1), new Vector2(u0, -h1));
            }
            // the last riser, up the foundation's edge
            float hl = treads * riser;
            AddQuad(verts, inds, at(1, 0, hl), at(1, 1, hl), at(1, 1, top), at(1, 0, top), -rise,
                new Vector2(0, -hl), new Vector2(1, -hl), new Vector2(1, -top), new Vector2(0, -top));
        }

        /// <summary>
        /// The wall texture's v for a height above the ground: the bottom of a storey's wall is at the bottom of the
        /// texture, as FreeSO's 3D walls map it.
        /// </summary>
        protected static float WallV(float height)
        {
            return 0.995f - (height / StoreyHeight) * 0.96f;
        }

        /// <summary>
        /// A quad (corners in order round it, tile units) seen from both sides, so it shows whichever way the camera
        /// culls.
        /// </summary>
        protected static void AddQuad(List<TerrainParallaxVertex> verts, List<int> inds, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, Vector3 normal,
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

        private static ushort? Concrete;

        /// <summary>
        /// The floor whose texture the foundations' concrete takes: the game's concrete floor (or failing that a stone
        /// one, or the first), 0 if there are no floors.
        /// </summary>
        public static ushort ConcreteFloor()
        {
            if (Concrete != null) return Concrete.Value;
            ushort found = 0;
            try
            {
                var floors = Content.Content.Get().WorldFloors.List().OfType<FloorReference>().Where(f => f.ID > 0 && f.ID < 65503).ToList();
                foreach (var word in new[] { "concrete", "cement", "stone" })
                {
                    var match = floors.FirstOrDefault(f => (f.Name ?? "").IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (match != null) { found = (ushort)match.ID; break; }
                }
                if (found == 0) found = (ushort)(floors.FirstOrDefault()?.ID ?? 0);
            }
            catch (Exception)
            {
                found = 0;
            }
            Concrete = found;
            return found;
        }

        /// <summary>Finds this group's texture (needs the world's sprite cache), before drawing: concrete, or its wall covering.</summary>
        public void ResolveTexture(GraphicsDevice gd, WorldState state)
        {
            if (Texture != null && !Texture.IsDisposed) return;
            Texture = null;
            if (state != null)
            {
                try
                {
                    if (WallPattern != 0)
                    {
                        var frame = Content.Content.Get().WorldWalls.Get(WallPattern)?.Near?.Frames;
                        if (frame != null && frame.Count > 2) Texture = state._2D.GetTexture(frame[2]);
                    }
                    else
                    {
                        var floor = ConcreteFloor();
                        var near = (floor == 0) ? null : Content.Content.Get().WorldFloors.Get(floor)?.Near;
                        if (near != null && near.Frames.Length > 0) Texture = state._2D.GetTexture(near.Frames[0]);
                    }
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
