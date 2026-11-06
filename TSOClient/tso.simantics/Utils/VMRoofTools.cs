using FSO.LotView.Model;
using FSO.SimAntics.Model;
using System;
using System.Linq;

namespace FSO.SimAntics.Utils
{
    /// <summary>
    /// The roof tool's build commands (as in The Sims 2): drawing a roof (ROOF_RECT), removing one (ROOF_ERASE) and
    /// going back to the game's automatic roof (ROOF_AUTO). They change VMArchitecture.Roofs, which is the real roofs
    /// or, while a tool previews a command, a copy of them.
    /// </summary>
    public static class VMRoofTools
    {
        /// <summary>Price of a drawn roof, per tile it covers. Removing a roof gives it back.</summary>
        public const int TilePrice = 10;
        /// <summary>The most drawn roofs a lot can have.</summary>
        public const int MaxPieces = 200;
        /// <summary>The longest side of one drawn roof, in tiles.</summary>
        public const int MaxSide = 48;

        //VMArchitecture.LastFailReason values (above the other tools')
        public const int FailOutside = 40;
        public const int FailNoRoof = 41;
        public const int FailTooMany = 42;
        public const int FailLevel = 43;

        /// <summary>The cost of a command (negative gives money back), 0 if it does nothing.</summary>
        public static int Run(VMArchitecture arch, VMArchitectureCommand com)
        {
            int fail;
            bool changed;
            var cost = Apply(arch.Roofs, arch.Width, arch.Height, arch.Stories, com, out fail, out changed);
            if (fail != 0) arch.LastFailReason = fail;
            if (changed) arch.SignalRoofChange();
            return cost;
        }

        /// <summary>
        /// Applies a roof command to <paramref name="roofs"/> on a lot of the given size: its cost (negative gives money
        /// back), why it did nothing (fail), and whether the roofs changed.
        /// </summary>
        public static int Apply(DrawnRoofs roofs, int width, int height, int stories, VMArchitectureCommand com, out int fail, out bool changed)
        {
            fail = 0;
            changed = false;
            switch (com.Type)
            {
                case VMArchitectureCommandType.ROOF_RECT:
                    {
                        //x, y: the tile at one corner. x2, y2: width - 1, height - 1. level: the roof's level (2 sits on the
                        //ground floor's walls). pattern: RoofPieceType. style: direction (low byte), pitch percent (high byte)
                        var piece = new RoofPiece
                        {
                            Level = com.level,
                            X = (short)com.x,
                            Y = (short)com.y,
                            Width = (short)(com.x2 + 1),
                            Height = (short)(com.y2 + 1),
                            Type = (RoofPieceType)Math.Min((int)com.pattern, (int)RoofPieceType.Shed),
                            Direction = (byte)((com.style & 0xFF) % 4),
                            Pitch = ((com.style >> 8) == 0) ? RoofPiece.DefaultPitch : (byte)Math.Max(10, Math.Min(200, com.style >> 8)),
                        };
                        if (piece.Level < 2 || piece.Level > stories + 1) { fail = FailLevel; return 0; }
                        if (piece.Width < 1 || piece.Height < 1 || piece.Width > MaxSide || piece.Height > MaxSide
                            || piece.X < 1 || piece.Y < 1 || piece.X + piece.Width > width - 1 || piece.Y + piece.Height > height - 1)
                        {
                            fail = FailOutside;
                            return 0;
                        }
                        if (roofs.Pieces.Count >= MaxPieces) { fail = FailTooMany; return 0; }
                        if (roofs.Pieces.Contains(piece)) return 0;
                        roofs.Pieces.Add(piece);
                        roofs.AutoRoof = false;
                        changed = true;
                        return piece.Tiles * TilePrice;
                    }
                case VMArchitectureCommandType.ROOF_ERASE:
                    {
                        //x, y: a tile. level: the roof's level. Removes the newest roof there.
                        for (int i = roofs.Pieces.Count - 1; i >= 0; i--)
                        {
                            var piece = roofs.Pieces[i];
                            if (piece.Level != com.level || !piece.Contains(com.x, com.y)) continue;
                            roofs.Pieces.RemoveAt(i);
                            changed = true;
                            return -piece.Tiles * TilePrice;
                        }
                        fail = FailNoRoof;
                        return 0;
                    }
                case VMArchitectureCommandType.ROOF_AUTO:
                    {
                        //style 1: back to the automatic roof (drawn roofs are removed and paid back). style 0: no automatic roof.
                        var auto = com.style != 0;
                        if (auto == roofs.AutoRoof && (!auto || roofs.Pieces.Count == 0)) return 0;
                        var refund = auto ? roofs.Pieces.Sum(x => x.Tiles) * TilePrice : 0;
                        if (auto) roofs.Pieces.Clear();
                        roofs.AutoRoof = auto;
                        changed = true;
                        return -refund;
                    }
            }
            return 0;
        }
    }
}
