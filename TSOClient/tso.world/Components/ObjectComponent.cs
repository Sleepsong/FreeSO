using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework.Graphics;
using FSO.Content;
using FSO.LotView.Utils;
using FSO.Files.Formats.IFF.Chunks;
using FSO.LotView.Model;
using Microsoft.Xna.Framework;
using FSO.Common.Utils;
using FSO.LotView.Components.Model;

namespace FSO.LotView.Components
{
    public class ObjectComponent : EntityComponent, IDisposable
    {
        private static Vector2[] PosCenterOffsets = new Vector2[]{
            new Vector2(2+16, 79+8),
            new Vector2(3+32, 158+16),
            new Vector2(5+64, 316+32)
        };

        public GameObject Obj;
        // the modes that this object renders with. most objects are 2d only (with 3d drop ins) but objects can also force 3d components in 2d
        public ComponentRenderMode Mode = ComponentRenderMode._2D;

        protected DGRP DrawGroup;
        protected DGRPRenderer dgrp;
        public WorldObjectRenderInfo RenderInfo;
        public int DynamicCounter; //REMOVE
        public List<SLOTItem> ContainerSlots;
        public List<ParticleComponent> Particles = new List<ParticleComponent>();
        public _2DStandaloneSprite HeadlineSprite;
        public MultitileObjectGroup MultitileGroup;
        public bool Dead;
        protected float ZOrder;

        public bool HideForCutaway;
        /// <summary>
        /// Hidden where the wall it's in is cut, as walls with Hide For Cutaway are, but only in a thick (cuttable) wall:
        /// TS1's doors. A gate in a fence stays, as fences aren't cut.
        /// </summary>
        public bool HideForCutawayInWall;
        public WallSegments AdjacentWall;

        private WallSegments _WallAttachment;
        /// <summary>
        /// The walls of its tile this object needs (paintings, windows, doors...): it stands as high as they do, so on
        /// a foundation's edge it goes up with the wall even from the tile outside.
        /// </summary>
        public WallSegments WallAttachment
        {
            get => _WallAttachment;
            set
            {
                if (_WallAttachment == value) return;
                _WallAttachment = value;
                if (blueprint?.Foundations?.Any == true) RefreshAltitude();
            }
        }
        public override WallSegments AttachedWalls => _WallAttachment;

        //for ultra lighting in 2d. objects can have a shadow component which handles drawing in 3d.
        private ObjectComponent ShadowComponent;
        public static Func<GameObject, ObjectComponent> MakeShadowComponent;

        protected bool _BoundsDirty = true;
        private Vector3[] BoundsCalcScratch = new Vector3[8];

        public new bool Visible {
            get { return _Visible; }
            set {
                if (_Visible != value)
                {
                    _Visible = value;
                    if (blueprint != null) blueprint.Changes.RegisterObjectChange(this);
                }
            }
        }

        #region 3D Members

        private BoundingBox _Bounds;
        private Matrix _World3D;
        private bool _World3DDirty;

        protected override bool _WorldDirty {
            get => base._WorldDirty;
            set {
                base._WorldDirty = value;
                if (value) _World3DDirty = true;
            }
        }

        public Matrix World3D
        {
            get
            {
                if (_World3DDirty || (Container != null))
                {
                    _BoundsDirty = true;
                    var worldPosition = WorldSpace.GetWorldFromTile(Position);
                    _World3D = Matrix.CreateTranslation(new Vector3(1.5f, 0.1f, 1.5f)) * Matrix.CreateTranslation(worldPosition);
                    if (GroundAlign != null) _World = GroundAlign.Value * _World;
                    _World3D = Matrix.CreateScale(3f) * Matrix.CreateRotationY(-RadianDirection) * _World3D;
                    _World3DDirty = false;
                }
                return _World3D;
            }
        }

        private BoundingBox GetTransformedBounds(BoundingBox bounds)
        {
            Matrix mat = World3D;
            Vector3[] points = BoundsCalcScratch;

            bounds.GetCorners(points);

            Vector3 max = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
            Vector3 min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);

            for (int i = 0; i < 8; i++)
            {
                Vector3 point = Vector3.Transform(points[i], mat);
                min = Vector3.Min(min, point);
                max = Vector3.Max(max, point);
            }

            return new BoundingBox(min, max);
        }

        public BoundingBox? GetRawBounds()
        {
            var bounds = dgrp.GetBounds();
            if (bounds != null)
            {
                Debug.BoundingBoxCache.AddToCacheIfMissing(Obj.OBJ.GUID, bounds.Value);
            }
            return bounds;
        }

        public BoundingBox GetBounds()
        {
            if (_BoundsDirty || _World3DDirty || Container != null)
            {
                var bounds = dgrp.GetBounds();

                if (bounds == null)
                {
                    if (!dgrp.CanHaveBounds)
                    {
                        // No need to keep checking this.
                        _BoundsDirty = false;
                    }

                    return new BoundingBox(); //don't cache, but also check again next time.
                }
                else
                {
                    _Bounds = GetTransformedBounds(bounds.Value);
                    _BoundsDirty = false;
                }
            }
            return _Bounds;
        }

        public float? IntersectsBounds(Ray ray)
        {
            return GetBounds().Intersects(ray);
        }

        #endregion

        public Rectangle Bounding { get { return dgrp.Bounding ?? new Rectangle(); } }

        public override sbyte Level
        {
            get { return _Level; }
            set { _Level = value; dgrp.Level = value; }
        }

        public override ushort Room
        {
            get
            {
                return dgrp.Room;
            }
            set
            {
                if (blueprint == null) return;
                dgrp.Room = (value> blueprint.Rooms.Count|| value == 0)?value:blueprint.Rooms[value].Base;
                dgrp.Level = Level;
            }
        }

        public override short ObjectID {
            get => base.ObjectID;
            set {
                dgrp.ObjectID = value;
                base.ObjectID = value;
            }
        }

        public override Vector3 GetSLOTPosition(int slot, bool avatar)
        {
            var item = (ContainerSlots != null && ContainerSlots.Count > slot) ? ContainerSlots[slot] : null;
            if (item != null)
            {
                var off = item.Offset;
                var centerRelative = new Vector3(off.X * (1 / 16.0f), off.Y * (1 / 16.0f), ((item.Height != 5 && item.Height != 0) ? SLOT.HeightOffsets[item.Height - 1] : off.Z) * (1 / 5.0f));
                centerRelative = Vector3.Transform(centerRelative, Matrix.CreateRotationZ(RadianDirection));
                if (avatar) centerRelative.Z = 0;
                return this.Position + centerRelative;
            } else return this.Position;
        }

        public ObjectComponent(GameObject obj) {
            this.Obj = obj;
            RenderInfo = new WorldObjectRenderInfo();
            if (obj.OBJ.BaseGraphicID > 0)
            {
                var gid = obj.OBJ.BaseGraphicID;
                this.DrawGroup = obj.Resource.Get<DGRP>(gid);
            }
            dgrp = new DGRPRenderer(this.DrawGroup, obj.OBJ);
            dgrp.DynamicSpriteBaseID = obj.OBJ.DynamicSpriteBaseId;
            dgrp.NumDynamicSprites = obj.OBJ.NumDynamicSprites;
            InterpolationOwner = this;
        }

        public DGRP DGRP
        {
            get
            {
                return DrawGroup;
            }
            set
            {
                if (DrawGroup != value)
                {
                    _BoundsDirty = true;
                    DrawGroup = value;
                }

                if (blueprint != null && dgrp.DGRP != value)
                {
                    blueprint.Changes.RegisterObjectChange(this);
                }
                dgrp.DGRP = value;
            }
        }

        private bool _CutawayHidden;

        public bool CutawayHidden
        {
            get
            {
                return _CutawayHidden;
            }
            set
            {
                if (blueprint != null && _CutawayHidden != value && RenderInfo.Layer == WorldObjectRenderLayer.STATIC)
                {
                    blueprint.Changes.RegisterObjectChange(this);
                }
                _CutawayHidden = value;
            }

        }

        private bool _ForceDynamic;

        public bool ForceDynamic
        {
            get
            {
                var bottomMost = GetBottomContainer();
                return _ForceDynamic || Headline != null || Mode.IsSet(ComponentRenderMode._3D) || bottomMost is AvatarComponent;
            }
            set
            {
                if (blueprint != null && _ForceDynamic != value)
                {
                    if (value) blueprint.Changes.RegisterObjectChange(this);
                    else blueprint.Changes.RegisterObjectChange(this);
                }
                _ForceDynamic = value;
            }
        }

        private ulong _DynamicSpriteFlags = 0x00000000;
        private ulong _DynamicSpriteFlags2 = 0x00000000;
        public ulong DynamicSpriteFlags
        {
            get {
                return _DynamicSpriteFlags;
            } set {

                if (dgrp != null && _DynamicSpriteFlags != value) {
                    dgrp.DynamicSpriteFlags = value;
                    if (blueprint != null) blueprint.Changes.RegisterObjectChange(this);
                }
                _DynamicSpriteFlags = value;
            }
        }

        public ulong DynamicSpriteFlags2
        {
            get
            {
                return _DynamicSpriteFlags2;
            }
            set
            {

                if (dgrp != null && _DynamicSpriteFlags2 != value)
                {
                    dgrp.DynamicSpriteFlags2 = value;
                    if (blueprint != null) blueprint.Changes.RegisterObjectChange(this);
                }
                _DynamicSpriteFlags2 = value;
            }
        }

        public float RadianDirection
        {
            get
            {
                //eight ways, 45 degrees apart from north (diagonals come from the 45 degree rotation cheat, in 3D)
                return FSO.Common.Utils.DirectionUtils.Log2Int((uint)_Direction) * (float)(Math.PI / 4);
            }
        }

        private Direction _Direction;
        public override Direction Direction
        {
            get
            {
                return _Direction;
            }
            set
            {
                _Direction = value;
                _WorldDirty = true;
                if (dgrp != null) {
                    dgrp.Direction = value;
                    dgrp.InvalidateRotation();
                }
            }
        }

        public override void OnRotationChanged(WorldState world)
        {
            base.OnRotationChanged(world);
            if (dgrp != null)
            {
                dgrp.InvalidateRotation();
            }
        }

        public override void OnZoomChanged(WorldState world)
        {
            base.OnZoomChanged(world);
            if (dgrp != null) {
                dgrp.InvalidateZoom();
            }
        }

        public override void OnPositionChanged()
        {
            dgrp.Position = Position;
        }

        public void UpdateDrawOrder(WorldState world)
        {
            if (world.CameraMode > CameraRenderMode._2DRotate)
            {
                if (!Visible) DrawOrder = 0;
                var w = World3D;
                var ctr = w.Translation;
                var forward = world.ViewProjection.Forward;
                forward.Z *= 1;
                if (forward.Z < 0) forward.Y = -forward.Y;
                DrawOrder = Vector3.Dot(ctr, forward);
                //an object in another (a sink in a counter) is drawn after it, so it can be cut into it
                if (Container is ObjectComponent holder && holder != this)
                {
                    holder.UpdateDrawOrder(world);
                    DrawOrder = Math.Max(DrawOrder, holder.DrawOrder + 0.001f);
                }
            }
            else
            {
                DrawOrder = world.WorldSpace.GetDepthFromTile(Position);
            }
        }

        public bool DoDraw(WorldState world)
        {
            if (!Visible || (Room == 0 && !world.DrawOOB)) return false;
            bool result = false;
            if (world.CameraMode == CameraRenderMode._2D && Mode.IsSet(ComponentRenderMode._2D))
            {
                ValidateSprite(world);
                if (dgrp.Bounding != null) result |= world.WorldRectangle.Intersects(dgrp.Bounding.Value);
                if (Mode.IsSet(ComponentRenderMode._3D))
                {
                    result |= world.Frustum.Intersects(GetBounds());
                }
            } else 
            {
                result |= world.Frustum.Intersects(GetBounds());
            }
            return result;
        }

        public void ValidateSprite(WorldState world)
        {
            dgrp.ValidateSprite(world);
        }

        public override Vector2 GetScreenPos(WorldState world)
        {
            var projected = Vector4.Transform(new Vector4(1.5f, 0, 1.5f, 1f), this.World * world.View * world.Projection);
            if (world.CameraMode < CameraRenderMode._3D) projected.Z = 1;
            var res1 = new Vector2(projected.X / projected.Z, -projected.Y / projected.Z);
            var size = PPXDepthEngine.GetWidthHeight();
            return new Vector2((size.X / PPXDepthEngine.SSAA) * 0.5f * (res1.X + 1f), (size.Y / PPXDepthEngine.SSAA) * 0.5f * (res1.Y + 1f));
        }

        public static Dictionary<WallSegments, Point> CutawayTests = new Dictionary<WallSegments, Point>
        {
            { WallSegments.BottomLeft, new Point(0,1) },
            { WallSegments.TopLeft, new Point(-1,0) },
            { WallSegments.TopRight, new Point(0,-1)},
            { WallSegments.BottomRight, new Point(1,0) }
        };

        public EntityComponent GetBottomContainer()
        {
            EntityComponent current = this;
            while (current.Container != null)
            {
                current = current.Container;
            }
            return current;
        }

        public override void Update(GraphicsDevice device, WorldState world)
        {
            if (Headline != null)
            {
                //todo: this and hideForCutaway should be part of the ForceDynamic bool (accessed by the static layer manager)
                if (blueprint != null && RenderInfo.Layer == WorldObjectRenderLayer.STATIC) blueprint.Changes.RegisterObjectChange(this);
            }

            var idleFrames = InterpolationOwner.IdleFrames;
            if (idleFrames > 0)
            {
                if (_IdleFramesPct > -3)
                {
                    _IdleFramesPct -= world.FramePerDraw / idleFrames;
                    _WorldDirty = true;
                    dgrp.Position = Position;
                }
            }
            else _IdleFramesPct = 0;

            if ((HideForCutaway || HideForCutawayInWall) && Level >= 0) //(the basement's doors too)
            {
                if (!(world.BuildMode > 1) && world.DynamicCutaway && Level == world.Level)
                {
                    if (blueprint != null && RenderInfo.Layer == WorldObjectRenderLayer.STATIC) blueprint.Changes.RegisterObjectChange(this);
                }

                CutawayHidden = CutsAway(world);
            }
        }

        /// <summary>Whether this object hides with the walls it's in (a door), when they're cut away.</summary>
        public bool HidesWithWalls => HideForCutaway || HideForCutawayInWall;

        /// <summary>
        /// Whether this object (a door) is hidden for cutaway, with the blueprint's cut and the level as they are now. Also
        /// used by a second view of the lot with its own cut (World.DrawPicture), which sets SilentCutawayHidden with it.
        /// </summary>
        public bool CutsAway(WorldState world)
        {
                if (Level != world.Level || world.BuildMode > 1) return false;
                else
                {
                    var tilePos = new Point((int)Math.Round(Position.X), (int)Math.Round(Position.Y));

                    if (tilePos.X >= 0 && tilePos.X < blueprint.Width && tilePos.Y >= 0 && tilePos.Y < blueprint.Height)
                    {
                        var wall = blueprint.Walls[Level - 1][tilePos.Y * blueprint.Width + tilePos.X];
                        var cutTest = new Point();
                        var side = AdjacentWall & wall.Segments;
                        if (!CutawayTests.TryGetValue(side, out cutTest))
                        {
                            side = wall.OccupiedWalls & wall.Segments;
                            CutawayTests.TryGetValue(side, out cutTest);
                        }
                        var positions = new Point[] { tilePos, tilePos + cutTest };

                        var canContinue = HideForCutaway || InThickWall(wall, side, tilePos);

                        foreach (var pos in positions)
                        {
                            canContinue = canContinue && (pos.X >= 0 && pos.X < blueprint.Width && pos.Y >= 0 && pos.Y < blueprint.Height
                                && blueprint.Cutaway[pos.Y * blueprint.Width + pos.X]);
                            if (!canContinue) break;
                        }
                        return canContinue;
                    }
                }
                return _CutawayHidden;
        }

        /// <summary>Sets whether it's hidden for cutaway without marking it changed (for a second view of the lot).</summary>
        public bool SilentCutawayHidden
        {
            get { return _CutawayHidden; }
            set { _CutawayHidden = value; }
        }

        /// <summary>
        /// Whether the wall on this side of the tile is thick, so cut with the walls (fences aren't). With no single side
        /// known, whether any wall at the tile is.
        /// </summary>
        private bool InThickWall(WallTile wall, WallSegments side, Point tile)
        {
            var level = (sbyte)Level;
            bool bottomLeft() => tile.Y + 1 < blueprint.Height && blueprint.GetWall((short)tile.X, (short)(tile.Y + 1), level).TopRightThick;
            bool bottomRight() => tile.X + 1 < blueprint.Width && blueprint.GetWall((short)(tile.X + 1), (short)tile.Y, level).TopLeftThick;
            switch (side)
            {
                case WallSegments.TopLeft: return wall.TopLeftThick;
                case WallSegments.TopRight: return wall.TopRightThick;
                case WallSegments.BottomLeft: return bottomLeft();
                case WallSegments.BottomRight: return bottomRight();
                default:
                    return wall.TopLeftThick || wall.TopRightThick
                        || ((wall.Segments & WallSegments.BottomLeft) > 0 && bottomLeft())
                        || ((wall.Segments & WallSegments.BottomRight) > 0 && bottomRight());
            }
        }

        public override float GetHeadlineScale()
        {
            return 0.5f;
        }

        public override Vector3 GetHeadlinePos()
        {
            return new Vector3(0, 0, -0.33f);
        }

        public override Vector3 GetLookTarget()
        {
            return new Vector3(Position.X, Position.Z + GetParticleBounds().Max.Y - 0.5f, Position.Y);
        }

        public override void Draw(GraphicsDevice device, WorldState world)
        {
            if (!Visible || (!world.DrawOOB && (Position.X < -2043 && Position.Y < -2043))) return;
            //#endif
            if (CutawayHidden) return;
            var pos = Position;

            if (world.CameraMode > CameraRenderMode._2D || Mode.IsSet(ComponentRenderMode._3D)) //)
            {
                var mworld = World3D;
                dgrp.World = mworld;
                //in a counter, not in a Sim's hand: its basin, if it has one, is cut into the counter's top
                var holder = Container as ObjectComponent;
                dgrp.InObject = holder != null;
                if (holder != null)
                {
                    dgrp.ContainerTop = holder.dgrp.MeshTop + (holder.Position.Z - Position.Z);
                    dgrp.ContainerMesh = holder.DrawGroup?.ChunkParent?.Filename + " " + holder.DrawGroup?.ChunkID;
                }
                if (this.DrawGroup != null) dgrp.Draw3D(world);
            }
            if (world.CameraMode == CameraRenderMode._2D && Mode.IsSet(ComponentRenderMode._2D))
            {
                if (this.DrawGroup != null)
                {
                    if (Container != null) dgrp.Position = pos;
                    dgrp.DrawImmediate(world);
                }
            }

            if (Headline != null && !Headline.IsDisposed && world.CameraMode == CameraRenderMode._2D)
            {
                if (HeadlineSprite == null) HeadlineSprite = new _2DStandaloneSprite();
                var headOff = new Vector3(0, 0, 0.66f);
                var headPx = world.WorldSpace.GetScreenFromTile(headOff);

                var item = HeadlineSprite;
                item.Pixel = Headline;
                item.Depth = TextureGenerator.GetWallZBuffer(device)[30];

                item.SrcRect = new Rectangle(0, 0, Headline.Width, Headline.Height);
                item.WorldPosition = headOff;
                var off = PosCenterOffsets[(int)world.Zoom - 1];
                item.DestRect = new Rectangle(
                    ((int)headPx.X - Headline.Width / 2) + (int)off.X,
                    ((int)headPx.Y - Headline.Height / 2) + (int)off.Y, Headline.Width, Headline.Height);

                item.AbsoluteDestRect = item.DestRect;
                item.AbsoluteDestRect.Offset(world.WorldSpace.GetScreenFromTile(pos));
                item.AbsoluteWorldPosition = item.WorldPosition + WorldSpace.GetWorldFromTile(pos);
                HeadlineSprite.PrepareVertices(device);
                world._2D.DrawImmediate(item);
            }

            for (int i = 0; i < Particles.Count; i++)
            {
                var part = Particles[i];
                if (part.BoundsDirty && part.AutoBounds && dgrp != null)
                {
                    //this particle needs updated bounds.
                    BoundingBox bounds;
                    if (ShadowComponent != null)
                        bounds = ShadowComponent.GetParticleBounds();
                    else
                        bounds = GetParticleBounds();
                    part.Volume = bounds;
                    part.BoundsDirty = false;
                    part.Dispose();
                }
                part.Level = Level;
                part.OwnerWorld = Matrix.CreateScale(3) * World * Matrix.CreateTranslation(1.5f, 0, 1.5f) * Matrix.CreateScale(2);
                if (part.Dead) Particles.RemoveAt(i--);
            }
        }

        public void DrawLMap(GraphicsDevice device, sbyte level)
        {
            //#if !DEBUG 
            if (!Visible || (Position.X < -2043 && Position.Y < -2043) || Level < 1) return;
            //#endif
            dgrp.World = World3D;
            if (this.DrawGroup != null)
            {
                float yOff = 0;
                if (this.Container != null)
                {
                    yOff = this.Position.Z - this.Container.Position.Z;
                }
                dgrp.DrawLMap(device, level, yOff);
            }
        }

        public virtual BoundingBox GetParticleBounds()
        {
            //make an estimation based off of the sprite height
            var bounds = dgrp.GetBounds();
            if (bounds != null) return bounds.Value;

            if (Debug.BoundingBoxCache.TryGetValue(Obj.OBJ.GUID, out var bounds2)) return bounds2;

            //if (world.CameraMode == CameraRenderMode._2D || Mode.HasFlag(ComponentRenderMode._3D)) return  ?? new BoundingBox();
            if (DGRP == null) return new BoundingBox(new Vector3(-0.4f, 0.1f, -0.4f), new Vector3(0.4f, 0.9f, 0.4f));
            else
            {
                var image = DGRP.GetImage(1, 3, 1);
                var maxY = int.MinValue;
                var minY = int.MaxValue;
                var objOffset = 0f;

                if (image.Sprites.Length == 0) return new BoundingBox(new Vector3(-0.4f, 0.1f, -0.4f), new Vector3(0.4f, 0.9f, 0.4f));

                foreach (var spr in image.Sprites)
                {
                    var dim = spr.GetDimensions();

                    var top = spr.SpriteOffset.Y;
                    var btm = top + dim.Y;

                    if (top < minY) minY = (int)top;
                    if (btm > maxY) maxY = (int)btm;
                    objOffset += spr.ObjectOffset.Z * 1f / 5f;
                }
                objOffset /= image.Sprites.Length;
                //128 is a height of zero
                var topY = Math.Max(0, Math.Min(2.95f, (100 - minY) / 95f));
                var btmY = Math.Max(0, Math.Min(2.95f, (100 - maxY) / 95f));
                return new BoundingBox(new Vector3(-0.4f, btmY, -0.4f), new Vector3(0.4f, topY, 0.4f));
            }
        }

        public override void Preload(GraphicsDevice device, WorldState world)
        {
            if (this.DrawGroup != null) dgrp.Preload(world, Mode);
        }

        /*
        public virtual void DrawLMap(GraphicsDevice device, sbyte level)
        {
            if (ShadowComponent == null) ShadowComponent = new RC.ObjectComponentRC(Obj);
            if (!Visible) return;
            if (Container != null && Container is AvatarComponent) return;
            if (ShadowComponent.UnmoddedPosition != UnmoddedPosition) ShadowComponent.Position = UnmoddedPosition;
            if (ShadowComponent.DGRP != DGRP) ShadowComponent.DGRP = DGRP;
            if (ShadowComponent.Direction != Direction) ShadowComponent.Direction = Direction;
            if (ShadowComponent.Room != Room) ShadowComponent.Room = Room;
            if (ShadowComponent.DynamicSpriteFlags != DynamicSpriteFlags) ShadowComponent.DynamicSpriteFlags = DynamicSpriteFlags;
            if (ShadowComponent.DynamicSpriteFlags2 != DynamicSpriteFlags2) ShadowComponent.DynamicSpriteFlags2 = DynamicSpriteFlags2;
            if (ShadowComponent.Level != Level) ShadowComponent.Level = Level;
            ShadowComponent.DrawLMap(device, level);
        }
        */

        public void Dispose()
        {
            dgrp.Dispose();
            HeadlineSprite?.Dispose();
        }
    }
}
