using FSO.Content.Model;
using FSO.LotView.Model;
using FSO.SimAntics.NetPlay.Model;
using FSO.SimAntics.Utils;
using System;
using System.IO;

namespace FSO.SimAntics.Model
{
    public class VMArchitectureTerrain : VMSerializable
    {
        public int Width;
        public int Height;

        public short[] Heights;
        public short[] Centers;
        public bool[] Sloped;
        public byte[] GrassState;

        public short[] VisHeights;
        public byte[] VisGrass;
        private short[] OldHeights;
        private byte[] OldGrass;

        /// <summary>Foundations and their steps (see <see cref="FoundationMap"/>), shared with the world for drawing.</summary>
        public FoundationMap Foundations;
        /// <summary>The copy previews (build tools dragging) change, while <see cref="EnterVis"/> is in effect.</summary>
        public FoundationMap VisFoundations;
        private FoundationMap OldFoundations;

        public TerrainType LightType = TerrainType.GRASS;
        public TerrainType DarkType = TerrainType.GRASS;
        public int Version;

        public static float[] TerrainXNoise = GeneratePerlinNoise(77, 1);
        public static float[] TerrainYNoise = GeneratePerlinNoise(77, 2);

        public VMArchitectureTerrain(int width, int height)
        {
            Width = width;
            Height = height;

            Heights = new short[width * height];
            GrassState = new byte[width * height];
            Centers = new short[width * height];
            Sloped = new bool[width * height];

            VisHeights = new short[width * height];
            VisGrass = new byte[width * height];

            Foundations = new FoundationMap(width, height);
            VisFoundations = new FoundationMap(width, height);
        }

        public void EnterVis()
        {
            OldHeights = Heights;
            OldGrass = GrassState;

            Array.Copy(Heights, VisHeights, Heights.Length);
            Array.Copy(GrassState, VisGrass, GrassState.Length);

            Heights = VisHeights;
            GrassState = VisGrass;

            OldFoundations = Foundations;
            VisFoundations.CopyFrom(Foundations);
            Foundations = VisFoundations;
        }

        public void ExitVis()
        {
            Heights = OldHeights;
            GrassState = OldGrass;
            if (OldFoundations != null) Foundations = OldFoundations;
            OldFoundations = null;
        }

        public void RegenerateCenters() //TODO: partial update
        {
            int i = 0;
            for (int y=0; y<Height; y++)
            {
                for (int x = 0; x < Width; x++) {
                    var h1 = Heights[i];
                    var h2 = Heights[y * Width + ((x + 1) % Width)];
                    var h3 = Heights[((y + 1) % Height) * Width + ((x + 1) % Width)];
                    var h4 = Heights[((y + 1) % Height) * Width + x];
                    
                    Centers[i] = (short)((Heights[i] + h2 + h3 + h4) / 4);
                    //on the last row and column the far corners wrap around to the other side of the lot.
                    //only compare corners on the lot, or hillside lots get sloped edges (and cars can't drive off).
                    var nx = Math.Min(x + 1, Width - 1);
                    var ny = Math.Min(y + 1, Height - 1);
                    Sloped[i] = (h1 != Heights[y * Width + nx]) || (h1 != Heights[ny * Width + nx]) || (h1 != Heights[ny * Width + x]);
                    i++;
                }
            }
        }

        public static float[] GeneratePerlinNoise(int size, int seed)
        {
            var random = new Random(seed);
            int width = size;
            float[] result = new float[size*size];
            int initial = width / 4; //divide by more for less noisyness!
            float factor = 0.42f / ((int)Math.Log(initial, 2));

            float min = 1;
            float max = 0;
            int offset;

            while (initial > 0)
            {
                var squared = initial * initial;
                var noise = new float[squared];
                for (int i = 0; i < squared; i++) noise[i] = (float)random.NextDouble() * factor;

                offset = 0;
                for (int x = 0; x < width; x++)
                {
                    double xInt = (x / (double)(width - 1)) * (initial - 1);
                    for (int y = 0; y < width; y++)
                    {
                        double yInt = (y / (double)(width - 1)) * (initial - 1);
                        float tl = noise[(int)(Math.Floor(yInt) * initial + Math.Floor(xInt))];
                        float tr = noise[(int)(Math.Floor(yInt) * initial + Math.Ceiling(xInt))];
                        float bl = noise[(int)(Math.Ceiling(yInt) * initial + Math.Floor(xInt))];
                        float br = noise[(int)(Math.Ceiling(yInt) * initial + Math.Ceiling(xInt))];
                        float p = (float)(xInt % 1.0);
                        float q = (float)(yInt % 1.0);
                        result[offset++] += (tl * (1 - p) + tr * (p)) * (1 - q) + (bl * (1 - p) + br * (p)) * q; //don't you love 2 dimensional linear interpolation?? ;)
                        if (initial == 1)
                        {
                            if (result[offset - 1] < min) min = result[offset - 1];
                            if (result[offset - 1] > max) max = result[offset - 1];
                        }
                    }
                }
                factor *= 1.35f;
                initial /= 2;
            }

            //var off = (min * 3 + max) / 4;
            var mul = 1/(max - min);
            offset = 0;
            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < width; y++)
                {
                    result[offset] -= min;
                    result[offset++] *= mul;
                    if (result[offset - 1] < 0) result[offset - 1] = 0;

                    //if within 8 of edges, gradiate to 0
                    var dist1 = Math.Abs(x);
                    var dist2 = Math.Abs(x - width);
                    if (dist2 < dist1) dist1 = dist2;
                    dist2 = Math.Abs(y);
                    if (dist2 < dist1) dist1 = dist2;
                    dist2 = Math.Abs(y - size);
                    if (dist2 < dist1) dist1 = dist2;

                    dist1 -= 4;
                    if (dist1 < 0) dist1 = 0;

                    if (dist1 < 8)
                    {
                        result[offset - 1] *= dist1 / 8f;
                        result[offset - 1] += 0.5f * (1-(dist1 / 8f));
                    }
                }
            }

            return result;
        }

        public void GenerateGrassStates(RestoreLotType type) //generates a set of grass states for a lot.
        {
            //right now only works for square lots, but that's all tso has!
            var random = new Random();
            int width = Width;

            if (type == RestoreLotType.Blank)
            {
                GrassState = new byte[Width * Height];
                return;
            }

            float[] result = new float[Width * Height];
            int initial = width / 4; //divide by more for less noisyness!
            float factor = 0.42f / ((int)Math.Log(initial, 2));

            float min = 1;
            float max = 0;
            if (LightType != DarkType) factor /= 2.5f;
            int offset;

            while (initial > 0)
            {
                var squared = initial * initial;
                var noise = new float[squared];
                for (int i = 0; i < squared; i++) noise[i] = (float)random.NextDouble() * factor;

                offset = 0;
                for (int x = 0; x < width; x++)
                {
                    double xInt = (x / (double)(width - 1)) * (initial - 1);
                    for (int y = 0; y < width; y++)
                    {
                        double yInt = (y / (double)(width - 1)) * (initial - 1);
                        float tl = noise[(int)(Math.Floor(yInt) * initial + Math.Floor(xInt))];
                        float tr = noise[(int)(Math.Floor(yInt) * initial + Math.Ceiling(xInt))];
                        float bl = noise[(int)(Math.Ceiling(yInt) * initial + Math.Floor(xInt))];
                        float br = noise[(int)(Math.Ceiling(yInt) * initial + Math.Ceiling(xInt))];
                        float p = (float)(xInt % 1.0);
                        float q = (float)(yInt % 1.0);
                        result[offset++] += (tl * (1 - p) + tr * (p)) * (1 - q) + (bl * (1 - p) + br * (p)) * q; //don't you love 2 dimensional linear interpolation?? ;)
                        if (initial == 1)
                        {
                            if (result[offset - 1] < min) min = result[offset - 1];
                            if (result[offset - 1] > max) max = result[offset - 1];
                        }
                    }
                }
                factor *= 1.25f;
                initial /= 2;
            }

            var off = (min * 3 + max) / 4;
            offset = 0;
            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < width; y++)
                {
                    result[offset++] -= off;
                    if (result[offset - 1] < 0) result[offset - 1] = 0;

                    //if within 8 of edges, gradiate to 0
                    var dist1 = Math.Abs(x);
                    var dist2 = Math.Abs(x - width);
                    if (dist2 < dist1) dist1 = dist2;
                    dist2 = Math.Abs(y);
                    if (dist2 < dist1) dist1 = dist2;
                    dist2 = Math.Abs(y - Height);
                    if (dist2 < dist1) dist1 = dist2;

                    if (dist1 < 8) result[offset-1] *= dist1 / 8f;
                }
            }

            GrassState = new byte[result.Length];
            for (int i = 0; i < result.Length; i++)
            {
                GrassState[i] = (byte)(result[i] * 255);
            }
        }

        public void SerializeInto(BinaryWriter writer)
        {
            writer.Write((byte)LightType);
            writer.Write((byte)DarkType);
            writer.Write(Heights.Length * 2);
            VMSerializableUtils.WriteArray(writer, Heights);
            writer.Write(GrassState.Length);
            writer.Write(GrassState);
        }

        /// <summary>
        /// The foundations' own revision, written in place of the flag saying a lot has any (a reader that takes it as a
        /// bool still sees true). 1: a covering per tile, all put on by the build tool itself (a brick, before the
        /// foundations were concrete); 2: a covering per tile, for all its sides; 3: a covering per side of each tile,
        /// painted on like walls'; 4: and each tile's kind (foundation or deck) and each set of steps' staircase.
        /// </summary>
        public const byte FoundationsRevision = 4;

        /// <summary>
        /// Foundations, saved at the very end of a save (version 39 on; see VMMarshal) rather than with the terrain, so
        /// builds without foundations still read the rest of the lot (they stop before this).
        /// </summary>
        public static void WriteFoundations(BinaryWriter writer, FoundationMap map)
        {
            var has = map != null && map.Any;
            writer.Write(has ? FoundationsRevision : (byte)0);
            if (!has) return;
            writer.Write(map.Width);
            writer.Write(map.Height);
            VMSerializableUtils.WriteArray(writer, map.Raise);
            writer.Write(map.Steps);
            VMSerializableUtils.WriteArray(writer, map.Sides);
            writer.Write(map.Kind);
            VMSerializableUtils.WriteArray(writer, map.StepStyle);
        }

        /// <summary>Reads what <see cref="WriteFoundations"/> wrote: null if the lot has none.</summary>
        public static FoundationMap ReadFoundations(BinaryReader reader)
        {
            var revision = reader.ReadByte();
            if (revision == 0) return null;
            int width = reader.ReadInt32(), height = reader.ReadInt32();
            var map = new FoundationMap(width, height);
            map.Raise = VMSerializableUtils.ReadArray<short>(reader, width * height);
            map.Steps = reader.ReadBytes(width * height);
            if (revision >= 3) map.Sides = VMSerializableUtils.ReadArray<ushort>(reader, width * height * 4);
            else
            {
                //a covering per tile: on all four of its sides. The first foundations' brick was the tool's, not the
                //player's: they take the foundation's own brick, like new ones
                var perTile = VMSerializableUtils.ReadArray<ushort>(reader, width * height);
                if (revision >= 2)
                    for (int i = 0; i < perTile.Length; i++) map.SetAllSides(i, perTile[i]);
            }
            if (revision >= 4)
            {
                map.Kind = reader.ReadBytes(width * height);
                map.StepStyle = VMSerializableUtils.ReadArray<uint>(reader, width * height);
            }
            map.Refresh();
            return map;
        }

        /// <summary>Takes on saved foundations (if they fit this lot).</summary>
        public void SetFoundations(FoundationMap map)
        {
            if (map == null || map.Width != Width || map.Height != Height) return;
            Foundations.CopyFrom(map);
            Foundations.Refresh();
        }

        public void Deserialize(BinaryReader reader)
        {
            LightType = (TerrainType)reader.ReadByte();
            DarkType = (TerrainType)reader.ReadByte();
            int byteCount = reader.ReadInt32();
            if (Version > 18)
            {
                Heights = VMSerializableUtils.ReadArray<short>(reader, byteCount / 2);
            }
            else
            {
                var dat = reader.ReadBytes(byteCount);
                Heights = Array.ConvertAll(dat, x => (short)x);
            }
            
            GrassState = reader.ReadBytes(reader.ReadInt32());

            Foundations = new FoundationMap(Width, Height);
            VisFoundations = new FoundationMap(Width, Height);
        }
    }
}
