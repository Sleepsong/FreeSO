using FSO.LotView.Model;
using FSO.LotView.Utils;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;

namespace FSO.LotView.Components.Geometry
{
    /// <summary>
    /// The visible parts of foundations and decks (see <see cref="FoundationMap"/>), as in The Sims 2, made from the
    /// game's own textures (see <see cref="FoundationLook"/>):
    ///  - a foundation: brick sides (or a wall covering painted on them, side by side as on walls) and a plywood top
    ///    where no floor is laid;
    ///  - a latticed deck: planks on top, a board round the edge, lattice under it;
    ///  - a column deck: planks on top, a board round the edge, open underneath (the lot's ground is drawn there, see
    ///    <see cref="TerrainComponent.GroundVertexBuffer"/>), on columns.
    /// Everything stays on its own tiles: nothing stands out past their edges.
    /// Steps are their staircase's bottom piece (drawn as an object, see <see cref="FoundationStepsObjects"/>); only steps
    /// with no staircase to take are drawn here, plain.
    /// One group (model) per material, and per covering for foundations' sides (<see cref="Groups"/>). Drawn with the
    /// ground floor by <see cref="_3DFloorGeometry"/>, in 2D and 3D alike, like the pools' models. Positions are in world
    /// units (3 per tile), heights without the lot's base altitude (the floor drawing takes that off).
    /// </summary>
    public class Modelled3DFoundation : Modelled3DFloor
    {
        /// <summary>What this group's parts are made of.</summary>
        public FoundationMaterial Material;
        /// <summary>For <see cref="FoundationMaterial.Covering"/>: the wall covering on this group's sides (0 for the brick).</summary>
        public ushort WallPattern;
        protected Texture2D Texture;

        protected const float StoreyHeight = 2.95f;
        /// <summary>How tall the board round a deck's edge is (tile units).</summary>
        public const float FasciaHeight = 0.16f;
        /// <summary>A column deck's columns: radius, and their base's.</summary>
        public const float ColumnRadius = 0.1f, ColumnBaseRadius = 0.15f, ColumnBaseHeight = 0.06f;
        private const int ColumnSides = 12;

        public Modelled3DFoundation(Blueprint bp, ushort pattern) : this(bp, FoundationMaterial.Covering, pattern)
        {
        }

        public Modelled3DFoundation(Blueprint bp, FoundationMaterial material, ushort pattern) : base(bp)
        {
            Material = material;
            WallPattern = (material == FoundationMaterial.Covering) ? pattern : (ushort)0;
        }

        /// <summary>A quad for a part of a foundation: corners in order round it (tile units), its facing, texture coordinates.</summary>
        public delegate void QuadSink(FoundationMaterial material, ushort pattern, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, Vector3 normal,
            Vector2 t0, Vector2 t1, Vector2 t2, Vector2 t3);

        /// <summary>
        /// Builds this group's geometry: every part of every foundation, deck and plain set of steps made of its material.
        /// A tile with floor laid on it gets no top here (the floor is drawn raised instead).
        /// </summary>
        protected override void GenerateGeometry()
        {
            var verts = new List<TerrainParallaxVertex>();
            var inds = new List<int>();
            var map = Bp.Foundations;
            if (map != null && map.Any)
            {
                QuadSink sink = (m, pattern, p0, p1, p2, p3, n, t0, t1, t2, t3) =>
                {
                    if (m != Material || (m == FoundationMaterial.Covering && pattern != WallPattern)) return;
                    AddQuad(verts, inds, p0, p1, p2, p3, n, t0, t1, t2, t3);
                };
                Build(map, sink);
            }
            Vertices = verts.ToArray();
            Indices = inds.ToArray();
        }

        /// <summary>Every part of every foundation, deck and plain set of steps on the lot, to <paramref name="sink"/>.</summary>
        public void Build(FoundationMap map, QuadSink sink)
        {
            var floors = Bp.Floors[0];
            var columns = ColumnTiles(map);
            for (int y = 0; y < map.Height; y++)
            {
                for (int x = 0; x < map.Width; x++)
                {
                    var i = y * map.Width + x;
                    //(no bare top over a stairwell into a basement under it: that's a hole)
                    if (map.Raise[i] > 0) AppendTile(map, x, y, floors[i].Pattern == 0 && Bp.Basement?.IsDug(x, y) != true, columns.Contains(i), sink);
                    else if (map.Steps[i] != 0 && FoundationLook.StairFor(map.StepStyle[i]) == null) AppendSteps(map, x, y, sink);
                }
            }
        }

        public static bool HasGeometry(FoundationMap map, int x, int y)
        {
            var i = y * map.Width + x;
            return map.Raise[i] > 0 || (map.Steps[i] != 0 && map.StepTop(x, y) > 0);
        }

        /// <summary>
        /// The groups (material, and covering for foundations' sides) the lot's foundations and decks need. Some may
        /// come out empty (a plywood top under a laid floor, say), which is fine.
        /// </summary>
        public static HashSet<(FoundationMaterial, ushort)> Groups(FoundationMap map)
        {
            var result = new HashSet<(FoundationMaterial, ushort)>();
            if (map == null || !map.Any) return result;
            for (int y = 0; y < map.Height; y++)
            {
                for (int x = 0; x < map.Width; x++)
                {
                    var i = y * map.Width + x;
                    if (map.Raise[i] > 0)
                    {
                        switch (map.KindOf(x, y))
                        {
                            case FoundationKind.Foundation:
                                result.Add((FoundationMaterial.Plywood, 0));
                                for (int dir = 0; dir < 4; dir++)
                                    if (Capped(map, x, y, dir)) result.Add((FoundationMaterial.Covering, map.SideOf(x, y, dir)));
                                break;
                            case FoundationKind.LatticedDeck:
                                result.Add((FoundationMaterial.Planks, 0));
                                result.Add((FoundationMaterial.Lattice, 0));
                                break;
                            case FoundationKind.ColumnDeck:
                                result.Add((FoundationMaterial.Planks, 0));
                                result.Add((FoundationMaterial.Column, 0));
                                break;
                        }
                    }
                    else if (map.Steps[i] != 0 && map.StepTop(x, y) > 0 && FoundationLook.StairFor(map.StepStyle[i]) == null)
                        result.Add((FoundationMaterial.Steps, 0));
                }
            }
            return result;
        }

        /// <summary>
        /// The column deck tiles with a column under them: one every other tile or so, each deck tile within a tile of
        /// one (going along the rows, a tile gets one if none of its neighbours has).
        /// </summary>
        public static HashSet<int> ColumnTiles(FoundationMap map)
        {
            var result = new HashSet<int>();
            for (int y = 0; y < map.Height; y++)
            {
                for (int x = 0; x < map.Width; x++)
                {
                    var i = y * map.Width + x;
                    if (map.Raise[i] <= 0 || map.KindOf(x, y) != FoundationKind.ColumnDeck) continue;
                    var near = false;
                    for (int dy = -1; dy <= 1 && !near; dy++)
                        for (int dx = -1; dx <= 1 && !near; dx++)
                            if (map.InBounds(x + dx, y + dy) && result.Contains((y + dy) * map.Width + x + dx)) near = true;
                    if (!near) result.Add(i);
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

        private float GroundAt(Point p) => GroundAtVertex(p.X, p.Y);

        /// <summary>
        /// Whether a foundation tile's side is drawn: it drops to lower ground or a lower foundation, and no plain steps
        /// cover it (a staircase's piece in front of it doesn't).
        /// </summary>
        public static bool Capped(FoundationMap map, int x, int y, int dir)
        {
            if (!map.SideShows(x, y, dir)) return false;
            if (!map.StepsUpTo(x, y, dir)) return true;
            var d = FoundationMap.Directions[dir];
            return FoundationLook.StairFor(map.StepStyle[(y + d.Y) * map.Width + x + d.X]) != null;
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

        private void AppendTile(FoundationMap map, int x, int y, bool bareTop, bool column, QuadSink sink)
        {
            var raise = map.RaiseOf(x, y) * Bp.TerrainFactor;
            var kind = map.KindOf(x, y);
            var deck = kind != FoundationKind.Foundation;
            var bandHeight = deck ? FasciaHeight : 0f; //a deck's board round its edge; a foundation's sides go right up
            var underBand = raise - bandHeight;
            var c00 = new Vector3(x, GroundAtVertex(x, y) + raise, y);
            var c10 = new Vector3(x + 1, GroundAtVertex(x + 1, y) + raise, y);
            var c11 = new Vector3(x + 1, GroundAtVertex(x + 1, y + 1) + raise, y + 1);
            var c01 = new Vector3(x, GroundAtVertex(x, y + 1) + raise, y + 1);

            if (bareTop)
            {
                if (deck) // planks laid diagonally, as on The Sims 2's decks
                    sink(FoundationMaterial.Planks, 0, c00, c10, c11, c01, Vector3.Up, Diagonal(x, y), Diagonal(x + 1, y), Diagonal(x + 1, y + 1), Diagonal(x, y + 1));
                else
                    sink(FoundationMaterial.Plywood, 0, c00, c10, c11, c01, Vector3.Up, new Vector2(x, y), new Vector2(x + 1, y), new Vector2(x + 1, y + 1), new Vector2(x, y + 1));
            }

            if (kind == FoundationKind.ColumnDeck)
            {
                // open underneath: the deck's underside, under the board's bottom
                var down = new Vector3(0, -bandHeight, 0);
                sink(FoundationMaterial.Planks, 0, c00 + down, c01 + down, c11 + down, c10 + down, Vector3.Down,
                    Diagonal(x, y), Diagonal(x, y + 1), Diagonal(x + 1, y + 1), Diagonal(x + 1, y));
                if (column)
                {
                    var g = (GroundAtVertex(x, y) + GroundAtVertex(x + 1, y) + GroundAtVertex(x, y + 1) + GroundAtVertex(x + 1, y + 1)) / 4;
                    AppendColumn(new Vector2(x + 0.5f, y + 0.5f), g, g + underBand, sink);
                }
            }

            for (int dir = 0; dir < 4; dir++)
            {
                if (!Capped(map, x, y, dir)) continue;
                var d = FoundationMap.Directions[dir];
                EdgeEnds(x, y, dir, out var a, out var b);
                var ga = GroundAt(a);
                var gb = GroundAt(b);
                var normal = new Vector3(d.X, 0, d.Y);
                float ua = (d.Y != 0) ? a.X : a.Y, ub = (d.Y != 0) ? b.X : b.Y;

                // the side under the band: the foundation's covering, or the lattice
                if (kind != FoundationKind.ColumnDeck)
                {
                    var bottom = map.SideBottom(x, y, dir) * Bp.TerrainFactor;
                    if (underBand > bottom)
                    {
                        if (deck)
                            sink(FoundationMaterial.Lattice, 0,
                                new Vector3(a.X, ga + bottom, a.Y), new Vector3(b.X, gb + bottom, b.Y),
                                new Vector3(b.X, gb + underBand, b.Y), new Vector3(a.X, ga + underBand, a.Y), normal,
                                new Vector2(ua, -bottom), new Vector2(ub, -bottom), new Vector2(ub, -underBand), new Vector2(ua, -underBand));
                        else
                        {
                            //cut round a basement window's opening, where a basement under it has one in a wall on this side
                            var pattern = map.SideOf(x, y, dir);
                            var opening = BasementWindowOn(x, y, dir);
                            //(the mask's laid on the wall from one end: on a tile's +x and +y sides that's this side's far end)
                            if (opening?.Open == null || !ShapedSide(sink, pattern, a, b, ga, gb, bottom, underBand, raise, ua, ub, normal,
                                opening.Value, dir == 1 || dir == 2))
                                CoveringPiece(sink, pattern, a, b, ga, gb, 0, 1, bottom, underBand, ua, ub, normal, underBand);
                        }
                    }
                }

                // a deck's board, flush with its side (not where a column deck as high carries on: it's inside that deck)
                if (deck && map.SideDrop(x, y, dir) > 0)
                {
                    var along = FoundationLook.PlanksAlongU();
                    Func<float, float, Vector2> uv = (u, h) => along ? new Vector2(u, -h) : new Vector2(-h, u); //one plank, lengthways
                    sink(FoundationMaterial.Planks, 0,
                        new Vector3(a.X, ga + underBand, a.Y), new Vector3(b.X, gb + underBand, b.Y),
                        new Vector3(b.X, gb + raise, b.Y), new Vector3(a.X, ga + raise, a.Y), normal,
                        uv(ua, underBand), uv(ub, underBand), uv(ub, raise), uv(ua, raise));
                }
            }
        }

        /// <summary>A deck's planks run diagonally: texture coordinates turned an eighth, at the floor's scale.</summary>
        private static Vector2 Diagonal(float x, float y)
        {
            const float s = 0.70710678f;
            return new Vector2((x + y) * s, (y - x) * s);
        }

        /// <summary>
        /// The texture's v on a foundation's side: a wall covering's middle (where it has no skirting or trim), its top
        /// at the foundation's top.
        /// </summary>
        /// <summary>
        /// The opening of a window in a basement wall on a foundation tile's side (see <see cref="WallOpening"/>), where the
        /// basement under it has one: the foundation's side is cut to show it. Null for none (or a door).
        /// </summary>
        private WallOpening? BasementWindowOn(int x, int y, int dir)
        {
            if (Bp.Basement?.IsDug(x, y) != true) return null;
            var walls = Bp.Walls?[-1];
            if (walls == null) return null;
            //a tile's walls are its -x side (top left) and -y side (top right); its +x and +y sides are its neighbours'
            int wx = x, wy = y;
            bool topLeft;
            switch (dir)
            {
                case 0: topLeft = false; break;
                case 1: wx = x + 1; topLeft = true; break;
                case 2: wy = y + 1; topLeft = false; break;
                default: topLeft = true; break;
            }
            if (wx < 0 || wy < 0 || wx >= Bp.Width || wy >= Bp.Height) return null;
            var wall = walls[wy * Bp.Width + wx];
            if ((wall.Segments & (topLeft ? WallSegments.TopLeft : WallSegments.TopRight)) == 0) return null;
            if (topLeft ? wall.TopLeftDoor : wall.TopRightDoor) return null;
            var style = topLeft ? wall.ObjSetTLStyle : wall.ObjSetTRStyle;
            if (style == 0) return null;
            return WallOpening.OfStyle(style);
        }

        /// <summary>A piece of a side's covering: from <paramref name="u0"/> to <paramref name="u1"/> along it, <paramref name="h0"/> to <paramref name="h1"/> up it.</summary>
        private static void CoveringPiece(QuadSink sink, ushort pattern, Point a, Point b, float ga, float gb, float u0, float u1, float h0, float h1,
            float ua, float ub, Vector3 normal, float sideTop)
        {
            if (u1 - u0 < 0.001f || h1 - h0 < 0.001f) return;
            Func<float, float, Vector3> at = (u, h) => new Vector3(a.X + (b.X - a.X) * u, ga + (gb - ga) * u + h, a.Y + (b.Y - a.Y) * u);
            float t0 = ua + (ub - ua) * u0, t1 = ua + (ub - ua) * u1;
            sink(FoundationMaterial.Covering, pattern, at(u0, h0), at(u1, h0), at(u1, h1), at(u0, h1), normal,
                new Vector2(t0, SideV(h0, sideTop)), new Vector2(t1, SideV(h0, sideTop)), new Vector2(t1, SideV(h1, sideTop)), new Vector2(t0, SideV(h1, sideTop)));
        }

        /// <summary>How deep the hole for a basement window in a foundation's side is lined: to the basement wall's inside face.</summary>
        public const float RevealDepth = 0.075f;

        /// <summary>
        /// A side with a basement window's opening cut in it, the opening's own shape (its wall style's mask, cell by cell: see
        /// <see cref="WallOpening.Open"/>), lined <see cref="RevealDepth"/> in where the brick meets the hole. The side's heights
        /// run from <paramref name="bottom"/> (the ground) to <paramref name="sideTop"/>; the wall under it goes up to the
        /// foundation's top, <paramref name="raise"/>. <paramref name="mirrored"/>: the mask runs from this side's far end.
        /// False if none of the opening is above the ground (the side is drawn whole).
        /// </summary>
        private static bool ShapedSide(QuadSink sink, ushort pattern, Point a, Point b, float ga, float gb, float bottom, float sideTop, float raise,
            float ua, float ub, Vector3 normal, WallOpening opening, bool mirrored)
        {
            const int gu = WallOpening.GridU, gk = WallOpening.GridK;
            Func<int, float> rowH = (j) => raise - (1 - j / (float)gk) * StoreyHeight; //(row j's foot, on the side)
            Func<int, int> col = (sCol) => mirrored ? gu - 1 - sCol : sCol;           //(the mask's column at this side's column)
            Func<int, int, bool> open = (sCol, j) => sCol >= 0 && sCol < gu && opening.IsOpen(col(sCol), j)
                && rowH(j + 1) > bottom + 0.001f && rowH(j) < sideTop - 0.001f;
            int jMin = int.MaxValue, jMax = int.MinValue;
            for (int j = 0; j < gk; j++)
                for (int sc = 0; sc < gu; sc++)
                    if (open(sc, j)) { jMin = Math.Min(jMin, j); jMax = Math.Max(jMax, j); }
            if (jMin > jMax) return false;

            //whole below and above the opening's rows; along its rows, the brick either side of the hole
            var holeFoot = Math.Max(bottom, rowH(jMin));
            var holeHead = Math.Min(sideTop, rowH(jMax + 1));
            CoveringPiece(sink, pattern, a, b, ga, gb, 0, 1, bottom, holeFoot, ua, ub, normal, sideTop);
            CoveringPiece(sink, pattern, a, b, ga, gb, 0, 1, holeHead, sideTop, ua, ub, normal, sideTop);
            Func<float, float, Vector3> at = (u, h) => new Vector3(a.X + (b.X - a.X) * u, ga + (gb - ga) * u + h, a.Y + (b.Y - a.Y) * u);
            var inward = -normal * RevealDepth;
            var along = new Vector3(b.X - a.X, 0, b.Y - a.Y);
            float dv = RevealDepth * 0.96f / StoreyHeight;
            Func<float, float> tAt = (u) => ua + (ub - ua) * u;
            for (int j = jMin; j <= jMax; j++)
            {
                float h0 = Math.Max(bottom, rowH(j)), h1 = Math.Min(sideTop, rowH(j + 1));
                if (h1 - h0 < 0.001f) continue;
                float v0 = SideV(h0, sideTop), v1 = SideV(h1, sideTop);
                //the brick: runs of shut cells along the row
                int run = -1;
                for (int sc = 0; sc <= gu; sc++)
                {
                    var shut = sc < gu && !open(sc, j);
                    if (shut && run < 0) run = sc;
                    else if (!shut && run >= 0)
                    {
                        CoveringPiece(sink, pattern, a, b, ga, gb, run / (float)gu, sc / (float)gu, h0, h1, ua, ub, normal, sideTop);
                        run = -1;
                    }
                }
                //the lining: wherever an open cell meets brick (or the ground, at its sill)
                for (int sc = 0; sc < gu; sc++)
                {
                    if (!open(sc, j)) continue;
                    float u0 = sc / (float)gu, u1 = (sc + 1) / (float)gu;
                    if (!open(sc - 1, j))
                        RevealQuad(sink, pattern, at(u0, h0), at(u0, h1), at(u0, h1) + inward, at(u0, h0) + inward, along,
                            new Vector2(tAt(u0), v0), new Vector2(tAt(u0), v1), new Vector2(tAt(u0) + RevealDepth, v1), new Vector2(tAt(u0) + RevealDepth, v0));
                    if (!open(sc + 1, j))
                        RevealQuad(sink, pattern, at(u1, h0), at(u1, h1), at(u1, h1) + inward, at(u1, h0) + inward, -along,
                            new Vector2(tAt(u1), v0), new Vector2(tAt(u1), v1), new Vector2(tAt(u1) - RevealDepth, v1), new Vector2(tAt(u1) - RevealDepth, v0));
                    if (!open(sc, j + 1))
                        RevealQuad(sink, pattern, at(u0, h1), at(u1, h1), at(u1, h1) + inward, at(u0, h1) + inward, Vector3.Down,
                            new Vector2(tAt(u0), v1), new Vector2(tAt(u1), v1), new Vector2(tAt(u1), v1 - dv), new Vector2(tAt(u0), v1 - dv));
                    if (!open(sc, j - 1))
                        RevealQuad(sink, pattern, at(u0, h0), at(u1, h0), at(u1, h0) + inward, at(u0, h0) + inward, Vector3.Up,
                            new Vector2(tAt(u0), v0), new Vector2(tAt(u1), v0), new Vector2(tAt(u1), v0 + dv), new Vector2(tAt(u0), v0 + dv));
                }
            }
            return true;
        }

        /// <summary>A quad of a hole's lining, wound as the sides are for its facing (<paramref name="normal"/>).</summary>
        private static void RevealQuad(QuadSink sink, ushort pattern, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, Vector3 normal,
            Vector2 uv0, Vector2 uv1, Vector2 uv2, Vector2 uv3)
        {
            //(a side's corners go so that (p1 - p0) x (p3 - p0) faces away from its facing)
            if (Vector3.Dot(Vector3.Cross(p1 - p0, p3 - p0), normal) > 0)
                sink(FoundationMaterial.Covering, pattern, p0, p3, p2, p1, normal, uv0, uv3, uv2, uv1);
            else
                sink(FoundationMaterial.Covering, pattern, p0, p1, p2, p3, normal, uv0, uv1, uv2, uv3);
        }

        private static float SideV(float height, float sideTop)
        {
            return 0.45f + ((sideTop - height) / StoreyHeight) * 0.96f;
        }

        /// <summary>A round column from the ground up to a deck's underside, on a wider base.</summary>
        private static void AppendColumn(Vector2 centre, float ground, float top, QuadSink sink)
        {
            Action<float, float, float> ring = (radius, from, to) =>
            {
                var circumference = (float)(Math.PI * 2 * radius);
                for (int i = 0; i < ColumnSides; i++)
                {
                    float a0 = (float)(Math.PI * 2 * i / ColumnSides), a1 = (float)(Math.PI * 2 * (i + 1) / ColumnSides);
                    var d0 = new Vector2((float)Math.Cos(a0), (float)Math.Sin(a0));
                    var d1 = new Vector2((float)Math.Cos(a1), (float)Math.Sin(a1));
                    var p0 = centre + d0 * radius;
                    var p1 = centre + d1 * radius;
                    var mid = Vector2.Normalize(d0 + d1);
                    float u0 = circumference * i / ColumnSides, u1 = circumference * (i + 1) / ColumnSides;
                    sink(FoundationMaterial.Column, 0,
                        new Vector3(p0.X, from, p0.Y), new Vector3(p1.X, from, p1.Y), new Vector3(p1.X, to, p1.Y), new Vector3(p0.X, to, p0.Y),
                        new Vector3(mid.X, 0, mid.Y), new Vector2(u0, -from), new Vector2(u1, -from), new Vector2(u1, -to), new Vector2(u0, -to));
                }
            };
            ring(ColumnRadius, ground, top);
            var baseTop = ground + ColumnBaseHeight;
            ring(ColumnBaseRadius, ground, baseTop);
            // the base's top, round the column
            for (int i = 0; i < ColumnSides; i++)
            {
                float a0 = (float)(Math.PI * 2 * i / ColumnSides), a1 = (float)(Math.PI * 2 * (i + 1) / ColumnSides);
                var d0 = new Vector2((float)Math.Cos(a0), (float)Math.Sin(a0));
                var d1 = new Vector2((float)Math.Cos(a1), (float)Math.Sin(a1));
                var o0 = centre + d0 * ColumnBaseRadius; var o1 = centre + d1 * ColumnBaseRadius;
                var i0 = centre + d0 * ColumnRadius; var i1 = centre + d1 * ColumnRadius;
                sink(FoundationMaterial.Column, 0,
                    new Vector3(o0.X, baseTop, o0.Y), new Vector3(o1.X, baseTop, o1.Y), new Vector3(i1.X, baseTop, i1.Y), new Vector3(i0.X, baseTop, i0.Y),
                    Vector3.Up, o0, o1, i1, i0);
            }
        }

        /// <summary>
        /// Plain steps, for a set whose staircase the game doesn't have: a flight from the ground up to the foundation it
        /// faces, three treads and four risers.
        /// </summary>
        private void AppendSteps(FoundationMap map, int x, int y, QuadSink sink)
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
            // at the floor's scale: the treads by where they are on the lot, the risers and sides by height
            Func<Vector3, Vector2> flat = (p) => new Vector2(p.X, p.Z);
            var across = at(0, 1, 0) - at(0, 0, 0);
            var m = FoundationMaterial.Steps;

            int treads = FoundationMap.StepCount - 1;
            float riser = top / FoundationMap.StepCount;
            for (int k = 0; k < treads; k++)
            {
                float u0 = k / (float)treads, u1 = (k + 1) / (float)treads;
                float h0 = k * riser, h1 = (k + 1) * riser;
                sink(m, 0, at(u0, 0, h0), at(u0, 1, h0), at(u0, 1, h1), at(u0, 0, h1), -rise,
                    new Vector2(0, -h0), new Vector2(1, -h0), new Vector2(1, -h1), new Vector2(0, -h1));
                var t0 = at(u0, 0, h1); var t1 = at(u0, 1, h1); var t2 = at(u1, 1, h1); var t3 = at(u1, 0, h1);
                sink(m, 0, t0, t1, t2, t3, Vector3.Up, flat(t0), flat(t1), flat(t2), flat(t3));
                sink(m, 0, at(u0, 0, 0), at(u1, 0, 0), at(u1, 0, h1), at(u0, 0, h1), -across,
                    new Vector2(u0, 0), new Vector2(u1, 0), new Vector2(u1, -h1), new Vector2(u0, -h1));
                sink(m, 0, at(u0, 1, 0), at(u1, 1, 0), at(u1, 1, h1), at(u0, 1, h1), across,
                    new Vector2(u0, 0), new Vector2(u1, 0), new Vector2(u1, -h1), new Vector2(u0, -h1));
            }
            float hl = treads * riser;
            sink(m, 0, at(1, 0, hl), at(1, 1, hl), at(1, 1, top), at(1, 0, top), -rise,
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

        /// <summary>Finds this group's texture (needs the world's sprite cache), before drawing.</summary>
        public void ResolveTexture(GraphicsDevice gd, WorldState state)
        {
            if (Texture != null && !Texture.IsDisposed) return;
            Texture = null;
            if (state != null)
            {
                try
                {
                    Texture = FoundationLook.Texture(gd, state, Material, WallPattern);
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
