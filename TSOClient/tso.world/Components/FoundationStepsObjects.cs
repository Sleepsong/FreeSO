using FSO.Content;
using FSO.LotView.Components.Geometry;
using FSO.LotView.Model;
using Microsoft.Xna.Framework;
using System.Collections.Generic;

namespace FSO.LotView.Components
{
    /// <summary>
    /// A staircase's bottom piece, standing on a foundation's steps tile to be its steps: only something to see (the
    /// simulation knows nothing of it, and Sims walk the steps as the foundation's), on the ground, not partway up the
    /// steps as things on them would be.
    /// </summary>
    public class FoundationStepComponent : ObjectComponent
    {
        public uint Style;
        public int StepDirection;

        public FoundationStepComponent(GameObject obj) : base(obj)
        {
        }

        public override Vector3 Position
        {
            get => base.Position;
            set
            {
                base.Position = value;
                if (blueprint != null)
                {
                    _Position.Z = value.Z + blueprint.TerrainAltitude(new Vector3(0.5f, 0.5f, 0) + value);
                    _WorldDirty = true;
                }
            }
        }
    }

    /// <summary>
    /// Foundation steps as the bottom pieces of the game's staircases (see <see cref="FoundationLook.StairPiece"/>): one
    /// object for each set of steps whose staircase the game has, kept in step with the lot's foundations. A staircase
    /// rises a storey over four tiles, so its bottom piece rises a quarter of one over a tile: a foundation's height.
    /// </summary>
    public class FoundationStepsObjects
    {
        private Blueprint Bp;
        private Dictionary<int, FoundationStepComponent> Placed = new Dictionary<int, FoundationStepComponent>();

        public FoundationStepsObjects(Blueprint bp)
        {
            Bp = bp;
        }

        /// <summary>The object facing for steps rising toward a direction (see <see cref="FoundationMap.Directions"/>): a staircase faces the way it rises.</summary>
        public static Direction FacingFor(int stepDirection) => (Direction)(1 << (stepDirection * 2));

        /// <summary>Adds, turns, changes and removes the steps' objects to match the lot's foundations.</summary>
        public void Sync()
        {
            var map = Bp.Foundations;
            var seen = new HashSet<int>();
            if (map != null && map.Any)
            {
                for (int y = 0; y < map.Height; y++)
                {
                    for (int x = 0; x < map.Width; x++)
                    {
                        var i = y * map.Width + x;
                        if (map.Steps[i] == 0 || map.StepTop(x, y) <= 0) continue;
                        var style = map.StepStyle[i];
                        var piece = FoundationLook.StairPiece(style);
                        if (piece == null) continue; //drawn plain, with the foundations
                        var dir = map.StepDirection(x, y);
                        seen.Add(i);
                        if (Placed.TryGetValue(i, out var comp))
                        {
                            if (comp.Style == style && comp.StepDirection == dir)
                            {
                                Place(comp, x, y); //(the ground or the rooms may have changed)
                                continue;
                            }
                            Remove(i);
                        }
                        comp = new FoundationStepComponent(piece) { Style = style, StepDirection = dir };
                        Bp.AddObject(comp);
                        Bp.ChangeObjectLocation(comp, LotTilePos.FromBigTile((short)x, (short)y, 1));
                        comp.Direction = FacingFor(dir);
                        Place(comp, x, y);
                        Placed[i] = comp;
                    }
                }
            }
            foreach (var i in new List<int>(Placed.Keys))
                if (!seen.Contains(i)) Remove(i);
        }

        private void Place(FoundationStepComponent comp, int x, int y)
        {
            var rooms = (Bp.RoomMap != null && Bp.RoomMap.Length > 0) ? Bp.RoomMap[0] : null;
            var index = y * Bp.Width + x;
            var room = (rooms != null && index < rooms.Length) ? (ushort)(rooms[index] & 0xFFFF) : (ushort)1;
            comp.Room = (room == 0) ? (ushort)1 : room;
            comp.Position = new Vector3(x, y, 0);
        }

        private void Remove(int index)
        {
            if (!Placed.TryGetValue(index, out var comp)) return;
            Bp.RemoveObject(comp);
            Placed.Remove(index);
        }
    }
}
