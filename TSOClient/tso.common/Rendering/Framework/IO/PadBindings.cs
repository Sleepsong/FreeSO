using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        /// <summary>
        /// The buttons that can be bound: one for each action by default. Not the Guide button, which the system keeps.
        /// </summary>
        public static readonly Buttons[] Bindable =
        {
            Buttons.A, Buttons.B, Buttons.X, Buttons.Y, Buttons.LeftShoulder, Buttons.RightShoulder,
            Buttons.LeftTrigger, Buttons.RightTrigger, Buttons.DPadUp, Buttons.DPadDown, Buttons.DPadLeft, Buttons.DPadRight,
            Buttons.Start, Buttons.Back, Buttons.LeftStick, Buttons.RightStick
        };

        public PadBindings Clone()
        {
            var copy = new PadBindings();
            for (int i = 0; i < Map.Length; i++) copy.Map[i] = (Buttons[])Map[i].Clone();
            return copy;
        }

        /// <summary>Whether an action has the same buttons, in the same order, as in <paramref name="other"/>.</summary>
        public bool SameAs(PadAction action, PadBindings other)
        {
            return Get(action).SequenceEqual(other.Get(action));
        }

        /// <summary>The action a button is bound to (the first, if a hand-written file gave it to more than one), or null.</summary>
        public PadAction? ActionOf(Buttons button)
        {
            for (int i = 0; i < Map.Length; i++)
                if (Map[i].Contains(button)) return (PadAction)i;
            return null;
        }

        /// <summary>
        /// Binds <paramref name="button"/> to <paramref name="action"/>, alone. Any other action with that button gives it
        /// up; one left with no button takes the button <paramref name="action"/> had, so the two swap and every action
        /// keeps a button (Confirm and Cancel can't be lost).
        /// </summary>
        public void Rebind(PadAction action, Buttons button)
        {
            var old = Get(action);
            Buttons? given = (old.Length > 0 && old[0] != button) ? old[0] : (Buttons?)null;
            for (int i = 0; i < Map.Length; i++)
            {
                if (i == (int)action || !Map[i].Contains(button)) continue;
                var rest = Map[i].Where(x => x != button).ToArray();
                if (rest.Length == 0 && given != null) rest = new[] { given.Value };
                Map[i] = rest;
            }
            Set(action, button);
        }

        /// <summary>
        /// Writes the bindings that differ from <paramref name="defaults"/> to <paramref name="path"/>, in the form
        /// <see cref="LoadOverrides"/> reads (the whole file is replaced).
        /// </summary>
        public void SaveOverrides(string path, PadBindings defaults)
        {
            var lines = new List<string>
            {
                "# Gamepad button bindings, on top of the defaults. Written by Options > Controller > Button Mapping;",
                "# it can be edited by hand too: one action per line, its buttons after '='.",
                "# Actions: " + string.Join(" ", System.Enum.GetNames(typeof(PadAction))),
                "# Buttons: " + string.Join(" ", Bindable.Select(x => x.ToString())),
            };
            for (int i = 0; i < Map.Length; i++)
            {
                var action = (PadAction)i;
                if (SameAs(action, defaults)) continue;
                lines.Add(action + " = " + string.Join(", ", Get(action).Select(x => x.ToString())));
            }
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllLines(path, lines);
        }

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
