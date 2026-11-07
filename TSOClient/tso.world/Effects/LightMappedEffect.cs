using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace FSO.LotView.Effects
{
    public class LightMappedEffect : WorldEffect
    {
        private EffectParameter pOutsideDark;
        private EffectParameter pMinAvg;
        private EffectParameter pWorldToLightFactor;
        private EffectParameter pLightOffset;
        private EffectParameter pMapLayout;
        private EffectParameter pLevel;
        private EffectParameter pLightingAdjust;

        private EffectParameter pAdvancedLight;
        private EffectParameter pAdvancedDirection;

        public Vector4 OutsideDark
        {
            set
            {
                pOutsideDark.SetValue(value);
            }
        }
        public Vector2 MinAvg
        {
            set
            {
                pMinAvg?.SetValue(value);
            }
        }
        public Vector3 WorldToLightFactor
        {
            set
            {
                pWorldToLightFactor.SetValue(value);
            }
        }
        private Vector2 _LightOffset;
        public Vector2 LightOffset
        {
            get { return _LightOffset; }
            set
            {
                _LightOffset = value;
                ApplyLightOffset();
            }
        }

        /// <summary>
        /// The light map's cells go floor by floor (see MapLayout), and the shaders find a floor's cell from Level: for the
        /// basement (level 0, Level below 0) that's the cell at (-1, -1), off the map. Moving the light offset on by a whole
        /// map in each direction takes it to the last cell instead, which no floor uses (6 cells, 5 floors): the basement's.
        /// </summary>
        public static readonly Vector2 BasementCellShift = new Vector2(1, 1);

        private bool _BasementLight;
        /// <summary>
        /// Whether what's drawn next is in the basement, so takes its light from the basement's cell of the light map.
        /// Setting <see cref="Level"/> sets this (a level below 0 is the basement); draws that work out their own level from
        /// the height (walls) set it themselves afterwards.
        /// </summary>
        public bool BasementLight
        {
            get { return _BasementLight; }
            set
            {
                if (value == _BasementLight) return;
                _BasementLight = value;
                ApplyLightOffset();
            }
        }

        private void ApplyLightOffset()
        {
            pLightOffset?.SetValue(_BasementLight ? _LightOffset + BasementCellShift : _LightOffset);
        }
        public Vector2 MapLayout
        {
            set
            {
                pMapLayout.SetValue(value);
            }
        }

        private float _CurrentLevel;
        public float Level
        {
            set
            {
                if (value != _CurrentLevel)
                {
                    _CurrentLevel = value;
                    pLevel.SetValue(value);
                }
                BasementLight = value < 0;
            }
        }

        public Texture2D AdvancedLight {
            set
            {
                pAdvancedLight.SetValue(value);
            }
        }
        public Texture2D AdvancedDirection
        {
            set
            {
                pAdvancedDirection?.SetValue(value);
            }
        }

        //should default to 1s. Used to adjust slow updating surround lighting to match main lighting.
        public Vector3 LightingAdjust
        {
            set
            {
                pLightingAdjust?.SetValue(value);
            }
        }

        public LightMappedEffect(GraphicsDevice graphicsDevice, byte[] effectCode) : base(graphicsDevice, effectCode)
        {
        }

        public LightMappedEffect(GraphicsDevice graphicsDevice, byte[] effectCode, int index, int count) : base(graphicsDevice, effectCode, index, count)
        {
        }

        public LightMappedEffect(Effect cloneSource) : base(cloneSource)
        {
        }

        protected override void PrepareParams()
        {
            base.PrepareParams();
            pOutsideDark = Parameters["OutsideDark"];
            pMinAvg = Parameters["MinAvg"];
            pWorldToLightFactor = Parameters["WorldToLightFactor"];
            pLightOffset = Parameters["LightOffset"];
            pMapLayout = Parameters["MapLayout"];
            pLevel = Parameters["Level"];
            pLightingAdjust = Parameters["LightingAdjust"];

            pAdvancedDirection = Parameters["advancedDirection"];
            pAdvancedLight = Parameters["advancedLight"];
        }
    }

    public enum LightingEmptyTechniques
    {

    }
}
