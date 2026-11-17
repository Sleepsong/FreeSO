using System.IO;
using FSO.Files.Utils;

namespace FSO.Files.Formats.IFF.Chunks
{
    /// <summary>
    /// This chunk type holds Behavior code in SimAntics.
    /// </summary>
    public class BHAV : IffChunk
    {
        public TREE RuntimeTree;
        public BHAVInstruction[] Instructions = new BHAVInstruction[0];
        public byte Type;
        public byte Args;
        public ushort Locals;
        public ushort Version;

        public uint RuntimeVer;

        /// <summary>
        /// Reads a BHAV from a stream.
        /// </summary>
        /// <param name="iff">Iff instance.</param>
        /// <param name="stream">A Stream instance holding a BHAV chunk.</param>
        /// <summary>
        /// Some files (eg. converted TSO objects) have a 0x8003 header on a body that is actually the 0x8002 layout.
        /// True if the size only works out as 0x8002.
        /// </summary>
        private static bool IsOldLayoutMislabeled(byte[] data)
        {
            if (data.Length < 13) return false;
            long newCount = BitConverter.ToUInt32(data, 9);
            if (newCount * 12 + 13 == data.Length) return false;
            long oldCount = BitConverter.ToUInt16(data, 2);
            return oldCount * 12 + 12 == data.Length;
        }

        public override void Read(IffFile iff, System.IO.Stream stream)
        {
            byte[] oldLayoutCheck = new byte[0];
            if (stream.CanSeek && stream.Length >= 13)
            {
                var start = stream.Position;
                oldLayoutCheck = new byte[stream.Length - start];
                stream.Read(oldLayoutCheck, 0, oldLayoutCheck.Length);
                stream.Position = start;
            }
            using (var io = IoBuffer.FromStream(stream, ByteOrder.LITTLE_ENDIAN))
            {
                var filetypeVersion = io.ReadUInt16();
                uint count = 0;

                if (filetypeVersion == 0x8003 && IsOldLayoutMislabeled(oldLayoutCheck)) filetypeVersion = 0x8002;

                if (filetypeVersion == 0x8000)
                {
                    count = io.ReadUInt16();
                    io.Skip(8);
                }
                else if (filetypeVersion == 0x8001)
                {
                    count = io.ReadUInt16();
                    var unknown = io.ReadBytes(8);
                }
                else if (filetypeVersion == 0x8002)
                {
                    count = io.ReadUInt16();
                    this.Type = io.ReadByte();
                    this.Args = io.ReadByte();
                    this.Locals = io.ReadUInt16();
                    this.Version = io.ReadUInt16();
                    io.Skip(2);
                }
                else if (filetypeVersion == 0x8003)
                {
                    this.Type = io.ReadByte();
                    this.Args = io.ReadByte();
                    this.Locals = io.ReadByte();
                    io.Skip(2);
                    this.Version = io.ReadUInt16();
                    count = io.ReadUInt32();
                }

                Instructions = new BHAVInstruction[count];
                for (var i = 0; i < count; i++)
                {
                    var instruction = new BHAVInstruction();
                    instruction.Opcode = io.ReadUInt16();
                    instruction.TruePointer = io.ReadByte();
                    instruction.FalsePointer = io.ReadByte();
                    instruction.Operand = io.ReadBytes(8);
                    Instructions[i] = instruction;
                }
            }
        }

        public override bool Write(IffFile iff, Stream stream)
        {
            using (var io = IoWriter.FromStream(stream, ByteOrder.LITTLE_ENDIAN))
            {
                if (IffFile.TargetTS1)
                { //version 0x8002
                    io.WriteUInt16(0x8002);
                    io.WriteUInt16((ushort)Instructions.Length);
                    io.WriteByte(Type);
                    io.WriteByte(Args);
                    io.WriteUInt16(Locals);
                    io.WriteUInt16(Version);
                    io.WriteBytes(new byte[] { 0, 0 });

                    foreach (var inst in Instructions)
                    {
                        io.WriteUInt16(inst.Opcode);
                        io.WriteByte(inst.TruePointer);
                        io.WriteByte(inst.FalsePointer);
                        io.WriteBytes(inst.Operand);
                    }
                }
                else
                {
                    io.WriteUInt16(0x8003);
                    io.WriteByte(Type);
                    io.WriteByte(Args);
                    io.WriteByte((byte)Locals);
                    io.WriteBytes(new byte[] { 0, 0 });
                    io.WriteUInt16(Version);
                    io.WriteUInt32((ushort)Instructions.Length);

                    foreach (var inst in Instructions)
                    {
                        io.WriteUInt16(inst.Opcode);
                        io.WriteByte(inst.TruePointer);
                        io.WriteByte(inst.FalsePointer);
                        io.WriteBytes(inst.Operand);
                    }
                }
            }
            return true;
        }

    }

    public class BHAVInstruction 
    {
        public ushort Opcode;
        public byte TruePointer;
        public byte FalsePointer;
        public byte[] Operand;
        public bool Breakpoint; //only used at runtime
    }
}
