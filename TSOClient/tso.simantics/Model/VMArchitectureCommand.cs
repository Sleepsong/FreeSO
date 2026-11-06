using System.IO;
using FSO.SimAntics.NetPlay.Model;

namespace FSO.SimAntics.Model
{
    public struct VMArchitectureCommand : VMSerializable
    {
        public VMArchitectureCommandType Type;
        public uint CallerUID;
        public int x;
        public int y;
        public sbyte level;

        public int x2; //for RECT: width and height. for LINE: length and direction. Not important for fill.
        public int y2;

        //note: for pattern dot x2 is "side". 0-5, 0-3 for normal walls and 4-5 for diagonal sides

        public ushort pattern;
        public ushort style; //for walls, obvious. maybe means something else for floors on diagonals

        #region VMSerializable Members
        public void SerializeInto(BinaryWriter writer)
        {
            writer.Write((byte)Type);
            writer.Write(x);
            writer.Write(y);
            writer.Write(level);

            writer.Write(x2);
            writer.Write(y2);

            writer.Write(pattern);
            writer.Write(style);
        }

        public void Deserialize(BinaryReader reader)
        {
            Type = (VMArchitectureCommandType)reader.ReadByte();
            x = reader.ReadInt32();
            y = reader.ReadInt32();
            level = reader.ReadSByte();

            x2 = reader.ReadInt32();
            y2 = reader.ReadInt32();

            pattern = reader.ReadUInt16();
            style = reader.ReadUInt16();
        }
        #endregion
    }

    public enum VMArchitectureCommandType: byte
    {
        WALL_LINE,
        WALL_DELETE,
        WALL_RECT,

        PATTERN_DOT,
        PATTERN_FILL,

        FLOOR_RECT,
        FLOOR_FILL,

        TERRAIN_RAISE,
        TERRAIN_FLATTEN,

        GRASS_DOT,

        FOUNDATION_RECT, //x, y, x2 (width - 1), y2 (height - 1): tiles. style: height, 0 removes. pattern: wall covering
        FOUNDATION_STEPS, //x, y: tile. pattern: direction (0-3, 255 for the adjacent foundation). style: 1 places, 0 removes

        BASEMENT_RECT, //x, y, x2 (width - 1), y2 (height - 1): tiles. style: bit 0 digs (1) or fills in (0), above it the earth sides' wall covering. pattern: the floor
        FOUNDATION_PAINT //x, y: tile. x2: its side (0-3), or 255 for every side of the foundation. pattern: wall covering, 0 for plain
    }
}
