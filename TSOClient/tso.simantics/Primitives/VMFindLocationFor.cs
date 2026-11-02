using System;
using System.Collections.Generic;
using System.Linq;
using FSO.SimAntics.Engine;
using FSO.Files.Utils;
using FSO.SimAntics.Model;
using FSO.LotView.Model;
using System.IO;

namespace FSO.SimAntics.Primitives
{
    public class VMFindLocationFor : VMPrimitiveHandler
    {
        private static LotTilePos[] DirectionVectors = {
            new LotTilePos(0, 16, 0),
            new LotTilePos(-16, 16, 0),
            new LotTilePos(-16, 0, 0),
            new LotTilePos(-16, -16, 0),
            new LotTilePos(0, -16, 0),
            new LotTilePos(16, -16, 0),
            new LotTilePos(16, 0, 0),
            new LotTilePos(16, 16, 0),
        };

        public override VMPrimitiveExitCode Execute(VMStackFrame context, VMPrimitiveOperand args)
        {
            var operand = (VMFindLocationForOperand)args;
            var refObj = (operand.UseLocalAsRef) ? context.VM.GetObjectById((short)context.Locals[operand.Local]) : context.Caller;

            var obj = context.StackObject;
            var flags = VMPlaceRequestFlags.AcceptSlots;
            if (operand.UserEditableTilesOnly) flags |= VMPlaceRequestFlags.UserBuildableLimit;

            //the travel log records every spot tried, and why it was refused, for a script it's following
            var attempts = (VMTravelLog.Enabled && VMTravelLog.Following == context.Thread) ? new List<string>() : null;
            var result = Find(context, operand, obj, refObj, flags, attempts);
            if (attempts != null) VMTravelLog.Placement(context, obj, refObj, operand.Mode, flags, result, attempts);
            return result;
        }

        private VMPrimitiveExitCode Find(VMStackFrame context, VMFindLocationForOperand operand, VMEntity obj, VMEntity refObj, VMPlaceRequestFlags flags, List<string> attempts)
        {

            switch (operand.Mode)
            {
                case 0:
                    //default
                    if (FindLocationFor(obj, refObj, context.VM.Context, flags, operand.PreferNonEmpty, attempts)) return VMPrimitiveExitCode.GOTO_TRUE;
                    else return VMPrimitiveExitCode.GOTO_FALSE;
                case 1:
                    //out of world
                    obj.SetPosition(LotTilePos.OUT_OF_WORLD, Direction.NORTH, context.VM.Context, flags);
                    return VMPrimitiveExitCode.GOTO_TRUE;
                case 2:
                    //"smoke cloud" - halfway between callee and caller (is "caller" actually reference object?)
                    var smokePos = context.Callee.Position;
                    smokePos += context.Caller.Position;
                    smokePos /= 2;
                    smokePos -= new LotTilePos(8, 8, 0); //smoke is 2x2... offset to center it.
                    return (obj.SetPosition(smokePos, Direction.NORTH, context.VM.Context).Status == VMPlacementError.Success)?
                        VMPrimitiveExitCode.GOTO_TRUE : VMPrimitiveExitCode.GOTO_FALSE;
                case 3:
                case 4:
                    //along object vector
                    var intDir = (int)Math.Round(Math.Log((double)refObj.Direction, 2));
                    if (operand.Mode == 4) intDir = (intDir + 2) % 8; //lateral to object vector
                    if (FindLocationVector(obj, refObj, context.VM.Context, intDir, flags, operand.PreferNonEmpty, attempts)) return VMPrimitiveExitCode.GOTO_TRUE;
                    else return VMPrimitiveExitCode.GOTO_FALSE;
                case 5:
                    //random
                    var ctx = context.VM.Context;
                    for (int i=0; i<100; i++)
                    {
                        var loc = LotTilePos.FromBigTile((short)(ctx.NextRandom((ulong)ctx.Architecture.Width - 2) + 1), (short)(ctx.NextRandom((ulong)ctx.Architecture.Height - 2) + 1), refObj.Position.Level);
                        if (obj.SetPosition(loc, Direction.NORTH, ctx, flags).Status == VMPlacementError.Success)
                            return VMPrimitiveExitCode.GOTO_TRUE;
                    }
                    return VMPrimitiveExitCode.GOTO_FALSE;
            }

            return VMPrimitiveExitCode.GOTO_FALSE;
        }

        private static bool TileOccupied(VMContext context, LotTilePos pos)
        {
            return (context.ObjectQueries.GetObjectsAt(pos)?.Count ?? 0) > 0;
        }

        /// <summary>Places the object, noting the outcome for the travel log when it's following the script.</summary>
        private static bool Try(VMEntity obj, LotTilePos pos, Direction dir, VMContext context, VMPlaceRequestFlags flags, List<string> attempts)
        {
            var placed = obj.SetPosition(pos, dir, context, flags);
            attempts?.Add($"({pos.x / 16f:0.##},{pos.y / 16f:0.##},{pos.Level}) {dir}: {placed.Status}" + ((placed.Object != null && placed.Status != VMPlacementError.Success) ? $" [{placed.Object}]" : ""));
            if (attempts != null && placed.Object != null && placed.Status != VMPlacementError.Success) VMTravelLog.NoteObstacle(placed.Object);
            return placed.Status == VMPlacementError.Success;
        }

        public static bool FindLocationVector(VMEntity obj, VMEntity refObj, VMContext context, int dir, VMPlaceRequestFlags flags, bool preferNonEmpty = false, List<string> attempts = null)
        {
            if (SearchVector(obj, refObj, context, dir, flags, preferNonEmpty, attempts, new LotTilePos())) return true;

            //Fallback: an object several tiles wide sits to one side of the vector (its parts are offset from its lead
            //tile), so on a lane exactly its width it hits whatever lines the lane. Studio Town's cab home (3 tiles wide,
            //placed along the car portal's facing) hit the spa tubs on one side of the drive every time and deleted
            //itself. Try again with the object centred on the vector, then shifted a tile either way. Placements that
            //already worked are unchanged: this only runs when every spot above was refused.
            LotTilePos step = DirectionVectors[dir];
            var lateral = new LotTilePos(step.y, (short)-step.x, 0);
            var centre = LateralCentre(obj, (Direction)(1 << dir), lateral);
            var shifts = new List<int> { -centre, -16, 16 }.Where(x => x != 0).Distinct().ToList();
            foreach (var shift in shifts)
            {
                attempts?.Add($"(shifted {shift / 16f:0.##} tiles sideways)");
                var offset = new LotTilePos((short)(lateral.x / 16 * shift), (short)(lateral.y / 16 * shift), 0);
                if (SearchVector(obj, refObj, context, dir, flags, preferNonEmpty, attempts, offset)) return true;
            }
            return false;
        }

        /// <summary>Where the middle of a multitile object's parts is across the vector, from its lead tile (in 1/16 tiles).</summary>
        private static int LateralCentre(VMEntity obj, Direction direction, LotTilePos lateral)
        {
            var group = obj.MultitileGroup;
            if (group == null || group.Objects.Count < 2 || (lateral.x == 0 && lateral.y == 0)) return 0;
            int dir = 0;
            switch (direction)
            {
                case Direction.EAST: dir = 2; break;
                case Direction.SOUTH: dir = 4; break;
                case Direction.WEST: dir = 6; break;
            }
            var rotation = Microsoft.Xna.Framework.Matrix.CreateRotationZ((float)(dir * Math.PI / 4.0));
            var lead = group.Offsets[Math.Max(0, group.Objects.IndexOf(group.BaseObject))];
            var lengthSquared = lateral.x * lateral.x + lateral.y * lateral.y;
            float min = float.MaxValue, max = float.MinValue;
            foreach (var offset in group.Offsets)
            {
                var off = Microsoft.Xna.Framework.Vector3.Transform(new Microsoft.Xna.Framework.Vector3(offset.x - lead.x, offset.y - lead.y, 0), rotation);
                var across = (off.X * lateral.x + off.Y * lateral.y) * 16f / lengthSquared;
                min = Math.Min(min, across);
                max = Math.Max(max, across);
            }
            return (int)Math.Round((min + max) / 2f);
        }

        private static bool SearchVector(VMEntity obj, VMEntity refObj, VMContext context, int dir, VMPlaceRequestFlags flags, bool preferNonEmpty, List<string> attempts, LotTilePos offset)
        {
            LotTilePos step = DirectionVectors[dir];
            var dirf = (Direction)(1 << (dir));
            var deferred = new List<LotTilePos>();
            var origin = new LotTilePos(refObj.Position) + offset;

            Func<LotTilePos, bool> evaluate = (LotTilePos pos) =>
            {
                if (preferNonEmpty && TileOccupied(context, pos))
                {
                    deferred.Add(pos);
                    return false;
                }
                else
                {
                    return Try(obj, pos, dirf, context, flags, attempts);
                }
            };

            for (int i = 0; i < 32; i++)
            {
                if (evaluate(origin + step * (i / 2))) return true;
                if (i%2 != 0)
                {
                    if (evaluate(origin - step * (i / 2))) return true;
                }
            }

            foreach (var tile in deferred)
            {
                if (Try(obj, tile, dirf, context, flags, attempts)) return true;
            }
            return false;
        }

        public static bool FindLocationFor(VMEntity obj, VMEntity refObj, VMContext context, VMPlaceRequestFlags flags, bool preferNonEmpty = false, List<string> attempts = null)
        {
            var deferred = new List<Tuple<LotTilePos, Direction>>();

            Func<LotTilePos, Direction, bool> evaluate = (LotTilePos pos, Direction dir) =>
            {
                if (preferNonEmpty && TileOccupied(context, pos))
                {
                    deferred.Add(new Tuple<LotTilePos, Direction>(pos, dir));
                    return false;
                }
                else
                {
                    return Try(obj, pos, dir, context, flags, attempts);
                }
            };

            for (int i = 0; i < 10; i++)
            {
                if (i == 0)
                {
                    var pos = new LotTilePos(refObj.Position);
                    for (int j = 0; j < 4; j++)
                    {
                        var dir = (Direction)(1 << (j * 2));
                        if (evaluate(pos, dir)) return true;
                    }
                }
                else
                {
                    LotTilePos bPos = refObj.Position;
                    for (int x = -i; x <= i; x++)
                    {
                        for (int j = 0; j < 8; j++)
                        {
                            var pos = LotTilePos.FromBigTile((short)(bPos.TileX + x), (short)(bPos.TileY + ((j % 2) * 2 - 1) * i), bPos.Level);
                            var dir = (Direction)(1 << ((j / 2) * 2));
                            if (evaluate(pos, dir)) return true;
                        }
                    }

                    for (int y = 1 - i; y < i; y++)
                    {
                        for (int j = 0; j < 8; j++)
                        {
                            var pos = LotTilePos.FromBigTile((short)(bPos.TileX + ((j % 2) * 2 - 1) * i), (short)(bPos.TileY + y), bPos.Level);
                            var dir = (Direction)(1 << ((j / 2) * 2));
                            if (evaluate(pos, dir)) return true;
                        }
                    }
                }
            }

            foreach (var tile in deferred)
            {
                if (Try(obj, tile.Item1, tile.Item2, context, flags, attempts)) return true;
            }
            return false;
        }
    }

    public class VMFindLocationForOperand : VMPrimitiveOperand
    {
        public byte Mode { get; set; }
        public byte Local { get; set; }
        public byte Flags { get; set; }

        #region VMPrimitiveOperand Members
        public void Read(byte[] bytes)
        {
            using (var io = IoBuffer.FromBytes(bytes, ByteOrder.LITTLE_ENDIAN))
            {
                Mode = io.ReadByte();
                Local = io.ReadByte();
                Flags = io.ReadByte();
            }
        }

        public void Write(byte[] bytes) {
            using (var io = new BinaryWriter(new MemoryStream(bytes)))
            {
                io.Write(Mode);
                io.Write(Local);
                io.Write(Flags);
            }
        }
        #endregion

        public bool UseLocalAsRef
        {
            get
            {
                return (Flags & 1) == 1;
            }
            set
            {
                if (value) Flags |= 1;
                else Flags &= unchecked((byte)~1);
            }
        }

        public bool PreferNonEmpty
        {
            get
            {
                return (Flags & 2) == 0;
            }
            set
            {
                if (!value) Flags |= 2;
                else Flags &= unchecked((byte)~2);
            }
        }

        public bool UserEditableTilesOnly
        {
            get
            {
                return (Flags & 4) == 4;
            }
            set
            {
                if (value) Flags |= 4;
                else Flags &= unchecked((byte)~4);
            }
        }
    }
}
