using FSO.Common.Rendering.Framework.IO;
using FSO.Common.Rendering.Framework.Model;
using FSO.Common.Utils;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Microsoft.Xna.Framework.Input.Touch;
using System.Collections.Generic;
using System.Linq;

namespace FSO.Common.Rendering.Framework
{
    /// <summary>
    /// A screen used for drawing.
    /// </summary>
    public class GameScreen
    {
        public List<IGraphicsLayer> Layers = new List<IGraphicsLayer>();
        public GraphicsDevice Device;
        public UpdateState State;

        public static Color ClearColor = new Color(0x72, 0x72, 0x72);

        private int touchedFrames;
		private int lastTouchCount;
		private MouseState lastMouseState;
        private Vector2? prevTouchAvg;
        private const int TOUCH_ACCEPT_TIME = 5;

        public VirtualCursor VirtualCursor = new VirtualCursor();
        private PointerDeviceTracker PointerDevice = new PointerDeviceTracker();
        private PadEchoFilter EchoFilter = new PadEchoFilter();
        private bool WasCapturedBeforeCursor;

        public GameScreen(GraphicsDevice device)
        {
            this.Device = device;

            State = new UpdateState();
        }

        private static List<char> TextCharacters = new List<char>();
        public static void TextInput(object sender, TextInputEventArgs e)
        {
            TextCharacters.Add(e.Character);
        }

        /// <summary>
        /// Adds a graphical element to this scene.
        /// </summary>
        /// <param name="layer">Element inheriting from IGraphicsLayer.</param>
        public void Add(IGraphicsLayer layer)
        {
            layer.Initialize(Device);
            Layers.Add(layer);
        }

        public void Update(GameTime time, bool hasFocus)
        {
            State.Time = time;
            State.PreviousKeyboardState = State.KeyboardState;
            State.FrameTextInput = TextCharacters;

            var touchMode = FSOEnvironment.SoftwareKeyboard;
            State.Pad.Update(time, hasFocus);
            State.PadWasCaptured = State.PadCaptured;
            State.PadCaptured = false;
            State.PadTextEntry = false;

            if (touchMode)
            {
                if (FSOEnvironment.SoftwareDepth) State.KeyboardState = new KeyboardState();
                TouchCollection touches = TouchPanel.GetState();
                if (touches.Count > 0) State.LastInputDevice = InputDevice.Touch;
                else if (State.Pad.Active) State.LastInputDevice = InputDevice.GamePad;

                var missing = new HashSet<MultiMouse>(State.MouseStates);
                //relate touches to their last virtual mouse
                foreach (var touch in touches)
                {
                    var mouse = State.MouseStates.FirstOrDefault(x => x.ID == touch.Id);
                    if (mouse == null)
                    {
                        mouse = new MultiMouse { ID = touch.Id };
                        State.MouseStates.Add(mouse);
                    }
                    missing.Remove(mouse);

                    mouse.MouseState = new MouseState(
                        (int)touch.Position.X, (int)touch.Position.Y, 0,
                        ButtonState.Pressed,
                        ButtonState.Released,
                        ButtonState.Released,
                        ButtonState.Released,
                        ButtonState.Released
                        );
                }
                
                //if virtual mouses no longer have their touch, they are performing a "mouse up"
                //if the state has mouseovers, we should record the mouse state as being lifted.
                foreach (var miss in missing)
                {
                    if (miss.LastMouseOver == null && miss.LastMouseDown == null)
                    {
                        State.MouseStates.Remove(miss);
                    } else
                    {
                        miss.MouseState = new MouseState(miss.MouseState.X, miss.MouseState.Y, 0, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released);
                        miss.Dead = true;
                    }
                }
            }
            else
            {
                //single mouse state
                if (hasFocus)
                {
                    State.MouseState = Mouse.GetState();
                    State.KeyboardState = Keyboard.GetState();
                }
                else
                {
                    State.MouseState = new MouseState();
                    State.KeyboardState = new KeyboardState();
                }

                var raw = State.MouseState;
                EchoFilter.Apply(State, (float)time.ElapsedGameTime.TotalSeconds, hasFocus && GamePadSettings.IgnoreEchoes);
                UpdatePointerDevice(time, hasFocus, raw, EchoFilter.Filtering);

                if (State.KeyboardState.IsKeyDown(Keys.LeftAlt) && State.MouseState.LeftButton == ButtonState.Pressed)
                {
                    //emulated middle click with alt
                    var ms = State.MouseState;
                    State.MouseState = new MouseState(ms.X, ms.Y, ms.ScrollWheelValue, ButtonState.Released, ButtonState.Pressed, ms.RightButton, ms.XButton1, ms.XButton2);
                }

                if (State.MouseStates.Count == 0)
                {
                    State.MouseStates.Add(new MultiMouse { ID = 1 });
                }

                State.MouseStates[0].MouseState = State.MouseState;
            }


            State.SharedData.Clear();
            State.Update();

            GameThread.DigestUpdate(State);

            foreach (var layer in Layers){
                layer.Update(State);
            }

            TextCharacters.Clear();
        }

        /// <summary>
        /// Picks the pointer device for this frame and, while it's the pad, replaces State.MouseState with the
        /// virtual cursor.
        /// </summary>
        /// <param name="raw">The hardware mouse this frame, before the echo filter.</param>
        /// <param name="ignoreMouse">The echo filter is dropping mouse input this frame.</param>
        private void UpdatePointerDevice(GameTime time, bool hasFocus, MouseState raw, bool ignoreMouse)
        {
            var previous = State.LastInputDevice;
            var real = State.MouseState;
            State.LastInputDevice = PointerDevice.Update(previous, raw, hasFocus, State.Pad.Active, VirtualCursor.LastWarp, ignoreMouse);
            if (!hasFocus) return;

            var width = Device.PresentationParameters.BackBufferWidth;
            var height = Device.PresentationParameters.BackBufferHeight;

            var captured = State.PadWasCaptured;
            if (State.LastInputDevice == InputDevice.GamePad && GamePadSettings.VirtualCursor && !captured)
            {
                if (previous != InputDevice.GamePad)
                {
                    //start from wherever the mouse was, so picking up the pad doesn't make the cursor jump.
                    VirtualCursor.MoveTo(real.Position, width, height);
                }
                else if (WasCapturedBeforeCursor)
                {
                    //a screen just handed the pad back: start where it asked (e.g. on what it had focused), else in
                    //the centre (e.g. a dialog opened over the lot: dialogs appear centred).
                    VirtualCursor.MoveTo(State.PadCursorStart ?? new Point(width / 2, height / 2), width, height);
                }
                VirtualCursor.Apply(State, time, width, height);
            }
            WasCapturedBeforeCursor = captured;
            if (!captured) State.PadCursorStart = null; //only for the hand-over

            if (VirtualCursor.WheelOffset != 0)
            {
                var ms = State.MouseState;
                State.MouseState = new MouseState(ms.X, ms.Y, ms.ScrollWheelValue + VirtualCursor.WheelOffset,
                    ms.LeftButton, ms.MiddleButton, ms.RightButton, ms.XButton1, ms.XButton2, ms.HorizontalScrollWheelValue);
            }
        }

        private void TouchStub(UpdateState state)
        {
            var test = TouchPanel.EnableMouseTouchPoint;
            TouchCollection touches = TouchPanel.GetState();
			if (touches.Count != lastTouchCount) touchedFrames = 0;
			lastTouchCount = touches.Count;
            if (touches.Count > 0)
            {
				Vector2 avg = new Vector2();
				for (int i = 0; i < touches.Count; i++)
				{
					avg += touches[i].Position;
				}
				avg /= touches.Count;

                if (touchedFrames < TOUCH_ACCEPT_TIME)
                {
                    avg = prevTouchAvg ?? avg;
					state.MouseState = new MouseState(
						(int)avg.X, (int)avg.Y, state.MouseState.ScrollWheelValue,
						ButtonState.Released,
						ButtonState.Released,
						ButtonState.Released,
						ButtonState.Released,
						ButtonState.Released
					);
                    touchedFrames++;
                }
                else
                {
                    state.MouseState = new MouseState(
                        (int)avg.X, (int)avg.Y, state.MouseState.ScrollWheelValue,
						(touches.Count > 1) ? ButtonState.Released : ButtonState.Pressed,
                        (touches.Count > 1) ? ButtonState.Pressed : ButtonState.Released,
                        (touches.Count > 1) ? ButtonState.Pressed : ButtonState.Released,
                        ButtonState.Released,
                        ButtonState.Released
                        );
                    prevTouchAvg = avg;

                    state.TouchMode = true;
                }
            }
            else
            {
                prevTouchAvg = null;
                touchedFrames = 0;
				if (state.TouchMode) state.MouseState = new MouseState(
						lastMouseState.X, lastMouseState.Y, state.MouseState.ScrollWheelValue,
						ButtonState.Released,
						ButtonState.Released,
						ButtonState.Released,
						ButtonState.Released,
						ButtonState.Released
					);
                //state.TouchMode = false;
            }
			lastMouseState = state.MouseState;
        }

        public void Draw(GameTime time)
        {
            lock (Device)
            {
                foreach (var layer in Layers.Reverse<IGraphicsLayer>())
                {
                    layer.PreDraw(Device);
                }
            }

            Device.SetRenderTarget(null);
            Device.BlendState = BlendState.AlphaBlend;
            Device.Clear(ClearColor);
            //Device.RasterizerState.AlphaBlendEnable = true;
            //Device.DepthStencilState.DepthBufferEnable = true;

            lock (Device)
            {
                foreach (var layer in Layers)
                {
                    layer.Draw(Device);
                }
            }
        }
    }
}
