using FSO.LotView.Model;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;

namespace FSO.LotView.LMap
{
    public class GPURoomMaps : IDisposable
    {
        public List<Texture2D> RoomMaps;
        private GraphicsDevice GD;

        public GPURoomMaps(GraphicsDevice device)
        {
            GD = device;
        }

        public void Init(Blueprint blueprint)
        {
            var w = blueprint.Width;
            var h = blueprint.Height;

            RoomMaps = new List<Texture2D>();
            for (int i = 0; i <= BasementSlot; i++) RoomMaps.Add(new Texture2D(GD, w, h, false, SurfaceFormat.Color));
        }

        /// <summary>Where the basement's (level 0's) room map is kept: after the 5 floors'.</summary>
        public const int BasementSlot = 5;

        /// <summary>The slot for a floor by level - 1: the basement (-1) is kept after the others.</summary>
        public static int Slot(int floor)
        {
            return (floor < 0) ? BasementSlot : floor;
        }

        /// <summary>A floor's room map, by level - 1 (the basement is -1).</summary>
        public Texture2D ForFloor(int floor)
        {
            return RoomMaps[Slot(floor)];
        }

        public void SetRoomMap(sbyte floor, uint[] map)
        {
            if (map == null) return;
            RoomMaps[Slot(floor)].SetData(map);
        }

        public void Dispose()
        {
            if (RoomMaps != null)
            {
                foreach (var map in RoomMaps)
                {
                    map?.Dispose();
                }
            }
        }
    }
}
