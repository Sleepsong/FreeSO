using System;
using FSO.Common.Rendering.Framework.Model;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace FSO.Common.Rendering.Framework.IO
{
    /// <summary>
    /// Fallback pointer for screens with no native gamepad support. While the pad is the active device it
    /// replaces the frame's mouse state:
    /// <list type="bullet">
    /// <item>left stick moves the cursor (quadratic response for precision near the centre)</item>
    /// <item>Confirm = left button, FaceLeft = right button, FaceTop = middle button</item>
    /// <item>right trigger / left trigger scroll the wheel up / down</item>
    /// <item>Cancel holds Escape</item>
    /// </list>
    /// The OS cursor is warped to the virtual position so the game's normal cursor graphics appear there.
    /// </summary>
    public class VirtualCursor
    {
        private const float BaseSpeed = 1000f; //px per second at full deflection, for a 1080px tall window
        private const float WheelRate = 1200f; //wheel units per second at full trigger (120 = one notch)

        private Vector2 Position;
        private float WheelRemainder;

        /// <summary>
        /// Total synthetic scroll so far. It's added to the real wheel value in every mode, so the value
        /// consumers see stays continuous when the player swaps between mouse and pad.
        /// </summary>
        public int WheelOffset { get; private set; }

        /// <summary>Where the OS cursor was last warped to, so the warp isn't mistaken for the player moving the mouse.</summary>
        public Point? LastWarp { get; private set; }

        /// <summary>Moves the OS cursor. Replaceable so the cursor can run without a window (tests).</summary>
        public Action<int, int> Warp = Mouse.SetPosition;

        public void MoveTo(Point position, int width, int height)
        {
            Position = Clamp(position.ToVector2(), width, height);
        }

        /// <summary>Replaces the frame's mouse and keyboard state with the gamepad-driven equivalents.</summary>
        public void Apply(UpdateState state, GameTime time, int width, int height)
        {
            var pad = state.Pad;
            var dt = (float)Math.Min(time.ElapsedGameTime.TotalSeconds, 0.1);

            var stick = pad.LeftStick;
            if (stick != Vector2.Zero)
            {
                //velocity grows with the square of deflection: small pushes for precise aiming, full tilt to cross the screen.
                var speed = BaseSpeed * GamePadSettings.CursorSpeed * (height / 1080f) * stick.Length();
                Position = Clamp(Position + new Vector2(stick.X, -stick.Y) * speed * dt, width, height);
            }

            WheelRemainder += (pad.RightTrigger - pad.LeftTrigger) * WheelRate * dt;
            var wholeTicks = (int)WheelRemainder;
            WheelRemainder -= wholeTicks;
            WheelOffset += wholeTicks;

            var real = state.MouseState;
            var point = new Point((int)Position.X, (int)Position.Y);
            state.MouseState = new MouseState(
                point.X, point.Y,
                real.ScrollWheelValue, //WheelOffset is applied by the caller in every mode
                pad.IsDown(PadAction.Confirm) ? ButtonState.Pressed : ButtonState.Released,
                pad.IsDown(PadAction.FaceTop) ? ButtonState.Pressed : ButtonState.Released,
                pad.IsDown(PadAction.FaceLeft) ? ButtonState.Pressed : ButtonState.Released,
                ButtonState.Released,
                ButtonState.Released,
                real.HorizontalScrollWheelValue);

            if (pad.IsDown(PadAction.Cancel) && !state.KeyboardState.IsKeyDown(Keys.Escape))
            {
                var held = state.KeyboardState.GetPressedKeys();
                var keys = new Keys[held.Length + 1];
                held.CopyTo(keys, 0);
                keys[held.Length] = Keys.Escape;
                state.KeyboardState = new KeyboardState(keys, state.KeyboardState.CapsLock, state.KeyboardState.NumLock);
            }

            if (LastWarp != point)
            {
                Warp(point.X, point.Y);
                LastWarp = point;
            }
        }

        private static Vector2 Clamp(Vector2 position, int width, int height)
        {
            //stay strictly inside the window: the UI layer treats the edge pixels as "outside" and stops dispatching events.
            return new Vector2(
                Math.Clamp(position.X, 1, Math.Max(1, width - 2)),
                Math.Clamp(position.Y, 1, Math.Max(1, height - 2)));
        }
    }
}
