using System;
using Microsoft.Xna.Framework;

namespace FSO.Common.Rendering.Framework
{
    public abstract class Game : Microsoft.Xna.Framework.Game
    {
        protected GraphicsDeviceManager Graphics;
        protected GameScreen Screen;

        /// <summary>
        /// A second opinion on focus, asked only while the platform says the game isn't active (e.g. an X11 check: SDL
        /// can miss the focus coming back after Alt+Tab, leaving the game deaf to the pad). Null for none.
        /// </summary>
        public static Func<bool> FocusFallback;

        /// <summary>Whether the window has input focus: <see cref="Microsoft.Xna.Framework.Game.IsActive"/>, else <see cref="FocusFallback"/>.</summary>
        public bool HasFocus => IsActive || (FocusFallback?.Invoke() ?? false);

		public Game() : base()
        {
            Graphics = new GraphicsDeviceManager(this);
        }

        protected override void Initialize(){
            base.Initialize();

            Screen = new GameScreen(GraphicsDevice);
        }

        protected override void Update(GameTime gameTime){
            Screen.Update(gameTime, HasFocus);
        }

        protected override void Draw(GameTime gameTime){
            base.Draw(gameTime);
            Screen.Draw(gameTime);
        }
    }
}
