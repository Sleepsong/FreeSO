using FSO.Common.Utils;
using FSO.Files;
using FSO.LotView.Effects;
using FSO.LotView.LMap;
using FSO.LotView.Model;
using FSO.LotView.Utils;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;

namespace FSO.LotView.Components
{
    public class RoofComponent : WorldComponent, IDisposable
    {
        public Blueprint blueprint;
        private List<RoofRect>[] RoofRects;
        public RoofDrawGroup[] Drawgroups;
        private GrassEffect Effect;
        public Texture2D Texture;
        public Texture2D ParallaxTexture;
        public Texture2D NormalMap;
        public Texture2D EdgeTexture;
        public Color RoofAvgColor;

        public uint RoofStyle;
        public float RoofPitch;

        public bool StyleDirty = true;
        public bool ShapeDirty = true;
        public float TexRescale = 1f;

        public void SetStylePitch(uint style, float pitch)
        {
            RoofStyle = style;
            RoofPitch = pitch;
            blueprint.Changes.SetFlag(BlueprintGlobalChanges.ROOF_STYLE_CHANGED);
        }

        public RoofComponent(Blueprint bp)
        {
            blueprint = bp;
            RoofRects = new List<RoofRect>[bp.Stories];
            Drawgroups = new RoofDrawGroup[bp.Stories];
            this.Effect = WorldContent.GrassEffect;
        }

        private void PrepTextures(GraphicsDevice device)
        {
            // TODO: content system for roof textures so they can be reused

            ParallaxTexture?.Dispose();
            NormalMap?.Dispose();
            EdgeTexture?.Dispose();
            var roofs = Content.Content.Get().WorldRoofs;
            var name = roofs.IDToName((int)RoofStyle);

            var indu = Array.FindIndex(name.ToCharArray(), (char c) => { return c == '_' || char.IsDigit(c) || c == '.'; });
            if (name.Contains("drk")) indu -= 3;

            var searchName = (indu == -1) ? name : name.Substring(0, indu);

            try
            {
                TexRescale = 0.2f;
                var path = $"Content/Textures/roof/{searchName}.png";
                if (File.Exists(path))
                {
                    using (var strm = File.OpenRead(path))
                    {
                        Texture = Texture2D.FromStream(device, strm);
                    }
                }
                else
                {
                    TexRescale = 1f;
                    Texture = roofs.Get(name).Get(device);
                }
            }
            catch
            {
                TexRescale = 1f;
                Texture = roofs.Get(name).Get(device);
            }

            ParallaxTexture = null;
            NormalMap = null;
            try
            {
                using (var strm = File.OpenRead($"Content/Textures/roof/{searchName}_h.png"))
                {
                    ParallaxTexture = Texture2D.FromStream(device, strm);
                }

                NormalMap = ImageLoader.MipTextureFromFile(device, $"Content/Textures/roof/{searchName}_n.png");
            } catch (Exception)
            {

            }

            EdgeTexture = ImageLoader.MipTextureFromFile(device, $"Content/Textures/roof/default_edge.png");

            var color = new Vector4();
            var data = new Color[Texture.Width * Texture.Height];
            Texture.GetData(data);
            foreach (var col in data)
            {
                color += col.ToVector4();
            }
            color /= data.Length;
            RoofAvgColor = new Color(color);
        }

        private void EnsureTextures(GraphicsDevice device)
        {
            if (StyleDirty || Texture == null)
            {
                if (RoofRects.Sum(x => x?.Count ?? 0) == 0 && !(DrawnMode && blueprint.DrawnRoofs.Pieces.Count > 0))
                {
                    // Don't need the texture right now.
                    return;
                }

                PrepTextures(device);

                StyleDirty = false;
            }
        }

        public void RegenRoof(GraphicsDevice device)
        {
            if (DrawnMode)
            {
                //roofs drawn with the roof tool: no automatic roof
                for (int i = 0; i < RoofRects.Length; i++) RoofRects[i] = new List<RoofRect>();
                EnsureTextures(device);
                for (int i = 1; i <= blueprint.Stories; i++) MeshRects((sbyte)(i + 1), device);
                blueprint.SM64?.UpdateRoof();
                return;
            }

            var (indoorsMap, hasAnyIndoors) = BuildIndoorsMap();

            for (int i = 1; i <= blueprint.Stories; i++)
            {
                if (hasAnyIndoors[i - 1])
                {
                    RegenRoof((sbyte)(i + 1), device, indoorsMap);
                }
                else if (Drawgroups[i - 1] != null)
                {
                    //nothing to roof on this level (any more)
                    RoofRects[i - 1] = new List<RoofRect>();
                    MeshRects((sbyte)(i + 1), device);
                }
            }

            blueprint.SM64?.UpdateRoof();
        }

        public void RemeshRoof(GraphicsDevice device)
        {
            EnsureTextures(device);

            for (int i = 1; i <= blueprint.Stories; i++)
            {
                MeshRects((sbyte)(i + 1), device);
            }
        }

        public void RegenRoof(sbyte level, GraphicsDevice device, bool[][] indoorsMap)
        {
            //algorithm overview:
            // 1. divide each tile into 4.
            // 2. find tile that IsRoofable
            // 3. place a starting rectangle on the tile
            // 4. Expand the rectangle along x and y axis one by one
            //    (for current axis)
            //    4a. if the expansion would be entirely contained by an existing rectangle (other axis), expand to the end of that rectangle.
            //    4b. if any tiles we're expanding into are not roofable, stop expanding this rectangle and add new ones that start from the same baseline (current axis), but split into the regions (other axis)
            //    4c. if all tiles are not roofable, rectangle is complete.

            var width = blueprint.Width * 2;
            var height = blueprint.Height * 2;

            var evaluated = new bool[width * height];

            var result = new List<RoofRect>();
            for (int y = 2; y < height; y++)
            {
                for (int x = 2; x < width; x++)
                {
                    var off = x + y * width;
                    if (!evaluated[off])
                    {
                        evaluated[off] = true;
                        var tilePos = new LotTilePos((short)(x * 8), (short)(y * 8), level);
                        if (IsRoofable(tilePos, indoorsMap))
                        {
                            //bingo. try expand a rectangle here.
                            RoofSpread(tilePos, evaluated, width, height, level, result, indoorsMap);
                        }
                    }
                }
            }
            RoofRects[level - 2] = result;

            EnsureTextures(device);
            MeshRects(level, device);
        }

        private Vector2 PosToTexCRim(Vector3 pos, float ypos)
        {
            return new Vector2((pos.X + pos.Z) * 0.66f, ypos);
        }

        /// <summary>
        /// A roof to mesh: four corners in 1/16 tile units (top left, top right, bottom right, bottom left), the points
        /// each corner's sides slope up to (x, y, and height in the same units, scaled by the pitch), and which sides slope.
        /// A side that doesn't slope is upright: a gable end, or a shed roof's high side.
        /// </summary>
        public struct RoofShape
        {
            public Point[] Corners;
            public Point3[] Ridge;
            public bool[] Slopes;
            public float Pitch;
        }

        public struct Point3
        {
            public int X, Y, Z;
            public Point3(int x, int y, int z) { X = x; Y = y; Z = z; }
        }

        /// <summary>Half a tile of eaves on a drawn roof's sloped sides (the automatic roof's rectangles include theirs).</summary>
        public const int Eave = 8;

        /// <summary>The automatic roof's shape for one of its rectangles: a hip roof.</summary>
        public static RoofShape HipShape(int x1, int y1, int x2, int y2, float pitch)
        {
            //determine roof height of the smallest edge. This height will be used on all edges
            var h = Math.Min(x2 - x1, y2 - y1) / 2;
            return new RoofShape
            {
                Corners = new[] { new Point(x1, y1), new Point(x2, y1), new Point(x2, y2), new Point(x1, y2) },
                Ridge = new[] { new Point3(x1 + h, y1 + h, h), new Point3(x2 - h, y1 + h, h), new Point3(x2 - h, y2 - h, h), new Point3(x1 + h, y2 - h, h) },
                Slopes = new[] { true, true, true, true },
                Pitch = pitch
            };
        }

        /// <summary>The shape of a roof drawn with the roof tool.</summary>
        public static RoofShape PieceShape(RoofPiece piece)
        {
            var slopes = new bool[4];
            var dir = piece.Direction % 4;
            switch (piece.Type)
            {
                case RoofPieceType.Gable:
                    if (dir % 2 == 0) slopes[0] = slopes[2] = true;
                    else slopes[1] = slopes[3] = true;
                    break;
                case RoofPieceType.Shed:
                    slopes[dir] = true;
                    break;
                default:
                    slopes[0] = slopes[1] = slopes[2] = slopes[3] = true;
                    break;
            }
            int x1 = piece.X * 16, y1 = piece.Y * 16, x2 = (piece.X + piece.Width) * 16, y2 = (piece.Y + piece.Height) * 16;
            //eaves overhang the walls on sloped sides; gable ends and a shed's high side stand on the walls
            if (slopes[0]) y1 -= Eave;
            if (slopes[1]) x2 += Eave;
            if (slopes[2]) y2 += Eave;
            if (slopes[3]) x1 -= Eave;
            var pitch = Math.Max(1, (int)piece.Pitch) / 100f;

            var corners = new[] { new Point(x1, y1), new Point(x2, y1), new Point(x2, y2), new Point(x1, y2) };
            Point3[] ridge;
            switch (piece.Type)
            {
                case RoofPieceType.Gable:
                    if (dir % 2 == 0)
                    {
                        var h = (y2 - y1) / 2; //ridge along x
                        ridge = new[] { new Point3(x1, y1 + h, h), new Point3(x2, y1 + h, h), new Point3(x2, y2 - h, h), new Point3(x1, y2 - h, h) };
                    }
                    else
                    {
                        var h = (x2 - x1) / 2; //ridge along y
                        ridge = new[] { new Point3(x1 + h, y1, h), new Point3(x2 - h, y1, h), new Point3(x2 - h, y2, h), new Point3(x1 + h, y2, h) };
                    }
                    break;
                case RoofPieceType.Shed:
                    {
                        //one slope, from the low side (dir) up to the opposite side, which is the full depth high
                        var high = (dir % 2 == 0) ? (y2 - y1) : (x2 - x1);
                        ridge = new Point3[4];
                        Point3 Raise(Point p) => new Point3(p.X, p.Y, high);
                        ridge[(dir + 2) % 4] = Raise(corners[(dir + 2) % 4]);
                        ridge[(dir + 3) % 4] = Raise(corners[(dir + 3) % 4]);
                        ridge[(dir + 1) % 4] = Raise(corners[(dir + 2) % 4]);
                        ridge[dir] = Raise(corners[(dir + 3) % 4]);
                        break;
                    }
                default:
                    return HipShape(x1, y1, x2, y2, pitch);
            }
            return new RoofShape { Corners = corners, Ridge = ridge, Slopes = slopes, Pitch = pitch };
        }

        /// <summary>True when the lot's roofs are drawn ones (the roof tool), not the automatic roof.</summary>
        public bool DrawnMode => blueprint.DrawnRoofs != null && !blueprint.DrawnRoofs.AutoRoof;

        /// <summary>The shapes on one level: the drawn roofs, or the automatic roof's rectangles.</summary>
        public List<RoofShape> ShapesAt(int level)
        {
            if (DrawnMode)
                return blueprint.DrawnRoofs.Pieces.Where(x => x.Level == level).Select(PieceShape).ToList();
            var rects = RoofRects[level - 2];
            if (rects == null) return null;
            return rects.Select(x => HipShape(x.x1, x.y1, x.x2, x.y2, RoofPitch)).ToList();
        }

        /// <summary>Gable ends and a shed roof's high side: plaster coloured.</summary>
        public static Vector4 GableColor = new Vector4(0.93f, 0.89f, 0.80f, 1f);

        public RoofData MeshRectData(int level)
        {
            var shapes = ShapesAt(level);
            if (shapes == null) return null;

            var slopeCount = shapes.Sum(s => s.Slopes.Count(x => x));
            var gableCount = shapes.Count * 4 - slopeCount;

            var numQuads = slopeCount; //each sloped side
            TerrainParallaxVertex[] Vertices = new TerrainParallaxVertex[numQuads * 4];
            int[] Indices = new int[numQuads * 6];

            //advanced roof: add down facing edges at each sloped side, edge guards, and the bottom
            var advQuads = slopeCount * (1 + 3) + shapes.Count;
            TerrainParallaxVertex[] AdvVertices = new TerrainParallaxVertex[advQuads * 4];
            int[] AdvIndices = new int[advQuads * 6];

            TerrainParallaxVertex[] GableVertices = new TerrainParallaxVertex[gableCount * 4];
            int[] GableIndices = new int[gableCount * 6];

            int geomOffset = 0, indexOffset = 0;
            int advGeomOffset = 0, advIndexOffset = 0;
            int gableGeomOffset = 0, gableIndexOffset = 0;

            var mat3 = Matrix.CreateRotationZ((float)(Math.PI / -2f)); //good
            var mat1 = Matrix.CreateRotationZ((float)(Math.PI / -2f));
            var mat4 = Matrix.CreateRotationX((float)(Math.PI / 2f)) * Matrix.CreateRotationZ((float)(Math.PI / -2f)); //good
            var mat2 = Matrix.CreateRotationX((float)(Math.PI / 2f)) * Matrix.CreateRotationZ((float)(Math.PI / 2f));
            var mats = new[] { mat1, mat2, mat3, mat4 };
            var flips = new[] { true, true, false, false };
            Func<Vector3, Vector2>[] uvs =
            {
                p => new Vector2(p.X, p.Z * -1),
                p => new Vector2(p.Z, p.X),
                p => new Vector2(p.X, p.Z),
                p => new Vector2(p.Z, p.X * -1),
            };

            var edgeT = 0.7f;
            var edgeO = new Vector3(0, 0.005f, 0);
            var advCol = RoofAvgColor.ToVector4();
            Vector2 texScale = new Vector2(2 / 3f, 1f) * TexRescale;
            var col = Color.White.ToVector4();
            var thickness = new Vector3(0, -0.5f, 0);

            void Quad(int[] indices, ref int io, int baseVert)
            {
                indices[io++] = baseVert + 2;
                indices[io++] = baseVert + 1;
                indices[io++] = baseVert;
                indices[io++] = baseVert;
                indices[io++] = baseVert + 3;
                indices[io++] = baseVert + 2;
            }

            void AddEdge(Vector3 l, Vector3 r, Vector3 m_l, Vector3 m_r, Vector3 normal, Matrix mat, bool flip)
            {
                var lToR = r - l;
                lToR.Normalize();
                var lInner = l + lToR * edgeT;
                var rInner = r - lToR * edgeT;

                var mlToL = l - m_l;
                var diagLength = mlToL.Length();
                mlToL.Normalize();
                var mlInner = m_l + (mlToL + lToR) * edgeT;
                var innerDiagLength = (mlInner - lInner).Length();

                var mrToR = r - m_r;
                mrToR.Normalize();
                var mrInner = m_r + (mrToR - lToR) * edgeT;

                for (int j = 0; j < 12; j += 4) Quad(AdvIndices, ref advIndexOffset, advGeomOffset + j);

                var idOff = (diagLength - innerDiagLength) * 0.1f;
                var hmul = 0.5f;
                AdvVertices[advGeomOffset++] = new TerrainParallaxVertex(mlInner + edgeO, advCol, new Vector2(-idOff * hmul, 0.33f), 0, normal, mat, flip);
                AdvVertices[advGeomOffset++] = new TerrainParallaxVertex(m_l + edgeO, advCol, new Vector2(0, 0), 0, normal, mat, flip);
                AdvVertices[advGeomOffset++] = new TerrainParallaxVertex(l + edgeO, advCol, new Vector2(diagLength * -hmul, 0), 0, normal, mat, flip);
                AdvVertices[advGeomOffset++] = new TerrainParallaxVertex(lInner + edgeO, advCol, new Vector2((idOff + innerDiagLength) * -hmul, 0.33f), 0, normal, mat, flip);

                var topLength = (m_r - m_l).Length();
                var innerTopLength = (mrInner - mlInner).Length();
                var itOff = (topLength - innerTopLength) / 2;

                AdvVertices[advGeomOffset++] = new TerrainParallaxVertex(mrInner + edgeO, advCol, new Vector2((itOff + innerTopLength) * hmul, 0.33f), 0, normal, mat, flip);
                AdvVertices[advGeomOffset++] = new TerrainParallaxVertex(m_r + edgeO, advCol, new Vector2(topLength * hmul, 0), 0, normal, mat, flip);
                AdvVertices[advGeomOffset++] = new TerrainParallaxVertex(m_l + edgeO, advCol, new Vector2(0, 0), 0, normal, mat, flip);
                AdvVertices[advGeomOffset++] = new TerrainParallaxVertex(mlInner + edgeO, advCol, new Vector2(itOff * hmul, 0.33f), 0, normal, mat, flip);

                AdvVertices[advGeomOffset++] = new TerrainParallaxVertex(mrInner + edgeO, advCol, new Vector2((topLength + idOff) * hmul, 0.33f), 0, normal, mat, flip);
                AdvVertices[advGeomOffset++] = new TerrainParallaxVertex(rInner + edgeO, advCol, new Vector2((topLength + idOff + innerDiagLength) * hmul, 0.33f), 0, normal, mat, flip);
                AdvVertices[advGeomOffset++] = new TerrainParallaxVertex(r + edgeO, advCol, new Vector2((topLength + diagLength) * hmul, 0f), 0, normal, mat, flip);
                AdvVertices[advGeomOffset++] = new TerrainParallaxVertex(m_r + edgeO, advCol, new Vector2(topLength * hmul, 0f), 0, normal, mat, flip);
            }

            foreach (var shape in shapes)
            {
                var pitch = shape.Pitch;
                //    /    \
                //   /      \
                //  /________\  each sloped side goes from its two corners up to their ridge points.
                var c = shape.Corners.Select(p => ToWorldPos(p.X, p.Y, 0, level, pitch)).ToArray();
                var m = shape.Ridge.Select(p => ToWorldPos(p.X, p.Y, p.Z, level, pitch)).ToArray();
                var b = c.Select(p => p + thickness).ToArray();
                var normals = new Vector3[4];

                for (int k = 0; k < 4; k++)
                {
                    var k1 = (k + 1) % 4;
                    var n = -Vector3.Normalize(Vector3.Cross(c[k] - c[k1], c[k1] - m[k1]));
                    if (float.IsNaN(n.X)) n = Vector3.Up;
                    normals[k] = n;
                    if (shape.Slopes[k])
                    {
                        Quad(Indices, ref indexOffset, geomOffset);
                        Vertices[geomOffset++] = new TerrainParallaxVertex(c[k], col, uvs[k](c[k]) * texScale, 0, n, mats[k], flips[k]);
                        Vertices[geomOffset++] = new TerrainParallaxVertex(c[k1], col, uvs[k](c[k1]) * texScale, 0, n, mats[k], flips[k]);
                        Vertices[geomOffset++] = new TerrainParallaxVertex(m[k1], col * 1.25f, uvs[k](m[k1]) * texScale, 0, n, mats[k], flips[k]);
                        Vertices[geomOffset++] = new TerrainParallaxVertex(m[k], col * 1.25f, uvs[k](m[k]) * texScale, 0, n, mats[k], flips[k]);

                        //thickness
                        var n5 = Vector3.Normalize(Vector3.Cross(c[k] - c[k1], c[k1] - b[k1]));
                        Quad(AdvIndices, ref advIndexOffset, advGeomOffset);
                        AdvVertices[advGeomOffset++] = new TerrainParallaxVertex(b[k1], advCol, PosToTexCRim(b[k1], 0.73f), 0, n5, mats[k], flips[k]);
                        AdvVertices[advGeomOffset++] = new TerrainParallaxVertex(c[k1], advCol, PosToTexCRim(c[k1], 0.4f), 0, n5, mats[k], flips[k]);
                        AdvVertices[advGeomOffset++] = new TerrainParallaxVertex(c[k], advCol, PosToTexCRim(c[k], 0.4f), 0, n5, mats[k], flips[k]);
                        AdvVertices[advGeomOffset++] = new TerrainParallaxVertex(b[k], advCol, PosToTexCRim(b[k], 0.73f), 0, n5, mats[k], flips[k]);
                    }
                    else
                    {
                        //an upright side: a gable end (a triangle) or a shed roof's high side
                        Quad(GableIndices, ref gableIndexOffset, gableGeomOffset);
                        GableVertices[gableGeomOffset++] = new TerrainParallaxVertex(c[k], GableColor, Vector2.Zero, 0, n, mats[k], flips[k]);
                        GableVertices[gableGeomOffset++] = new TerrainParallaxVertex(c[k1], GableColor, Vector2.Zero, 0, n, mats[k], flips[k]);
                        GableVertices[gableGeomOffset++] = new TerrainParallaxVertex(m[k1], GableColor, Vector2.Zero, 0, n, mats[k], flips[k]);
                        GableVertices[gableGeomOffset++] = new TerrainParallaxVertex(m[k], GableColor, Vector2.Zero, 0, n, mats[k], flips[k]);
                    }
                }

                //bottom (solid black)
                var nb = Vector3.Normalize(Vector3.Cross(b[0] - b[2], b[2] - b[3]));
                Quad(AdvIndices, ref advIndexOffset, advGeomOffset);
                AdvVertices[advGeomOffset++] = new TerrainParallaxVertex(b[3], advCol, new Vector2(0, 0.4f + 0.33f), 0, nb, mat4, false);
                AdvVertices[advGeomOffset++] = new TerrainParallaxVertex(b[2], advCol, new Vector2(0, 0.4f + 0.33f), 0, nb, mat4, false);
                AdvVertices[advGeomOffset++] = new TerrainParallaxVertex(b[1], advCol, new Vector2(0, 0.4f + 0.33f), 0, nb, mat4, false);
                AdvVertices[advGeomOffset++] = new TerrainParallaxVertex(b[0], advCol, new Vector2(0, 0.4f + 0.33f), 0, nb, mat4, false);

                //outer edge: 3 quads for top, left and right edges of each sloped side
                for (int k = 0; k < 4; k++)
                {
                    if (!shape.Slopes[k]) continue;
                    var k1 = (k + 1) % 4;
                    AddEdge(c[k], c[k1], m[k], m[k1], normals[k], mats[k], flips[k]);
                }
            }

            return new RoofData()
            {
                Vertices = Vertices,
                Indices = Indices,
                NumPrimitives = numQuads * 2,

                AdvVertices = AdvVertices,
                AdvIndices = AdvIndices,
                AdvNumPrimitives = advQuads * 2,

                GableVertices = GableVertices,
                GableIndices = GableIndices,
                GableNumPrimitives = gableCount * 2,
            };
        }

        public void MeshRects(int level, GraphicsDevice device)
        {
            var data = MeshRectData(level);
            if (data == null) return;
            
            if (Drawgroups[level - 2] != null && Drawgroups[level - 2].NumPrimitives > 0)
            {
                Drawgroups[level - 2].VertexBuffer.Dispose();
                Drawgroups[level - 2].IndexBuffer.Dispose();

                Drawgroups[level - 2].AdvVertexBuffer?.Dispose();
                Drawgroups[level - 2].AdvIndexBuffer?.Dispose();

                Drawgroups[level - 2].GableVertexBuffer?.Dispose();
                Drawgroups[level - 2].GableIndexBuffer?.Dispose();
            }

            var result = new RoofDrawGroup() { Data = data };
            if (data.NumPrimitives > 0)
            {
                result.VertexBuffer = new VertexBuffer(device, typeof(TerrainParallaxVertex), data.Vertices.Length, BufferUsage.None);
                if (data.Vertices.Length > 0) result.VertexBuffer.SetData(data.Vertices);

                result.IndexBuffer = new IndexBuffer(device, IndexElementSize.ThirtyTwoBits, sizeof(int) * data.Indices.Length, BufferUsage.None);
                if (data.Vertices.Length > 0) result.IndexBuffer.SetData(data.Indices);
            }

            if (data.AdvNumPrimitives > 0)
            {
                result.AdvVertexBuffer = new VertexBuffer(device, typeof(TerrainParallaxVertex), data.AdvVertices.Length, BufferUsage.None);
                if (data.AdvVertices.Length > 0) result.AdvVertexBuffer.SetData(data.AdvVertices);

                result.AdvIndexBuffer = new IndexBuffer(device, IndexElementSize.ThirtyTwoBits, sizeof(int) * data.AdvIndices.Length, BufferUsage.None);
                if (data.AdvVertices.Length > 0) result.AdvIndexBuffer.SetData(data.AdvIndices);
            }

            if (data.GableNumPrimitives > 0)
            {
                result.GableVertexBuffer = new VertexBuffer(device, typeof(TerrainParallaxVertex), data.GableVertices.Length, BufferUsage.None);
                result.GableVertexBuffer.SetData(data.GableVertices);

                result.GableIndexBuffer = new IndexBuffer(device, IndexElementSize.ThirtyTwoBits, sizeof(int) * data.GableIndices.Length, BufferUsage.None);
                result.GableIndexBuffer.SetData(data.GableIndices);
            }

            result.NumPrimitives = data.NumPrimitives;
            result.AdvNumPrimitives = data.AdvNumPrimitives;
            result.GableNumPrimitives = data.GableNumPrimitives;

            Drawgroups[level - 2] = result;
        }

        private Vector3 ToWorldPos(int x, int y, int z, int level, float pitch)
        {
            return new Vector3((x / 16f) * 3f, (z * pitch / 16f) * 3f + ((level - 1) * 2.95f * 3f) + blueprint.GetAltitude(x / 16, y / 16) * 3, (y / 16f) * 3f);
        }

        private static Point[] advanceByDir = new Point[]
        {
            new Point(8, 0),
            new Point(0, 8),
            new Point(-8, 0),
            new Point(0, -8)
        };

        private Point StartLocation(RoofRect rect, int dir)
        {
            switch (dir)
            {
                case 0:
                    return new Point(rect.x2, rect.y1);
                case 1:
                    return new Point(rect.x2 - 8, rect.y2);
                case 2:
                    return new Point(rect.x1 - 8, rect.y2 - 8);
                case 3:
                    return new Point(rect.x1, rect.y1 - 8);
            }
            return new Point();
        }

        private bool RangeCheck(RoofRect me, RoofRect into, int dir)
        {
            switch (dir % 2)
            {
                case 0:
                    return (me.y1 > into.y1 && me.y2 < into.y2);
                case 1:
                    return (me.x1 > into.x1 && me.x2 < into.x2);
            }
            return false;
        }

        private static int[] ExpandOrder = new int[]
        {
            2, 3, 1, -1
        };

        private void RoofSpread(LotTilePos start, bool[] evaluated, int width, int height, sbyte level, List<RoofRect> result, bool[][] indoorsMap)
        {
            var rect = new RoofRect(start.x, start.y, start.x + 8, start.y + 8);
            var toCtr = new Point(4, 4);

            while (rect.ExpandDir != -1)
            {
                //still have to expand in a direction
                //order: 0, 2, 1, 3, (xpos,xneg,ypos,yneg)
                var dir = rect.ExpandDir;
                var startPt = StartLocation(rect, dir);
                var testPt = startPt;
                var inc = advanceByDir[(dir + 1) % 4];
                var count = Math.Abs(rect.GetByDir((dir + 1) % 4) - rect.GetByDir((dir + 3) % 4)) / 8;

                bool canExpand = true;
                for (int i = 0; i < count; i++)
                {
                    var tile = new LotTilePos((short)testPt.X, (short)testPt.Y, level);
                    if (!IsRoofable(tile, indoorsMap))
                    {
                        canExpand = false;
                        break;
                    }
                    testPt += inc;
                }

                if (!canExpand) rect.ExpandDir = ExpandOrder[rect.ExpandDir];
                else
                {
                    //mark as complete - new roof rects cannot START on these tiles..
                    testPt = startPt;
                    for (int i = 0; i < count; i++)
                    {
                        evaluated[(testPt.X / 8) + (testPt.Y / 8) * width] = true;
                        testPt += inc;
                    }

                    //SPEEDUP: if expansion is within an existing roof rectangle skip to the other end of it
                    var midPt = startPt + new Point(inc.X * count / 2, inc.Y * count / 2) + toCtr;

                    var expandInto = result.FirstOrDefault(x => x.Contains(midPt) && RangeCheck(rect, x, dir));
                    if (expandInto != null)
                    {
                        rect.SetByDir(dir, expandInto.GetByDir(dir));
                    }
                    else
                    {
                        //on no detection, expand by 1
                        rect.SetByDir(dir, rect.GetByDir(dir) + ((dir > 1) ? -8 : 8));
                    }
                }
            }

            result.Add(rect);
        }

        public class RoofRect
        {
            public int ExpandDir;

            public int x1;
            public int x2;
            public int y1;
            public int y2;

            public int GetByDir(int dir)
            {
                switch (dir)
                {
                    case 0: return x2;
                    case 1: return y2;
                    case 2: return x1;
                    case 3: return y1;
                }
                return 0;
            }

            public void SetByDir(int dir, int value)
            {
                switch (dir)
                {
                    case 0: x2 = value; break;
                    case 1: y2 = value; break;
                    case 2: x1 = value; break;
                    case 3: y1 = value; break;
                }
            }

            public RoofRect(int x1, int y1, int x2, int y2)
            {
                if (x1 > x2) { var temp = x1; x1 = x2; x2 = temp; }
                if (y1 > y2) { var temp = y1; y1 = y2; y2 = temp; }

                this.x1 = x1;
                this.y1 = y1;
                this.x2 = x2;
                this.y2 = y2;
            }

            public bool Contains(Point pt)
            {
                return (pt.X >= x1 && pt.X <= x2) && (pt.Y >= y1 && pt.Y <= y2);
            }

            public bool HardContains(Point pt)
            {
                return (pt.X > x1 && pt.X < x2) && (pt.Y > y1 && pt.Y < y2);
            }

            public Point Closest(int x, int y)
            {
                return new Point(Math.Max(Math.Min(x2, x), x1), Math.Max(Math.Min(y2, y), y1));
            }

            public bool Intersects(RoofRect other)
            {
                return !((other.x1 >= x2 || other.x2 <= x1) || (other.y1 >= y2 || other.y2 <= y1));
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool TileIndoors(int x, int y, bool[] map)
        {
            return map[x + y * blueprint.Width];
        }

        public bool IndoorsOrFloor(int x, int y, int level, bool[] map)
        {
            if (level <= blueprint.Stories)
            {
                if (TileIndoors(x, y, map)) return true;
                if (blueprint.GetFloor((short)x, (short)y, (sbyte)level).Pattern != 0) return true;
                var wall = blueprint.GetWall((short)x, (short)y, (sbyte)level);
                if ((wall.Segments & WallSegments.AnyDiag) > 0) return true;
            }
            return false;
        }

        private (bool[][], bool[]) BuildIndoorsMap()
        {
            bool[] roomIndoors = blueprint.Rooms.Select(x => !x.IsOutside).ToArray();
            bool[][] indoors = new bool[blueprint.Stories][];
            bool[] hasAnyIndoors = new bool[blueprint.Stories];
            
            for (int i = 0; i < blueprint.Stories; i++)
            {
                bool anyIndoors = false;
                var roomMap = blueprint.RoomMap[i];
                var floorIndoors = new bool[blueprint.Width * blueprint.Height];
                indoors[i] = floorIndoors;

                for (int j = 0; j < floorIndoors.Length; j++)
                {
                    uint room = roomMap[j];

                    bool tileIndoors = false;

                    var room1 = room & 0xFFFF;
                    var room2 = (room >> 16) & 0x7FFF;
                    if (room1 < roomIndoors.Length && roomIndoors[room1]) tileIndoors = true;
                    if (room2 > 0 && room2 < roomIndoors.Length && roomIndoors[room2]) tileIndoors = true;

                    floorIndoors[j] = tileIndoors;
                    anyIndoors |= tileIndoors;
                }

                hasAnyIndoors[i] = anyIndoors;
            }

            return (indoors, hasAnyIndoors);
        }

        public bool IsRoofable(LotTilePos pos, bool[][] indoorsMap)
        {
            if (pos.Level == 1) return false;
            var tileX = pos.TileX;
            var tileY = pos.TileY;
            var level = pos.Level;
            if (tileX <= 0 || tileX >= blueprint.Width - 1 || tileY <= 0 || tileY >= blueprint.Height - 1) return false;
            var fDiag = false;
            //must be over indoors
            var halftile = false;

            var indoorsLower = indoorsMap[level - 2];
            var indoorsUpper = level <= blueprint.Stories ? indoorsMap[level - 1] : null;

            if (!TileIndoors(tileX, tileY, indoorsLower))
            {
                //are a half tile away from indoors?
                bool found = false;
                if (pos.x % 16 == 8)
                {
                    if (TileIndoors(tileX + 1, tileY, indoorsLower)) found = true;
                }
                else
                {
                    if (TileIndoors(tileX - 1, tileY, indoorsLower)) found = true;
                }

                if (pos.y % 16 == 8)
                {
                    if (TileIndoors(tileX, tileY + 1, indoorsLower)) found = true;
                }
                else
                {
                    if (TileIndoors(tileX, tileY - 1, indoorsLower)) found = true;
                }

                if (TileIndoors(tileX + ((pos.x % 16 == 8) ? 1 : -1), tileY + ((pos.y % 16 == 8) ? 1 : -1), indoorsLower))
                {
                    if (!found) fDiag = true;
                    found = true;
                }
                if (!found) return false;
                halftile = true;
            }
            //on our level, the tile must not be indoors or floored
            if (IndoorsOrFloor(tileX, tileY, level, indoorsUpper)) return false;

            if (halftile)
            {
                if (pos.x % 16 == 8)
                {
                    if (IndoorsOrFloor(tileX + 1, tileY, level, indoorsUpper)) return false;
                }
                else
                {
                    if (IndoorsOrFloor(tileX - 1, tileY, level, indoorsUpper)) return false;
                }

                if (pos.y % 16 == 8)
                {
                    if (IndoorsOrFloor(tileX, tileY + 1, level, indoorsUpper)) return false;
                }
                else
                {
                    if (IndoorsOrFloor(tileX, tileY - 1, level, indoorsUpper)) return false;
                }
                
                if (fDiag && IndoorsOrFloor(tileX + ((pos.x % 16 == 8) ? 1 : -1), tileY + ((pos.y % 16 == 8) ? 1 : -1), level, indoorsUpper)) return false;
            }

            return true;
        }

        public void Dispose()
        {
            ParallaxTexture?.Dispose();
            NormalMap?.Dispose();
            EdgeTexture?.Dispose();

            foreach (var buf in Drawgroups)
            {
                if (buf != null && buf.NumPrimitives > 0)
                {
                    buf.IndexBuffer.Dispose();
                    buf.VertexBuffer.Dispose();
                    
                    buf.AdvIndexBuffer?.Dispose();
                    buf.AdvVertexBuffer?.Dispose();

                    buf.GableIndexBuffer?.Dispose();
                    buf.GableVertexBuffer?.Dispose();
                }
            }
        }

        public override void Draw(GraphicsDevice device, WorldState world)
        {
            device.RasterizerState = RasterizerState.CullNone;
            if (ShapeDirty)
            {
                RegenRoof(device);
                ShapeDirty = false;
                StyleDirty = false;
            }
            else if (StyleDirty)
            {
                RemeshRoof(device);
                StyleDirty = false;
            }

            if (Drawgroups.Length == 0)
            {
                return;
            }

            var enableParallax = WorldConfig.Current.Complex && ParallaxTexture != null;

            device.RasterizerState = RasterizerState.CullClockwise;
            device.BlendState = BlendState.AlphaBlend;
            int maxLevel = world.ScrollAnchor?.MyMario != null ? world.Level - 2 : world.Level - 1;
            for (int i = 0; i < Drawgroups.Length; i++)
            {
                if (i > maxLevel) break;
                Effect.Level = (float)i + 1.0001f;
                if (Drawgroups[i] != null)
                {
                    var dg = Drawgroups[i];
                    if (dg.NumPrimitives == 0) continue;
                    PPXDepthEngine.RenderPPXDepth(Effect, true, (depthMode) =>
                    {
                        Effect.View = world.View;
                        Effect.Projection = world.Projection;
                        Effect.World = Matrix.Identity;
                        Effect.DiffuseColor = new Vector4(world.OutsideColor.R / 255f, world.OutsideColor.G / 255f, world.OutsideColor.B / 255f, 1.0f);
                        Effect.UseTexture = true;
                        Effect.BaseTex = Texture;
                        Effect.Alpha = 1f;
                        Effect.IgnoreColor = false;
                        Effect.TexOffset = Vector2.Zero;
                        Effect.TexMatrix = new Vector4(1, 0, 0, 1);

                        Effect.ParallaxUVTexMat = new Vector4(1, 0, 1, 0);
                        Effect.ParallaxHeight = 0.2f * TexRescale;
                        Effect.ParallaxTex = ParallaxTexture;
                        Effect.NormalMapTex = NormalMap;

                        Effect.GrassShininess = 0.01f;//005f);

                        var pass3d = (enableParallax) ? 3 : 2;
                        var pass2d = (enableParallax && WorldConfig.Current.AdvancedLighting) ? 3 : WorldConfig.Current.PassOffset;

                        device.SetVertexBuffer(dg.VertexBuffer);
                        device.Indices = dg.IndexBuffer;

                        Effect.SetTechnique(GrassTechniques.DrawBase);
                        var pass = Effect.CurrentTechnique.Passes[(world.CameraMode == CameraRenderMode._3D)?pass3d:pass2d];
                        pass.Apply();
                        device.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, dg.NumPrimitives);

                        if (dg.AdvNumPrimitives > 0)
                        {
                            device.SetVertexBuffer(dg.AdvVertexBuffer);
                            device.Indices = dg.AdvIndexBuffer;
                            Effect.BaseTex = EdgeTexture;
                            Effect.GrassShininess = 0.003f;
                            pass = Effect.CurrentTechnique.Passes[(world.CameraMode == CameraRenderMode._3D) ? 2 : WorldConfig.Current.PassOffset];
                            pass.Apply();
                            device.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, dg.AdvNumPrimitives);
                        }

                        if (dg.GableNumPrimitives > 0)
                        {
                            //gable ends: plaster coloured (the vertex colour on plain white)
                            device.SetVertexBuffer(dg.GableVertexBuffer);
                            device.Indices = dg.GableIndexBuffer;
                            Effect.BaseTex = TextureGenerator.GetPxWhite(device);
                            Effect.GrassShininess = 0.002f;
                            pass = Effect.CurrentTechnique.Passes[(world.CameraMode == CameraRenderMode._3D) ? 2 : WorldConfig.Current.PassOffset];
                            pass.Apply();
                            device.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, dg.GableNumPrimitives);
                        }
                    });
                }
            }
            device.BlendState = BlendState.Opaque;
            device.RasterizerState = RasterizerState.CullNone;
        }

        public void DrawOne(GraphicsDevice device, Matrix view, Matrix projection, WorldState world, int i)
        {
            device.RasterizerState = RasterizerState.CullNone;
            if (ShapeDirty)
            {
                RegenRoof(device);
                ShapeDirty = false;
                StyleDirty = false;
            }
            else if (StyleDirty)
            {
                RemeshRoof(device);
                StyleDirty = false;
            }
            if (i > world.Level - 1) return;
            Effect.Level = (float)i + 1.0001f;
            if (Drawgroups[i] != null)
            {
                var dg = Drawgroups[i];
                if (dg.NumPrimitives == 0) return;
                Effect.View = view;
                Effect.Projection = projection;
                Effect.World = Matrix.Identity;
                Effect.DiffuseColor = new Vector4(world.OutsideColor.R / 255f, world.OutsideColor.G / 255f, world.OutsideColor.B / 255f, 1.0f);
                Effect.UseTexture = true;
                Effect.BaseTex = Texture;
                Effect.Alpha = 1f;
                Effect.IgnoreColor = false;
                Effect.TexOffset = Vector2.Zero;
                Effect.TexMatrix = new Vector4(1, 0, 0, 1);

                Effect.FadeRectangle = new Vector4(-1000, -1000, 2000, 2000);
                Effect.FadeWidth = 35f * 3;

                device.SetVertexBuffer(dg.VertexBuffer);
                device.Indices = dg.IndexBuffer;

                Effect.SetTechnique(GrassTechniques.DrawBase);
                var pass = Effect.CurrentTechnique.Passes[2];
                pass.Apply();
                device.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, dg.NumPrimitives);

                if (dg.AdvNumPrimitives > 0)
                {
                    device.SetVertexBuffer(dg.AdvVertexBuffer);
                    device.Indices = dg.AdvIndexBuffer;
                    Effect.BaseTex = EdgeTexture;
                    Effect.GrassShininess = 0.003f;
                    pass = Effect.CurrentTechnique.Passes[2];
                    pass.Apply();
                    device.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, dg.AdvNumPrimitives);
                }

                if (dg.GableNumPrimitives > 0)
                {
                    device.SetVertexBuffer(dg.GableVertexBuffer);
                    device.Indices = dg.GableIndexBuffer;
                    Effect.BaseTex = TextureGenerator.GetPxWhite(device);
                    Effect.GrassShininess = 0.002f;
                    pass = Effect.CurrentTechnique.Passes[2];
                    pass.Apply();
                    device.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, dg.GableNumPrimitives);
                }
            }
        }

        public void DrawLMap(GraphicsDevice gd, LightData light, Matrix projection, Matrix lightTransform)
        {
            if (ShapeDirty)
            {
                RegenRoof(gd);
                ShapeDirty = false;
                StyleDirty = false;
            }
            else if (StyleDirty)
            {
                RemeshRoof(gd);
                StyleDirty = false;
            }

            var s = Matrix.Identity;
            s.M22 = 0;
            s.M33 = 0;
            s.M23 = 1;
            s.M32 = 1;

            for (int i = light.Level; i < Drawgroups.Length; i++)
            {
                Effect.Level = (float)i + 1;
                if (Drawgroups[i] != null)
                {
                    var dg = Drawgroups[i];
                    if (dg.NumPrimitives == 0) continue;

                    Effect.UseTexture = false;
                    Effect.Projection = projection;
                    var view = Matrix.Identity;
                    Effect.View = view;

                    var worldmat = Matrix.CreateScale(1 / 3f, 0, 1 / 3f) * Matrix.CreateTranslation(0, 1f * (i - (light.Level-1)), 0) * s * lightTransform;

                    Effect.World = worldmat;
                    Effect.DiffuseColor = new Vector4(1, 1, 1, 1) * (float)(4 - (i - (light.Level))) / 5f;

                    gd.SetVertexBuffer(dg.VertexBuffer);
                    gd.Indices = dg.IndexBuffer;

                    Effect.SetTechnique(GrassTechniques.DrawLMap);
                    foreach (var pass in Effect.CurrentTechnique.Passes)
                    {
                        pass.Apply();
                        gd.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, dg.NumPrimitives);
                    }
                }
            }
        }
    }

    public class RoofData
    {
        public int[] Indices;
        public TerrainParallaxVertex[] Vertices;
        public int NumPrimitives;

        public int[] AdvIndices;
        public TerrainParallaxVertex[] AdvVertices;
        public int AdvNumPrimitives;

        //upright sides of drawn roofs: gable ends, a shed roof's high side
        public int[] GableIndices;
        public TerrainParallaxVertex[] GableVertices;
        public int GableNumPrimitives;
    }

    public class RoofDrawGroup
    {
        public RoofData Data;

        public IndexBuffer IndexBuffer;
        public VertexBuffer VertexBuffer;
        public int NumPrimitives;
        
        public IndexBuffer AdvIndexBuffer;
        public VertexBuffer AdvVertexBuffer;
        public int AdvNumPrimitives;

        public IndexBuffer GableIndexBuffer;
        public VertexBuffer GableVertexBuffer;
        public int GableNumPrimitives;
    }
}
