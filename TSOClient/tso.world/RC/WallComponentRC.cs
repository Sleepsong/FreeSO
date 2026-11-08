using FSO.Content.Model;
using FSO.LotView.Effects;
using FSO.LotView.LMap;
using FSO.LotView.Model;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;

namespace FSO.LotView.RC
{
    /// <summary>
    /// This component allows FreeSO to draw walls in 3D. 
    /// This is not enabled in 2D mode due to it misrepresenting certain wall directions.
    /// 
    /// Basically, wall geometry is generated as if the wall were thick around the tile edge. 
    /// At runtime, this is offset back.
    /// </summary>
    public class WallComponentRC
    {
        public Blueprint blueprint;

        public List<Dictionary<Tuple<Texture2D, Texture2D>, WallGroupRC>> GroupsByTexture = new List<Dictionary<Tuple<Texture2D, Texture2D>, WallGroupRC>>();
        /// <summary>The basement's (level 0's) walls: GroupsByTexture is by level - 1 from the ground floor.</summary>
        public Dictionary<Tuple<Texture2D, Texture2D>, WallGroupRC> BasementGroups = new Dictionary<Tuple<Texture2D, Texture2D>, WallGroupRC>();

        public Dictionary<ushort, Wall> WallCache = new Dictionary<ushort, Wall>();
        /// <summary>White, as the walls' tops: keys the basement walls' tops lit as the ground floor apart (see <see cref="WallGroupRC.GroundLit"/>).</summary>
        private static Texture2D GroundTopPx;
        public Dictionary<ushort, WallStyle> WallStyleCache = new Dictionary<ushort, WallStyle>();

        private Wall GetPattern(ushort id)
        {
            if (!WallCache.ContainsKey(id)) WallCache.Add(id, Content.Content.Get().WorldWalls.Get(id));
            return WallCache[id];
        }

        private WallStyle GetStyle(ushort id)
        {
            if (!WallStyleCache.ContainsKey(id)) WallStyleCache.Add(id, Content.Content.Get().WorldWalls.GetWallStyle(id));
            return WallStyleCache[id];
        }

        /// <summary>How many times all the walls have been made again (they changed): a second view's own walls follow it.</summary>
        public int FullGenerations;

        public static Vector2[] CutCheckDir =
        {
            new Vector2(-1,-1),
            new Vector2(-1,1),
            new Vector2(1,1),
            new Vector2(1,-1)
        };

        /// <summary>
        /// Whether a basement (level 0) wall's pieces get cut down at the ground outside: a foundation lifts some of the
        /// basement above the ground outside it (see <see cref="EarthAbove(Blueprint, Vector2)"/>).
        /// </summary>
        private bool BasementRaised()
        {
            var map = blueprint.Basement;
            var fnd = blueprint.Foundations;
            return map != null && map.Any && fnd != null && fnd.Any;
        }

        /// <summary>
        /// For a point of a basement wall (lot tiles, across the ground): 0 if it's over the basement; if it's outside it (a
        /// wall on the basement's edge is half outside, and its ends and corners reach out past the edge), how much of the
        /// storey is above the ground there, from the foundation lifting the basement beside it.
        /// </summary>
        public static float EarthAbove(Blueprint blueprint, Vector2 p)
        {
            var map = blueprint.Basement;
            var fnd = blueprint.Foundations;
            int tx = (int)Math.Floor(p.X), ty = (int)Math.Floor(p.Y);
            if (map.IsDug(tx, ty)) return 0;
            int most = 0;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                    if ((dx != 0 || dy != 0) && map.IsDug(tx + dx, ty + dy))
                        most = Math.Max(most, fnd.RaiseOf(tx + dx, ty + dy) - fnd.RaiseOf(tx, ty));
            return most * blueprint.TerrainFactor / 2.95f;
        }

        /// <summary>Where a line (lot tiles) from a to b crosses tile edges, 0 to 1 along it, with its ends: it's cut there.</summary>
        public static List<float> TileCuts(Vector2 a, Vector2 b)
        {
            var result = new List<float> { 0, 1 };
            Action<float, float> cross = (from, to) =>
            {
                if (Math.Abs(to - from) < 0.0001f) return;
                for (var edge = Math.Ceiling(Math.Min(from, to)); edge < Math.Max(from, to); edge++)
                {
                    var t = (float)((edge - from) / (to - from));
                    if (t > 0.001f && t < 0.999f) result.Add(t);
                }
            };
            cross(a.X, b.X);
            cross(a.Y, b.Y);
            result.Sort();
            return result;
        }

        private bool HasRoof(int x, int y, int level)
        {
            return !TileIndoors(x, y, level) && TileIndoors(x, y, level - 1);
        }

        private bool TileIndoors(int x, int y, int level)
        {
            var room = blueprint.RoomMap[level - 1][x + y * blueprint.Width];
            var room1 = room & 0xFFFF;
            var room2 = (room >> 16) & 0x7FFF;
            if (room1 < blueprint.Rooms.Count && !blueprint.Rooms[(int)room1].IsOutside) return true;
            if (room2 > 0 && room2 < blueprint.Rooms.Count && !blueprint.Rooms[(int)room2].IsOutside) return true;
            return false;
        }

        public void Generate(GraphicsDevice device, WorldState world, bool cutaway, bool allowCut = true)
        {
            var wallContent = Content.Content.Get().WorldWalls;
            var floorContent = Content.Content.Get().WorldFloors;
            if (!cutaway) { Dispose(); FullGenerations++; }
            var white = Color.White.ToVector4();
            var whitepx = Common.Utils.TextureGenerator.GetPxWhite(device);
            var darker = new Vector4(0.85f, 0.85f, 0.85f, 1);
            var wallTop = new Color(151, 120, 76, 255).ToVector4();
            var wallHeight = new Vector3(0, 0, 2.95f);
            var thickness = 0.15f/2;
            var thickDiag = thickness;
            var xz = CutCheckDir[(int)world.CutRotation] * 0.66f;
            var raisedBasement = BasementRaised();
            if (float.IsNaN(xz.X)) xz = new Vector2();

            //draw walls: the basement (level 0) and up
            for (sbyte level = 0; level <= blueprint.Floors.Length; level++)
            {
                bool canCut = (level == world.Level);
                if (cutaway && !canCut) continue;
                if (level == 0)
                {
                    foreach (var grp2 in BasementGroups.Values) grp2.Dispose();
                    BasementGroups.Clear();
                }
                else if (GroupsByTexture.Count < level) GroupsByTexture.Add(new Dictionary<Tuple<Texture2D, Texture2D>, WallGroupRC>());
                else
                {
                    //clear previous data for cutaway
                    foreach (var grp2 in GroupsByTexture[level - 1].Values)
                    {
                        grp2.Dispose();
                    }
                    GroupsByTexture[level - 1].Clear();
                }
                var grp = (level == 0) ? BasementGroups : GroupsByTexture[level - 1];

                var rMap = blueprint.RoomMap[level - 1];

                var forceUp = false; //drawing a door's wall: full height (a half wall's: half height)
                var halfCap = 1f; //drawing a half wall: no higher than this (WallTile.HalfHeight)
                Func<Vector3, float> GetWallHeight = (Vector3 pos) =>
                {
                    if (level != world.Level) return halfCap;
                    pos.X += xz.X; pos.Y += xz.Y;
                    var index = (int)pos.Y * blueprint.Width + (int)pos.X;
                    if (forceUp) return halfCap;
                    if (index < 0 || index >= blueprint.Cutaway.Length) return halfCap;
                    return Math.Min(halfCap, allowCut && blueprint.Cutaway[index] ? 0.12f : 1);
                };
                //a half wall's thick neighbour along its line, or a wall's: a wall of the other height is capped between them
                Func<WallTile, WallTile, bool> sameTL = (a, b) => b.TopLeftThick && a.TopLeftHalf == b.TopLeftHalf;
                Func<WallTile, WallTile, bool> sameTR = (a, b) => b.TopRightThick && a.TopRightHalf == b.TopRightHalf;

                //Walls with doors in them, as the 2D walls (WallComponent.GenerateWallData) draw them: a door's wall is left
                //out where the tiles on both sides of it are cut (the door is hidden for cutaway there too), and is otherwise
                //drawn at full height around the door, never cut down or sloped. Here they were cut like any wall, a point at
                //a time, so where the cut ended at a doorway the door's wall stood or sloped up from the low walls: a line.
                Func<int, int, bool> cutAt = (int tx, int ty) =>
                    level == world.Level && allowCut && tx >= 0 && ty >= 0 && tx < blueprint.Width && ty < blueprint.Height
                    && blueprint.Cutaway[ty * blueprint.Width + tx];
                Func<int, int, bool> tlGone = (int tx, int ty) => blueprint.GetWall((short)tx, (short)ty, level).TopLeftDoor && cutAt(tx, ty) && cutAt(tx - 1, ty);
                Func<int, int, bool> trGone = (int tx, int ty) => blueprint.GetWall((short)tx, (short)ty, level).TopRightDoor && cutAt(tx, ty) && cutAt(tx, ty - 1);
                //thick, and drawn: a gone door's wall leaves the walls either side of it open, so they're capped
                Func<int, int, bool> tlThick = (int tx, int ty) => blueprint.GetWall((short)tx, (short)ty, level).TopLeftThick && !tlGone(tx, ty);
                Func<int, int, bool> trThick = (int tx, int ty) => blueprint.GetWall((short)tx, (short)ty, level).TopRightThick && !trGone(tx, ty);

                Action<int, int, Vector2, Vector2, ushort, ushort, int, float, float, float> addLineGeom = (int x, int y, Vector2 from, Vector2 to, ushort pattern, ushort style, int topMode, float starttc, float endtc, float aboveFloor) => {
                    if (style == FSO.Content.WorldWallProvider.HalfWallStyle) style = 1; //masked as a wall: its height is from halfCap
                    var tex = world._2D.GetTexture(GetPattern(pattern)?.Near?.Frames[2]);
                    var mask = world._2D.GetTexture(GetStyle(style)?.WallsUpNear?.Frames[(topMode != 4) ? 0 : 2]);

                    var g = Fetch(tex, mask, style, grp);
                    g.UseOffset = (topMode != 4);

                    // a wall on a foundation's edge stands on the foundation: both its faces (either side of the edge) and
                    // its end caps take the highest foundation beside the line
                    var mid = (from + to) / 2 + new Vector2(x, y);
                    var raise = blueprint.FoundationNear(mid.X, mid.Y, 0.2f);
                    var p1 = new Vector3(from.X + x, from.Y + y, 0);
                    var a1 = blueprint.TerrainAltitude(p1) + raise + (level - 1) * 2.95f;
                    p1.Z = a1;
                    var p2 = new Vector3(to.X + x, to.Y + y, 0);
                    var a2 = blueprint.TerrainAltitude(p2) + raise + (level - 1) * 2.95f;
                    p2.Z = a2;

                    //generate the geometry for this line
                    var l = level - 1;
                    var h1 = (topMode == 4) ? 0.98f : GetWallHeight(p1);
                    var h2 = (topMode == 4) ? 0.98f : GetWallHeight(p2);
                    //the basement's walls where a foundation lifts it above the ground outside: what's of them outside the
                    //basement (the outside face of one on its edge, the ends and corners reaching past it) stops at the
                    //ground there, and their tops are only over the basement: the foundation's own side shows above, as the
                    //house shows it. Pieces cross tiles at their edges, so they're cut there.
                    var clip = raisedBasement && level == 0 && topMode != 4;
                    var lineFrom = from + new Vector2(x, y);
                    var lineTo = to + new Vector2(x, y);
                    var cuts = clip ? TileCuts(lineFrom, lineTo) : new List<float> { 0, 1 };
                    var col = (from.X == to.X) ? darker : white;
                    for (int i = 0; i + 1 < cuts.Count; i++)
                    {
                        float s0 = cuts[i], s1 = cuts[i + 1];
                        var above = clip ? EarthAbove(blueprint, Vector2.Lerp(lineFrom, lineTo, (s0 + s1) / 2)) : 0;
                        var k1 = Math.Min(MathHelper.Lerp(h1, h2, s0), 1 - above);
                        var k2 = Math.Min(MathHelper.Lerp(h1, h2, s1), 1 - above);
                        if (k1 <= 0 && k2 <= 0) continue;
                        var q1 = Vector3.Lerp(p1, p2, s0);
                        var q2 = Vector3.Lerp(p1, p2, s1);
                        var tc1 = MathHelper.Lerp(starttc, endtc, s0);
                        var tc2 = MathHelper.Lerp(starttc, endtc, s1);
                        var baseI = g.Verts.Count;
                        g.Verts.Add(new WallVertexRC(q1, col, new Vector3(tc1, l, aboveFloor)));
                        g.Verts.Add(new WallVertexRC(q2, col, new Vector3(tc2, l, aboveFloor)));
                        g.Verts.Add(new WallVertexRC(q1 + wallHeight * k1, col, new Vector3(tc1, k1 + l, aboveFloor)));
                        g.Verts.Add(new WallVertexRC(q2 + wallHeight * k2, col, new Vector3(tc2, k2 + l, aboveFloor)));

                        g.Indices.Add(baseI); g.Indices.Add(baseI + 2); g.Indices.Add(baseI + 1);
                        g.Indices.Add(baseI + 2); g.Indices.Add(baseI + 3); g.Indices.Add(baseI + 1);
                    }

                    if (topMode < 4)
                    {
                        var g2 = Fetch(whitepx, whitepx, 0, grp);
                        //a basement wall's top, where it's whole: flush with the ground floor, it's lit as the ground floor
                        //while that's shown (its own group: see Draw); the basement under it can be dark (a black line)
                        WallGroupRC gTop = null;
                        if (level == 0)
                        {
                            if (GroundTopPx == null || GroundTopPx.IsDisposed || GroundTopPx.GraphicsDevice != device)
                            {
                                GroundTopPx = new Texture2D(device, 1, 1);
                                GroundTopPx.SetData(new[] { Color.White });
                            }
                            gTop = Fetch(GroundTopPx, whitepx, 0, grp);
                            gTop.GroundLit = true;
                        }

                        Vector3 toBack = Vector3.Zero;

                        switch (topMode)
                        {
                            case 0:
                                toBack = new Vector3(-thickness * 2, 0, 0);
                                break;
                            case 1:
                                toBack = new Vector3(0, -thickness * 2, 0);
                                break;
                            case 2:
                                toBack = new Vector3(thickness * -2, thickness * -2, 0);
                                break;
                            case 3:
                                toBack = new Vector3(thickness * 2, thickness * -2, 0);
                                break;
                        }

                        h1 -= 0.001f; h2 -= 0.001f;
                        //(clipped: its halves either side of the wall's middle, a tile at a time)
                        var across = clip ? new float[] { 0, 0.5f, 1 } : new float[] { 0, 1 };
                        var back = new Vector2(toBack.X, toBack.Y);
                        for (int i = 0; i + 1 < cuts.Count; i++)
                        {
                            float s0 = cuts[i], s1 = cuts[i + 1];
                            for (int j = 0; j + 1 < across.Length; j++)
                            {
                                float c0 = across[j], c1 = across[j + 1];
                                if (clip && EarthAbove(blueprint, Vector2.Lerp(lineFrom, lineTo, (s0 + s1) / 2) + back * ((c0 + c1) / 2)) > 0) continue;
                                var k1 = MathHelper.Lerp(h1, h2, s0);
                                var k2 = MathHelper.Lerp(h1, h2, s1);
                                var q1 = Vector3.Lerp(p1, p2, s0) + wallHeight * k1;
                                var q2 = Vector3.Lerp(p1, p2, s1) + wallHeight * k2;
                                var gt = (gTop != null && k1 > 0.9f && k2 > 0.9f) ? gTop : g2;
                                var baseI = gt.Verts.Count;
                                gt.Verts.Add(new WallVertexRC(q1 + toBack * c0, wallTop, new Vector3(0, k1 + l, aboveFloor)));
                                gt.Verts.Add(new WallVertexRC(q2 + toBack * c0, wallTop, new Vector3(0, k2 + l, aboveFloor)));
                                gt.Verts.Add(new WallVertexRC(q1 + toBack * c1, wallTop, new Vector3(0, k1 + l, aboveFloor)));
                                gt.Verts.Add(new WallVertexRC(q2 + toBack * c1, wallTop, new Vector3(0, k2 + l, aboveFloor)));

                                gt.Indices.Add(baseI); gt.Indices.Add(baseI + 2); gt.Indices.Add(baseI + 1);
                                gt.Indices.Add(baseI + 2); gt.Indices.Add(baseI + 3); gt.Indices.Add(baseI + 1);
                            }
                        }
                    }
                };

                for (short y = 0; y < blueprint.Height; y++)
                {
                    for (short x = 0; x < blueprint.Height; x++)
                    {
                        var comp = blueprint.GetWall(x, y, level);
                        if (comp.Segments != 0)
                        {
                            float bleedLight = (level <= 1 || blueprint.GetFloor(x, y, level).Pattern != 0 || HasRoof(x, y, level)) ? 0 : 1;
                            if ((comp.Segments & WallSegments.TopLeft) > 0)
                            {
                                if (comp.TopLeftThick && tlGone(x, y)) { }
                                else if (comp.TopLeftThick)
                                {
                                    halfCap = comp.TopLeftHalf ? WallTile.HalfHeight : 1;
                                    //a door's wall stands full height: cap it where the wall it meets is cut down
                                    var lowBack = comp.TopLeftDoor && GetWallHeight(new Vector3(x + thickness, y, 0)) < halfCap;
                                    var lowFront = comp.TopLeftDoor && GetWallHeight(new Vector3(x + thickness, y + 1, 0)) < halfCap;
                                    forceUp = comp.TopLeftDoor;
                                    float extentBack = 0;
                                    float extentFront = 0;
                                    float bleedLight2 = (level <= 1 || blueprint.GetFloor((short)(x-1), y, level).Pattern != 0 || HasRoof(x-1, y, level)) ? 0 : 1;
                                    if (y > 0 && (!tlThick(x, y - 1) || !sameTL(comp, blueprint.GetWall(x, (short)(y - 1), level)) || lowBack))
                                    {
                                        extentBack = -thickness;
                                        //cap this end
                                        addLineGeom(x, y, new Vector2(thickness, extentBack + 0.005f), new Vector2(-thickness, extentBack + 0.005f), comp.TopLeftPattern, 1, 5, 0, thickness * 2, bleedLight);
                                    }
                                    if (y < blueprint.Height - 1 && (!tlThick(x, y + 1) || !sameTL(comp, blueprint.GetWall(x, (short)(y + 1), level)) || lowFront))
                                    {
                                        extentFront = thickness;
                                        //cap this end
                                        addLineGeom(x, y, new Vector2(-thickness, 1 + extentFront - 0.005f), new Vector2(thickness, 1 + extentFront - 0.005f), comp.TopLeftPattern, 1, 5, 0, thickness * 2, bleedLight);
                                    }
                                    if (x > 0)
                                        addLineGeom(x, y, new Vector2(-thickness, extentBack), new Vector2(-thickness, 1 + extentFront), blueprint.GetWall((short)(x - 1), y, level).BottomRightPattern, (comp.ObjSetTLStyle == 0) ? comp.TopLeftStyle : comp.ObjSetTLStyle, 5, -(0 + extentBack), -(1 + extentFront), bleedLight2);
                                    addLineGeom(x, y, new Vector2(thickness, 1+extentFront), new Vector2(thickness, extentBack), comp.TopLeftPattern, (comp.ObjSetTLStyle == 0) ? comp.TopLeftStyle : comp.ObjSetTLStyle, 0, 0-extentFront, 1-extentBack, bleedLight);
                                    forceUp = false;
                                    halfCap = 1;
                                }
                                else
                                {
                                    //fence tl
                                    addLineGeom(x, y, new Vector2(0, 1), new Vector2(0, 0), comp.TopLeftPattern, (comp.ObjSetTLStyle == 0) ? comp.TopLeftStyle : comp.ObjSetTLStyle, 4, 0, 1, bleedLight);
                                }
                            }
                            if ((comp.Segments & WallSegments.TopRight) > 0)
                            {
                                if (comp.TopRightThick && trGone(x, y)) { }
                                else if (comp.TopRightThick)
                                {
                                    halfCap = comp.TopRightHalf ? WallTile.HalfHeight : 1;
                                    var lowBack = comp.TopRightDoor && GetWallHeight(new Vector3(x, y + thickness, 0)) < halfCap;
                                    var lowFront = comp.TopRightDoor && GetWallHeight(new Vector3(x + 1, y + thickness, 0)) < halfCap;
                                    forceUp = comp.TopRightDoor;
                                    float extentBack = 0;
                                    float extentFront = 0;
                                    float bleedLight2 = (level <= 1 || blueprint.GetFloor(x, (short)(y-1), level).Pattern != 0 || HasRoof(x, y-1, level)) ? 0 : 1;
                                    if (x > 0 && (!trThick(x - 1, y) || !sameTR(comp, blueprint.GetWall((short)(x - 1), y, level)) || lowBack))
                                    {
                                        extentBack = -thickness;
                                        //cap this end
                                        addLineGeom(x, y, new Vector2(extentBack+0.005f, -thickness), new Vector2(extentBack + 0.005f, thickness), comp.TopRightPattern, 1, 5, 0, thickness*2, bleedLight);
                                    }
                                    if (x < blueprint.Width - 1 && (!trThick(x + 1, y) || !sameTR(comp, blueprint.GetWall((short)(x + 1), y, level)) || lowFront))
                                    {
                                        extentFront = thickness;
                                        //cap this end
                                        addLineGeom(x, y, new Vector2(1 + extentFront - 0.005f, thickness), new Vector2(1 + extentFront - 0.005f, -thickness), comp.TopRightPattern, 1, 5, 0, thickness * 2, bleedLight);
                                    }
                                    if (y > 0)
                                        addLineGeom(x, y, new Vector2(1 + extentFront, -thickness), new Vector2(extentBack, -thickness), blueprint.GetWall(x, (short)(y - 1), level).BottomLeftPattern, (comp.ObjSetTRStyle == 0) ? comp.TopRightStyle : comp.ObjSetTRStyle, 5, extentFront, -(1 - extentBack), bleedLight2);
                                    addLineGeom(x, y, new Vector2(extentBack, thickness), new Vector2(1+extentFront, thickness), comp.TopRightPattern, (comp.ObjSetTRStyle == 0) ? comp.TopRightStyle : comp.ObjSetTRStyle, 1, extentBack, 1+extentFront, bleedLight);
                                    forceUp = false;
                                    halfCap = 1;
                                }
                                else
                                {
                                    //fence tr
                                    addLineGeom(x, y, new Vector2(0, 0), new Vector2(1, 0), comp.TopRightPattern, (comp.ObjSetTRStyle == 0) ? comp.TopRightStyle : comp.ObjSetTRStyle, 4, 0, 1, bleedLight);
                                }
                            }

                            if ((comp.Segments & WallSegments.BottomLeft) > 0 && y < blueprint.Height)
                            {
                                //fence bl
                                var comp2 = blueprint.GetWall(x, (short)(y + 1), level);
                                if (!comp2.TopRightThick)
                                    addLineGeom(x, y, new Vector2(1, 1), new Vector2(0, 1), comp.BottomLeftPattern, (comp2.ObjSetTRStyle == 0) ? comp2.TopRightStyle : comp.ObjSetTRStyle, 4, 0, 1, bleedLight);
                            }
                            if ((comp.Segments & WallSegments.BottomRight) > 0 && x < blueprint.Width)
                            {
                                //fence br

                                var comp2 = blueprint.GetWall((short)(x + 1), y, level);
                                if (!comp2.TopLeftThick)
                                    addLineGeom(x, y, new Vector2(1, 0), new Vector2(1, 1), comp.BottomRightPattern, (comp2.ObjSetTLStyle == 0) ? comp2.TopLeftStyle : comp2.ObjSetTLStyle, 4, 0, 1, bleedLight);
                            }

                            if ((comp.Segments & WallSegments.HorizontalDiag) > 0)
                            {
                                var bl2 = (level <= 1 || comp.TopLeftStyle != 0) ? 0 : 1;
                                var bl1 = (level <= 1 || comp.TopLeftPattern != 0) ? 0 : 1;
                                halfCap = comp.TopRightHalf ? WallTile.HalfHeight : 1;
                                if (WallTile.IsThickStyle(comp.TopRightStyle))
                                {
                                    addLineGeom(x, y, new Vector2(thickDiag, 1+thickDiag), new Vector2(1+thickDiag, thickDiag), comp.BottomRightPattern, (comp.ObjSetTRStyle == 0) ? comp.TopRightStyle : comp.ObjSetTRStyle, 2, 0, 1, bl1);
                                    addLineGeom(x, y, new Vector2(1-thickDiag, -thickDiag), new Vector2(-thickDiag, 1 - thickDiag), comp.BottomLeftPattern, (comp.ObjSetTRStyle == 0) ? comp.TopRightStyle : comp.ObjSetTRStyle, 5, 0, 1, bl2);

                                    //caps
                                    addLineGeom(x, y, new Vector2(-thickDiag, 1 - thickDiag), new Vector2(thickDiag, 1 + thickDiag), comp.BottomRightPattern, 1, 5, 0, thickDiag*2, Math.Min(bl1, bl2));
                                    addLineGeom(x, y, new Vector2(1 + thickDiag, thickDiag), new Vector2(1 - thickDiag, -thickDiag), comp.BottomLeftPattern, 1, 5, 0, thickDiag * 2, Math.Min(bl1, bl2));
                                }
                                else
                                {
                                    //fence horiz
                                    addLineGeom(x, y, new Vector2(0, 1), new Vector2(1, 0), comp.BottomRightPattern, (comp.ObjSetTRStyle == 0) ? comp.TopRightStyle : comp.ObjSetTRStyle, 4, 0, 1, bl1);
                                    addLineGeom(x, y, new Vector2(1, 0), new Vector2(0, 1), comp.BottomLeftPattern, (comp.ObjSetTRStyle == 0) ? comp.TopRightStyle : comp.ObjSetTRStyle, 4, 0, 1, bl2);
                                }
                                halfCap = 1;
                            }
                            if ((comp.Segments & WallSegments.VerticalDiag) > 0)
                            {
                                var bl1 = (level <= 1 || comp.TopLeftStyle != 0) ? 0 : 1;
                                var bl2 = (level <= 1 || comp.TopLeftPattern != 0) ? 0 : 1;
                                halfCap = comp.TopRightHalf ? WallTile.HalfHeight : 1;
                                if (WallTile.IsThickStyle(comp.TopRightStyle))
                                {
                                    addLineGeom(x, y, new Vector2(-thickDiag, thickDiag), new Vector2(1- thickDiag, 1+thickDiag), comp.BottomLeftPattern, (comp.ObjSetTRStyle == 0) ? comp.TopRightStyle : comp.ObjSetTRStyle, 3, 0, 1, bl1);
                                    addLineGeom(x, y, new Vector2(1+ thickDiag, 1- thickDiag), new Vector2(0 + thickDiag, -thickDiag), comp.BottomRightPattern, (comp.ObjSetTRStyle == 0) ? comp.TopRightStyle : comp.ObjSetTRStyle, 5, 0, 1, bl2);

                                    //caps 
                                    addLineGeom(x, y, new Vector2(0 + thickDiag, -thickDiag), new Vector2(-thickDiag, thickDiag), comp.BottomLeftPattern, 1, 5, 0, thickDiag*2, Math.Min(bl1, bl2));
                                    addLineGeom(x, y, new Vector2(1 - thickDiag, 1 + thickDiag), new Vector2(1 + thickDiag, 1 - thickDiag), comp.BottomRightPattern, 1, 5, 0, thickDiag * 2, Math.Min(bl1, bl2));

                                }
                                else
                                {
                                    // fence vert
                                    addLineGeom(x, y, new Vector2(0, 0), new Vector2(1, 1), comp.BottomLeftPattern, (comp.ObjSetTRStyle == 0) ? comp.TopRightStyle : comp.ObjSetTRStyle, 4, 0, 1, bl1);
                                    addLineGeom(x, y, new Vector2(1, 1), new Vector2(0, 0), comp.BottomRightPattern, (comp.ObjSetTRStyle == 0) ? comp.TopRightStyle : comp.ObjSetTRStyle, 4, 0, 1, bl2);
                                }
                                halfCap = 1;
                            }
                        }
                    }
                }
                foreach (var g in grp.Values) g.Complete(device);
            }

            if (!cutaway && !allowCut)
            {
                blueprint.SM64?.UpdateWalls();
            }
        }

        public void Draw(GraphicsDevice gd, WorldState state)
        {
            var effect = WorldContent.RCObject;

            effect.SetTechnique(RCObjectTechniques.WallDraw);

            var lastSideMask = false;

            var xz = state.GetWallOffset() * 0.7f;

            gd.BlendState = BlendState.Opaque;
            if (!gd.RasterizerState.ScissorTestEnable) gd.RasterizerState = RasterizerState.CullCounterClockwise;
            var baseWorld = Matrix.CreateRotationX((float)Math.PI / 2) * Matrix.CreateScale(3f, -3f, 3f);
            effect.World = baseWorld;
            effect.SideMask = 0f;
            effect.Level = (float)(state.Level - 0.999f);
            //effect.Parameters["CutawayTex"].SetValue(Cutaway);
            //effect.Parameters["CurrentLevel"].SetValue(state.Level - 1);
            if (GroupsByTexture.Count < state.Level) return;

            //the basement (level 0, i = -1) and up to the level shown
            var baseOffset = effect.LightOffset;
            for (int i = -1; i < state.Level; i++)
            {
                var grp = (i == -1) ? BasementGroups : GroupsByTexture[i];
                foreach (var g in grp.Values)
                {
                    if (g.PrimCount == 0) continue;
                    //(walls find their floor's light by their height: the basement's is moved; its walls' tops, flush with
                    //the ground floor, take the ground floor's while it's shown)
                    var groundLit = g.GroundLit && state.Level > 0;
                    effect.BasementLight = (i == -1) && !groundLit;
                    effect.LightOffset = groundLit ? baseOffset + LightMappedEffect.GroundCellFromBasement : baseOffset;
                    effect.AnisoTex = g.Pixel;
                    effect.MaskTex = g.Mask;

                    if (lastSideMask != g.UseOffset)
                    {
                        effect.SideMask = g.UseOffset ? 1f : 0f;
                        effect.World = (g.UseOffset) ? (Matrix.CreateTranslation(new Vector3(xz, 0)) * baseWorld) : baseWorld;
                        lastSideMask = g.UseOffset;
                    }
                    foreach (var pass in effect.CurrentTechnique.Passes)
                    {
                        pass.Apply();
                        gd.SetVertexBuffer(g.GVerts);
                        gd.Indices = g.GIndices;
                        gd.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, g.PrimCount);
                    }
                }
            }
            effect.BasementLight = false;
            effect.LightOffset = baseOffset;
            if (!gd.RasterizerState.ScissorTestEnable) gd.RasterizerState = RasterizerState.CullNone;
        }

        public BlendState MaxBlendRed = new BlendState()
        {
            AlphaBlendFunction = BlendFunction.Max,
            ColorBlendFunction = BlendFunction.Max,
            ColorDestinationBlend = Blend.One,
            ColorWriteChannels = ColorWriteChannels.Red | ColorWriteChannels.Alpha
        };

        public BlendState MaxBlendGreen = new BlendState()
        {
            AlphaBlendFunction = BlendFunction.Max,
            ColorBlendFunction = BlendFunction.Max,
            ColorDestinationBlend = Blend.One,
            ColorWriteChannels = ColorWriteChannels.Green | ColorWriteChannels.Alpha
        };

        public void DrawLMap(GraphicsDevice gd, LightData light, Matrix projection, Matrix lightTransform)
        {
            var effect = WorldContent.RCObject;

            effect.SetTechnique(RCObjectTechniques.WallLMap);

            var lastSideMask = false;

            //gd.DepthStencilState = DepthStencilState.None;
            //gd.RasterizerState = RasterizerState.CullNone;
            //var baseWorld = Matrix.CreateRotationX((float)Math.PI / 2) * Matrix.CreateScale(3f, -3f, 3f);

            //mat.M31 = 0; mat.M32 = 0; mat.M33 = 0; mat.M34 = 0f; //z is unimportant, so it is zero.

            effect.World = Matrix.CreateTranslation(0, 0, -light.Level+(0.07f / light.FalloffMultiplier)) * lightTransform;
            effect.Level = (float)(light.Level);
            effect.SideMask = 0f;
            effect.ViewProjection = projection;

            //dont care about base texture or offset here.
            //(the basement's light, level -1, starts with the basement's walls)
            for (int i = light.Level; i < GroupsByTexture.Count; i++)
            {
                var grp = (i < 0) ? BasementGroups : GroupsByTexture[i];
                foreach (var g in grp.Values)
                {
                    if (g.PrimCount == 0) continue;
                    effect.MaskTex = g.Mask;

                    if (lastSideMask != g.UseOffset)
                    {
                        gd.BlendState = (g.UseOffset) ? MaxBlendRed : MaxBlendGreen;
                        effect.SideMask = g.UseOffset ? 1f : 0f;
                        lastSideMask = g.UseOffset;
                    }
                    foreach (var pass in effect.CurrentTechnique.Passes)
                    {
                        pass.Apply();
                        gd.SetVertexBuffer(g.GVerts);
                        gd.Indices = g.GIndices;
                        gd.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, g.PrimCount);
                    }
                }
            }
            //gd.DepthStencilState = DepthStencilState.None;
            //gd.BlendState = BlendState.AlphaBlend;
        }

        public WallGroupRC Fetch(Texture2D pixel, Texture2D mask, ushort maskId, Dictionary<Tuple<Texture2D, Texture2D>, WallGroupRC> grp)
        {
            WallGroupRC result;
            var t = new Tuple<Texture2D, Texture2D>(pixel, mask);
            if (!grp.TryGetValue(t, out result))
            {
                result = new WallGroupRC() { Pixel = pixel, Mask = mask, MaskId = maskId };
                grp[t] = result;
            }
            return result;
        }

        public void Dispose()
        {
            foreach (var grp in GroupsByTexture)
            {
                foreach (var g in grp.Values)
                {
                    g.Dispose();
                }
            }
            GroupsByTexture.Clear();
            foreach (var g in BasementGroups.Values) g.Dispose();
            BasementGroups.Clear();
        }
    }

    public class WallGroupRC : IDisposable
    {
        public Texture2D Pixel;
        public Texture2D Mask;
        public ushort MaskId;
        public bool UseOffset = true;

        public List<WallVertexRC> Verts = new List<WallVertexRC>();
        public List<int> Indices = new List<int>();

        public VertexBuffer GVerts;
        public IndexBuffer GIndices;
        public int PrimCount;

        public bool ClearOnComplete;
        /// <summary>
        /// The basement walls' tops where they're whole: flush with the ground floor, they're lit as it is while it's shown.
        /// Light maps still take them at the basement's height (they're what keep its light in its rooms).
        /// </summary>
        public bool GroundLit;

        public void Complete(GraphicsDevice gd)
        {
            GVerts = new VertexBuffer(gd, typeof(WallVertexRC), Verts.Count, BufferUsage.None);
            GVerts.SetData(Verts.ToArray());
            GIndices = new IndexBuffer(gd, IndexElementSize.ThirtyTwoBits, Indices.Count, BufferUsage.None);
            GIndices.SetData(Indices.ToArray());

            PrimCount = Indices.Count / 3;

            if (ClearOnComplete)
            {
                Verts = null;
                Indices = null;
            }
        }

        public void Dispose()
        {
            GVerts.Dispose();
            GIndices.Dispose();
        }
    }
}
