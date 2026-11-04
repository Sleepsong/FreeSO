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
                    var before = obj.Position;
                    var beforeDir = obj.Direction;
                    var found = FindLocationVector(obj, refObj, context.VM.Context, intDir, flags, operand.PreferNonEmpty, attempts);
                    if (operand.Mode == 3 && IsCar(obj))
                        found = SettleCar(obj, refObj, context.VM.Context, intDir, flags, found, before, beforeDir, attempts);
                    return found ? VMPrimitiveExitCode.GOTO_TRUE : VMPrimitiveExitCode.GOTO_FALSE;
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
            return SearchVector(obj, refObj, context, dir, flags, preferNonEmpty, attempts, new LotTilePos());
        }

        /// <summary>A car, bus or limo: a multitile object of 12 tiles or more.</summary>
        private static bool IsCar(VMEntity obj)
        {
            return (obj?.MultitileGroup?.Objects.Count ?? 0) >= 12;
        }

        /// <summary>A car placed this many tiles or fewer from its portal is left where the search put it.</summary>
        private const int NearPortal = 3;
        /// <summary>How far beside and along the portal's line <see cref="SettleCar"/> looks, in tiles.</summary>
        private const int LaneReach = 12;

        //which way a car drives: north is -y (a car's lead tile is its front; DirectionVectors above points behind)
        private static LotTilePos[] Forward = {
            new LotTilePos(0, -16, 0), //NORTH
            new LotTilePos(16, 0, 0), //EAST
            new LotTilePos(0, 16, 0), //SOUTH
            new LotTilePos(-16, 0, 0), //WEST
        };

        //spots beside the portal's line, nearest first: (tiles across, tiles along)
        private static readonly List<Tuple<int, int>> LaneOrder = Enumerable.Range(-LaneReach, LaneReach * 2 + 1)
            .SelectMany(k => Enumerable.Range(-LaneReach, LaneReach * 2 + 1).Select(j => Tuple.Create(k, j)))
            .Where(x => x.Item1 != 0)
            .OrderBy(x => Math.Abs(x.Item1) + Math.Abs(x.Item2)).ThenBy(x => Math.Abs(x.Item1)).ThenBy(x => x.Item2).ThenBy(x => x.Item1)
            .ToList();

        /// <summary>
        /// A car leaves a TS1 lot by driving forward until it's off the lot: "drive away" snaps it ahead along its
        /// facing, and when something's in the way it honks and tries again, for up to two game hours, before it
        /// vanishes. So a car is only any use where it can drive off.
        ///
        /// Studio Town lot 85's car portal is inside the spa building, facing north. The only spot on its line that
        /// fits a car is a pull-in 12 tiles behind it, nose to the building's front, so every car stopped there
        /// (drop-off cars, and the cab home) sat honking until it gave up, and the next car had nowhere to go. The
        /// street is 6 tiles to the side. How the original game gets cars there isn't known; this gets them there.
        ///
        /// A car the search along the line put more than <see cref="NearPortal"/> tiles from its portal, with something
        /// other than a Sim or another car between it and the lot's edge, or that it couldn't place at all, goes to the
        /// nearest spot on a line beside the portal's (same facing) that it can drive off from. Failing that it stays
        /// where it was put, or isn't placed. Cars placed by their portal, as on every other lot seen so far, are
        /// left alone.
        /// </summary>
        private static bool SettleCar(VMEntity obj, VMEntity refObj, VMContext context, int dir, VMPlaceRequestFlags flags,
            bool found, LotTilePos before, Direction beforeDir, List<string> attempts)
        {
            if (dir % 2 != 0) return found;
            var facing = (Direction)(1 << dir);
            LotTilePos? spot = null;
            if (found)
            {
                spot = obj.Position;
                var distance = TileDistance(obj.Position, refObj.Position);
                if (distance <= NearPortal) return true;
                var blocker = DriveBlocker(obj, obj.Position, facing, context, flags);
                if (blocker == null) return true;
                attempts?.Add($"note: the spot on the line is {distance} tiles from {refObj}, and a car there can't drive off the lot ({blocker} is ahead); looking beside the line");
            }
            else attempts?.Add("note: nothing on the line fits; looking beside it");

            var step = DirectionVectors[dir];
            var lateral = new LotTilePos(step.y, (short)-step.x, 0);
            var origin = refObj.Position;
            foreach (var lane in LaneOrder)
            {
                var pos = new LotTilePos((short)(origin.x + lateral.x * lane.Item1 + step.x * lane.Item2),
                    (short)(origin.y + lateral.y * lane.Item1 + step.y * lane.Item2), origin.Level);
                if (context.IsOutOfBounds(pos)) continue;
                if (!Try(obj, pos, facing, context, flags, attempts)) continue;
                var blocker = DriveBlocker(obj, pos, facing, context, flags);
                if (blocker == null)
                {
                    attempts?.Add($"note: placed {Math.Abs(lane.Item1)} tiles to the side of the line, {Math.Abs(lane.Item2)} along it, where a car can drive off the lot");
                    return true;
                }
                if (attempts != null) attempts[attempts.Count - 1] += $", but {blocker} is ahead";
            }

            //nothing beside the line either: back to where the search left it
            if (spot != null)
            {
                obj.SetPosition(spot.Value, facing, context, flags);
                attempts?.Add("note: no spot beside the line can drive off either; kept the one on it");
                return true;
            }
            obj.SetPosition(before, beforeDir, context, flags);
            return false;
        }

        private static int TileDistance(LotTilePos a, LotTilePos b)
        {
            return Math.Max(Math.Abs(a.TileX - b.TileX), Math.Abs(a.TileY - b.TileY));
        }

        /// <summary>
        /// What would stop a car at this spot driving forward off the lot, or null if nothing would. Sims (they're
        /// shooed) and other cars (they leave) don't count. Checks without moving the car.
        /// </summary>
        public static string DriveBlocker(VMEntity obj, LotTilePos pos, Direction facing, VMContext context, VMPlaceRequestFlags flags)
        {
            int dir;
            switch (facing)
            {
                case Direction.NORTH: dir = 0; break;
                case Direction.EAST: dir = 2; break;
                case Direction.SOUTH: dir = 4; break;
                case Direction.WEST: dir = 6; break;
                default: return null;
            }
            var group = obj.MultitileGroup;
            if (group == null || group.Objects.Count == 0) return null;
            var rotation = Microsoft.Xna.Framework.Matrix.CreateRotationZ((float)(dir * Math.PI / 4.0));
            var lead = group.Offsets[Math.Max(0, group.Objects.IndexOf(group.BaseObject))];
            var parts = new Microsoft.Xna.Framework.Vector3[group.Objects.Count];
            for (int i = 0; i < parts.Length; i++)
                parts[i] = Microsoft.Xna.Framework.Vector3.Transform(
                    new Microsoft.Xna.Framework.Vector3(group.Offsets[i].x - lead.x, group.Offsets[i].y - lead.y, 0), rotation);

            flags &= ~VMPlaceRequestFlags.UserBuildableLimit; //"drive away" goes onto the street
            var forward = Forward[dir / 2];
            var steps = Math.Max(context.Architecture.Width, context.Architecture.Height) + 8;
            for (int s = 1; s <= steps; s++)
            {
                var ahead = new LotTilePos((short)(pos.x + forward.x * s), (short)(pos.y + forward.y * s), pos.Level);
                for (int i = 0; i < parts.Length; i++)
                {
                    var part = group.Objects[i];
                    var at = new LotTilePos((short)Math.Round(ahead.x + parts[i].X), (short)Math.Round(ahead.y + parts[i].Y),
                        (sbyte)(ahead.Level + group.Offsets[i].Level));
                    if (context.IsOutOfBounds(at)) return null; //off the lot: "drive away" is done
                    var result = part.PositionValid(at, facing, context, flags);
                    if (result.Status == VMPlacementError.Success) continue;
                    if (result.Status == VMPlacementError.LocationOutOfBounds) return null;
                    var other = result.Object;
                    if (other is VMAvatar || IsCar(other)) continue;
                    return (other != null) ? $"{other} at ({at.TileX},{at.TileY})" : $"{result.Status} at ({at.TileX},{at.TileY})";
                }
            }
            return null;
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
