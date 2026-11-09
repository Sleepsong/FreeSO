using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using FSO.Files.Formats.IFF.Chunks;
using FSO.LotView.Model;
using FSO.Common.Rendering;
using Microsoft.Xna.Framework.Graphics;
using FSO.Files.RC;
using FSO.LotView.Effects;
using FSO.Common;
using FSO.Common.Utils;

namespace FSO.LotView.Utils
{
    public class DGRPRendererItem 
    {
        public _2DStandaloneSprite Sprite;
        public DGRPSprite DGRPSprite;
    }

    /// <summary>
    /// Used for rendering DGRPs in the world.
    /// </summary>
    public class DGRPRenderer : IDisposable
    {
        protected DGRP DrawGroup;
        protected OBJD Source;
        public Rectangle? Bounding;
        public ulong DynamicSpriteFlags = 0x00000000;
        public ulong DynamicSpriteFlags2 = 0x00000000;
        public ushort DynamicSpriteBaseID;
        public ushort NumDynamicSprites;

        public ushort Room;
        public sbyte Level = 1;
        public short ObjectID;

        //2d cache
        private List<DGRPRendererItem> Items = new List<DGRPRendererItem>();

        //3d cache
        private DGRP3DMesh Mesh;
        public Matrix World;
        /// <summary>Whether the object is in another object (a sink in a counter): only then is its basin cut into it.</summary>
        public bool InObject;
        /// <summary>The top of the object this one is in, in this mesh's units above this object's base (NaN if unknown).</summary>
        public float ContainerTop = float.NaN;
        /// <summary>The name of the mesh of the object this one is in (for notes).</summary>
        public string ContainerMesh;
        /// <summary>The top of this object's mesh, in its own units (NaN until it's loaded).</summary>
        public float MeshTop => (Mesh?.Bounds == null) ? float.NaN : Mesh.Bounds.Value.Max.Y;

        /// <summary>Notes for a log (the frontend sets it, e.g. with -logs): what's cut into the object an object is in.</summary>
        public static Action<string> Note;
        private static HashSet<string> Noted = new HashSet<string>();
        
        public DGRPRenderer(DGRP group, OBJD source)
        {
            this.DrawGroup = group;
            this.Source = source;
        }

        /// <summary>
        /// Gets or sets the DGRP used by this DGRPRenderer instance.
        /// </summary>
        public DGRP DGRP
        {
            get
            {
                return DrawGroup;
            }
            set
            {
                DrawGroup = value;
                _TextureDirty = true;
                _Dirty = ComponentRenderMode.Both;
            }
        }

        protected ComponentRenderMode _Dirty = ComponentRenderMode.Both;
        private bool _TextureDirty = true;

        private Direction _Direction;

        /// <summary>
        /// Gets or sets the direction that the DGRP is facing.
        /// </summary>
        public Direction Direction
        {
            get{
                return _Direction;
            }
            set{
                _Direction = value;
                _TextureDirty = true;
                _Dirty = ComponentRenderMode.Both;
            }
        }

        private Vector3 _Position;
        public Vector3 Position
        {
            get
            {
                return _Position;
            }
            set
            {
                if (_Position != value) _Dirty = ComponentRenderMode.Both;
                _Position = value;
            }
        }

        private float RadianDirection
        {
            get
            {
                switch (_Direction)
                {
                    case Direction.NORTH:
                        return 0;
                    case Direction.EAST:
                        return (float)Math.PI / 2;
                    case Direction.SOUTH:
                        return (float)Math.PI;
                    case Direction.WEST:
                        return (float)Math.PI * 1.5f;
                    default:
                        return 0;
                }
            }
        }

        public bool CanHaveBounds => DGRP != null && Mesh != null && Mesh.Geoms.Count > 0;

        public void InvalidateRotation()
        {
            _TextureDirty = true;
            _Dirty = ComponentRenderMode.Both;
        }

        public void InvalidateZoom()
        {
            _TextureDirty = true;
            _Dirty = ComponentRenderMode.Both;
        }

        public void InvalidateScroll()
        {
            //_Dirty = true;
        }

        public BoundingBox? GetBounds()
        {
            if (_Dirty.IsSet(ComponentRenderMode._3D) && DrawGroup != null)
            {
                Mesh = Content.Content.Get().RCMeshes.Get(DrawGroup, Source);
                _Dirty &= ~ComponentRenderMode._3D;
            }
            return Mesh?.Bounds;
        }

        public virtual void ValidateSprite(WorldState world)
        {
            if (DrawGroup == null) return;
            if (_Dirty.IsSet(ComponentRenderMode._2D))
            {
                if (_TextureDirty)
                {
                    Dispose();
                    Items.Clear();
                    var direction = (uint)_Direction;

                    /** Compute the direction **/
                    var image = DrawGroup.GetImage(direction, (uint)world.Zoom, (uint)world.Rotation);
                    int maxX = int.MinValue, maxY = int.MinValue;
                    int minX = int.MaxValue, minY = int.MaxValue;
                    if (image != null)
                    {
                        foreach (var dgrpSprite in image.Sprites)
                        {
                            if (dgrpSprite == null) continue;
                            var texture = world._2D.GetWorldTexture(dgrpSprite);
                            if (texture.Pixel == null) { continue; }

                            var isDynamic = dgrpSprite.SpriteID >= DynamicSpriteBaseID && dgrpSprite.SpriteID < (DynamicSpriteBaseID + NumDynamicSprites);
                            if (isDynamic)
                            {
                                var dynamicIndex = (ushort)(dgrpSprite.SpriteID - DynamicSpriteBaseID);
                                
                                var isVisible = (dynamicIndex > 63) ? ((DynamicSpriteFlags2 & ((ulong)0x1 << (dynamicIndex-64))) > 0):
                                    ((DynamicSpriteFlags & ((ulong)0x1 << dynamicIndex)) > 0);
                                if (!isVisible)
                                {
                                    continue;
                                }
                            }
                            var sprite = new _2DStandaloneSprite(); //do not use sprite pool for DGRP, since we can reliably remember our own sprites.
                            sprite.Pixel = texture.Pixel;
                            sprite.Depth = texture.ZBuffer;
                            if (texture.ZBuffer != null)
                            {
                                sprite.RenderMode = _2DBatchRenderMode.Z_BUFFER;
                                sprite.WorldPosition = dgrpSprite.ObjectOffset;
                            }
                            else
                            {
                                sprite.RenderMode = _2DBatchRenderMode.NO_DEPTH;
                            }

                            var pt = ((TextureInfo)sprite.Pixel.Tag).Size;
                            sprite.SrcRect = new Rectangle(0, 0, pt.X, pt.Y);
                            sprite.DestRect = new Rectangle(0, 0, pt.X, pt.Y);
                            sprite.FlipHorizontally = dgrpSprite.Flip;
                            sprite.ObjectID = ObjectID;

                            Items.Add(new DGRPRendererItem { Sprite = sprite, DGRPSprite = dgrpSprite });

                            var pxX = (world.WorldSpace.CadgeWidth / 2.0f) + dgrpSprite.SpriteOffset.X;
                            var pxY = (world.WorldSpace.CadgeBaseLine - sprite.DestRect.Height) + dgrpSprite.SpriteOffset.Y;

                            if (dgrpSprite.ObjectOffset != Vector3.Zero) { }
                            var centerRelative = dgrpSprite.ObjectOffset * new Vector3(1f / 16f, 1f / 16f, 1f / 5f);
                            centerRelative = Vector3.Transform(centerRelative, Matrix.CreateRotationZ(RadianDirection));

                            var pxOff = world.WorldSpace.GetScreenFromTile(centerRelative);

                            sprite.DestRect.X = (int)(pxX + pxOff.X);
                            sprite.DestRect.Y = (int)(pxY + pxOff.Y);

                            if (sprite.DestRect.X < minX) minX = sprite.DestRect.X;
                            if (sprite.DestRect.Y < minY) minY = sprite.DestRect.Y;
                            if (sprite.DestRect.X + sprite.Pixel.Width > maxX) maxX = sprite.DestRect.X + sprite.Pixel.Width;
                            if (sprite.DestRect.Y + sprite.Pixel.Height > maxY) maxY = sprite.DestRect.Y + sprite.Pixel.Height;

                            sprite.WorldPosition = centerRelative * 3f;
                            var y = sprite.WorldPosition.Z;
                            sprite.WorldPosition.Z = sprite.WorldPosition.Y;
                            sprite.WorldPosition.Y = y;
                            sprite.Room = ((dgrpSprite.Flags & DGRPSpriteFlags.Luminous) > 0 && Room != 65534 && Room != 65533) ? (ushort)65535 : Room;
                            sprite.Floor = Level;
                        }
                    }

                    _TextureDirty = false;
                    Bounding = new Rectangle(minX, minY, maxX - minX, maxY - minY);
                }

                Rectangle? bounding = null;
                foreach (var item in Items) {
                    item.Sprite.AbsoluteDestRect = item.Sprite.DestRect;
                    item.Sprite.AbsoluteDestRect.Offset(world.WorldSpace.GetScreenFromTile(_Position));
                    if (bounding == null) bounding = item.Sprite.AbsoluteDestRect;
                    else bounding = Rectangle.Union(bounding.Value, item.Sprite.AbsoluteDestRect);
                    item.Sprite.AbsoluteWorldPosition = item.Sprite.WorldPosition + WorldSpace.GetWorldFromTile(_Position);
                    item.Sprite.PrepareVertices(world.Device);
                }
                Bounding = bounding ?? new Rectangle();

                _Dirty &= ~ComponentRenderMode._2D;
            }
        }

        public virtual void Draw(WorldState world)
        {
            ValidateSprite(world);

            foreach (var item in Items)
            {
                world._2D.Draw(item.Sprite);
            }
        }

        public virtual void DrawImmediate(WorldState world)
        {
            ValidateSprite(world);

            foreach (var item in Items)
            {
                world._2D.DrawImmediate(item.Sprite);
            }
        }

        public void Draw3D(WorldState world)
        {
            if (DrawGroup == null) return;
            if (_Dirty.IsSet(ComponentRenderMode._3D) || Mesh == null)
            {
                Mesh = Content.Content.Get().RCMeshes.Get(DrawGroup, Source);
                _Dirty &= ~ComponentRenderMode._3D;
            }

            //immedately draw the mesh.
            var device = world.Device;
            var effect = WorldContent.RCObject;

            effect.World = World;
            effect.Level = (float)(Level - 0.999f);
            var advDir = WorldConfig.Current.Directional && WorldConfig.Current.AdvancedLighting;

            var basin = (InObject && Mesh.MaskType != DGRP3DMaskType.Portal) ? Mesh.BasinVolume : null;
            if (InObject && Note != null) NoteBasin();
            if (basin?.Verts != null)
            {
                //a column over the basin, from its bottom to just above the top of what it's in (a counter: capped, so it
                //can't reach far above the counter and take a Sim's hands with it). Each of its faces behind what's drawn
                //so far flips the stencil: once (1) where that is inside the column, which is the counter top, and twice or
                //never (0) where it's in front of the column (a Sim) or behind it. Then the depth is cleared where it's 1,
                //and the basin is drawn into the counter.
                var top = (float.IsNaN(ContainerTop) ? 0.02f : Math.Max(-0.05f, Math.Min(0.12f, ContainerTop))) + 0.03f;
                var bottom = Math.Min(Mesh.BasinBottom, top - 0.05f);
                effect.World = Matrix.CreateScale(1, top - bottom, 1) * Matrix.CreateTranslation(0, bottom, 0) * World;

                var lastRaster = device.RasterizerState;
                device.RasterizerState = RasterizerState.CullNone;
                effect.SetTechnique(RCObjectTechniques.DepthClear);
                effect.CurrentTechnique.Passes[0].Apply();
                device.DepthStencilState = BasinMark;
                device.Indices = basin.Indices;
                device.SetVertexBuffer(basin.Verts);
                device.BlendState = NoColor;
                device.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, basin.PrimCount);

                device.DepthStencilState = DepthClear2;
                DrawDepthClearPass(device, effect, basin.PrimCount);

                device.DepthStencilState = DepthStencilState.Default;
                device.BlendState = BlendState.NonPremultiplied;
                device.RasterizerState = lastRaster;
                effect.World = World;
                effect.SetTechnique(RCObjectTechniques.Draw);
            }
            else if (Mesh.DepthMask != null)
            {
                var geom = Mesh.DepthMask;
                //depth mask for drawing into a surface or wall (a basin cut into what it's in replaces it)
                if (geom.Verts != null)
                {
                    effect.SetTechnique(RCObjectTechniques.DepthClear);
                    effect.CurrentTechnique.Passes[0].Apply();

                    device.DepthStencilState = DepthClear1;
                    device.Indices = geom.Indices;
                    device.SetVertexBuffer(geom.Verts);

                    device.BlendState = NoColor;
                    device.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, geom.PrimCount);

                    device.DepthStencilState = (Mesh.MaskType == DGRP3DMaskType.Portal) ? DepthClear2Strict : DepthClear2;
                    DrawDepthClearPass(device, effect, geom.PrimCount);

                    device.DepthStencilState = DepthStencilState.Default;
                    device.BlendState = BlendState.NonPremultiplied;
                    effect.SetTechnique(RCObjectTechniques.Draw);
                }
            }

            if (Room == 65533) effect.SetTechnique(RCObjectTechniques.DisabledDraw);

            int i = 0;
            foreach (var spr in Mesh.Geoms)
            {
                if (i == 0 || (((i - 1) > 63) ? ((DynamicSpriteFlags2 & ((ulong)0x1 << ((i - 1) - 64))) > 0) :
                    ((DynamicSpriteFlags & ((ulong)0x1 << (i - 1))) > 0)) || (Mesh.MaskType == DGRP3DMaskType.Portal && i == Mesh.Geoms.Count - 1))
                {
                    foreach (var geom in spr.Values)
                    {
                        if (geom.PrimCount == 0) continue;
                        if (Mesh.MaskType == DGRP3DMaskType.Portal && i == Mesh.Geoms.Count - 1)
                            device.DepthStencilState = Portal;
                        effect.MeshTex = geom.Pixel;
                        var info = geom.Pixel?.Tag as TextureInfo;
                        effect.UVScale = info?.UVScale ?? Vector2.One;
                        var pass = effect.CurrentTechnique.Passes[(advDir && Room < 65533) ? 1 : 0];
                        pass.Apply();
                        if (geom.Rendered)
                        {
                            device.Indices = geom.Indices;
                            device.SetVertexBuffer(geom.Verts);

                            device.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, geom.PrimCount);
                        }
                        if (Mesh.MaskType == DGRP3DMaskType.Portal && i == Mesh.Geoms.Count - 1)
                            device.DepthStencilState = DepthStencilState.Default;
                    }
                }
                i++;
            }

            if (Mesh.MaskType == DGRP3DMaskType.Portal)
            {
                var geom = Mesh.DepthMask;
                //clear the stencil, so it doesn't interfere with future portals.
                if (geom.Verts != null)
                {
                    effect.SetTechnique(RCObjectTechniques.DepthClear);

                    device.DepthStencilState = StencilClearOnly;
                    device.Indices = geom.Indices;
                    device.SetVertexBuffer(geom.Verts);

                    device.BlendState = NoColor;
                    DrawDepthClearPass(device, effect, geom.PrimCount);
                    device.BlendState = BlendState.NonPremultiplied;
                }
                device.DepthStencilState = DepthStencilState.Default;
                effect.SetTechnique(RCObjectTechniques.Draw);
            }
            if (Room == 65533) effect.SetTechnique(RCObjectTechniques.Draw);
        }

        /// <summary>
        /// The depth clear technique's second pass: depth pushed to the far plane where the stencil marks it (behind a
        /// basin's opening, or a depth mask). Its pixel shader writes gl_FragDepth, which phone shaders (GLSL ES 1.00,
        /// FSOEnvironment.SoftwareDepth) don't have, so it can't compile there. On phones the same geometry is drawn with
        /// the first pass's shader and a view-projection whose depth equals its w, which puts every point just in front of
        /// the far plane: the GPU writes the far depth instead of the shader.
        /// </summary>
        private static void DrawDepthClearPass(GraphicsDevice device, RCObjectEffect effect, int primCount)
        {
            if (!FSOEnvironment.SoftwareDepth)
            {
                effect.CurrentTechnique.Passes[1].Apply();
                device.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, primCount);
                return;
            }
            const float nearlyFar = 0.99999f; //(z = w would sit exactly on the far plane, where it could be clipped)
            var vp = effect.ViewProjection;
            var far = vp;
            far.M13 = vp.M14 * nearlyFar;
            far.M23 = vp.M24 * nearlyFar;
            far.M33 = vp.M34 * nearlyFar;
            far.M43 = vp.M44 * nearlyFar;
            effect.ViewProjection = far;
            effect.CurrentTechnique.Passes[0].Apply();
            device.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, primCount);
            effect.ViewProjection = vp; //(uploaded with the next pass applied)
        }

        /// <summary>Notes once, for each object in another, what's cut into it and why (or why not).</summary>
        private void NoteBasin()
        {
            if (Mesh.Name == "Loading") return; //still streaming in: noted once it's loaded
            var name = Mesh.Name ?? "?";
            var key = name + " in " + ContainerMesh;
            lock (Noted) { if (!Noted.Add(key)) return; }
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string f(float v) => float.IsNaN(v) ? "unknown" : v.ToString("0.000", inv);
            string what;
            if (Mesh.MaskType == DGRP3DMaskType.Portal) what = "it has a portal mask, so nothing is cut";
            else if (Mesh.BasinVolume != null)
                what = "its basin (" + Mesh.BasinPoints + " point outline, bottom " + f(Mesh.BasinBottom) + ") is cut into it"
                    + ((Mesh.DepthMask != null) ? ", in place of the mesh's own depth mask" : "");
            else what = "nothing reaches far enough below its base to cut (lowest point " + f(Mesh.LowestY) + ")"
                    + ((Mesh.DepthMask != null) ? "; the mesh's own depth mask is used" : "");
            Note(name + " is in " + (ContainerMesh ?? "?") + ": " + what + ". Top of what it's in: " + f(ContainerTop)
                + " (tiles above its base).");
        }

        public void DrawLMap(GraphicsDevice device, sbyte level, float yOff)
        {
            if (DrawGroup == null) return;
            if (_Dirty.IsSet(ComponentRenderMode._3D))
            {
                Mesh = Content.Content.Get().RCMeshes.Get(DrawGroup, Source);
                _Dirty &= ~ComponentRenderMode._3D;
            }

            if (Mesh.MaskType == DGRP3DMaskType.Portal) return;
            //immedately draw the mesh.
            var effect = WorldContent.RCObject;

            var mat = World;
            mat.M42 = ((Level - level) - 1) * 2.95f + yOff; //set y translation to 0
            effect.World = mat;

            int i = 0;
            foreach (var spr in Mesh.Geoms)
            {
                if (i == 0 || (((i - 1) > 63) ? ((DynamicSpriteFlags2 & ((ulong)0x1 << ((i - 1) - 64))) > 0) :
                    ((DynamicSpriteFlags & ((ulong)0x1 << (i - 1))) > 0)))
                {
                    foreach (var geom in spr.Values)
                    {
                        if (geom.PrimCount == 0) continue;
                        foreach (var pass in effect.CurrentTechnique.Passes)
                        {
                            pass.Apply();
                            if (!geom.Rendered) continue;
                            device.Indices = geom.Indices;
                            device.SetVertexBuffer(geom.Verts);

                            device.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, geom.PrimCount);
                        }
                    }
                }
                i++;
            }
        }

        public virtual void Preload(WorldState world, ComponentRenderMode mode)
        {
            if (mode.IsSet(ComponentRenderMode._2D) && world.CameraMode != CameraRenderMode._3D)
                ValidateSprite(world);

            if (mode.IsSet(ComponentRenderMode._3D) || world.CameraMode != CameraRenderMode._2D || WorldConfig.Current.UltraLighting)
            {
                if (_Dirty.IsSet(ComponentRenderMode._3D))
                {
                    Mesh = Content.Content.Get().RCMeshes.Get(DrawGroup, Source);
                    _Dirty &= ~ComponentRenderMode._3D;
                }
            }
        }

        public void Dispose()
        {
            foreach (var item in Items)
            {
                item.Sprite.Dispose();
            }
        }

        #region 3D GPU States

        //depth clear mask
        //how it works:
        //pass 1: draw mask to stencil 1 with normal depth rules. no depth write.
        //pass 2: draw mask where stencil 1 exists with max far depth. depth write override, stencil clear.
        //pass 3: draw object normally

        public static DepthStencilState DepthClear1 = new DepthStencilState()
        {
            StencilEnable = true,
            StencilFunction = CompareFunction.Always,

            TwoSidedStencilMode = true,

            CounterClockwiseStencilFail = StencilOperation.Keep,
            CounterClockwiseStencilPass = StencilOperation.Replace,
            CounterClockwiseStencilDepthBufferFail = StencilOperation.Keep,

            StencilDepthBufferFail = StencilOperation.Keep,
            StencilPass = StencilOperation.Zero,
            StencilFail = StencilOperation.Keep,

            ReferenceStencil = 1,
            DepthBufferWriteEnable = true
        };

        /// <summary>
        /// A basin's column: each face behind what's drawn flips the stencil's lowest bit, so it's 1 where what's drawn is
        /// inside the column. No depth or colour written; either winding.
        /// </summary>
        public static DepthStencilState BasinMark = new DepthStencilState()
        {
            StencilEnable = true,
            StencilFunction = CompareFunction.Always,
            StencilPass = StencilOperation.Keep,
            StencilFail = StencilOperation.Keep,
            StencilDepthBufferFail = StencilOperation.Invert,
            StencilWriteMask = 1,
            ReferenceStencil = 1,
            DepthBufferEnable = true,
            DepthBufferWriteEnable = false,
            DepthBufferFunction = CompareFunction.LessEqual
        };

        public static DepthStencilState DepthClear2 = new DepthStencilState()
        {
            StencilEnable = true,
            StencilFunction = CompareFunction.Equal,
            StencilFail = StencilOperation.Keep,
            StencilPass = StencilOperation.Zero,
            StencilDepthBufferFail = StencilOperation.Zero,
            ReferenceStencil = 1,
            DepthBufferWriteEnable = true,
            DepthBufferFunction = CompareFunction.Always
        };

        public static DepthStencilState DepthClear2Strict = new DepthStencilState()
        {
            StencilEnable = true,
            StencilFunction = CompareFunction.Equal,
            StencilFail = StencilOperation.Keep,
            StencilPass = StencilOperation.Keep,
            StencilDepthBufferFail = StencilOperation.Keep,
            ReferenceStencil = 1,
            DepthBufferWriteEnable = true,
            DepthBufferFunction = CompareFunction.Always
        };


        public static DepthStencilState Portal = new DepthStencilState()
        {
            StencilEnable = true,
            StencilFunction = CompareFunction.Equal,
            StencilFail = StencilOperation.Keep,
            StencilPass = StencilOperation.Keep,
            StencilDepthBufferFail = StencilOperation.Keep,
            ReferenceStencil = 1,
            DepthBufferWriteEnable = true,
        };

        public static DepthStencilState StencilClearOnly = new DepthStencilState()
        {
            StencilEnable = true,
            StencilFunction = CompareFunction.Equal,
            StencilFail = StencilOperation.Keep,
            StencilPass = StencilOperation.Zero,
            StencilDepthBufferFail = StencilOperation.Keep,
            ReferenceStencil = 1,
            DepthBufferWriteEnable = false,
            DepthBufferFunction = CompareFunction.Always
        };

        public static BlendState NoColor = new BlendState()
        {
            ColorWriteChannels = ColorWriteChannels.None
        };

        #endregion
    }
}
