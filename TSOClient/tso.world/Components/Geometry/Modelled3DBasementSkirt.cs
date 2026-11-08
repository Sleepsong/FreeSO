using FSO.LotView.Model;
using FSO.LotView.Utils;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System.Collections.Generic;

namespace FSO.LotView.Components.Geometry
{
    /// <summary>
    /// Black below the basement's floor all round its edge, down past a pool's depth: while the basement's shown on the void
    /// (see WorldStatic.DrawBg), what's under its floor (a swimming Sim's legs, the void's grid under a basement raised by a
    /// foundation) would otherwise show beyond its edge. Black, it's just more of the void. Drawn with the basement's floor.
    /// </summary>
    public class Modelled3DBasementSkirt : Modelled3DFoundation
    {
        /// <summary>How far below the floor it goes (tile units): past a pool's depth, and a foundation's height.</summary>
        public const float Depth = 2.5f;

        private static Texture2D Black;

        public Modelled3DBasementSkirt(Blueprint bp) : base(bp, 0)
        {
        }

        public override void PrepareGPU(GraphicsDevice gd)
        {
            if (Black == null || Black.IsDisposed || Black.GraphicsDevice != gd)
            {
                Black = new Texture2D(gd, 1, 1);
                Black.SetData(new Color[] { Color.Black });
            }
            Texture = Black;
            base.PrepareGPU(gd);
        }

        protected override void GenerateGeometry()
        {
            var verts = new List<TerrainParallaxVertex>();
            var inds = new List<int>();
            var map = Bp.Basement;
            if (map != null && map.Any)
            {
                for (int y = 0; y < map.Height; y++)
                {
                    for (int x = 0; x < map.Width; x++)
                    {
                        if (!map.IsDug(x, y)) continue;
                        for (int dir = 0; dir < 4; dir++)
                        {
                            if (!map.EarthSide(x, y, dir)) continue; //(walls on the edge or not: they don't go below the floor)
                            var d = BasementMap.Directions[dir];
                            Point a, b;
                            switch (dir)
                            {
                                case 0: a = new Point(x, y); b = new Point(x + 1, y); break;
                                case 1: a = new Point(x + 1, y); b = new Point(x + 1, y + 1); break;
                                case 2: a = new Point(x + 1, y + 1); b = new Point(x, y + 1); break;
                                default: a = new Point(x, y + 1); b = new Point(x, y); break;
                            }
                            var raise = Bp.FoundationAt(x, y);
                            var ga = GroundAtVertex(a.X, a.Y) + raise;
                            var gb = GroundAtVertex(b.X, b.Y) + raise;
                            var normal = new Vector3(-d.X, 0, -d.Y);
                            AddQuad(verts, inds,
                                new Vector3(a.X, ga - Depth, a.Y), new Vector3(b.X, gb - Depth, b.Y),
                                new Vector3(b.X, gb, b.Y), new Vector3(a.X, ga, a.Y), normal,
                                Vector2.Zero, Vector2.Zero, Vector2.Zero, Vector2.Zero);
                        }
                    }
                }
            }
            Vertices = verts.ToArray();
            Indices = inds.ToArray();
        }
    }
}
