using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework.Input;

namespace FSO.Common.Rendering.Framework.IO
{
    /// <summary>
    /// Maps each <see cref="PadAction"/> to one or more physical buttons. Any bound button holds the action down.
    /// </summary>
    public class PadBindings
    {
        private readonly Buttons[][] Map = new Buttons[GamePadInput.ActionCount][];

        public PadBindings() : this(false) { }

        /// <param name="swapConfirmCancel">Put Confirm on B and Cancel on A, matching Nintendo-style layouts.</param>
        public PadBindings(bool swapConfirmCancel)
        {
            Set(PadAction.Confirm, swapConfirmCancel ? Buttons.B : Buttons.A);
            Set(PadAction.Cancel, swapConfirmCancel ? Buttons.A : Buttons.B);
            Set(PadAction.FaceLeft, Buttons.X);
            Set(PadAction.FaceTop, Buttons.Y);
            Set(PadAction.ShoulderLeft, Buttons.LeftShoulder);
            Set(PadAction.ShoulderRight, Buttons.RightShoulder);
            Set(PadAction.TriggerLeft, Buttons.LeftTrigger);
            Set(PadAction.TriggerRight, Buttons.RightTrigger);
            Set(PadAction.Up, Buttons.DPadUp);
            Set(PadAction.Down, Buttons.DPadDown);
            Set(PadAction.Left, Buttons.DPadLeft);
            Set(PadAction.Right, Buttons.DPadRight);
            Set(PadAction.Start, Buttons.Start);
            Set(PadAction.Select, Buttons.Back);
            Set(PadAction.LeftStickClick, Buttons.LeftStick);
            Set(PadAction.RightStickClick, Buttons.RightStick);
        }

        public Buttons[] Get(PadAction action) => Map[(int)action];

        public void Set(PadAction action, params Buttons[] buttons)
        {
            Map[(int)action] = buttons ?? Array.Empty<Buttons>();
        }

        /// <summary>
        /// Applies overrides from a plain text file with one binding per line:
        /// <code>
        /// # comments start with '#'
        /// Confirm = A
        /// Cancel = B, Back
        /// </code>
        /// Action names are <see cref="PadAction"/> members and button names are XNA <see cref="Buttons"/> members,
        /// both case-insensitive. Lines that don't parse are skipped and reported in the returned list,
        /// so a typo never leaves an action unbound.
        /// </summary>
        public List<string> LoadOverrides(string path)
        {
            var errors = new List<string>();
            if (!File.Exists(path)) return errors;

            var lineNo = 0;
            foreach (var rawLine in File.ReadAllLines(path))
            {
                lineNo++;
                var line = rawLine.Trim();
                if (line.Length == 0 || line[0] == '#') continue;

                var split = line.IndexOf('=');
                if (split < 1)
                {
                    errors.Add($"line {lineNo}: expected 'Action = Button[, Button]'");
                    continue;
                }

                var actionName = line.Substring(0, split).Trim();
                if (!System.Enum.TryParse(actionName, true, out PadAction action))
                {
                    errors.Add($"line {lineNo}: unknown action '{actionName}'");
                    continue;
                }

                var buttons = new List<Buttons>();
                var valid = true;
                foreach (var part in line.Substring(split + 1).Split(','))
                {
                    var name = part.Trim();
                    if (name.Length == 0) continue;
                    if (System.Enum.TryParse(name, true, out Buttons button)) buttons.Add(button);
                    else
                    {
                        errors.Add($"line {lineNo}: unknown button '{name}'");
                        valid = false;
                    }
                }

                if (valid && buttons.Count > 0) Set(action, buttons.ToArray());
                else if (valid) errors.Add($"line {lineNo}: no buttons given for '{actionName}'");
            }
            return errors;
        }
    }
}
