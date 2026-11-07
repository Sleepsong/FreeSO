using FSO.Content;
using FSO.Content.Model;
using FSO.Files.Formats.IFF.Chunks;
using FSO.LotView.Model;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace FSO.LotView.Components.Geometry
{
    /// <summary>What a part of a foundation or deck is made of (see <see cref="FoundationLook"/>).</summary>
    public enum FoundationMaterial : byte
    {
        /// <summary>A foundation's sides: a wall covering (its pattern; 0 for <see cref="FoundationLook.BrickWall"/>).</summary>
        Covering,
        /// <summary>A foundation's top where nothing is laid: plywood.</summary>
        Plywood,
        /// <summary>The dark wood round a foundation's top edge, and its corners.</summary>
        Trim,
        /// <summary>A deck's planks: its top, the board round its edge, its underside.</summary>
        Planks,
        /// <summary>The lattice under a latticed deck.</summary>
        Lattice,
        /// <summary>A column deck's columns.</summary>
        Column,
        /// <summary>Plain steps, when the game has no staircase to take a set of steps from.</summary>
        Steps
    }

    /// <summary>
    /// The game's own textures that foundations and decks are made from, as in The Sims 2's: found by name among the
    /// walls and floors the game has (brick, plywood, dark wood, deck planks, lattice, concrete), so whatever the game
    /// (and its expansion packs) installed, the closest it has. <see cref="Describe"/> says what was picked, for the logs.
    /// </summary>
    public static class FoundationLook
    {
        private static Dictionary<string, ushort> FloorCache = new Dictionary<string, ushort>();
        private static ushort? Brick;
        private static ushort? LatticeWall;

        private static readonly string[] PlywoodWords = { "plywood", "particle", "subfloor", "sub-floor", "sub floor", "unfinished", "pine", "birch", "maple", "light wood", "oak", "wood" };
        private static readonly string[] TrimWords = { "walnut", "mahogany", "dark wood", "ebony", "cherry", "teak", "dark", "wood" };
        private static readonly string[] DeckWords = { "deck", "redwood", "cedar", "boardwalk", "plank", "teak", "wood" };
        private static readonly string[] ColumnWords = { "concrete", "cement", "stone" };

        private static List<FloorReference> Floors()
        {
            return Content.Content.Get().WorldFloors.List().OfType<FloorReference>().Where(f => f.ID > 0 && f.ID < 65503).ToList();
        }

        private static List<WallReference> Walls()
        {
            return Content.Content.Get().WorldWalls.List().OfType<WallReference>().Where(w => w.ID != 0).ToList();
        }

        private static bool Has(string name, string word) => (name ?? "").IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>The first floor (by the order of <paramref name="words"/>) whose name has one of them; 0 if none.</summary>
        private static ushort FindFloor(string key, string[] words)
        {
            lock (FloorCache)
            {
                if (FloorCache.TryGetValue(key, out var cached)) return cached;
                ushort found = 0;
                try
                {
                    var floors = Floors();
                    foreach (var word in words)
                    {
                        var match = floors.FirstOrDefault(f => Has(f.Name, word));
                        if (match != null) { found = (ushort)match.ID; break; }
                    }
                    if (found == 0) found = (ushort)(floors.FirstOrDefault()?.ID ?? 0);
                }
                catch (Exception) { found = 0; }
                FloorCache[key] = found;
                return found;
            }
        }

        /// <summary>A foundation's sides when they aren't painted: a brick wall covering (or stone, or the first).</summary>
        public static ushort BrickWall()
        {
            if (Brick != null) return Brick.Value;
            ushort found = 0;
            try
            {
                var walls = Walls();
                foreach (var word in new[] { "brick", "stone", "block" })
                {
                    var match = walls.FirstOrDefault(w => Has(w.Name, word));
                    if (match != null) { found = (ushort)match.ID; break; }
                }
                if (found == 0) found = (ushort)(walls.FirstOrDefault()?.ID ?? 0);
            }
            catch (Exception) { found = 0; }
            Brick = found;
            return found;
        }

        /// <summary>A lattice wall covering, if the game has one (0 if not: the lattice is then made from the deck's wood).</summary>
        public static ushort LatticeCovering()
        {
            if (LatticeWall != null) return LatticeWall.Value;
            ushort found = 0;
            try
            {
                found = (ushort)(Walls().FirstOrDefault(w => Has(w.Name, "lattice"))?.ID ?? 0);
            }
            catch (Exception) { found = 0; }
            LatticeWall = found;
            return found;
        }

        public static ushort PlywoodFloor() => FindFloor("plywood", PlywoodWords);
        public static ushort TrimFloor() => FindFloor("trim", TrimWords);
        public static ushort DeckFloor() => FindFloor("deck", DeckWords);
        public static ushort ColumnFloor() => FindFloor("column", ColumnWords);

        private static Texture2D FloorTexture(WorldState state, ushort id)
        {
            if (id == 0) return null;
            var near = Content.Content.Get().WorldFloors.Get(id)?.Near;
            return (near != null && near.Frames.Length > 0) ? state._2D.GetTexture(near.Frames[0]) : null;
        }

        private static Texture2D WallTexture(WorldState state, ushort id)
        {
            if (id == 0) return null;
            var frames = Content.Content.Get().WorldWalls.Get(id)?.Near?.Frames;
            return (frames != null && frames.Count > 2) ? state._2D.GetTexture(frames[2]) : null;
        }

        private static Texture2D GeneratedLattice;

        /// <summary>
        /// The texture for a part (needs the world's sprite cache), or null if the game has nothing for it. A wall
        /// covering's pattern is only for <see cref="FoundationMaterial.Covering"/>.
        /// </summary>
        public static Texture2D Texture(GraphicsDevice gd, WorldState state, FoundationMaterial material, ushort pattern)
        {
            switch (material)
            {
                case FoundationMaterial.Covering: return WallTexture(state, (pattern != 0) ? pattern : BrickWall());
                case FoundationMaterial.Plywood: return FloorTexture(state, PlywoodFloor());
                case FoundationMaterial.Trim: return FloorTexture(state, TrimFloor());
                case FoundationMaterial.Planks: return FloorTexture(state, DeckFloor());
                case FoundationMaterial.Column:
                case FoundationMaterial.Steps: return FloorTexture(state, ColumnFloor());
                case FoundationMaterial.Lattice:
                    var wall = LatticeCovering();
                    if (wall != 0) return WallTexture(state, wall);
                    if (GeneratedLattice == null || GeneratedLattice.IsDisposed)
                        GeneratedLattice = MakeLattice(gd, FloorTexture(state, TrimFloor()) ?? FloorTexture(state, DeckFloor()));
                    return GeneratedLattice;
            }
            return null;
        }

        /// <summary>
        /// Lattice made from a wood texture of the game's: strips of it crossing diagonally (four each way to a tile),
        /// with the dark under the deck showing between them.
        /// </summary>
        public static Texture2D MakeLattice(GraphicsDevice gd, Texture2D wood)
        {
            const int size = 128;
            Color[] woodData = null;
            int ww = 0, wh = 0;
            if (wood != null)
            {
                try
                {
                    ww = wood.Width; wh = wood.Height;
                    woodData = new Color[ww * wh];
                    wood.GetData(woodData);
                }
                catch (Exception) { woodData = null; }
            }
            var data = new Color[size * size];
            const float period = size / 4f; //four strips each way across a tile
            const float half = period * 0.17f; //each strip's half width
            var gap = new Color(24, 18, 12);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float a = Mod(x + y, period), b = Mod(x - y, period);
                    bool onA = Math.Abs(a - period / 2) < half, onB = Math.Abs(b - period / 2) < half;
                    if (!onA && !onB) { data[y * size + x] = gap; continue; }
                    var c = (woodData != null) ? woodData[(y * wh / size) * ww + (x * ww / size)] : new Color(110, 72, 40);
                    // the strip on top (B) a little lighter, its edges darker: wood laid over wood
                    float shade = onB ? 1f : 0.82f;
                    var edge = onB ? Math.Abs(b - period / 2) / half : Math.Abs(a - period / 2) / half;
                    if (edge > 0.75f) shade *= 0.8f;
                    data[y * size + x] = new Color((int)(c.R * shade), (int)(c.G * shade), (int)(c.B * shade), 255);
                }
            }
            var tex = new Texture2D(gd, size, size, false, SurfaceFormat.Color);
            tex.SetData(data);
            return tex;
        }

        private static float Mod(float a, float m) => ((a % m) + m) % m;

        private static Dictionary<uint, GameObject> StairPieces = new Dictionary<uint, GameObject>();
        private static Dictionary<uint, string> StairPieceNames = new Dictionary<uint, string>();

        /// <summary>
        /// The bottom piece of a staircase (its catalog GUID): the part named "... Low" (not its pad, stubs or middle
        /// parts), which rises a quarter of a storey over one tile, as foundation steps do. Null if it has none.
        /// </summary>
        public static GameObject StairPiece(uint staircase)
        {
            if (staircase == 0) return null;
            lock (StairPieces)
            {
                if (StairPieces.TryGetValue(staircase, out var cached)) return cached;
                GameObject found = null;
                try
                {
                    var master = Content.Content.Get().WorldObjects.Get(staircase);
                    var objds = master?.Resource?.List<OBJD>();
                    if (objds != null)
                    {
                        var low = objds.FirstOrDefault(o => IsLowPiece(o.ChunkLabel));
                        if (low != null)
                        {
                            found = Content.Content.Get().WorldObjects.Get(low.GUID);
                            StairPieceNames[staircase] = low.ChunkLabel;
                        }
                    }
                }
                catch (Exception) { found = null; }
                StairPieces[staircase] = found;
                return found;
            }
        }

        /// <summary>Whether a staircase part's name is its bottom piece's: "Stair - Low", not "Low Pad" or "Middle Low".</summary>
        public static bool IsLowPiece(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            var words = new HashSet<string>(name.ToLowerInvariant().Split(new[] { ' ', '-', '_', '(', ')', ',', '.', '/' }, StringSplitOptions.RemoveEmptyEntries));
            if (!words.Contains("low") && !words.Contains("lower") && !words.Contains("bottom")) return false;
            foreach (var not in new[] { "pad", "mid", "middle", "stub", "hi", "high", "top", "upper" })
                if (words.Contains(not)) return false;
            return true;
        }

        /// <summary>What foundations are made from in this game, and what else it has that could be, for the logs.</summary>
        public static string Describe(IEnumerable<uint> staircases)
        {
            var sb = new StringBuilder();
            try
            {
                var floors = Floors();
                var walls = Walls();
                Func<ushort, string> floorName = (id) => (id == 0) ? "(none)" : $"#{id} \"{floors.FirstOrDefault(f => f.ID == id)?.Name}\"";
                Func<ushort, string> wallName = (id) => (id == 0) ? "(none)" : $"#{id} \"{walls.FirstOrDefault(w => w.ID == id)?.Name}\"";
                sb.AppendLine("Foundations, made from the game's own walls and floors:");
                sb.AppendLine("  foundation sides (unpainted): wall " + wallName(BrickWall()));
                sb.AppendLine("  foundation top (bare): floor " + floorName(PlywoodFloor()));
                sb.AppendLine("  foundation trim: floor " + floorName(TrimFloor()));
                sb.AppendLine("  deck planks: floor " + floorName(DeckFloor()));
                sb.AppendLine("  lattice: " + ((LatticeCovering() != 0) ? "wall " + wallName(LatticeCovering()) : "made from the trim's wood"));
                sb.AppendLine("  columns, plain steps: floor " + floorName(ColumnFloor()));
                sb.AppendLine("Floors with wood in their name: " + string.Join(", ", floors.Where(f => new[] { "wood", "plank", "deck", "pine", "oak", "ply" }.Any(w => Has(f.Name, w))).Select(f => $"#{f.ID} \"{f.Name}\"")));
                sb.AppendLine("Walls with brick, stone or lattice in their name: " + string.Join(", ", walls.Where(w => new[] { "brick", "stone", "lattice" }.Any(x => Has(w.Name, x))).Select(w => $"#{w.ID} \"{w.Name}\"")));
                var styles = new List<string>();
                for (ushort i = 1; i < 512; i++)
                {
                    var style = Content.Content.Get().WorldWalls.GetWallStyle(i);
                    if (style != null) styles.Add($"#{i} \"{style.Name}\"");
                }
                sb.AppendLine("Wall styles (fences): " + string.Join(", ", styles));
            }
            catch (Exception e)
            {
                sb.AppendLine("(couldn't list them: " + e.Message + ")");
            }
            sb.AppendLine("Staircases, for foundation steps:");
            foreach (var guid in staircases ?? Enumerable.Empty<uint>())
            {
                var piece = StairPiece(guid);
                string parts = "";
                try
                {
                    var objds = Content.Content.Get().WorldObjects.Get(guid)?.Resource?.List<OBJD>();
                    if (objds != null) parts = string.Join(", ", objds.Select(o => $"\"{o.ChunkLabel}\""));
                }
                catch (Exception) { }
                sb.AppendLine($"  0x{guid:X8}: " + ((piece != null) ? $"bottom piece \"{StairPieceNames[guid]}\" (0x{piece.OBJ.GUID:X8})" : "no bottom piece found (plain steps)") + "; its parts: " + parts);
            }
            return sb.ToString();
        }
    }
}
