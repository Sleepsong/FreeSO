using FSO.LotView.Model;
using FSO.LotView.RC;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;

namespace FSO.LotView
{
    /// <summary>
    /// A second, live view of the lot, drawn by World.DrawPicture into its own target: a close look at one point (TS1's
    /// "Live Picture in Picture", in event popups). It keeps its own walls, cut as it asks, so the main view's walls and cut
    /// are left alone. 3D only.
    /// </summary>
    public class WorldPicture : IDisposable
    {
        /// <summary>What it's drawn into (square, with depth and stencil, kept between frames).</summary>
        public RenderTarget2D Target;

        /// <summary>The point it looks at, in tiles: x, height, y (as EntityComponent.GetLookTarget).</summary>
        public Vector3 Focus;
        /// <summary>The floor it shows: floors and walls above it are hidden.</summary>
        public sbyte Level = 1;
        /// <summary>Which tiles' walls are cut down on that floor (as Blueprint.Cutaway); null for walls up.</summary>
        public bool[] Cut;
        /// <summary>How far the camera is from the point, in world units (a tile is 3), and how far it looks down.</summary>
        public float Distance = 11f;
        public float Pitch = 0.6f;

        internal WallComponentRC Walls;
        internal bool[] WallsCut;
        internal sbyte WallsLevel;
        internal WorldRotation WallsRotation;
        internal int WallsSeen = -1;

        public WorldPicture(GraphicsDevice gd, int size)
        {
            Target = new RenderTarget2D(gd, size, size, false, SurfaceFormat.Color, DepthFormat.Depth24Stencil8, 0, RenderTargetUsage.PreserveContents);
        }

        public void Dispose()
        {
            Target?.Dispose();
            Target = null;
            Walls?.Dispose();
            Walls = null;
        }
    }
}
