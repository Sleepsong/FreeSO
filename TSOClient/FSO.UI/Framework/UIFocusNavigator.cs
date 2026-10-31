using System;
using System.Collections.Generic;
using FSO.Client.UI.Controls;
using FSO.Client.UI.Model;
using FSO.Common.Rendering.Framework.IO;
using FSO.Common.Rendering.Framework.Model;
using FSO.Common.Utils;
using FSO.HIT;
using Microsoft.Xna.Framework;

namespace FSO.Client.UI.Framework
{
    /// <summary>
    /// An element that takes gamepad focus as a unit, such as a list or a carousel, and may use directional input
    /// itself before focus moves on.
    /// </summary>
    public interface IPadNavigable
    {
        /// <summary>Whether it can take focus right now (e.g. a list with items).</summary>
        bool PadFocusable { get; }

        /// <summary>Bounds in the element's local space, for spatial navigation and the focus outline.</summary>
        Rectangle PadBounds { get; }

        /// <summary>
        /// A direction was pressed while focused. Return true if it was used (e.g. moving within a list), or false to
        /// move focus to the neighbouring element in that direction.
        /// </summary>
        bool PadMove(PadDirection direction);

        /// <summary>Confirm was pressed while focused.</summary>
        void PadActivate();
    }

    /// <summary>A focus scope, usually a dialog, that chooses its initial focus and what Cancel does.</summary>
    public interface IPadScope
    {
        /// <summary>Element to focus first, or null for the top-left one.</summary>
        UIElement PadDefaultFocus { get; }

        /// <summary>Cancel was pressed. Return true if it did something.</summary>
        bool PadCancel();
    }

    /// <summary>
    /// Moves gamepad focus between the buttons and <see cref="IPadNavigable"/> elements inside a scope:
    /// directions move to the nearest element that way, Confirm clicks, and Cancel goes to the scope's
    /// <see cref="IPadScope.PadCancel"/>. Draws an outline around the focused element while the pad is in use.
    /// </summary>
    public class UIFocusNavigator
    {
        public UIElement Scope { get; private set; }
        public UIElement Focused { get; private set; }

        private bool ShowOutline;
        private bool Armed; //Confirm/Cancel held when the scope appeared must be released first, so one press can't act twice
        private float Time;
        private readonly List<UIElement> Candidates = new List<UIElement>();

        /// <summary>
        /// Runs one frame of navigation inside <paramref name="scope"/>. Returns false, and does nothing, if the scope
        /// has nothing focusable, so the caller can leave the pad to something else.
        /// </summary>
        public bool Update(UpdateState state, UIElement scope)
        {
            var pad = state.Pad;
            if (scope != Scope)
            {
                Scope = scope;
                Focused = null;
                Armed = false;
            }

            Candidates.Clear();
            Collect(scope, Candidates);
            if (Candidates.Count == 0)
            {
                Focused = null;
                return false;
            }
            if (Focused == null || !Candidates.Contains(Focused)) Focused = InitialFocus(scope);

            ShowOutline = state.LastInputDevice == InputDevice.GamePad;
            Time += (float)state.Time.ElapsedGameTime.TotalSeconds;
            if (!Armed && !pad.IsDown(PadAction.Confirm) && !pad.IsDown(PadAction.Cancel)) Armed = true;

            for (int i = 0; i < 4; i++)
            {
                var direction = (PadDirection)i;
                if (pad.NavPressedRepeat(direction)) Move(direction);
            }

            if (Armed && pad.Pressed(PadAction.Confirm)) Activate(Focused);
            else if (Armed && pad.Pressed(PadAction.Cancel)) (scope as IPadScope)?.PadCancel();
            return true;
        }

        public void Clear()
        {
            Scope = null;
            Focused = null;
            ShowOutline = false;
        }

        /// <summary>Whether a scope currently has anything the navigator could focus.</summary>
        public static bool HasFocusable(UIElement scope)
        {
            if (scope == null) return false;
            var found = new List<UIElement>();
            Collect(scope, found);
            return found.Count > 0;
        }

        private static void Collect(UIElement element, List<UIElement> found)
        {
            if (!element.Visible || element.Opacity < 0.5f) return;
            if (element is IPadNavigable navigable)
            {
                if (navigable.PadFocusable) found.Add(element);
                return; //it handles its own contents
            }
            if (element is UIButton button)
            {
                if (!button.Disabled && button.HasClickHandler) found.Add(element);
                return;
            }
            if (element is UIContainer container)
            {
                foreach (var child in container.GetChildren()) Collect(child, found);
            }
        }

        private UIElement InitialFocus(UIElement scope)
        {
            var preferred = (scope as IPadScope)?.PadDefaultFocus;
            if (preferred != null && Candidates.Contains(preferred)) return preferred;

            //otherwise reading order: top-most row first, then left-most.
            UIElement best = null;
            var bestScore = float.MaxValue;
            foreach (var candidate in Candidates)
            {
                var centre = GlobalRect(candidate).Center.ToVector2();
                var score = centre.Y + centre.X * 0.25f;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }
            }
            return best;
        }

        private void Move(PadDirection direction)
        {
            if (Focused is IPadNavigable navigable && navigable.PadMove(direction)) return;

            var from = GlobalRect(Focused);
            var next = PickNeighbour(from, Candidates, Focused, direction);
            if (next != null)
            {
                Focused = next;
                HITVM.Get()?.PlaySoundEvent(UISounds.Click);
            }
        }

        /// <summary>
        /// Picks the element nearest to <paramref name="from"/> in a direction. Elements overlapping on the
        /// perpendicular axis (the same row or column) always win over diagonal ones.
        /// </summary>
        public static UIElement PickNeighbour(Rectangle from, List<UIElement> candidates, UIElement exclude, PadDirection direction)
        {
            var rects = new List<Rectangle>(candidates.Count);
            foreach (var candidate in candidates) rects.Add(GlobalRect(candidate));
            var index = PickNeighbour(from, rects, candidates.IndexOf(exclude), direction);
            return (index < 0) ? null : candidates[index];
        }

        /// <summary>Geometry-only form of <see cref="PickNeighbour(Rectangle, List{UIElement}, UIElement, PadDirection)"/>.</summary>
        public static int PickNeighbour(Rectangle from, List<Rectangle> rects, int exclude, PadDirection direction)
        {
            var axis = Axis(direction);
            var fromCentre = from.Center.ToVector2();
            var best = -1;
            var bestScore = float.MaxValue;
            for (int i = 0; i < rects.Count; i++)
            {
                if (i == exclude) continue;
                var rect = rects[i];
                var offset = rect.Center.ToVector2() - fromCentre;
                var along = Vector2.Dot(offset, axis);
                if (along <= 1) continue; //not in that direction

                var across = Math.Abs(offset.X * axis.Y - offset.Y * axis.X);
                var sameLine = (axis.X != 0)
                    ? (rect.Top < from.Bottom && rect.Bottom > from.Top)
                    : (rect.Left < from.Right && rect.Right > from.Left);
                //anything in the same row/column beats anything diagonal; within a tier, nearest wins.
                var score = (sameLine ? 0 : 100000f) + along + across * (sameLine ? 0.5f : 2f);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = i;
                }
            }
            return best;
        }

        private static Vector2 Axis(PadDirection direction)
        {
            switch (direction)
            {
                case PadDirection.Up: return new Vector2(0, -1);
                case PadDirection.Down: return new Vector2(0, 1);
                case PadDirection.Left: return new Vector2(-1, 0);
                default: return new Vector2(1, 0);
            }
        }

        private void Activate(UIElement element)
        {
            if (element is IPadNavigable navigable) navigable.PadActivate();
            else if (element is UIButton button) button.PerformClick();
        }

        /// <summary>An element's bounds in screen space (the space UI draws in).</summary>
        public static Rectangle GlobalRect(UIElement element)
        {
            var local = (element is IPadNavigable navigable) ? navigable.PadBounds : element.GetBounds();
            if (local.Width <= 0 || local.Height <= 0)
            {
                var size = element.Size;
                local = new Rectangle(0, 0, (int)size.X, (int)size.Y);
            }
            var a = element.LocalPoint(new Vector2(local.Left, local.Top));
            var b = element.LocalPoint(new Vector2(local.Right, local.Bottom));
            return new Rectangle((int)Math.Min(a.X, b.X), (int)Math.Min(a.Y, b.Y), (int)Math.Abs(b.X - a.X), (int)Math.Abs(b.Y - a.Y));
        }

        /// <summary>Draws a pulsing outline around the focused element while the pad is the device in use.</summary>
        public void Draw(UISpriteBatch batch)
        {
            if (!ShowOutline || Focused == null || Scope == null || !Focused.WillDraw()) return;
            var rect = GlobalRect(Focused);
            rect.Inflate(4, 4);
            var px = TextureGenerator.GetPxWhite(batch.GraphicsDevice);
            var color = new Color(255, 230, 90) * (0.75f + 0.25f * (float)Math.Sin(Time * 6));
            const int t = 3;
            batch.Draw(px, new Rectangle(rect.X, rect.Y, rect.Width, t), color);
            batch.Draw(px, new Rectangle(rect.X, rect.Bottom - t, rect.Width, t), color);
            batch.Draw(px, new Rectangle(rect.X, rect.Y + t, t, rect.Height - t * 2), color);
            batch.Draw(px, new Rectangle(rect.Right - t, rect.Y + t, t, rect.Height - t * 2), color);
        }
    }
}
