using FSO.Files.Utils;
using System.IO;

namespace FSO.Files.Formats.IFF.Chunks
{
    public class HOUS : IffChunk
    {
        public int Version;
        public int UnknownFlag;
        public int UnknownOne;
        public int UnknownNumber;
        public int UnknownNegative;
        public short CameraDir;
        public short UnknownOne2;
        public short UnknownFlag2;
        public float RoofPitch; //0 flat, 0.4 shallow, 0.6 medium, 1 steep
        public string RoofName;

        public override void Read(IffFile iff, Stream stream)
        {
            using (var io = IoBuffer.FromStream(stream, ByteOrder.LITTLE_ENDIAN))
            {
                var zero = io.ReadInt32();
                Version = io.ReadInt32();
                var suoh = io.ReadCString(4);
                UnknownFlag = io.ReadInt32();
                UnknownOne = io.ReadInt32();
                UnknownNumber = io.ReadInt32();
                UnknownNegative = io.ReadInt32();
                CameraDir = io.ReadInt16();
                UnknownOne2 = io.ReadInt16();
                UnknownFlag2 = io.ReadInt16();
                RoofPitch = io.ReadFloat();
                RoofName = io.ReadNullTerminatedString();
            }
        }
    }
}
