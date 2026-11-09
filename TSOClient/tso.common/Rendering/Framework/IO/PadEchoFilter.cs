using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using FSO.Common.Rendering.Framework.Model;

namespace FSO.Common.Rendering.Framework.IO
{
    /// <summary>
    /// Drops keyboard and mouse input that arrives while the pad is being held. Steam Input's desktop layout (and
    /// other remappers) send keyboard and mouse events for pad buttons and sticks on top of the pad itself, so
    /// pressing Select also presses Tab, the right stick also moves the mouse, a trigger also clicks. While the pad is
    /// held, and for <see cref="Grace"/> seconds after, the keyboard reads as empty, typed text is dropped, and the
    /// mouse stays where it was with its buttons up. Nobody types or uses the mouse with a thumb on the pad, so nothing
    /// real is lost, and the mouse and keyboard work as normal once the pad is let go.
    /// <para>
    /// "Held" is any button down, a trigger past <see cref="TriggerHeld"/>, a stick past <see cref="StickHeld"/>, or a
    /// stick off centre that moved in the last <see cref="StickSettle"/> seconds. A stick resting a little off centre
    /// (drift past the dead zone) doesn't count, so it can't lock the mouse out.
    /// </para>
    /// </summary>
    public class PadEchoFilter
    {
        /// <summary>Seconds after the pad is let go that echoes are still dropped (key-ups and clicks can lag a little).</summary>
        public const float Grace = 0.25f;
        public const float TriggerHeld = 0.3f;
        public const float StickHeld = 0.5f;
        public const float StickSettle = 1f;
        private const float StickMotion = 0.02f; //per frame

        private float SinceHeld = float.MaxValue;
        private float SinceStickMoved = float.MaxValue;
        private Vector2 LastLeft, LastRight;
        private MouseState Held;

        /// <summary>Whether the pad counted as held this frame.</summary>
        public bool PadHeld { get; private set; }

        /// <summary>Whether input was dropped this frame.</summary>
        public bool Filtering { get; private set; }

        /// <param name="enabled">The setting, and whether the window has focus.</param>
        public void Apply(UpdateState state, float dt, bool enabled)
        {
            var pad = state.Pad;
            PadHeld = false;
            if (pad.Connected)
            {
                var moved = (pad.LeftStick - LastLeft).Length() > StickMotion || (pad.RightStick - LastRight).Length() > StickMotion;
                SinceStickMoved = moved ? 0 : SinceStickMoved + dt;
                var stick = System.Math.Max(pad.LeftStick.Length(), pad.RightStick.Length());
                PadHeld = pad.AnyButtonDown || pad.LeftTrigger > TriggerHeld || pad.RightTrigger > TriggerHeld
                    || stick > StickHeld || (stick > 0 && SinceStickMoved < StickSettle);
                LastLeft = pad.LeftStick;
                LastRight = pad.RightStick;
            }
            SinceHeld = PadHeld ? 0 : SinceHeld + dt;
            Filtering = enabled && SinceHeld <= Grace;
            if (!Filtering)
            {
                Held = state.MouseState;
                return;
            }

            state.KeyboardState = new KeyboardState();
            state.FrameTextInput = new System.Collections.Generic.List<char>();
            state.MouseState = new MouseState(Held.X, Held.Y, Held.ScrollWheelValue,
                ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released,
                Held.HorizontalScrollWheelValue);
        }
    }
}
