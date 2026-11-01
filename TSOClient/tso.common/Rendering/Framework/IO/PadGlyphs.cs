using System;
using Microsoft.Xna.Framework.Input;

namespace FSO.Common.Rendering.Framework.IO
{
    public enum PadGlyphSet
    {
        Xbox,
        PlayStation
    }

    /// <summary>
    /// Names gamepad buttons for on-screen prompts, in the naming of the pad in use: Xbox (A, B, LB...) or
    /// PlayStation (Cross, Circle, L1...). Text only; no console artwork. Prompts name the button an action is bound
    /// to, so swapped or rebound buttons read correctly.
    /// </summary>
    public static class PadGlyphs
    {
        /// <summary>"auto" (from the pad's name), "xbox" or "playstation".</summary>
        public static string Setting = "auto";

        public static PadGlyphSet Resolve(string setting, string padName)
        {
            switch ((setting ?? "").Trim().ToLowerInvariant())
            {
                case "xbox": return PadGlyphSet.Xbox;
                case "playstation":
                case "ps": return PadGlyphSet.PlayStation;
            }
            return LooksLikePlayStation(padName) ? PadGlyphSet.PlayStation : PadGlyphSet.Xbox;
        }

        public static bool LooksLikePlayStation(string padName)
        {
            if (string.IsNullOrEmpty(padName)) return false;
            var name = padName.ToLowerInvariant();
            //Sony's own pads report as "Wireless Controller" over Bluetooth and USB on most platforms.
            return name.Contains("playstation") || name.Contains("dualshock") || name.Contains("dualsense")
                || name.Contains("ps3") || name.Contains("ps4") || name.Contains("ps5") || name.Contains("sony")
                || name.Trim() == "wireless controller";
        }

        public static string ButtonName(Buttons button, PadGlyphSet set)
        {
            var ps = set == PadGlyphSet.PlayStation;
            switch (button)
            {
                case Buttons.A: return ps ? "Cross" : "A";
                case Buttons.B: return ps ? "Circle" : "B";
                case Buttons.X: return ps ? "Square" : "X";
                case Buttons.Y: return ps ? "Triangle" : "Y";
                case Buttons.LeftShoulder: return ps ? "L1" : "LB";
                case Buttons.RightShoulder: return ps ? "R1" : "RB";
                case Buttons.LeftTrigger: return ps ? "L2" : "LT";
                case Buttons.RightTrigger: return ps ? "R2" : "RT";
                case Buttons.Start: return ps ? "Options" : "Start";
                case Buttons.Back: return ps ? "Share" : "Back";
                case Buttons.LeftStick: return ps ? "L3" : "LS";
                case Buttons.RightStick: return ps ? "R3" : "RS";
                case Buttons.DPadUp: return "Up";
                case Buttons.DPadDown: return "Down";
                case Buttons.DPadLeft: return "Left";
                case Buttons.DPadRight: return "Right";
                case Buttons.BigButton: return "Guide";
            }
            return button.ToString();
        }

        /// <summary>The name of the first button bound to an action, or "?" if it's unbound.</summary>
        public static string ActionName(PadAction action, PadBindings bindings, PadGlyphSet set)
        {
            var buttons = bindings?.Get(action);
            return (buttons == null || buttons.Length == 0) ? "?" : ButtonName(buttons[0], set);
        }

        /// <summary>For the pad in use, with the current settings and bindings.</summary>
        public static string ActionName(PadAction action, GamePadInput pad)
        {
            return ActionName(action, GamePadSettings.Bindings, Resolve(Setting, pad?.DisplayName));
        }
    }
}
