using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FSO.LotView.Model
{
    public enum RoofPieceType : byte
    {
        /// <summary>Slopes on all four sides.</summary>
        Hip = 0,
        /// <summary>Two slopes meeting at a ridge, with upright triangular ends.</summary>
        Gable = 1,
        /// <summary>One slope, high on one side (a lean-to).</summary>
        Shed = 2,
    }

    /// <summary>
    /// A roof drawn with the roof tool, as in The Sims 2: a rectangle of tiles, the shape, which way it faces, and its
    /// pitch. It sits on top of the walls of the floor below its level (level 2 is on the ground floor's walls, as with
    /// the automatic roof).
    /// </summary>
    public struct RoofPiece : IEquatable<RoofPiece>
    {
        public sbyte Level;
        public short X;
        public short Y;
        public short Width;
        public short Height;
        public RoofPieceType Type;
        /// <summary>
        /// Gable: 0 or 2 for a ridge running along x, 1 or 3 along y. Shed: the low side (0 is -y, 1 is +x, 2 is +y, 3 is -x).
        /// </summary>
        public byte Direction;
        /// <summary>Pitch in percent (66 is the game's usual roof).</summary>
        public byte Pitch;

        public const byte DefaultPitch = 66;

        public int Tiles => Width * Height;

        public bool Contains(int x, int y)
        {
            return x >= X && y >= Y && x < X + Width && y < Y + Height;
        }

        public void Write(BinaryWriter writer)
        {
            writer.Write(Level);
            writer.Write(X);
            writer.Write(Y);
            writer.Write(Width);
            writer.Write(Height);
            writer.Write((byte)Type);
            writer.Write(Direction);
            writer.Write(Pitch);
        }

        public static RoofPiece Read(BinaryReader reader)
        {
            return new RoofPiece
            {
                Level = reader.ReadSByte(),
                X = reader.ReadInt16(),
                Y = reader.ReadInt16(),
                Width = reader.ReadInt16(),
                Height = reader.ReadInt16(),
                Type = (RoofPieceType)reader.ReadByte(),
                Direction = reader.ReadByte(),
                Pitch = reader.ReadByte(),
            };
        }

        public bool Equals(RoofPiece other)
        {
            return Level == other.Level && X == other.X && Y == other.Y && Width == other.Width && Height == other.Height
                && Type == other.Type && Direction == other.Direction && Pitch == other.Pitch;
        }

        public override bool Equals(object obj) => obj is RoofPiece other && Equals(other);
        public override int GetHashCode() => (X * 397) ^ (Y * 31) ^ (Width << 8) ^ (Height << 16) ^ (Level << 24) ^ ((int)Type << 28) ^ Direction ^ (Pitch << 4);
    }

    /// <summary>
    /// A lot's drawn roofs. While AutoRoof is on (the default, and what every lot from the game has), the game's
    /// automatic roof covers the house and there are no drawn roofs. Drawing a roof turns it off, so the house only has
    /// the roofs drawn on it, as in The Sims 2.
    /// </summary>
    public class DrawnRoofs
    {
        public bool AutoRoof = true;
        public List<RoofPiece> Pieces = new List<RoofPiece>();

        /// <summary>Anything to save: lots that keep the automatic roof save nothing extra.</summary>
        public bool Any => !AutoRoof || Pieces.Count > 0;

        public DrawnRoofs Clone()
        {
            return new DrawnRoofs { AutoRoof = AutoRoof, Pieces = new List<RoofPiece>(Pieces) };
        }

        public bool SameAs(DrawnRoofs other)
        {
            if (other == null) return !Any;
            return AutoRoof == other.AutoRoof && Pieces.SequenceEqual(other.Pieces);
        }

        // ---- saving: a block at the very end of a lot's save, which builds without drawn roofs never read ----

        /// <summary>"ROOF", marking the block.</summary>
        public const int Magic = 0x464F4F52;
        public const byte FormatVersion = 1;

        public void Write(BinaryWriter writer)
        {
            writer.Write(Magic);
            writer.Write(FormatVersion);
            writer.Write(AutoRoof);
            writer.Write(Pieces.Count);
            foreach (var piece in Pieces) piece.Write(writer);
        }

        /// <summary>
        /// Reads the drawn roofs block if the save has one after <paramref name="reader"/>'s position (it's the last
        /// thing in a save); null if there isn't one.
        /// </summary>
        public static DrawnRoofs ReadIfPresent(BinaryReader reader)
        {
            var stream = reader.BaseStream;
            if (!stream.CanSeek || stream.Length - stream.Position < 9) return null;
            var start = stream.Position;
            if (reader.ReadInt32() != Magic)
            {
                stream.Position = start;
                return null;
            }
            var version = reader.ReadByte();
            if (version > FormatVersion) return null;
            var result = new DrawnRoofs { AutoRoof = reader.ReadBoolean() };
            var count = reader.ReadInt32();
            for (int i = 0; i < count; i++) result.Pieces.Add(RoofPiece.Read(reader));
            return result;
        }
    }
}
