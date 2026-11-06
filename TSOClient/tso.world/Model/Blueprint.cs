using FSO.Common;
using FSO.LotView.Components;
using FSO.LotView.Effects;
using FSO.LotView.LMap;
using FSO.LotView.RC;
using FSO.LotView.Utils;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;

namespace FSO.LotView.Model
{
    /// <summary>
    /// Holds all the objects that exist in the world for rendering
    /// and SimAntics.
    /// </summary>
    public class Blueprint
    {
        public List<BlueprintDamage> Damage = new List<BlueprintDamage>();

        public int Width;
        public int Height;
        public sbyte Stories = 5;

        public BlueprintChanges Changes; // changes for the renderer to track, to invalidate components, lighting and scroll buffers.

        /// <summary>
        /// Only read these arrays, do not modify them!
        /// </summary>
        public WallTile[][] Walls;
        public List<int>[] WallsAt;
        public WallComponent WallComp;

        public FloorTile[][] Floors;
        public FloorComponent FloorComp;
        public _3DFloorGeometry FloorGeom;

        public WallComponentRC WCRC;
        public List<_2DDrawBuffer> WallCache2D = new List<_2DDrawBuffer>(); //temporary: to replace with a smarter wall component with caching

        public RoofComponent RoofComp;

        public bool[][] Supported; //directly the VM's copy at all times. DO NOT MODIFY.

        public List<ObjectComponent> Objects = new List<ObjectComponent>();
        public List<AvatarComponent> Avatars = new List<AvatarComponent>();
        public List<SubWorldComponent> SubWorlds = new List<SubWorldComponent>();
        public TerrainComponent Terrain;

        public HashSet<EntityComponent> HeadlineObjects = new HashSet<EntityComponent>();

        public List<ParticleComponent> Particles = new List<ParticleComponent>();
        public List<ParticleComponent> ObjectParticles = new List<ParticleComponent>();

        public List<DebugLinesComponent> DebugLines = new List<DebugLinesComponent>();

        /// <summary>
        /// Walls Cutaway sections. Remember to manage these correctly - i.e remove when you're finished with them!
        /// </summary>
        /// 
        public bool[] Cutaway;

        private Color _OutsideColor = Color.White;
        public float MinOutMul = (175 / 400f);
        public Vector4 ColorMul = new Vector4(0.8f, 0.8f, 1f, 0.87f);
        public Color MinOut;
        public Color OutsideColor
        {
            get
            {
                return _OutsideColor;
            }
            set
            {
                _OutsideColor = value;
                var aFactor = 1f - ((value.R + value.G + value.B) - 115) / (512f*2);
                MinOut = new Color(value.ToVector4() * MinOutMul * aFactor * ColorMul);
                if (MinOut.R == 0) MinOut.R = 1;
                if (MinOut.G == 0) MinOut.G = 1;
                if (MinOut.B == 0) MinOut.B = 1;
                if (MinOut.A == 0) MinOut.A = 1;
                //MinOut = value * (float)(150 / Math.Sqrt(value.R * value.R + value.G * value.G + value.B * value.B));
            }
        }

        private Color PowColor(Color col, float pow)
        {
            var vec = col.ToVector4();
            vec.X = (float)Math.Pow(vec.X, pow);
            vec.Y = (float)Math.Pow(vec.Y, pow);
            vec.Z = (float)Math.Pow(vec.Z, pow);
            vec.W = (float)Math.Pow(vec.W, pow);

            return new Color(vec);
        }

        public double OutsideTime;
        public float OutsideSkyP;
        public Color OutsideWeatherTint;
        public float OutsideWeatherTintP;

        public RoomLighting[] Light = new RoomLighting[0];
        public uint[][] RoomMap;
        public byte[] Indoors;
        public List<Room> Rooms = new List<Room>();

        public Color[] RoomColors;
        public Rectangle BuildableArea;
        public bool[] FineArea;
        public Rectangle TargetBuildableArea;
        public LightData OutdoorsLight;
        public WeatherController Weather;

        public short[] Altitude;
        public short[] AltitudeCenters;
        public float TerrainFactor = 3 / 160f;
        public int BaseAlt;

        // AF2022, obviously getting removed in a day
        public SM64Component SM64;

        public Blueprint(int width, int height){
            this.Width = width;
            this.Height = height;

            var numTiles = width * height;
            this.WallComp = new WallComponent();
            WallComp.blueprint = this;
            this.RoofComp = new RoofComponent(this);
            this.FloorGeom = new _3DFloorGeometry(this);
            if (true || WorldConfig.Current.Shadow3D || FSOEnvironment.Enable3D)
            {
                this.WCRC = new WallComponentRC();
                WCRC.blueprint = this;
            }
        
            RoomColors = new Color[65536];
            this.WallsAt = new List<int>[Stories];
            this.Walls = new WallTile[Stories][];
            this.RoomMap = new uint[Stories][];

            this.Floors = new FloorTile[Stories][];

            for (int i=0; i<Stories; i++)
            {
                this.WallsAt[i] = new List<int>();
                this.Walls[i] = new WallTile[numTiles];

                this.Floors[i] = new FloorTile[numTiles];
            }
            this.Cutaway = new bool[numTiles];
            this.Weather = new WeatherController(this);

            this.Changes = new BlueprintChanges(this);

            this.SM64 = new SM64Component(this);
        }

        /// <summary>Foundations on the ground floor (raised tiles and their steps), shared with the simulation. Null for none.</summary>
        public FoundationMap Foundations;

        private bool HasFoundations => Foundations != null && Foundations.Any;

        /// <summary>The height of a tile's centre: the ground, and the tile's foundation if it has one.</summary>
        public float GetAltitude(int x, int y)
        {
            if (x <= 0 || y <= 0) return 0f;
            var raise = HasFoundations ? Foundations.RaiseOf(x % Width, y % Height) : 0;
            return (AltitudeCenters[((y % Height) * Width + (x % Width))] - BaseAlt + raise) * TerrainFactor;
        }

        /// <summary>
        /// The height a tile's walls stand at: the ground under the tile, raised to the higher of the foundations either
        /// side of each wall (a wall on a foundation's edge stands on the foundation, whichever tile it belongs to).
        /// </summary>
        public float GetWallAltitude(int x, int y, WallSegments segments)
        {
            if (x <= 0 || y <= 0) return 0f;
            var raise = HasFoundations ? Foundations.WallRaise(x % Width, y % Height, segments) : 0;
            return (AltitudeCenters[((y % Height) * Width + (x % Width))] - BaseAlt + raise) * TerrainFactor;
        }

        /// <summary>The height at a tile corner (x, y): the ground, raised to the highest foundation touching the corner.</summary>
        public float GetCornerAltitude(int x, int y)
        {
            if (x <= 0 || y <= 0) return 0f;
            var raise = HasFoundations ? Foundations.RaiseNear(x, y, 0.25f) : 0;
            return (AltitudeCenters[((y % Height) * Width + (x % Width))] - BaseAlt + raise) * TerrainFactor;
        }

        /// <summary>
        /// The foundation side a camera ray meets first (world units, as <see cref="WorldState.CameraRayAtScreenPos"/> gives
        /// for the ground floor): its tile, and which side (see <see cref="FoundationMap.Directions"/>). Only sides that
        /// show, facing the camera; a foundation's top in front of one hides it. For painting coverings on.
        /// </summary>
        public bool FoundationSideAt(Ray ray, out Point tile, out int dir)
        {
            tile = Point.Zero;
            dir = -1;
            var map = Foundations;
            if (map == null || !map.Any || Altitude == null) return false;
            var p = ray.Position / 3; //tile units, and the same distance along the ray
            var v = ray.Direction / 3;
            Func<int, int, float> ground = (gx, gy) =>
            {
                gx = Math.Max(0, Math.Min(Width - 1, gx));
                gy = Math.Max(0, Math.Min(Height - 1, gy));
                return (Altitude[gy * Width + gx] - BaseAlt) * TerrainFactor;
            };
            float best = float.MaxValue;
            bool found = false;
            for (int y = 0; y < map.Height; y++)
            {
                for (int x = 0; x < map.Width; x++)
                {
                    var raise = map.RaiseOf(x, y);
                    if (raise <= 0) continue;
                    var r = raise * TerrainFactor;

                    // its top (level, at the corners' average)
                    if (v.Y != 0)
                    {
                        var h = (ground(x, y) + ground(x + 1, y) + ground(x, y + 1) + ground(x + 1, y + 1)) / 4 + r;
                        var t = (h - p.Y) / v.Y;
                        var q = p + v * t;
                        if (t > 0 && t < best && q.X >= x && q.X <= x + 1 && q.Z >= y && q.Z <= y + 1)
                        {
                            best = t;
                            found = false;
                        }
                    }

                    for (int d = 0; d < 4; d++)
                    {
                        if (!map.SideShows(x, y, d)) continue;
                        var n = FoundationMap.Directions[d];
                        if (n.X * v.X + n.Y * v.Z >= 0) continue; //facing away
                        Components.Geometry.Modelled3DFoundation.EdgeEnds(x, y, d, out var a, out var b);
                        float t;
                        if (n.X != 0) t = (a.X - p.X) / v.X;
                        else t = (a.Y - p.Z) / v.Z;
                        if (t <= 0 || t >= best) continue;
                        var q = p + v * t;
                        // how far along the edge, from a to b
                        float along = (n.X != 0) ? (q.Z - a.Y) / (b.Y - a.Y) : (q.X - a.X) / (b.X - a.X);
                        if (along < 0 || along > 1) continue;
                        var g = ground(a.X, a.Y) * (1 - along) + ground(b.X, b.Y) * along;
                        var bottom = g + (raise - map.SideDrop(x, y, d)) * TerrainFactor;
                        if (q.Y < bottom || q.Y > g + r) continue;
                        best = t;
                        found = true;
                        tile = new Point(x, y);
                        dir = d;
                    }
                }
            }
            if (!found) dir = -1;
            return found;
        }

        /// <summary>The highest foundation (in height, like <see cref="InterpAltitude"/>) within <paramref name="radius"/> tiles of a point.</summary>
        public float FoundationNear(float x, float y, float radius)
        {
            return HasFoundations ? Foundations.RaiseNear(x, y, radius) * TerrainFactor : 0f;
        }

        /// <summary>A tile's own foundation raise, in height (0 for none).</summary>
        public float FoundationAt(int x, int y)
        {
            return HasFoundations ? Foundations.RaiseOf(x, y) * TerrainFactor : 0f;
        }

        /// <summary>
        /// The height of an object attached to walls: its tile's, unless a wall it's attached to stands on a higher
        /// foundation (a window on the outside of a wall on a foundation's edge).
        /// </summary>
        public float WallObjectAltitude(Vector3 position, WallSegments attached)
        {
            var result = InterpAltitude(position);
            if (!HasFoundations || attached == 0) return result;
            int x = (int)Math.Floor(position.X), y = (int)Math.Floor(position.Y);
            var own = Foundations.RaiseOf(x, y);
            var wall = Foundations.WallRaise(x, y, attached);
            if (wall > own) result += (wall - own) * TerrainFactor;
            return result;
        }


        public float GetAltPoint(int x, int y)
        {
            //x += 1; y += 1;
            if (x <= 0 || y <= 0) return 0f;
            return (Altitude[((y % Height) * Width + (x % Width))]);
        }

        /// <summary>
        /// The height under a point (tile units): the ground, and the foundation there (partway up, on steps).
        /// </summary>
        public float InterpAltitude(Vector3 Position)
        {
            var result = TerrainAltitude(Position);
            if (HasFoundations) result += Foundations.RaiseAt(Position.X, Position.Y) * TerrainFactor;
            return result;
        }

        /// <summary>The height of the ground itself under a point (tile units), without foundations.</summary>
        public float TerrainAltitude(Vector3 Position)
        {
            if (Altitude == null) return 0f;
            var baseX = (int)Math.Max(1, Math.Min(Width-1, Math.Floor(Position.X)));
            var baseY = (int)Math.Max(1, Math.Min(Height-1, Math.Floor(Position.Y)));
            if (baseX < 0 || baseY < 0) return 0;
            var nextX = (int)Math.Max(1, Math.Min(Width - 1, Math.Ceiling(Position.X)));
            var nextY = (int)Math.Max(1, Math.Min(Height - 1, Math.Ceiling(Position.Y)));
            var xLerp = Position.X % 1f;
            var yLerp = Position.Y % 1f;

            if (xLerp < 0)
            {
                xLerp = -xLerp;
            }

            if (yLerp < 0)
            {
                yLerp = -yLerp;
            }

            var by = (baseY % Height) * Width;
            var bx = (baseX % Width);
            var ny = (nextY % Height) * Width;
            var nx = (nextX % Width);
            float h00 = Altitude[(by + bx)];
            float h01 = Altitude[(ny + bx)];
            float h10 = Altitude[(by + nx)];
            float h11 = Altitude[(ny + nx)];

            float xl1 = xLerp * h10 + (1 - xLerp) * h00;
            float xl2 = xLerp * h11 + (1 - xLerp) * h01;

            return (yLerp * xl2 + (1 - yLerp) * xl1 - BaseAlt) * TerrainFactor;
        }

        public Vector3 InterpNormal(Vector3 Position)
        {
            if (Altitude == null) return Vector3.Backward;

            var baseX = (int)Math.Max(1, Math.Min(Width - 1, Position.X));
            var baseY = (int)Math.Max(1, Math.Min(Height - 1, Position.Y));
            if (baseX < 0 || baseY < 0) return Vector3.Backward;
            var nextX = (int)Math.Max(1, Math.Min(Width - 1, Math.Ceiling(Position.X)));
            var nextY = (int)Math.Max(1, Math.Min(Height - 1, Math.Ceiling(Position.Y)));
            var xLerp = Position.X % 1f;
            var yLerp = Position.Y % 1f;

            var by = (baseY % Height) * Width;
            var bx = (baseX % Width);
            var ny = (nextY % Height) * Width;
            var nx = (nextX % Width);
            float h00 = Altitude[(by + bx)];
            float h01 = Altitude[(ny + bx)];
            float h10 = Altitude[(by + nx)];
            float h11 = Altitude[(ny + nx)];

            float xl1 = xLerp * h10 + (1 - xLerp) * h00;
            float xl2 = xLerp * h11 + (1 - xLerp) * h01;

            // Reduced to a single line in the Y direction. 

            float xDist1 = (h10 - h00) * TerrainFactor;
            float xDist2 = (h11 - h01) * TerrainFactor;
            float xChange = xLerp * xDist1 + (1 - xLerp) * xDist2;
            float yChange = (xl2 - xl1) * TerrainFactor;

            return - Vector3.Normalize(Vector3.Cross(new Vector3(1, xChange, 0), new Vector3(0, yChange, 1)));
        }

        public static void SetLightColor(LightMappedEffect effect, Color outside, Color minOut)
        {
            //return;
            effect.OutsideDark = minOut.ToVector4();
            var avg = (minOut.R + minOut.G + minOut.B) / (255 * 3f);
            var minAvg = new Vector2(avg, 1 / (1 - avg));
            if (float.IsInfinity(minAvg.Y)) minAvg.Y = 1;
            effect.MinAvg = minAvg;
        }

        public sbyte GetFloorsUsed()
        {
            sbyte bestResult = 1;
            for (sbyte i=1; i<Stories; i++)
            {
                var hasAnything = false;
                foreach (var floor in Floors[i])
                {
                    if (floor.Pattern != 0)
                    {
                        hasAnything = true;
                        break;
                    }
                }
                if (!hasAnything)
                {
                    foreach (var wall in Walls[i])
                    {
                        if (wall.Segments != 0)
                        {
                            hasAnything = true;
                            break;
                        }
                    }
                }
                if (hasAnything) bestResult = (sbyte)(i + 1);
            }
            return bestResult;
        }

        public void GenerateRoomLights()
        {
            var minOut = MinOut;
            minOut.A = 255;

            foreach (var effect in WorldContent.LightEffects)
            {
                SetLightColor(effect, OutsideColor, minOut);
            }

            for (int i=0; i<Light.Length; i++)
            {
                var outside = OutsideColor * (Light[i].OutsideLight / 100f);
                var ambient = Color.White * (Light[i].AmbientLight / 100f);

                outside.R = Math.Max(minOut.R, outside.R);
                outside.G = Math.Max(minOut.G, outside.G);
                outside.B = Math.Max(minOut.B, outside.B);

                RoomColors[i] = new Color(
                    Math.Min(255, outside.R + ambient.R),
                    Math.Min(255, outside.G + ambient.G),
                    Math.Min(255, outside.B + ambient.B),
                    255);
            }
            RoomColors[65535] = Color.White;
        }

        public void AddAvatar(AvatarComponent avatar){
            this.Avatars.Add(avatar);
        }

        public void RemoveAvatar(AvatarComponent avatar)
        {
            this.Avatars.Remove(avatar);
            HeadlineObjects.Remove(avatar);

            SM64?.RemoveMario(avatar);
        }

        public void SignalWallChange()
        {
            Changes.SetFlag(BlueprintGlobalChanges.WALL_CHANGED);
            //todo: should this even have a position? we're rerendering the whole thing atm
            //should eventually consider level
        }

        public void SignalRoomChange()
        {
            Changes.SetFlag(BlueprintGlobalChanges.ROOM_CHANGED);
            //todo: should this even have a position? we're rerendering the whole thing atm
            //should eventually consider level
        }

        public void SignalFloorChange()
        {
            Changes.SetFlag(BlueprintGlobalChanges.FLOOR_CHANGED);
        }

        public WallTile GetWall(short tileX, short tileY, sbyte level)
        {
            return Walls[level-1][GetOffset(tileX, tileY)];
        }

        public FloorTile GetFloor(short tileX, short tileY, sbyte level)
        {
            var offset = GetOffset(tileX, tileY);
            return Floors[level-1][offset];
        }

        public bool TileInbounds(Vector2 tile)
        {
            return (tile.X >= 0 && tile.Y >= 0 && tile.X < Width && tile.Y < Height);
        }

        public void ChangeObjectLocation(ObjectComponent component, LotTilePos pos)
        {
            short tileX = (pos.x < 0) ? (short)0 : pos.TileX;
            short tileY = (pos.y < 0) ? (short)0 : pos.TileY;
            sbyte level = pos.Level;

            Changes.RegisterObjectChange(component);

            component.blueprint = this;
            component.TileX = tileX;
            component.TileY = tileY;
            component.Level = level;
        }

        public void AddObject(ObjectComponent component)
        {
            Changes.RegisterObject(component);
            Objects.Add(component);
        }

        public void RemoveObject(ObjectComponent component)
        {
            Changes.UnregisterObject(component);
            Objects.Remove(component);
            HeadlineObjects.Remove(component);
            component.Dead = true;
            //remove all of this object's particles, and make sure we dispose their vertex buffers.
            foreach (var part in component.Particles)
            {
                part.Dispose();
                ObjectParticles.Remove(part);
            }

            SM64?.RemoveObject(component);
        }

        private ushort GetOffset(int tileX, int tileY){
            return (ushort)((tileY * Width) + tileX);
        }

        public Rectangle GetFineBounds()
        {
            var minx = int.MaxValue;
            var miny = int.MaxValue;
            var maxx = 0;
            var maxy = 0;

            int i = 0;
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    if (FineArea[i++])
                    {
                        if (x < minx) minx = x;
                        if (x > maxx) maxx = x;
                        if (y < miny) miny = y;
                        if (y > maxy) maxy = y;
                    }
                }
            }

            return new Rectangle(minx, miny, (maxx - minx) + 1, (maxy - miny) + 1);
        }

        public Vector2 GetThumbCenterTile(WorldState state)
        {
            if (FineArea == null) return new Vector2(Width / 2, Height / 2);
            else
            {
                var bounds = GetFineBounds();
                var result = new Vector2(bounds.X + bounds.Width / 2f, bounds.Y + bounds.Height / 2f);
                result -= (InterpAltitude(new Vector3(bounds.X + bounds.Width, bounds.Y + bounds.Height, 0)) / 2.95f) * state.WorldSpace.GetTileFromScreen(new Vector2(0, 230)) / (1 << (3 - (int)state.Zoom));
                return result;
            }
        }

        public byte[] GetIndoors()
        {
            if (Indoors != null) return Indoors;
            else
            {
                //generate the indoors map
                var partition = 255 / (Stories+1);
                Indoors = new byte[Width * Height];
                for (int flr = 0; flr < Stories; flr++)
                {
                    var pflr = (byte)(partition * flr);
                    var pflrn = (byte)(partition * (flr + 1));
                    var rooms = RoomMap[flr];
                    var floors = Floors[flr];
                    for (int i=0; i<Indoors.Length; i++)
                    {
                        if (floors[i].Pattern > 0) Indoors[i] = pflr;
                        var room = rooms[i];
                        if ((room & 0xFFFF) > 0)
                        {
                            var rm = Rooms[(int)room & 0xFFFF];
                            if (!rm.IsOutside) Indoors[i] = pflrn;
                            if (((int)room >> 16) > 0)
                            {
                                rm = Rooms[(int)room >> 16];
                                if (!rm.IsOutside) Indoors[i] = pflrn;
                            }
                        }
                    }
                }
            }
            return Indoors;
        }

        public bool IsIndoorsPrecise(Vector2 pos, int floor)
        {
            Point testPos = pos.ToPoint();

            if (testPos.X >= 0 && testPos.X < Width && testPos.Y >= 0 && testPos.Y < Height)
            {
                int offset = testPos.X + testPos.Y * Width;
                var room = RoomMap[floor][offset];

                if ((ushort)room != (ushort)((room >> 16) & 0x7FFF))
                {
                    // Need to evaluate the diagonal orientation. It's stored in the wall segments in TS1.
                    var wall = Walls[floor][offset];
                    float xFrac = pos.X - testPos.X;
                    float yFrac = pos.Y - testPos.Y;

                    if (wall.Segments == WallSegments.HorizontalDiag)
                    {
                        if (xFrac + yFrac > 1)
                        {
                            room = ((room >> 16) & 0x7FFF);
                        }
                    }
                    else if (wall.Segments == WallSegments.VerticalDiag)
                    {
                        if (xFrac - yFrac < 0)
                        {
                            room = ((room >> 16) & 0x7FFF);
                        }
                    }
                }

                return !Rooms[Rooms[(ushort)room].Base].IsOutside;
            }

            return false;
        }

        private byte[] GrassMask;
        public byte[] GetGrassMask()
        {
            if (GrassMask != null) return GrassMask;
            else
            {
                GrassMask = new byte[Width * Height];

                //1: top tri clear
                //2: right tri clear
                //4: bottom tri clear
                //8: left tri clear

                var walls = Walls[0];
                var floors = Floors[0];
                for (int i=0; i<GrassMask.Length; i++)
                {
                    var wall = walls[i];
                    if ((wall.Segments & WallSegments.AnyDiag) > 0)
                    {
                        //diagonal tile here - check both sides for grass
                        byte result = 0;
                        if ((wall.Segments & WallSegments.HorizontalDiag) > 0)
                        {

                        }
                        else
                        {

                        }
                    } else
                    {
                        GrassMask[i] = (byte)((floors[i].Pattern == 0) ? 0xFF : 0);
                    }
                }
                return GrassMask;
            }
        }
    }

    [Flags]
    public enum BlueprintOccupiedTileType {
        OBJECT = 0x1,
        FLOOR = 0x2
    }

    public class BlueprintOccupiedTile : IIsometricTile {
        public short TileX { get; set; }
        public short TileY { get; set; }
        public BlueprintOccupiedTileType Type;
    }

    public class BlueprintDamage {
        public BlueprintDamageType Type;
        public short TileX;
        public short TileY;
        public sbyte Level;
        public WorldComponent Component;

        public BlueprintDamage(BlueprintDamageType type)
        {
            this.Type = type;
        }

        public BlueprintDamage(BlueprintDamageType type, short tileX, short tileY, sbyte level, WorldComponent component)
        {
            this.Type = type;
            this.TileX = tileX;
            this.TileY = tileY;
            this.Level = level;
            this.Component = component;
        }

        public BlueprintDamage(BlueprintDamageType type, short tileX, short tileY, sbyte level){
            this.Type = type;
            this.TileX = tileX;
            this.TileY = tileY;
            this.Level = level;
        }
    }

    public enum BlueprintDamageType {
        OBJECT_MOVE,
        OBJECT_GRAPHIC_CHANGE,
        OBJECT_RETURN_TO_STATIC,
        FLOOR_CHANGED,
        WALL_CHANGED,
        SCROLL,
        ROTATE,
        ZOOM,
        PRECISE_ZOOM,
        WALL_CUT_CHANGED,
        LEVEL_CHANGED,
        LIGHTING_CHANGED,
        OUTDOORS_LIGHTING_CHANGED,
        ROOM_CHANGED,
        ROOF_STYLE_CHANGED,
        OPENGL_SECOND_DRAW
    }

    public class BlueprintObjectList {
        public List<ObjectComponent> Objects = new List<ObjectComponent>();

        public void RemoveObject(ObjectComponent comp){
            Objects.Remove(comp);
        }

        public void AddObject(ObjectComponent comp){
            Objects.Add(comp);
        }
    }
}
