using FSO.Common;
using FSO.Common.Utils;
using FSO.LotView.Components.Geometry;
using FSO.LotView.Components.Model;
using FSO.LotView.Effects;
using FSO.LotView.Model;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace FSO.LotView.Components
{
    /// <summary>
    /// Handles indices and groups floor geometry by texture for a each floor.
    /// 
    /// </summary>
    /// 

    // these are future plans, really.
    // indices for individual tiles
    // w is ((width+1) * 2) - 1. this is a point for each tile
    //  0________2
    //   |\    /|
    //   | \  / |
    //   |  \/1 |
    //   |  /\  |
    //   | /  \ |
    // +w|/____\|+w+2

    public class _3DFloorGeometry : IDisposable
    {
        public Blueprint Bp;
        /// <summary>By level - 1: [-1] is the basement (its dug out tiles, and the earth's sides round them).</summary>
        public LevelArray<FloorLevel> Floors;
        /// <summary>
        /// The ground floor's tiles over the basement: its ceiling, kept apart so it can be left out while the basement is
        /// shown (the ground floor's other tiles, the ground among them, stay).
        /// </summary>
        public FloorLevel Ceiling;

        public static bool af2019;
        static Dictionary<int, Texture2D> PoolReplace; //af2019
        static Dictionary<int, Texture2D> PoolReplaceParallax;

        public _3DFloorGeometry(Blueprint bp)
        {
            Bp = bp;
            Floors = new LevelArray<FloorLevel>(bp.Stories);
            for (int i = LevelArray<int>.MinIndex; i<bp.Stories; i++)
            {
                Floors[i] = new FloorLevel(bp);
            }
            Ceiling = new FloorLevel(bp);
        }

        private void LoadPoolReplace(GraphicsDevice gd, string folder)
        {
            //don't worry this will be removed when i do graphics refactor
            //probably
            PoolReplace = new Dictionary<int, Texture2D>();
            PoolReplaceParallax = new Dictionary<int, Texture2D>();

            for (int i=0; i<16; i++)
            {
                using (var file = System.IO.File.Open($"Content/Textures/{folder}/diffuse/pool{i}.png", System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.Read)) {
                    PoolReplace[i] = Files.ImageLoader.FromStream(gd, file);
                }
                using (var file = System.IO.File.Open($"Content/Textures/{folder}/parallax/pool{i}.png", System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.Read))
                {
                    PoolReplaceParallax[i] = Files.ImageLoader.FromStream(gd, file);
                }
            }
        }

        public void FullReset(GraphicsDevice gd, bool buildMode)
        {
            if (af2019 && PoolReplace == null) LoadPoolReplace(gd, "af19");
            for (int i = LevelArray<int>.MinIndex; i<Floors.Length; i++) //the basement (-1) and up
            {
                var lvl = Floors[i];
                var data = Bp.Floors[i];
                var walls = Bp.Walls[i];
                lvl.Clear();
                if (i == 0) Ceiling.Clear();
                var width = Bp.Width;
                var end = data.Length - width;
                for (int j=width; j<end; j++)
                {
                    var pat = data[j].Pattern;
                    if ((j + 1) % Bp.Width < 2) continue;
                    var wall = walls[j];
                    if ((wall.Segments & WallSegments.AnyDiag) > 0)
                    {
                        var vertical = (wall.Segments & WallSegments.VerticalDiag) > 0;
                        var dug = IsDug(j);
                        if (i == -1 && !dug) continue; //the basement is earth where it isn't dug out
                        var target = (i == 0 && dug) ? Ceiling : lvl;

                        if (wall.TopLeftPattern != 0 || i == 0) target.AddTileDiag(wall.TopLeftPattern, (ushort)j, true, vertical);
                        if (wall.TopLeftStyle != 0 || i == 0) target.AddTileDiag(wall.TopLeftStyle, (ushort)j, false, vertical);
                    }
                    else
                    {
                        AddFloorTile(i, lvl, j, pat, buildMode);
                    }
                }
                FinishLevel(gd, i, lvl);
            }
        }

        /// <summary>
        /// Whether a ground floor tile is drawn by the foundation models instead: steps, and a foundation's top where
        /// nothing is laid on it (no grass on a foundation).
        /// </summary>
        private bool HiddenByFoundation(int index, ushort pattern)
        {
            var map = Bp.Foundations;
            if (map == null || !map.Any) return false;
            //plain steps are drawn over the whole tile; a staircase's piece stands on the ground like any object
            if (map.Steps[index] != 0 && map.StepTop(index % map.Width, index / map.Width) > 0)
                return FoundationLook.StairFor(map.StepStyle[index]) == null;
            return pattern == 0 && map.Raise[index] > 0;
        }

        /// <summary>Whether a ground floor tile is under a column deck: open, with the lot's ground to be seen under it.</summary>
        private bool UnderOpenDeck(int index)
        {
            var map = Bp.Foundations;
            return map != null && map.Any && map.Raise[index] > 0 && map.Kind[index] == (byte)FoundationKind.ColumnDeck;
        }

        /// <summary>The foundations' and decks' models: one per material, and per covering on foundations' sides.</summary>
        private void AddFoundations(FloorLevel lvl)
        {
            foreach (var (material, pattern) in Modelled3DFoundation.Groups(Bp.Foundations))
                lvl.FoundationGroups.Add(new Modelled3DFoundation(Bp, material, pattern));
            // the lot's own ground under column decks, from the unraised ground (TerrainComponent.GroundVertexBuffer)
            var map = Bp.Foundations;
            if (map != null && map.Any)
                for (int j = 0; j < map.Raise.Length; j++)
                    if (UnderOpenDeck(j)) lvl.GroundUnderDecks.AddIndex((ushort)j);
        }

        private bool IsDug(int index)
        {
            var map = Bp.Basement;
            return map != null && map.Any && map.Dug[index] != 0;
        }

        /// <summary>
        /// Adds a floor tile to its level (by index: -1 is the basement), as the resets do: the basement only has its dug
        /// out tiles, foundations draw their own tops and steps, and a ground floor tile over the basement goes in the
        /// <see cref="Ceiling"/>.
        /// </summary>
        private void AddFloorTile(int i, FloorLevel lvl, int j, ushort pat, bool buildMode)
        {
            var dug = IsDug(j);
            if (i == -1 && (!dug || pat == 0)) return;
            if (i == 0 && dug && pat == 0) return; //a hole over the basement (a stairwell), not ground
            // steps, and foundations with nothing laid on them, are drawn by the foundation models
            if (i == 0 && HiddenByFoundation(j, pat)) return;
            var convert = PatternConvert(pat, (ushort)j, (sbyte)(i + 1), buildMode);
            if (convert == 0 && i != 0) return;
            ((i == 0 && dug) ? Ceiling : lvl).AddTile(convert, (ushort)j);
        }

        /// <summary>The models a level has besides its tiles (foundations on the ground floor, the earth's sides round the basement), then its GPU data.</summary>
        private void FinishLevel(GraphicsDevice gd, int i, FloorLevel lvl)
        {
            if (i == 0) AddFoundations(lvl);
            if (i == -1) AddBasementSides(lvl);
            lvl.RegenAll(gd);
            if (i == 0) Ceiling.RegenAll(gd);
        }

        /// <summary>One model per wall covering for the earth's sides round the basement's dug out tiles.</summary>
        private void AddBasementSides(FloorLevel lvl)
        {
            var map = Bp.Basement;
            if (map == null || !map.Any) return;
            var patterns = new HashSet<ushort>();
            for (int y = 0; y < map.Height; y++)
                for (int x = 0; x < map.Width; x++)
                    if (Modelled3DBasement.HasGeometry(map, x, y)) patterns.Add(map.Pattern[y * map.Width + x]);
            foreach (var pattern in patterns) lvl.FoundationGroups.Add(new Modelled3DBasement(Bp, pattern));
            if (patterns.Count > 0) lvl.FoundationGroups.Add(new Modelled3DBasementSkirt(Bp)); //(black under its floor, round its edge)
        }

        /// <summary>The basement's walls changed: its earth's sides are made again (they leave out the edges walls stand on).</summary>
        public void BasementWallsChanged(GraphicsDevice gd)
        {
            if (Bp.Basement == null || !Bp.Basement.Any) return;
            foreach (var group in Floors[LevelArray<int>.MinIndex].FoundationGroups)
                if (group is Modelled3DBasement) group.PrepareGPU(gd);
            //and foundations' sides over it (they're left out where the basement's walls stand in front of them)
            if (Bp.Foundations != null && Bp.Foundations.Any)
                foreach (var group in Floors[0].FoundationGroups) group.PrepareGPU(gd);
        }

        public void SliceReset(GraphicsDevice gd, Rectangle slice)
        {
            for (int i = LevelArray<int>.MinIndex; i < Floors.Length; i++) //the basement (-1) and up
            {
                var lvl = Floors[i];
                var data = Bp.Floors[i];
                lvl.Clear();
                if (i == 0) Ceiling.Clear();

                for (int x=slice.X; x<slice.Right; x++)
                {
                    for (int y = slice.Y; y < slice.Bottom; y++)
                    {
                        var j = y * Bp.Width + x;
                        var pat = data[j].Pattern;
                        if ((j + 1) % Bp.Width < 2) continue;
                        AddFloorTile(i, lvl, j, pat, false);
                    }
                }
                FinishLevel(gd, i, lvl);
            }
        }

        public void BuildableReset(GraphicsDevice gd, bool[] buildable)
        {
            for (int i = LevelArray<int>.MinIndex; i < Floors.Length; i++) //the basement (-1) and up
            {
                var lvl = Floors[i];
                var data = Bp.Floors[i];
                lvl.Clear();
                if (i == 0) Ceiling.Clear();

                for (int j = 0; j < data.Length; j++)
                {
                    if (!buildable[j]) continue;
                    var pat = data[j].Pattern;
                    if ((j + 1) % Bp.Width < 2) continue;
                    AddFloorTile(i, lvl, j, pat, false);
                }
                FinishLevel(gd, i, lvl);
            }
        }

        private static Point[] PoolDirections =
        {
            new Point(0, -1),
            new Point(1, -1),
            new Point(1, 0),
            new Point(1, 1),
            new Point(0, 1),
            new Point(-1, 1),
            new Point(-1, 0),
            new Point(-1, -1),
        };

        private static Dictionary<WorldRotation, Vector4> TexMat = new Dictionary<WorldRotation, Vector4>()
        {
            {WorldRotation.TopLeft, new Vector4(1,0,0,1) },
            {WorldRotation.TopRight, new Vector4(0,1,1,0) },
            {WorldRotation.BottomRight, new Vector4(1,0,0,1) },
            {WorldRotation.BottomLeft, new Vector4(0,1,1,0) }
        };

        private static Dictionary<WorldRotation, Vector4> CounterTexMat = new Dictionary<WorldRotation, Vector4>()
        {
            {WorldRotation.TopLeft, new Vector4(1,0,0,1) },
            {WorldRotation.TopRight, new Vector4(0,1,-1,0) },
            {WorldRotation.BottomRight, new Vector4(-1,0,0,-1) },
            {WorldRotation.BottomLeft, new Vector4(0,-1,1,0) }
        };

        private static Dictionary<WorldZoom, Vector2> TexOffset = new Dictionary<WorldZoom, Vector2>()
        {
            {WorldZoom.Near, new Vector2(0, -0.5f/64) },
            {WorldZoom.Medium, new Vector2(0, -0.5f/32) },
            {WorldZoom.Far, new Vector2(0, -0.5f/16) },
        };

        public ushort PatternConvert(ushort pattern, ushort index, sbyte level, bool buildMode)
        {
            //65520 - 65535 (inclusive): pool tiles
            //65504 - 65519 (inclusive): water tiles
            //65503: air tile
            //corners to come later...

            if (buildMode && pattern == 0 && level > 1)
            {
                var x = index % Bp.Width;
                var y = index / Bp.Width;
                if (Bp.Supported[level - 2][y * Bp.Height + x])
                {
                    return 65503;
                }
                else return 0;
            }
            else if (pattern < 65534 || (pattern == 65535 && !af2019)) return pattern;
            else
            {
                //pool tile... check adjacent tiles
                var x = index % Bp.Width;
                var y = index / Bp.Width;

                int poolAdj = 0;
                for (int i = 0; i < PoolDirections.Length; i++)
                {
                    var testTile = new Point(x, y) + PoolDirections[i];
                    if ((testTile.X <= 0 || testTile.X >= Bp.Width - 1) || (testTile.Y <= 0 || testTile.Y >= Bp.Height - 1)
                        || Bp.GetFloor((short)testTile.X, (short)testTile.Y, level).Pattern == pattern) poolAdj |= 1 << i;
                }

                var adj = (PoolSegments)poolAdj;
                ushort spriteNum = 0;
                if ((adj & PoolSegments.TopRight) > 0) spriteNum |= 1;
                if ((adj & PoolSegments.TopLeft) > 0) spriteNum |= 2;
                if ((adj & PoolSegments.BottomLeft) > 0) spriteNum |= 4;
                if ((adj & PoolSegments.BottomRight) > 0) spriteNum |= 8;

                if (pattern == 65535) spriteNum += 65520;
                else spriteNum += 65504;

                return spriteNum;
            }
        }

        /// <summary>The ground under column decks, for grass blades (drawn from TerrainComponent.GroundVertexBuffer): its index buffer and primitives.</summary>
        public IndexBuffer UnderDeckGrass(out int primitives)
        {
            var grp = Floors[0].GroundUnderDecks;
            primitives = (grp.GPUData == null) ? 0 : grp.GeomForOffset.Count * 2;
            return grp.GPUData;
        }

        public int SetGrassIndices(GraphicsDevice gd, Effect e, WorldState state)
        {
            var floor = Floors[0];
            FloorTileGroup grp = null;
            if (!floor.GroupForTileType.TryGetValue(0, out grp)) return 0;
            var dat = grp.GPUData;
            if (dat == null) return 0;
            gd.Indices = dat;
            return dat.IndexCount/3;
        }

        public SamplerState CustomWrap = new SamplerState()
        {
            Filter = TextureFilter.Linear,
            AddressU = TextureAddressMode.Wrap,
            AddressV = TextureAddressMode.Wrap,
            AddressW = TextureAddressMode.Wrap,
        };

        public DepthStencilState NoDS = new DepthStencilState()
        {
            DepthBufferWriteEnable = false
        };

        private bool Alt;

        private Vector4 GetParallaxMatrix(WorldState state)
        {
            Vector4 mat;

            switch (state.Rotation)
            {
                case WorldRotation.TopLeft:
                    mat = new Vector4(0.7071f, -0.7071f, 0.7071f, 0.7071f);
                    break;
                case WorldRotation.TopRight:
                    mat = new Vector4(0.7071f, 0.7071f, -0.7071f, 0.7071f);
                    break;
                case WorldRotation.BottomRight:
                    mat = new Vector4(-0.7071f, 0.7071f, -0.7071f, -0.7071f);
                    break;
                default:
                    mat = new Vector4(-0.7071f, -0.7071f, 0.7071f, -0.7071f);
                    break;
            }

            return mat;
        }

        public void DrawFloor(GraphicsDevice gd, GrassEffect e, WorldZoom zoom, WorldRotation rot, List<Texture2D> roommaps, HashSet<sbyte> floors, EffectPass pass, 
            Matrix? lightWorld = null, WorldState state = null, int minFloor = 0, bool screenAlignUV = false, bool withCeilings = false)
        {
            bool is3D = state?.CameraMode == CameraRenderMode._3D;
            var parallax = WorldConfig.Current.Complex;
            var buggedTex = WorldConfig.Current.AdvancedLighting ? 1 : 0;
            //assumes the effect and all its parameters have been set up already
            //we just need to get the right texture and offset
            var flrContent = Content.Content.Get().WorldFloors;

            e.TexOffset = new Vector2();// TexOffset[zoom]*-1f);
            var tmat = TexMat[rot];
            e.TexMatrix = tmat;
            e.ScreenAlignUV = screenAlignUV;
            var baseRS = gd.RasterizerState;

            if (withCeilings)
            {
                gd.RasterizerState = RasterizerState.CullCounterClockwise;
            }

            //the basement (index -1) and up; the ground floor's tiles over the basement (its ceiling) are drawn with the
            //ground floor, except while the basement is shown
            var drawCeiling = state == null || state.Level > 0;
            for (int fi = LevelArray<int>.MinIndex; fi <= Floors.Length; fi++)
            {
                FloorLevel floor;
                int index;
                if (fi == Floors.Length)
                {
                    if (!drawCeiling) continue;
                    floor = Ceiling;
                    index = 0;
                }
                else
                {
                    floor = Floors[fi];
                    index = fi;
                }
                if (!floors.Contains((sbyte)index)) continue;
                var f = index + 1;

                Matrix worldmat;
                if (lightWorld == null)
                    worldmat = Matrix.CreateTranslation(0, 2.95f * (f - 1) * 3 - Bp.BaseAlt * Bp.TerrainFactor * 3, 0);
                else
                {
                    worldmat = Matrix.CreateScale(1, 0, 1) * Matrix.CreateTranslation(0, 1f * (f - (1 + minFloor)), 0) * lightWorld.Value;
                    e.DiffuseColor = new Vector4(1, 1, 1, 1) * (float)(6 - (f - (minFloor)))/5f;
                }
                
                e.World = worldmat;
                e.Level = (float)(f-((lightWorld == null)?0.999f:1f)); //(the basement's, below 0, takes the basement's light)
                if (roommaps != null) e.RoomMap = roommaps[LMap.GPURoomMaps.Slot(f-1)];
                foreach (var type in floor.GroupForTileType)
                {
                    bool water = false;
                    if (type.Value is Modelled3DFloor)
                    {
                        //we generated a special model for this tile type
                        var mdl = type.Value as Modelled3DFloor;
                        e.TexMatrix = new Vector4(1, 0, 0, 1);
                        e.ScreenAlignUV = false;
                        e.GrassShininess = 0.02f;
                        e.Bias = -0.5f;

                        gd.Indices = mdl.GPUData;
                        gd.SetVertexBuffer(mdl.VertGPUData);
                        var tex = mdl.GetTexture(gd);
                        e.BaseTex = tex;
                        if (tex != null) e.TexSize = new Vector2(tex.Width, tex.Height);
                        //e.CurrentTechnique.Passes[2].Apply();
                        pass.Apply();
                        gd.SamplerStates[0] = CustomWrap;
                        gd.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, mdl.PrimitiveCount);
                        gd.SetVertexBuffer(Bp.Terrain.VertexBuffer);

                        e.TexMatrix = tmat;
                        e.ScreenAlignUV = screenAlignUV;
                        e.Bias = -999;
                        continue;
                    }
                    var dat = type.Value.GPUData;
                    if (dat == null) continue;
                    gd.Indices = dat;

                    var id = type.Key;
                    var doubleDraw = false;
                    Texture2D SPR = null;
                    Texture2D pSPR = null;

                    if (id == 0)
                    {
                        e.UseTexture = false;
                        e.IgnoreColor = false;
                        e.GrassShininess = 0.02f;// (float)0.25);
                    }
                    else
                    {
                        e.GrassShininess = (id >= 65503)?0.02f:0f;
                        if (id >= 65503)
                        {
                            if (id == 65503)
                            {
                                water = true;
                                var airTiles = TextureGenerator.GetAirTiles(gd);
                                switch (zoom)
                                {
                                    case WorldZoom.Far:
                                        SPR = airTiles[2];
                                        break;
                                    case WorldZoom.Medium:
                                        SPR = airTiles[1];
                                        break;
                                    case WorldZoom.Near:
                                        SPR = airTiles[0];
                                        break;
                                }
                            }
                            else
                            {
                                e.Water = true;
                                e.ScreenAlignUV = false;
                                var pool = id >= 65520;
                                water = true;
                                if (!pool)
                                {
                                    e.UseTexture = false;
                                    e.IgnoreColor = false;

                                    //quickly draw under the water
                                    pass.Apply();
                                    gd.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, type.Value.GeomForOffset.Count * 2);

                                    e.UseTexture = true;
                                    e.IgnoreColor = true;
                                    if (lightWorld == null) e.World = worldmat * Matrix.CreateTranslation(0, 0.05f, 0);
                                    id -= 65504;
                                }
                                else
                                {
                                    id -= 65520;
                                }

                                e.TexMatrix = CounterTexMat[rot];

                                var roti = (int)rot;
                                roti = (4 - roti) % 4;
                                id = (ushort)(((id << roti) & 15) | (id >> (4 - roti)));
                                //pools & water are drawn with special logic, and may also be drawn slightly above the ground.

                                int baseSPR;
                                int frameNum = 0;
                                if (state != null)
                                {
                                    if (PoolReplace != null && pool)
                                    {
                                        SPR = PoolReplace[id];
                                        if (parallax) pSPR = PoolReplaceParallax[id];
                                    }
                                    else
                                    {
                                        switch (zoom)
                                        {
                                            case WorldZoom.Far:
                                                baseSPR = (pool) ? 0x400 : 0x800;
                                                frameNum = (pool) ? 0 : 2;
                                                SPR = state._2D.GetTexture(flrContent.GetGlobalSPR((ushort)(baseSPR + id)).Frames[frameNum]);
                                                break;
                                            case WorldZoom.Medium:
                                                baseSPR = (pool) ? 0x410 : 0x800;
                                                frameNum = (pool) ? 0 : 1;
                                                SPR = state._2D.GetTexture(flrContent.GetGlobalSPR((ushort)(baseSPR + id)).Frames[frameNum]);
                                                break;
                                            default:
                                                baseSPR = (pool) ? 0x420 : 0x800;
                                                SPR = state._2D.GetTexture(flrContent.GetGlobalSPR((ushort)(baseSPR + id)).Frames[frameNum]);
                                                break;
                                        }
                                    }
                                }
                            }
                        }
                        else
                        {
                            var flr = flrContent.Get(id);

                            if (flr == null) continue;

                            if (state != null)
                            {
                                switch (zoom)
                                {
                                    case WorldZoom.Far:
                                        SPR = state._2D.GetTexture(flr.Far.Frames[0]);
                                        break;
                                    case WorldZoom.Medium:
                                        SPR = state._2D.GetTexture(flr.Medium.Frames[0]);
                                        break;
                                    default:
                                        SPR = state._2D.GetTexture(flr.Near.Frames[0]);
                                        break;
                                }
                            }
                        }

                        //e.UseTexture = SPR != null);

                    }

                    e.BaseTex = SPR;
                    if (SPR != null) e.TexSize = new Vector2(SPR.Width, SPR.Height);
                    if (SPR != null && SPR.Name == null)
                    {
                        doubleDraw = true;
                        SPR.Name = Alt.ToString();
                    }
                    if (pSPR != null)
                    {
                        var parallaxPass = e.CurrentTechnique.Passes[4];
                        e.ParallaxTex = pSPR;
                        e.ParallaxUVTexMat = GetParallaxMatrix(state);
                        e.ParallaxHeight = 0.1f;
                        parallaxPass.Apply();
                    }
                    else
                    {
                        pass.Apply();
                    }
                    if (Alt && !FSOEnvironment.DirectX)
                    {
                        //opengl bug workaround. For some reason, the texture is set to clamp mode by some outside force on first draw. 
                        //Monogame then thinks the texture is wrapping.
                        gd.SamplerStates[buggedTex] = CustomWrap;
                    }
                    gd.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, type.Value.GeomForOffset.Count * 2);

                    if (f > 1 && is3D && withCeilings)
                    {
                        // Draw ceilings in 3D mode.
                        gd.RasterizerState = RasterizerState.CullClockwise;
                        e.Ceiling = true;
                        pass.Apply();

                        gd.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, type.Value.GeomForOffset.Count * 2);

                        gd.RasterizerState = RasterizerState.CullCounterClockwise;
                        e.Ceiling = false;
                    }

                    if (id == 0)
                    {
                        e.UseTexture = true;
                        e.IgnoreColor = true;
                    }
                    if (water)
                    {
                        e.ScreenAlignUV = screenAlignUV;
                        e.World = worldmat;
                        e.TexMatrix = tmat;
                        e.Water = false;
                    }
                }

                // the ground under column decks: drawn as the lot's ground (as tile 0 is, above), unraised
                var groundVB = Bp.Terrain?.GroundVertexBuffer;
                if (floor.GroundUnderDecks.GPUData != null && floor.GroundUnderDecks.GeomForOffset.Count > 0 && groundVB != null)
                {
                    e.UseTexture = false;
                    e.IgnoreColor = false;
                    e.GrassShininess = 0.02f;
                    gd.SetVertexBuffer(groundVB);
                    gd.Indices = floor.GroundUnderDecks.GPUData;
                    pass.Apply();
                    gd.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, floor.GroundUnderDecks.GeomForOffset.Count * 2);
                    gd.SetVertexBuffer(Bp.Terrain.VertexBuffer);
                    e.UseTexture = true;
                    e.IgnoreColor = true;
                }

                // while the basement's shown (with all above it hidden, round it is the void: see WorldStatic.DrawBg)
                var basementShown = fi == LevelArray<int>.MinIndex && lightWorld == null && state != null && state.Level < 1;

                // foundations: their sides, bare tops and steps, with the ground floor
                // (the earth's sides round the basement stand on its rooms' edges, as walls do: like walls, they take their
                // light from just in front of them, not half from the earth behind)
                var earthSides = fi == LevelArray<int>.MinIndex && lightWorld == null && floor.FoundationGroups.Count > 0;
                var floorOffset = e.LightOffset;
                if (earthSides) e.LightOffset = WorldContent.RCObject.LightOffset;
                foreach (var fnd in floor.FoundationGroups)
                {
                    //the earth's sides come down where the walls do, while the basement's shown (they're whole otherwise)
                    if (lightWorld == null && fnd is Modelled3DBasement sides)
                        sides.UseView(gd, basementShown, Bp.Cutaway, state?.CutRotation ?? WorldRotation.TopLeft);
                    if (fnd.PrimitiveCount == 0 || fnd.GPUData == null) continue;
                    fnd.ResolveTexture(gd, state);
                    e.TexMatrix = new Vector4(1, 0, 0, 1);
                    e.ScreenAlignUV = false;
                    e.GrassShininess = (fnd is Modelled3DBasementSkirt) ? 0f : 0.02f; //(the skirt stays black)
                    e.Bias = -0.5f;
                    e.UseTexture = true;
                    e.IgnoreColor = true;
                    gd.Indices = fnd.GPUData;
                    gd.SetVertexBuffer(fnd.VertGPUData);
                    var ftex = fnd.GetTexture(gd);
                    e.BaseTex = ftex;
                    if (ftex != null) e.TexSize = new Vector2(ftex.Width, ftex.Height);
                    pass.Apply();
                    gd.SamplerStates[0] = CustomWrap;
                    gd.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, fnd.PrimitiveCount);
                    gd.SetVertexBuffer(Bp.Terrain.VertexBuffer);
                    e.TexMatrix = tmat;
                    e.ScreenAlignUV = screenAlignUV;
                    e.Bias = -999;
                }
                if (earthSides) e.LightOffset = floorOffset;
            }
            e.Water = false;
            e.BasementLight = false; //(back to the ground's light for whatever's drawn after)
            Alt = !Alt;

            gd.RasterizerState = baseRS;
        }

        /*
        public void Regenerate(Rectangle bounds)
        {
            //find all meshes that are dirty:
            // - all floor textures with bounds intersecting these bounds (old)
            // - all floor textures present now (new, union)

            var floors = Bp.Floors[FloorNum];
            //get all 
            var set = new HashSet<ushort>();
            for (int y = bounds.Top; y < bounds.Bottom; y++)
            {
                var idx = y * Bp.Width + bounds.Left;
                for (int x = bounds.Left; x < bounds.Right; x++)
                {
                    set.Add(floors[idx++].Pattern);
                }
            }

            foreach (var geom in IDToGeom.Values)
            {
                if (bounds.Intersects(geom.Bounds) || set.Contains(geom.FloorID))
                {
                    set.Add(geom.FloorID);
                    bounds = Rectangle.Union(geom.Bounds, bounds);
                }
            }

            var idxwidth = (Bp.Width * 2) + 1;
            //build indices for all the geometry
            for (int y=bounds.Top; y<bounds.Bottom; y++)
            {
                var by = idxwidth * y;
                for (int x=bounds.Bottom; x<bounds.Right; x++)
                {
                    //

                    var idxs = new List<int>();
                    var basei = x*2 + by;

                    //top tri
                    idxs.Add(basei); //tl
                    idxs.Add(basei + 2); //tr
                    idxs.Add(basei + 1); //ctr
                    
                    //right tri
                    idxs.Add(basei + 2); //tr
                    idxs.Add(basei + idxwidth + 2); //br
                    idxs.Add(basei + 1); //ctr

                    //left tri
                    idxs.Add(basei); //tl
                    idxs.Add(basei + 1); //ctr
                    idxs.Add(basei + idxwidth); //bl

                    //bottom tri
                    idxs.Add(basei + 1); //ctr
                    idxs.Add(basei + idxwidth + 2); //br
                    idxs.Add(basei + idxwidth); //bl

                }
            }
        }
        */

        public void Dispose()
        {
            foreach (var floor in Floors)
            {
                floor.Dispose();
            }
            Floors.Basement?.Dispose();
            Ceiling?.Dispose();
        }
    }

    public class FloorLevel : IDisposable
    {
        public Dictionary<ushort, FloorTileGroup> GroupForTileType = new Dictionary<ushort, FloorTileGroup>();
        /// <summary>The foundations' models (ground floor only), one per wall covering.</summary>
        public List<Modelled3DFoundation> FoundationGroups = new List<Modelled3DFoundation>();
        /// <summary>Ground tiles under column decks, drawn as the lot's ground from the unraised ground's vertices.</summary>
        public FloorTileGroup GroundUnderDecks = new FloorTileGroup();
        public ushort[] Tiles;
        private Blueprint Bp;

        public FloorLevel(Blueprint bp)
        {
            Bp = bp;
        }

        public FloorTileGroup AddGroup(ushort tileID)
        {
            FloorTileGroup group;

            if (!_3DFloorGeometry.af2019 && tileID == 65535)
            {
                //pool tile group
                group = new Modelled3DPool(Bp);
            }
            else
            {
                group = new FloorTileGroup();
            }
            GroupForTileType[tileID] = group;
            return group;
        }

        public void AddTile(ushort tileID, ushort offset)
        {
            FloorTileGroup group;
            if (!GroupForTileType.TryGetValue(tileID, out group))
            {
                group = AddGroup(tileID);
            }
            group.AddIndex(offset);
        }

        public void AddTileDiag(ushort tileID, ushort offset, bool side, bool vertical)
        {
            FloorTileGroup group;
            if (!GroupForTileType.TryGetValue(tileID, out group))
            {
                group = AddGroup(tileID);
                GroupForTileType[tileID] = group;
            }
            group.AddDiagIndex(offset, side, vertical);
        }

        public void Regen(GraphicsDevice gd, HashSet<ushort> floors)
        {

        }

        public void RegenAll(GraphicsDevice gd)
        {
            foreach (var group in GroupForTileType)
            {
                group.Value.PrepareGPU(gd);
            }
            foreach (var group in FoundationGroups) group.PrepareGPU(gd);
            if (GroundUnderDecks.GeomForOffset.Count > 0) GroundUnderDecks.PrepareGPU(gd);

            Bp.SM64?.UpdateFloors();
        }

        public void Clear()
        {
            Dispose();
            GroupForTileType.Clear();
            FoundationGroups.Clear();
            GroundUnderDecks = new FloorTileGroup();
        }

        public void Dispose()
        {
            foreach (var group in GroupForTileType)
            {
                group.Value.Dispose();
            }
            foreach (var group in FoundationGroups) group.Dispose();
            GroundUnderDecks.Dispose();
        }
    }

    public class FloorTileGroup : IDisposable
    {
        public Dictionary<ushort, FloorTileIndices> GeomForOffset = new Dictionary<ushort, FloorTileIndices>();
        public IndexBuffer GPUData;

        public virtual void PrepareGPU(GraphicsDevice gd)
        {
            if (GPUData != null) GPUData.Dispose();
            var dat = BuildIndexData();
            GPUData = new IndexBuffer(gd, IndexElementSize.ThirtyTwoBits, dat.Length, BufferUsage.None);
            GPUData.SetData(dat);
        }

        private int[] BuildIndexData()
        {
            var result = new int[GeomForOffset.Count * 6];
            var resultSpan = result.AsSpan();
            int i = 0;
            foreach (var geom in GeomForOffset.Values)
            {
                var geomCopy = geom;
                int newI = i + geom.Length;
                geomCopy.GetSpan().CopyTo(resultSpan[i..newI]);
                i = newI;
            }
            return result;
        }

        public void AddIndex(ushort offset)
        {
            //
            var o2 = offset * 4;
            var result = new FloorTileIndices(o2, o2 + 1, o2 + 2, o2 + 2, o2 + 3, o2);
            GeomForOffset[offset] = result;
        }

        public void AddDiagIndex(ushort offset, bool side, bool vertical)
        {
            //
            var o2 = offset * 4;
            FloorTileIndices result;
            if (vertical)
            {
                if (side) result = new FloorTileIndices(o2, o2 + 1, o2 + 2);
                else result = new FloorTileIndices(o2 + 2, o2 + 3, o2);
            } else
            {
                if (side) result = new FloorTileIndices(o2 +1, o2 + 2, o2 + 3);
                else result = new FloorTileIndices(o2, o2 + 1, o2 + 3);
            }
            GeomForOffset[(ushort)(offset + (side?32768:0))] = result;
        }


        public bool RemoveIndex(ushort offset)
        {
            GeomForOffset.Remove(offset);
            return GeomForOffset.Count > 0;
        }

        public virtual void Dispose()
        {
            GPUData?.Dispose();
        }
    }

    public class FloorTextureGroup
    {
        public Rectangle Bounds;
        public ushort FloorID;
    }
}
