using System;

namespace FSO.Common.Rendering.Framework.IO
{
    /// <summary>
    /// Gamepad options. These live in tso.common so the input loop can read them; the frontend fills them
    /// in from its own persisted settings via <see cref="Configure"/>.
    /// </summary>
    public static class GamePadSettings
    {
        public static bool Enabled = true;

        /// <summary>Radial stick dead zone, 0-0.9.</summary>
        public static float DeadZone = 0.2f;

        /// <summary>
        /// Lets the left stick drive a stand-in mouse cursor on screens that have no native gamepad support.
        /// </summary>
        public static bool VirtualCursor = true;

        /// <summary>Multiplier on the virtual cursor's base speed.</summary>
        public static float CursorSpeed = 1f;

        public static PadBindings Bindings = new PadBindings();

        /// <summary>
        /// Ignore the keyboard and mouse while the pad is held (<see cref="PadEchoFilter"/>), so remappers that also
        /// send keys and mouse movement for the pad (Steam Input's desktop layout) don't act twice.
        /// </summary>
        public static bool IgnoreEchoes = true;

        /// <summary>
        /// Applies persisted settings. If <paramref name="overridesPath"/> exists, its bindings are applied on
        /// top of the defaults; the returned string describes any lines that were skipped, or is null.
        /// </summary>
        public static string Configure(bool enabled, float deadZone, bool virtualCursor, float cursorSpeed,
            bool swapConfirmCancel, string overridesPath, string glyphs = "auto", bool ignoreEchoes = true)
        {
            Enabled = enabled;
            IgnoreEchoes = ignoreEchoes;
            PadGlyphs.Setting = glyphs;
            DeadZone = Math.Clamp(deadZone, 0f, 0.9f);
            VirtualCursor = virtualCursor;
            CursorSpeed = Math.Clamp(cursorSpeed, 0.1f, 5f);

            var bindings = new PadBindings(swapConfirmCancel);
            string problems = null;
            if (overridesPath != null)
            {
                try
                {
                    var errors = bindings.LoadOverrides(overridesPath);
                    if (errors.Count > 0) problems = overridesPath + ": " + string.Join("; ", errors);
                }
                catch (Exception e)
                {
                    problems = overridesPath + ": " + e.Message;
                }
            }
            Bindings = bindings;
            return problems;
        }
    }
}
