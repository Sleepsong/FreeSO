using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace FSO.Common.Rendering.Framework.IO
{
    /// <summary>
    /// Decides whether the mouse or the gamepad is driving the pointer. Whichever was used most recently wins.
    /// Keyboard input never changes the device, and the mouse wins ties.
    /// </summary>
    public class PointerDeviceTracker
    {
        /// <summary>
        /// Pixels the mouse must move before it counts as the player using it. Absorbs rounding when the OS
        /// reports a warped cursor position back to us.
        /// </summary>
        public const int MoveTolerance = 3;

        private MouseState LastReal;
        private bool HadFocus;

        /// <param name="current">The device chosen last frame.</param>
        /// <param name="real">This frame's hardware mouse state.</param>
        /// <param name="padActive">The pad was newly used this frame (GamePadInput.Active).</param>
        /// <param name="lastWarp">Where the virtual cursor last moved the OS cursor, if anywhere.</param>
        /// <returns>The device for this frame.</returns>
        public InputDevice Update(InputDevice current, MouseState real, bool hasFocus, bool padActive, Point? lastWarp)
        {
            if (!hasFocus)
            {
                //the mouse state is zeroed while unfocused, so regaining focus must not look like mouse movement.
                HadFocus = false;
                return current;
            }

            var result = current;
            if (HadFocus && MouseWasUsed(real, lastWarp)) result = InputDevice.Mouse;
            else if (padActive) result = InputDevice.GamePad;

            LastReal = real;
            HadFocus = true;
            return result;
        }

        private bool MouseWasUsed(MouseState real, Point? lastWarp)
        {
            var last = LastReal;
            var moved = Distance(real.Position, last.Position) > MoveTolerance
                && (lastWarp == null || Distance(real.Position, lastWarp.Value) > MoveTolerance);

            var clicked = (real.LeftButton == ButtonState.Pressed && last.LeftButton == ButtonState.Released)
                || (real.RightButton == ButtonState.Pressed && last.RightButton == ButtonState.Released)
                || (real.MiddleButton == ButtonState.Pressed && last.MiddleButton == ButtonState.Released);

            var scrolled = real.ScrollWheelValue != last.ScrollWheelValue
                || real.HorizontalScrollWheelValue != last.HorizontalScrollWheelValue;

            return moved || clicked || scrolled;
        }

        private static int Distance(Point a, Point b)
        {
            return Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
        }
    }
}
