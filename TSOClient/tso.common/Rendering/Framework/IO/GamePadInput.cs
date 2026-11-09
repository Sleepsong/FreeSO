using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace FSO.Common.Rendering.Framework.IO
{
    /// <summary>
    /// The pointing device the player used most recently. Keyboard input is neutral and never changes it.
    /// Frontends use this to choose between mouse affordances (the OS cursor) and gamepad affordances
    /// (selector, focus highlight, button prompts).
    /// </summary>
    public enum InputDevice
    {
        Mouse,
        Touch,
        GamePad
    }

    /// <summary>
    /// Semantic gamepad actions. Consumers query these rather than raw buttons so bindings can be remapped
    /// (for example a Nintendo-style confirm/cancel swap) without touching the consumers.
    /// Up/Down/Left/Right are the D-pad only; use <see cref="GamePadInput.NavPressedRepeat"/> for
    /// navigation that should also accept the left stick.
    /// </summary>
    public enum PadAction
    {
        Confirm,
        Cancel,
        FaceLeft,
        FaceTop,
        ShoulderLeft,
        ShoulderRight,
        TriggerLeft,
        TriggerRight,
        Up,
        Down,
        Left,
        Right,
        Start,
        Select,
        LeftStickClick,
        RightStickClick
    }

    public enum PadDirection
    {
        Up,
        Down,
        Left,
        Right
    }

    /// <summary>
    /// Polls the connected gamepad in use (see <see cref="ChoosePad"/>) once per frame. Exposes dead-zoned analog values and
    /// press/release/auto-repeat edges for each <see cref="PadAction"/>. Lives on UpdateState.Pad.
    /// </summary>
    public class GamePadInput
    {
        public const int ActionCount = (int)PadAction.RightStickClick + 1;
        private const int StickSlot = ActionCount; //four extra slots for left stick directions, in PadDirection order
        private const int SlotCount = ActionCount + 4;

        public const float TriggerThreshold = 0.5f;
        private const float TriggerDeadZone = 0.1f;
        private const float StickNavThreshold = 0.5f;
        private const double RepeatDelay = 0.4; //seconds a button is held before it starts repeating
        private const double RepeatInterval = 0.1;
        private const int MaxPads = 8; //pad slots looked at

        public bool Connected { get; private set; }
        public int PlayerIndex { get; private set; } = -1;

        /// <summary>The connected pad's name as the platform reports it (e.g. for <see cref="PadGlyphs"/>), or "".</summary>
        public string DisplayName { get; private set; } = "";

        /// <summary>Raw state of the active pad (no dead zone applied).</summary>
        public GamePadState State { get; private set; }

        /// <summary>Left stick with the radial dead zone applied and rescaled to 0-1. +Y is up.</summary>
        public Vector2 LeftStick { get; private set; }
        public Vector2 RightStick { get; private set; }
        public float LeftTrigger { get; private set; }
        public float RightTrigger { get; private set; }

        /// <summary>
        /// True if the player started using the pad this frame: a button was pressed, or a stick or trigger
        /// left its rest position. Held input does not count, so a drifting stick can't steal focus from the mouse.
        /// </summary>
        public bool Active { get; private set; }

        /// <summary>Any physical button is down (d-pad included, triggers not), bound to an action or not.</summary>
        public bool AnyButtonDown { get; private set; }

        /// <summary>The pad was used this frame while the window was treated as unfocused (so it was ignored).</summary>
        public bool UsedWhileUnfocused { get; private set; }

        private readonly bool[] Down = new bool[SlotCount];
        private readonly bool[] WasDown = new bool[SlotCount];
        private readonly bool[] Repeat = new bool[SlotCount];
        private readonly double[] NextRepeat = new double[SlotCount];
        private readonly GamePadState[] PadStates = new GamePadState[MaxPads];

        public bool IsDown(PadAction action) => Down[(int)action];
        public bool Pressed(PadAction action) => Down[(int)action] && !WasDown[(int)action];
        public bool Released(PadAction action) => !Down[(int)action] && WasDown[(int)action];

        /// <summary>Pressed this frame, or held long enough to auto-repeat. Use for list/menu navigation.</summary>
        public bool PressedRepeat(PadAction action) => Pressed(action) || Repeat[(int)action];

        /// <summary>The left stick pushed past the navigation threshold in a direction, with auto-repeat.</summary>
        public bool StickPressedRepeat(PadDirection dir)
        {
            var i = StickSlot + (int)dir;
            return (Down[i] && !WasDown[i]) || Repeat[i];
        }

        /// <summary>Navigation in a direction from either the D-pad or the left stick, with auto-repeat.</summary>
        public bool NavPressedRepeat(PadDirection dir)
        {
            return PressedRepeat(DirectionAction(dir)) || StickPressedRepeat(dir);
        }

        private static PadAction DirectionAction(PadDirection dir)
        {
            switch (dir)
            {
                case PadDirection.Up: return PadAction.Up;
                case PadDirection.Down: return PadAction.Down;
                case PadDirection.Left: return PadAction.Left;
                default: return PadAction.Right;
            }
        }

        public void Update(GameTime time, bool focused)
        {
            var now = time.TotalGameTime.TotalSeconds;
            Process(Poll(now), now, focused);
        }

        /// <summary>
        /// Advances one frame using an already-read pad state. Update() calls this with the polled hardware state;
        /// it is public so input can be injected (tests, replays).
        /// </summary>
        /// <param name="now">Current time in seconds; drives auto-repeat.</param>
        public void Process(GamePadState state, double now, bool focused)
        {
            Array.Copy(Down, WasDown, SlotCount);
            Active = false;
            AnyButtonDown = false;
            Connected = state.IsConnected;
            UsedWhileUnfocused = Connected && !focused && GamePadSettings.Enabled && InUse(state);

            if (!Connected || !focused || !GamePadSettings.Enabled)
            {
                State = default(GamePadState);
                LeftStick = RightStick = Vector2.Zero;
                LeftTrigger = RightTrigger = 0;
                Array.Clear(Down, 0, SlotCount);
                Array.Clear(Repeat, 0, SlotCount);
                return;
            }

            State = state;
            var lastLeft = LeftStick;
            var lastRight = RightStick;
            var lastLT = LeftTrigger;
            var lastRT = RightTrigger;

            var deadZone = GamePadSettings.DeadZone;
            LeftStick = ApplyRadialDeadZone(state.ThumbSticks.Left, deadZone);
            RightStick = ApplyRadialDeadZone(state.ThumbSticks.Right, deadZone);
            LeftTrigger = ApplyTriggerDeadZone(state.Triggers.Left);
            RightTrigger = ApplyTriggerDeadZone(state.Triggers.Right);

            var bindings = GamePadSettings.Bindings;
            for (int i = 0; i < ActionCount; i++)
            {
                var down = false;
                foreach (var button in bindings.Get((PadAction)i))
                {
                    if (IsButtonDown(ref state, button))
                    {
                        down = true;
                        break;
                    }
                }
                Down[i] = down;
            }

            Down[StickSlot + (int)PadDirection.Up] = LeftStick.Y > StickNavThreshold;
            Down[StickSlot + (int)PadDirection.Down] = LeftStick.Y < -StickNavThreshold;
            Down[StickSlot + (int)PadDirection.Left] = LeftStick.X < -StickNavThreshold;
            Down[StickSlot + (int)PadDirection.Right] = LeftStick.X > StickNavThreshold;

            for (int i = 0; i < SlotCount; i++)
            {
                Repeat[i] = false;
                if (!Down[i]) continue;
                if (!WasDown[i])
                {
                    NextRepeat[i] = now + RepeatDelay;
                    Active = true;
                }
                else if (now >= NextRepeat[i])
                {
                    Repeat[i] = true;
                    NextRepeat[i] = now + RepeatInterval;
                }
            }

            AnyButtonDown = IsAnyButtonDown(ref state);

            if ((lastLeft == Vector2.Zero && LeftStick != Vector2.Zero)
                || (lastRight == Vector2.Zero && RightStick != Vector2.Zero)
                || (lastLT == 0 && LeftTrigger > 0)
                || (lastRT == 0 && RightTrigger > 0))
            {
                Active = true;
            }
        }

        private GamePadState Poll(double now)
        {
            if (!GamePadSettings.Enabled)
            {
                PlayerIndex = -1;
                DisplayName = "";
                return default(GamePadState);
            }

            var count = Math.Min(MaxPads, GamePad.MaximumGamePadCount);
            for (int i = 0; i < count; i++) PadStates[i] = GamePad.GetState(i, GamePadDeadZone.None, GamePadDeadZone.None);
            var chosen = ChoosePad(PlayerIndex, PadStates, count);
            if (chosen != PlayerIndex)
            {
                PlayerIndex = chosen;
                DisplayName = (chosen == -1) ? "" : (GamePad.GetCapabilities(chosen).DisplayName ?? "");
            }
            return (chosen == -1) ? default(GamePadState) : PadStates[chosen];
        }

        /// <summary>
        /// Which of the connected pads to read (an index into <paramref name="states"/>, or -1 for none): the current
        /// one while it's being used; else another that's being used; else the current one; else the first connected.
        /// Several pads can be connected for one controller (Steam can show a game more than one copy of a controller,
        /// and only one of them carries its input), so whichever the player is actually pressing wins.
        /// </summary>
        public static int ChoosePad(int current, GamePadState[] states, int count)
        {
            bool Connected(int i) => i >= 0 && i < count && states[i].IsConnected;
            if (Connected(current) && InUse(states[current])) return current;
            for (int i = 0; i < count; i++)
            {
                if (i != current && Connected(i) && InUse(states[i])) return i;
            }
            if (Connected(current)) return current;
            for (int i = 0; i < count; i++)
            {
                if (Connected(i)) return i;
            }
            return -1;
        }

        /// <summary>The player is using this pad: a button down, or a stick or trigger pushed past halfway.</summary>
        public static bool InUse(GamePadState state)
        {
            return IsAnyButtonDown(ref state)
                || state.ThumbSticks.Left.Length() > 0.5f || state.ThumbSticks.Right.Length() > 0.5f
                || state.Triggers.Left > 0.5f || state.Triggers.Right > 0.5f;
        }

        private static readonly Buttons[] PhysicalButtons =
        {
            Buttons.A, Buttons.B, Buttons.X, Buttons.Y, Buttons.Back, Buttons.Start, Buttons.BigButton,
            Buttons.LeftShoulder, Buttons.RightShoulder, Buttons.LeftStick, Buttons.RightStick,
            Buttons.DPadUp, Buttons.DPadDown, Buttons.DPadLeft, Buttons.DPadRight
        };

        /// <summary>Any physical button down, bound to an action or not.</summary>
        private static bool IsAnyButtonDown(ref GamePadState state)
        {
            foreach (var button in PhysicalButtons)
            {
                if (IsButtonDown(ref state, button)) return true;
            }
            return false;
        }

        private static bool IsButtonDown(ref GamePadState state, Buttons button)
        {
            //triggers and the d-pad are evaluated directly so the result doesn't depend on
            //how the platform backend synthesises their virtual button flags.
            switch (button)
            {
                case Buttons.LeftTrigger: return state.Triggers.Left > TriggerThreshold;
                case Buttons.RightTrigger: return state.Triggers.Right > TriggerThreshold;
                case Buttons.DPadUp: return state.DPad.Up == ButtonState.Pressed;
                case Buttons.DPadDown: return state.DPad.Down == ButtonState.Pressed;
                case Buttons.DPadLeft: return state.DPad.Left == ButtonState.Pressed;
                case Buttons.DPadRight: return state.DPad.Right == ButtonState.Pressed;
                default: return state.IsButtonDown(button);
            }
        }

        /// <summary>
        /// Zeroes the stick inside a circle of radius <paramref name="deadZone"/>, then rescales the remainder to 0-1
        /// so output starts smoothly at the edge of the dead zone instead of jumping.
        /// </summary>
        public static Vector2 ApplyRadialDeadZone(Vector2 stick, float deadZone)
        {
            var length = stick.Length();
            if (length <= deadZone) return Vector2.Zero;
            var scaled = Math.Min(1f, (length - deadZone) / (1f - deadZone));
            return stick * (scaled / length);
        }

        private static float ApplyTriggerDeadZone(float value)
        {
            if (value <= TriggerDeadZone) return 0;
            return Math.Min(1f, (value - TriggerDeadZone) / (1f - TriggerDeadZone));
        }
    }
}
