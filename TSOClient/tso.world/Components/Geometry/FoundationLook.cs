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
        /// <summary>Dark wood (the lattice is made from it).</summary>
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

        private static readonly string[] PlywoodWords = { "plywood", "particle", "subfloor", "unfinished", "pine", "birch", "maple", "light", "oak", "wood", "wooden", "hardwood", "planks", "plank" };
        private static readonly string[] TrimWords = { "walnut", "mahogany", "ebony", "cherry", "teak", "dark", "wood", "wooden", "hardwood" };
        private static readonly string[] DeckWords = { "deck", "decking", "redwood", "cedar", "boardwalk", "planks", "plank", "hardwood", "teak", "oak", "pine", "wood", "wooden", "parquet" };
        private static readonly string[] ColumnWords = { "concrete", "cement", "stone" };
        /// <summary>Words that make a floor anything but wood, whatever else is in its name ("Deck Tile", "Woodland Stone").</summary>
        private static readonly string[] NotWood = { "stone", "tile", "marble", "granite", "concrete", "cement", "slate", "brick", "carpet", "rug",
            "linoleum", "vinyl", "terrazzo", "cobble", "pebble", "mosaic", "checker", "metal", "steel", "glass", "grass", "sand", "dirt", "gravel" };

        private static List<FloorReference> Floors()
        {
            return Content.Content.Get().WorldFloors.List().OfType<FloorReference>().Where(f => f.ID > 0 && f.ID < 65503).ToList();
        }

        private static List<WallReference> Walls()
        {
            return Content.Content.Get().WorldWalls.List().OfType<WallReference>().Where(w => w.ID != 0).ToList();
        }

        private static bool Has(string name, string word) => (name ?? "").IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>Whether a name has a whole word ("Oak Planks" has "oak"; "Soaked Tile" doesn't).</summary>
        private static bool HasWord(string name, string word) => Words(name).Contains(word);

        /// <summary>What wood of each use looks like: brown, and how light (plywood light, trim dark, planks between).</summary>
        private struct WoodTone { public float MinValue, MaxValue; }
        private static readonly WoodTone AnyWood = new WoodTone { MinValue = 0.2f, MaxValue = 0.9f };

        /// <summary>
        /// A floor's average colour (hue in degrees, saturation and value 0-1), from its near picture's pixels, or null if
        /// it can't be read.
        /// </summary>
        public static Vector3? AverageHSV(ushort floor)
        {
            try
            {
                var near = Content.Content.Get().WorldFloors.Get(floor)?.Near;
                if (near == null || near.Frames.Length == 0) return null;
                var frame = near.Frames[0];
                frame.DecodeIfRequired(false);
                var px = frame.PixelData;
                if (px == null || px.Length == 0) return null;
                long r = 0, g = 0, b = 0, n = 0;
                for (int i = 0; i < px.Length; i += 3)
                {
                    if (px[i].A < 128) continue;
                    r += px[i].R; g += px[i].G; b += px[i].B; n++;
                }
                if (n == 0) return null;
                float rf = r / (255f * n), gf = g / (255f * n), bf = b / (255f * n);
                float max = Math.Max(rf, Math.Max(gf, bf)), min = Math.Min(rf, Math.Min(gf, bf));
                float hue = 0;
                if (max > min)
                {
                    if (max == rf) hue = 60 * (((gf - bf) / (max - min)) % 6);
                    else if (max == gf) hue = 60 * ((bf - rf) / (max - min) + 2);
                    else hue = 60 * ((rf - gf) / (max - min) + 4);
                }
                if (hue < 0) hue += 360;
                return new Vector3(hue, (max == 0) ? 0 : (max - min) / max, max);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Whether a floor looks like wood (brown, as light as asked), by its colour.</summary>
        private static bool LooksLikeWood(ushort floor, WoodTone tone)
        {
            var hsv = AverageHSV(floor);
            if (hsv == null) return true; //(can't tell: trust its name)
            var c = hsv.Value;
            return c.X >= 12 && c.X <= 50 && c.Y >= 0.18f && c.Z >= tone.MinValue && c.Z <= tone.MaxValue;
        }

        /// <summary>
        /// The first floor (by the order of <paramref name="words"/>) whose name has one of them as a whole word; 0 if
        /// none. Wood (<paramref name="tone"/> given) has to look like it too: floors whose names say they're something
        /// else are left out, and so are ones that aren't brown (or are lighter or darker than asked). With no name to
        /// go by, the floor that looks most like that wood.
        /// </summary>
        private static ushort FindFloor(string key, string[] words, WoodTone? tone = null)
        {
            lock (FloorCache)
            {
                if (FloorCache.TryGetValue(key, out var cached)) return cached;
                ushort found = 0;
                try
                {
                    var floors = Floors();
                    if (tone != null) floors = floors.Where(f => !NotWood.Any(w => HasWord(f.Name, w))).ToList();
                    foreach (var word in words)
                    {
                        var match = floors.FirstOrDefault(f => HasWord(f.Name, word) && (tone == null || LooksLikeWood((ushort)f.ID, tone.Value)));
                        if (match != null) { found = (ushort)match.ID; break; }
                    }
                    if (found == 0 && tone != null)
                    {
                        // nothing by name: the most wood-like colour, nearest the middle of the lightness asked for
                        var mid = (tone.Value.MinValue + tone.Value.MaxValue) / 2;
                        var best = floors.Where(f => LooksLikeWood((ushort)f.ID, tone.Value) && AverageHSV((ushort)f.ID) != null)
                            .OrderBy(f => Math.Abs(AverageHSV((ushort)f.ID).Value.Z - mid)).FirstOrDefault();
                        if (best != null) found = (ushort)best.ID;
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
                found = (ushort)(Walls().FirstOrDefault(w => HasWord(w.Name, "lattice"))?.ID ?? 0);
            }
            catch (Exception) { found = 0; }
            LatticeWall = found;
            return found;
        }

        public static ushort PlywoodFloor() => FindFloor("plywood", PlywoodWords, new WoodTone { MinValue = 0.5f, MaxValue = 0.95f });
        public static ushort TrimFloor() => FindFloor("trim", TrimWords, new WoodTone { MinValue = 0.15f, MaxValue = 0.6f });
        public static ushort DeckFloor() => FindFloor("deck", DeckWords, new WoodTone { MinValue = 0.3f, MaxValue = 0.8f });
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
                        GeneratedLattice = MakeLattice(gd, FloorTexture(state, DeckFloor()) ?? FloorTexture(state, TrimFloor()));
                    return GeneratedLattice;
            }
            return null;
        }

        /// <summary>
        /// Lattice made from a wood texture of the game's (the deck's own planks, so it matches): strips of it crossing
        /// diagonally (four each way to a tile), with the dark under the deck showing between them.
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

        private static bool? AlongU;

        /// <summary>
        /// Whether the deck planks' picture has its planks running across it (along u): colour changes more from row to
        /// row than along a row. A deck's board takes one plank lengthways either way.
        /// </summary>
        public static bool PlanksAlongU()
        {
            if (AlongU != null) return AlongU.Value;
            bool result = true;
            try
            {
                var near = Content.Content.Get().WorldFloors.Get(DeckFloor())?.Near;
                if (near != null && near.Frames.Length > 0)
                {
                    var frame = near.Frames[0];
                    frame.DecodeIfRequired(false);
                    var px = frame.PixelData;
                    int w = frame.Width, h = frame.Height;
                    if (px != null && px.Length >= w * h && w > 1 && h > 1)
                    {
                        double alongRows = 0, acrossRows = 0;
                        Func<Color, Color, int> diff = (c1, c2) => Math.Abs(c1.R - c2.R) + Math.Abs(c1.G - c2.G) + Math.Abs(c1.B - c2.B);
                        for (int y = 0; y < h - 1; y++)
                            for (int x = 0; x < w - 1; x++)
                            {
                                alongRows += diff(px[y * w + x], px[y * w + x + 1]);
                                acrossRows += diff(px[y * w + x], px[(y + 1) * w + x]);
                            }
                        result = acrossRows >= alongRows;
                    }
                }
            }
            catch (Exception) { result = true; }
            AlongU = result;
            return result;
        }

        /// <summary>A straight staircase's bottom piece, and which way the staircase rises (in its own frame, facing north).</summary>
        public class Stair
        {
            public GameObject Piece;
            /// <summary>The direction (see <see cref="FoundationMap.Directions"/>) the staircase rises toward, facing north.</summary>
            public int Rise;
            public string PieceName;

            /// <summary>The facing that makes it rise toward <paramref name="stepDirection"/>.</summary>
            public Direction FacingFor(int stepDirection) => (Direction)(1 << (((stepDirection - Rise + 4) % 4) * 2));
        }

        private static Dictionary<uint, Stair> Stairs = new Dictionary<uint, Stair>();
        private static Dictionary<uint, string> StairNotes = new Dictionary<uint, string>();

        /// <summary>
        /// A staircase (its catalog GUID) that foundation steps can be made from, or null: a straight one (not a spiral
        /// or a turning one), with a bottom piece ("... Low") and a top piece ("... Hi") three tiles beyond it in a line.
        /// A staircase rises a storey over those four tiles, so its bottom piece rises a quarter of one over a tile: a
        /// foundation's height. Which way it rises comes from where its pieces are, so it can be turned to face up the
        /// steps.
        /// </summary>
        public static Stair StairFor(uint staircase)
        {
            if (staircase == 0) return null;
            lock (Stairs)
            {
                if (Stairs.TryGetValue(staircase, out var cached)) return cached;
                Stair found = null;
                string note;
                try
                {
                    found = FindStair(staircase, out note);
                }
                catch (Exception e)
                {
                    note = "couldn't read it: " + e.Message;
                }
                Stairs[staircase] = found;
                StairNotes[staircase] = note;
                return found;
            }
        }

        /// <summary>A multitile part's place in its object, as the simulation lays them out (VMMultitileGroup.AddObject).</summary>
        public static Point PartOffset(OBJD part) => new Point((sbyte)(((ushort)part.SubIndex) >> 8), (sbyte)(((ushort)part.SubIndex) & 0xFF));

        private static Stair FindStair(uint staircase, out string note)
        {
            var master = Content.Content.Get().WorldObjects.Get(staircase);
            var objds = master?.Resource?.List<OBJD>();
            if (objds == null) { note = "not found"; return null; }
            var masterID = master.OBJ.MasterID;
            var parts = objds.Where(o => o.MasterID == masterID && o.SubIndex != -1).ToList();
            if (parts.Count == 0) parts = objds.Where(o => o.SubIndex != -1).ToList();
            var low = parts.FirstOrDefault(o => IsLowPiece(o.ChunkLabel));
            var hi = parts.FirstOrDefault(o => IsHighPiece(o.ChunkLabel));
            if (low == null || hi == null) { note = "no bottom and top piece by name (not a straight staircase)"; return null; }
            var lo = PartOffset(low);
            var ho = PartOffset(hi);
            int dx = ho.X - lo.X, dy = ho.Y - lo.Y;
            if (!((dx == 0 && Math.Abs(dy) == 3) || (dy == 0 && Math.Abs(dx) == 3)) || low.LevelOffset != hi.LevelOffset)
            {
                note = $"its bottom ({lo.X}, {lo.Y}) and top ({ho.X}, {ho.Y}) aren't three tiles apart in a line (not a straight staircase)";
                return null;
            }
            var rise = Array.IndexOf(FoundationMap.Directions, new Point(Math.Sign(dx), Math.Sign(dy)));
            var piece = Content.Content.Get().WorldObjects.Get(low.GUID);
            if (piece == null) { note = "its bottom piece didn't load"; return null; }

            // the bottom piece has to draw just its own quarter: some staircases are drawn whole by one piece (and
            // the rest draw nothing), which would stand a whole flight on the steps' tile, or none
            var lowHeight = PictureHeight(piece);
            var others = parts.Where(o => o != low && !Words(o.ChunkLabel).Contains("pad") && !Words(o.ChunkLabel).Contains("stub"))
                .Select(o => PictureHeight(Content.Content.Get().WorldObjects.Get(o.GUID))).DefaultIfEmpty(0).Max();
            var measures = $"pictures: bottom {lowHeight}px tall, the others up to {others}px";
            if (lowHeight <= 0) { note = $"its bottom piece draws nothing ({measures})"; return null; }
            if (others <= 0 || lowHeight > others * 1.6f) { note = $"its bottom piece draws the whole staircase ({measures})"; return null; }
            // in 3D it's drawn from its 3D model, which may be a replacement (a remesh) with the whole staircase on this
            // piece, or nothing on it
            var model = ModelSize(piece);
            if (model != null)
            {
                var size = model.Value;
                measures += $"; 3D model {size.X:0.00} x {size.Y:0.00} x {size.Z:0.00} tiles";
                if (size.X <= 0.02f && size.Y <= 0.02f && size.Z <= 0.02f) { note = $"its bottom piece's 3D model is empty ({measures})"; return null; }
                if (size.Y > 2.3f || Math.Max(size.X, size.Z) > 1.7f) { note = $"its bottom piece's 3D model is the whole staircase ({measures})"; return null; }
            }

            note = $"bottom piece \"{low.ChunkLabel}\" (0x{low.GUID:X8}) at ({lo.X}, {lo.Y}), top \"{hi.ChunkLabel}\" at ({ho.X}, {ho.Y}): rises toward {FoundationMap.Directions[rise]}; {measures}";
            return new Stair { Piece = piece, Rise = rise, PieceName = low.ChunkLabel };
        }

        /// <summary>
        /// The size (tiles: across, up, along) of the 3D model the game draws an object with in 3D (its replacement
        /// model if it has one, else the one made from its pictures); null if there's none to be had (2D, say).
        /// </summary>
        public static Vector3? ModelSize(GameObject obj)
        {
            if (obj == null || obj.OBJ.BaseGraphicID == 0) return null;
            try
            {
                var meshes = Content.Content.Get().RCMeshes;
                var dgrp = obj.Resource.Get<DGRP>(obj.OBJ.BaseGraphicID);
                if (meshes == null || dgrp == null) return null;
                var mesh = meshes.Get(dgrp, obj.OBJ);
                if (mesh == null || mesh.Bounds == null) return null;
                var b = mesh.Bounds.Value;
                return b.Max - b.Min;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// How tall (pixels) an object's picture is, facing north in close-up: from the top of its highest sprite to the
        /// bottom of its lowest. 0 if it draws nothing.
        /// </summary>
        public static int PictureHeight(GameObject obj)
        {
            if (obj == null || obj.OBJ.BaseGraphicID == 0) return 0;
            try
            {
                var dgrp = obj.Resource.Get<DGRP>(obj.OBJ.BaseGraphicID);
                var image = dgrp?.GetImage(1, (uint)WorldZoom.Near, 0);
                if (image?.Sprites == null) return 0;
                float top = float.MaxValue, bottom = float.MinValue;
                foreach (var sprite in image.Sprites)
                {
                    var size = sprite.GetDimensions();
                    if (size.X <= 1 && size.Y <= 1) continue;
                    //(as DGRPRenderer places them: a sprite's bottom at its offset from the baseline)
                    top = Math.Min(top, sprite.SpriteOffset.Y - size.Y);
                    bottom = Math.Max(bottom, sprite.SpriteOffset.Y);
                }
                return (bottom > top) ? (int)(bottom - top) : 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static HashSet<string> Words(string name) =>
            new HashSet<string>((name ?? "").ToLowerInvariant().Split(new[] { ' ', '-', '_', '(', ')', ',', '.', '/' }, StringSplitOptions.RemoveEmptyEntries));

        /// <summary>Whether a staircase part's name is its bottom piece's: "Stair - Low", not "Low Pad" or "Middle Low".</summary>
        public static bool IsLowPiece(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            var words = Words(name);
            if (!words.Contains("low") && !words.Contains("lower") && !words.Contains("bottom")) return false;
            foreach (var not in new[] { "pad", "mid", "middle", "stub", "hi", "high", "top", "upper" })
                if (words.Contains(not)) return false;
            return true;
        }

        /// <summary>Whether a staircase part's name is its top piece's: "Stair - Hi", not "Hi Pad", a stub or "Middle Hi".</summary>
        public static bool IsHighPiece(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            var words = Words(name);
            if (!words.Contains("hi") && !words.Contains("high") && !words.Contains("top") && !words.Contains("upper")) return false;
            foreach (var not in new[] { "pad", "mid", "middle", "stub", "low", "lower", "bottom" })
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
                sb.AppendLine("  lattice: " + ((LatticeCovering() != 0) ? "wall " + wallName(LatticeCovering()) : "strips of the deck planks' wood"));
                sb.AppendLine("  deck planks run " + (PlanksAlongU() ? "across" : "down") + " their picture (boards round decks take one plank lengthways)");
                sb.AppendLine("  columns, plain steps: floor " + floorName(ColumnFloor()));
                sb.AppendLine("Every floor, with its average colour (hue, saturation, value):");
                foreach (var f in floors)
                {
                    var hsv = AverageHSV((ushort)f.ID);
                    sb.AppendLine($"  #{f.ID} \"{f.Name}\" ({f.FileName})" + ((hsv != null) ? $": {hsv.Value.X:0} {hsv.Value.Y:0.00} {hsv.Value.Z:0.00}" : ""));
                }
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
            sb.AppendLine("Staircases, for foundation steps (only straight ones):");
            foreach (var guid in staircases ?? Enumerable.Empty<uint>())
            {
                StairFor(guid);
                string parts = "";
                try
                {
                    var objds = Content.Content.Get().WorldObjects.Get(guid)?.Resource?.List<OBJD>();
                    if (objds != null) parts = string.Join(", ", objds.Select(o => $"\"{o.ChunkLabel}\" ({PartOffset(o).X}, {PartOffset(o).Y})"));
                }
                catch (Exception) { }
                StairNotes.TryGetValue(guid, out var note);
                sb.AppendLine($"  0x{guid:X8}: " + ((Stairs.TryGetValue(guid, out var st) && st != null) ? "yes, " : "no, ") + note + "; its parts: " + parts);
            }
            return sb.ToString();
        }
    }
}
