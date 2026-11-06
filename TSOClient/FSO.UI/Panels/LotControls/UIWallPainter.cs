using Microsoft.Xna.Framework;
using System.Collections.Generic;
using FSO.LotView;
using FSO.LotView.Components;
using FSO.LotView.Model;
using FSO.Common.Rendering.Framework.Model;
using FSO.HIT;
using FSO.SimAntics;
using FSO.SimAntics.Entities;
using FSO.SimAntics.Model;
using FSO.SimAntics.NetPlay.Model.Commands;
using FSO.SimAntics.Utils;
using FSO.Client.UI.Model;
using FSO.UI.Panels.LotControls;

namespace FSO.Client.UI.Panels.LotControls
{
    public class UIWallPainter : UICustomLotControl
    {

        VMMultitileGroup WallCursor;
        VM vm;
        LotView.World World;
        ILotControl Parent;

        bool Drawing;
        private List<VMArchitectureCommand> Commands;
        int CursorDir = 0;

        ushort Pattern;

        public UIWallPainter (VM vm, LotView.World world, ILotControl parent, List<int> parameters)
        {
            Pattern = (ushort)parameters[0];

            this.vm = vm;
            World = parent.World;
            Parent = parent;
            WallCursor = vm.Context.CreateObjectInstance(0x00000439, LotTilePos.OUT_OF_WORLD, FSO.LotView.Model.Direction.NORTH, true);

            ((ObjectComponent)WallCursor.Objects[0].WorldUI).ForceDynamic = true;
            Commands = new List<VMArchitectureCommand>();

            SetCursorGraphic(2);
        }

        public void SetCursorGraphic(short id)
        {
            WallCursor.Objects[0].SetValue(VMStackObjectVariable.Graphic, id);
            ((VMGameObject)WallCursor.Objects[0]).RefreshGraphic();
        }

        public override void MouseDown(UpdateState state)
        {
            HITVM.Get().PlaySoundEvent(UISounds.BuildDragToolDown);
            Drawing = true;
        }

        public override void MouseUp(UpdateState state)
        {
            HITVM.Get().PlaySoundEvent(UISounds.BuildDragToolUp);

            vm.SendCommand(new VMNetArchitectureCmd
            {
                Commands = new List<VMArchitectureCommand>(Commands)
            });

            Commands.Clear();
            Drawing = false;
        }

        public override void Release()
        {
            WallCursor.Delete(vm.Context);
            vm.Context.Architecture.Commands.Clear();
            vm.Context.Architecture.SignalRedraw();
        }

        /// <summary>A command that paints a whole room's walls or a whole foundation's sides (with shift).</summary>
        private static bool IsFill(VMArchitectureCommand cmd)
        {
            return cmd.Type == VMArchitectureCommandType.PATTERN_FILL
                || (cmd.Type == VMArchitectureCommandType.FOUNDATION_PAINT && cmd.x2 == VMFoundationTools.PaintWholeFoundation);
        }

        public override void Update(UpdateState state, bool scrolled)
        {
            ushort pattern = (Modifiers.IsSet(UILotControlModifiers.CTRL)) ? (ushort)0 : Pattern;

            var tilePos = World.EstTileAtPosWithScroll(new Vector2(MousePosition.X, MousePosition.Y));
            Point cursor = new Point((int)tilePos.X, (int)tilePos.Y);

            if (!Drawing && Commands.Count > 0)
            {
                vm.Context.Architecture.SignalRedraw();
                Commands.Clear();
            }

            //a foundation's side under the cursor (on the ground floor) is painted like a wall: one side, or with shift
            //every side of that foundation
            Point fTile = Point.Zero;
            int fDir = -1;
            var onFoundation = World.State.Level == 1 && vm.Context.Blueprint != null
                && vm.Context.Blueprint.FoundationSideAt(World.State.CameraRayAtScreenPos(new Vector2(MousePosition.X, MousePosition.Y), 1), out fTile, out fDir);
            if (onFoundation)
            {
                var whole = Modifiers.IsSet(UILotControlModifiers.SHIFT);
                var cmd = new VMArchitectureCommand
                {
                    Type = VMArchitectureCommandType.FOUNDATION_PAINT,
                    level = 1,
                    pattern = pattern,
                    x = fTile.X,
                    y = fTile.Y,
                    x2 = whole ? VMFoundationTools.PaintWholeFoundation : fDir
                };
                if (whole || (Commands.Count > 0 && IsFill(Commands[0])))
                {
                    //a fill replaces anything else
                    if (Commands.Count != 1 || !Commands[0].Equals(cmd))
                    {
                        Commands.Clear();
                        vm.Context.Architecture.SignalRedraw();
                        Commands.Add(cmd);
                    }
                }
                else if (!Commands.Contains(cmd))
                {
                    vm.Context.Architecture.SignalRedraw();
                    Commands.Add(cmd);
                }
                //the roller faces the side: its wall direction is one on from the foundation's
                CursorDir = (fDir + 2) % 4;
                cursor = fTile;
            }
            else if (Modifiers.IsSet(UILotControlModifiers.SHIFT))
            {
                if (Commands.Count == 0 || Commands[0].Type != VMArchitectureCommandType.PATTERN_FILL)
                {
                    Commands.Clear();
                    vm.Context.Architecture.SignalRedraw();
                    Commands.Add(new VMArchitectureCommand
                    {
                        Type = VMArchitectureCommandType.PATTERN_FILL,
                        level = World.State.Level,
                        pattern = pattern,
                        style = 0,
                        x = cursor.X,
                        y = cursor.Y
                    });
                }
            } else
            {
                if (Commands.Count > 0 && IsFill(Commands[0]))
                {
                    Commands.Clear();
                    vm.Context.Architecture.SignalRedraw();
                }
                int dir = 0;
                int altdir = 0;
                Vector2 fract = new Vector2(tilePos.X - cursor.X, tilePos.Y - cursor.Y);
                switch (World.State.CutRotation)
                {
                    case WorldRotation.BottomRight:
                        if (fract.X - fract.Y > 0) { dir = 2; altdir = 3; }
                        else { dir = 3; altdir = 2; }
                        break;
                    case WorldRotation.TopRight:
                        if (fract.X + fract.Y > 1) { dir = 3; altdir = 0; }
                        else { dir = 0; altdir = 3; }
                        break;
                    case WorldRotation.TopLeft:
                        //+x is right down. +y is left down
                        if (fract.X - fract.Y > 0) { dir = 1; altdir = 0; }
                        else { dir = 0; altdir = 1; }
                        break;
                    case WorldRotation.BottomLeft:
                        if (fract.X + fract.Y > 1) { dir = 2; altdir = 1; }
                        else { dir = 1; altdir = 2; }
                        break;
                }

                var finalDir = VMArchitectureTools.GetPatternDirection(vm.Context.Architecture, cursor, pattern, dir, altdir, World.State.Level);
                if (finalDir != -1)
                {
                    CursorDir = (finalDir + 1) % 4;
                    var cmd = new VMArchitectureCommand
                    {
                        Type = VMArchitectureCommandType.PATTERN_DOT,
                        level = World.State.Level,
                        pattern = pattern,
                        style = 0,
                        x = cursor.X,
                        y = cursor.Y,
                        x2 = dir,
                        y2 = altdir
                    };
                    if (!Commands.Contains(cmd))
                    {
                        vm.Context.Architecture.SignalRedraw();
                        Commands.Add(cmd);
                    }
                }
            }

            var cmds = vm.Context.Architecture.Commands;
            cmds.Clear();
            foreach (var cmd in Commands)
            {
                cmds.Add(cmd);
            }

            if (cmds.Count > 0)
            {
                var cost = vm.Context.Architecture.LastTestCost;
                if (cost != 0)
                {
                    var disallowed = Parent.ActiveEntity != null && cost > Parent.Budget;
                    state.UIState.TooltipProperties.Show = true;
                    state.UIState.TooltipProperties.Color = disallowed ? Color.DarkRed : Color.Black;
                    state.UIState.TooltipProperties.Opacity = 1;
                    state.UIState.TooltipProperties.Position = new Vector2(MousePosition.X, MousePosition.Y);
                    state.UIState.Tooltip = (cost < 0) ? ("-$" + (-cost)) : ("$" + cost);
                    state.UIState.TooltipProperties.UpdateDead = false;

                    if (disallowed) HITVM.Get().PlaySoundEvent(UISounds.Error);
                }
                else
                {
                    state.UIState.TooltipProperties.Show = false;
                    state.UIState.TooltipProperties.Opacity = 0;
                }
            }

            WallCursor.SetVisualPosition(new Vector3(cursor.X+0.5f, cursor.Y+0.5f, (World.State.Level-1)*2.95f), (Direction)(1<<(CursorDir*2)), vm.Context);
        }
    }
}
