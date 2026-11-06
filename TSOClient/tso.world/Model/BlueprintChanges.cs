using FSO.LotView.Components;
using FSO.LotView.Model;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;

namespace FSO.LotView
{
    public class BlueprintChanges
    {
        public bool Subworld = false;
        public TimedDynamicStaticLayers Layers = new TimedDynamicStaticLayers(5);

        // why the cached 2D image was marked for redrawing (for performance logs)
        private static readonly string[] StaticReasonNames = { "view", "scroll", "light", "walls", "rooms", "second draw", "objects added/removed", "objects live/still", "many objects moved" };
        private static int[] StaticReasons = new int[9];
        private void MarkStatic(int reason)
        {
            StaticSurfaceDirty = true;
            StaticReasons[reason]++;
        }
        /// <summary>The reasons the cached 2D image was marked dirty since the last call, e.g. "scroll 2, objects live/still 5".</summary>
        public static string TakeStaticReasons()
        {
            var parts = new System.Collections.Generic.List<string>();
            for (int i = 0; i < StaticReasons.Length; i++)
            {
                if (StaticReasons[i] > 0) parts.Add(StaticReasonNames[i] + " " + StaticReasons[i]);
                StaticReasons[i] = 0;
            }
            parts.Add($"to live {TimedDynamicStaticLayers.ToDynamic}, back {TimedDynamicStaticLayers.ToStatic}");
            TimedDynamicStaticLayers.ToDynamic = TimedDynamicStaticLayers.ToStatic = 0;
            return string.Join(", ", parts);
        }

        public HashSet<ObjectComponent> DynamicObjects => Layers.DynamicObjects;
        public HashSet<ObjectComponent> StaticObjects => Layers.StaticObjects;

        public HashSet<ObjectComponent> ObjectMoved = new HashSet<ObjectComponent>();
        public Dictionary<short, bool> RoomLightInvalid = new Dictionary<short, bool>();

        public object FloorChanges;
        public object WallChanges;
        public BlueprintGlobalChanges Dirty;
        public int TicksSinceLight;
        public int LastSubLightUpdate = -1;

        public ScrollBuffer StaticSurface; //copied in from parent - reference is for checking pxOffset

        private bool ObjectSetDirty;
        public bool StaticSurfaceDirty;
        public bool StaticObjectDirty;

        public bool DrawImmediate;

        public bool Arch2D;

        private Blueprint Blueprint;

        public BlueprintChanges(Blueprint blueprint)
        {
            Blueprint = blueprint;
        }

        public double LastTimeOfDay = -99999;
        private long LastOutdoorsRedraw;

        public void PreDraw(GraphicsDevice gd, WorldState state)
        {
            DrawImmediate = state.ForceImmediate;
            if (state.CameraMode < CameraRenderMode._3D)
            {
                state.CameraMode = (state.Cameras.Safe2D) ? CameraRenderMode._2D : CameraRenderMode._2DRotate;
            }
            if (state.CameraMode > CameraRenderMode._2D) DrawImmediate = true;

            var timeChange = Math.Abs(Blueprint.OutsideTime - LastTimeOfDay);
            bool outdoorsChanged;
            if (FSO.Common.FSOEnvironment.SoftwareDepth && state.CameraMode < CameraRenderMode._3D)
            {
                // 2D with software depth (phones): an outdoor light change redraws the whole static buffer, which
                // costs a few hundred ms there, so update it every 15 game minutes and at most every 8 seconds,
                // instead of every 1.4 game minutes (about every 1.5 s at normal speed). Big jumps (loading, an
                // hour or more) still update straight away.
                var now = Environment.TickCount64;
                outdoorsChanged = timeChange > (1 / 24f) || (timeChange > (1 / 96f) && now - LastOutdoorsRedraw > 8000);
                if (outdoorsChanged) LastOutdoorsRedraw = now;
            }
            else outdoorsChanged = timeChange > 0.001f;
            if (outdoorsChanged)
            {
                Dirty |= BlueprintGlobalChanges.OUTDOORS_LIGHTING_CHANGED;
                LastTimeOfDay = Blueprint.OutsideTime;
            }

            if ((Dirty & BlueprintGlobalChanges.ALL) > 0)
            {
                if ((Dirty & BlueprintGlobalChanges.VIEW_CHANGE_2D) > 0)
                {
                    MarkStatic(0);
                    Dirty |= BlueprintGlobalChanges.WALL_CHANGED;
                    Dirty |= BlueprintGlobalChanges.FLOOR_CHANGED;
                    if ((Dirty & BlueprintGlobalChanges.ROTATE) > 0)
                    {
                        foreach (var obj in Blueprint.Objects) obj.UpdateDrawOrder(state);
                    }

                    //invalidate walls
                    //invalidate floors
                    //invalidate outdoors?
                }
                if ((Dirty & BlueprintGlobalChanges.SCROLL) > 0)
                {
                    //invalidate scroll buffers if our new scroll view is outside their ranges
                    var pxOffset = -state.WorldSpace.GetScreenOffset();
                    if (StaticSurface == null || StaticSurface.PxOffset != StaticSurface.GetScrollIncrement(pxOffset, state))
                    {
                        MarkStatic(1);
                    }
                }
                if ((Dirty & BlueprintGlobalChanges.PRECISE_ZOOM) > 0)
                {
                    DrawImmediate = true; 
                }

                if ((Dirty & BlueprintGlobalChanges.LIGHTING_UNIMPORTANT) > 0)
                {
                    MarkStatic(2);
                }

                if ((Dirty & BlueprintGlobalChanges.LIGHTING_ANY) > 0)
                {
                    Blueprint.GenerateRoomLights();
                    state.OutsideColor = Blueprint.RoomColors[1];
                    state.OutsidePx.SetData(new Color[] { new Color(Blueprint.OutsideColor, (Blueprint.OutsideColor.R + Blueprint.OutsideColor.G + Blueprint.OutsideColor.B) / (255 * 3f)) });
                    if (state.AmbientLight != null)
                    {
                        state.AmbientLight.SetData(Blueprint.RoomColors);
                    }
                    if ((Dirty & BlueprintGlobalChanges.ROOM_CHANGED) == 0)
                    {
                        if ((Dirty & BlueprintGlobalChanges.LIGHTING_CHANGED) > 0 && state.Light != null)
                        {
                            //pass invalidated rooms
                            foreach (var room in RoomLightInvalid)
                            {
                                state.Light?.InvalidateRoom((ushort)room.Key, room.Value);
                            }

                            RoomLightInvalid.Clear();
                        }
                        if ((Dirty & BlueprintGlobalChanges.OUTDOORS_LIGHTING_CHANGED) > 0)
                        {
                            state.Light?.InvalidateOutdoors();

                            if (Blueprint.SubWorlds.Count > 0)
                            {
                                Blueprint.SubWorlds[LastSubLightUpdate].RefreshLighting();
                                LastSubLightUpdate = (LastSubLightUpdate + 1) % Blueprint.SubWorlds.Count;
                            }
                        }
                    }

                    if (LastSubLightUpdate == -1)
                    {
                        foreach (var sub in Blueprint.SubWorlds) sub.RefreshLighting();
                        LastSubLightUpdate = 0;
                    }

                    TicksSinceLight = 0;
                    MarkStatic(2);
                }

                var wallFlags = (Dirty & (BlueprintGlobalChanges.WALL_CHANGED | BlueprintGlobalChanges.WALL_CUT_CHANGED));
                if (wallFlags > 0)
                {
                    //process wall changes
                    state.Platform.RecacheWalls(gd, state, wallFlags == BlueprintGlobalChanges.WALL_CUT_CHANGED);
                    MarkStatic(3);
                }

                if ((Dirty & BlueprintGlobalChanges.ROOF_STYLE_CHANGED) > 0)
                {
                    Blueprint.RoofComp.StyleDirty = true;
                }

                if ((Dirty & BlueprintGlobalChanges.ARCH_CHANGED) > 0)
                {
                    if ((Dirty & BlueprintGlobalChanges.FLOOR_CHANGED) > 0)
                    {
                        //process floor changes
                        Blueprint.FloorGeom.FullReset(gd, state.BuildMode > 1);
                    }
                    if ((Dirty & BlueprintGlobalChanges.ROOM_CHANGED) > 0)
                    {
                        for (sbyte i = 0; i < Blueprint.RoomMap.Length; i++)
                        {
                            state.Rooms.SetRoomMap(i, Blueprint.RoomMap[i]);
                        }
                        if (state.Light != null)
                        {
                            state.Light.InvalidateAll();
                        }
                        Blueprint.Indoors = null;
                        Blueprint.RoofComp.ShapeDirty = true;
                    }

                    MarkStatic(4);
                }

                if ((Dirty & (BlueprintGlobalChanges.OPENGL_SECOND_DRAW)) > 0)
                {
                    //must redraw to avoid issues with floors
                    MarkStatic(5);
                }
            }
            Dirty = 0;

            if (state.Light?.ParseInvalidated((sbyte)(state.Level + ((state.DrawRoofs) ? 1 : 0)), state) == true)
            {
                Dirty |= BlueprintGlobalChanges.LIGHTING_UNIMPORTANT;
            }

            //for moved objects, regenerate depth and move them to dynamic if they aren't already there.
            // On phones (software depth) dynamic objects are expensive to draw every frame, and when a lot loads every
            // object is moved into place at once, which made them all dynamic for a few seconds. When many move
            // together, keep the static ones static and redraw the static buffer once instead.
            var bulkMove = FSO.Common.FSOEnvironment.SoftwareDepth && ObjectMoved.Count > 32;
            var bulkStatic = false;
            foreach (var obj in ObjectMoved)
            {
                if (obj.Dead) continue;
                obj.UpdateDrawOrder(state);
                if (bulkMove && obj.RenderInfo.Layer == WorldObjectRenderLayer.STATIC) bulkStatic = true;
                else Layers.EnsureDynamic(obj);
            }
            if (bulkStatic) MarkStatic(8);
            StaticObjectDirty = ObjectSetDirty;
            ObjectSetDirty = false;
            if (StaticObjectDirty) MarkStatic(6);
            ObjectMoved.Clear();

            if (Layers.Update())
            {
                //static buffer is dirty.
                MarkStatic(7);
            }
        }

        public void LightChange(short roomID, bool important)
        {
            Dirty |= BlueprintGlobalChanges.LIGHTING_CHANGED;
            if (!RoomLightInvalid.TryGetValue(roomID, out var existingImportant) || (!existingImportant && important))
            {
                RoomLightInvalid[roomID] = important;
            }
        }

        public void RegisterObject(ObjectComponent item)
        {
            ObjectSetDirty = true;
            Layers.RegisterObject(item);
        }

        public void UnregisterObject(ObjectComponent item)
        {
            ObjectSetDirty = true;
            Layers.UnregisterObject(item);
        }

        public void RegisterObjectChange(ObjectComponent item)
        {
            ObjectMoved.Add(item);
        }

        public void SetFlag(BlueprintGlobalChanges flag)
        {
            Dirty |= flag;
        }

        public void Preload(GraphicsDevice gd, WorldState state)
        {
            if ((Dirty & BlueprintGlobalChanges.FLOOR_CHANGED) > 0)
            {
                Blueprint.FloorGeom.FullReset(gd, state.BuildMode > 1);
                Dirty &= ~BlueprintGlobalChanges.FLOOR_CHANGED;
            }

            if ((Dirty & BlueprintGlobalChanges.WALL_CHANGED) > 0)
            {
                state.Platform.RecacheWalls(gd, state, false);
                MarkStatic(3);
                Dirty &= ~BlueprintGlobalChanges.WALL_CHANGED;
            }
        }
    }

    [Flags]
    public enum BlueprintGlobalChanges : int
    {
        SCROLL = 1,
        ROTATE = 1 << 1,
        ZOOM = 1 << 2,
        PRECISE_ZOOM = 1 << 3,
        LEVEL_CHANGED = 1 << 4,
        LIGHTING_CHANGED = 1 << 5, //also has per-room component
        OUTDOORS_LIGHTING_CHANGED = 1 << 6,
        ROOF_STYLE_CHANGED = 1 << 7,
        OPENGL_SECOND_DRAW = 1 << 8, //workaround for some stupid texture switch stuff
        FLOOR_CHANGED = 1 << 9, //global invalidation of scroll buffer, but more specific invalidation for texture-geometry groups
        WALL_CHANGED = 1 << 10, //global invalidation of scroll buffer, but more specific invalidation for regions
        WALL_CUT_CHANGED = 1 << 11,  //global invalidation of scroll buffer, but more specific invalidation for regions
        ROOM_CHANGED = 1 << 12,
        LIGHTING_UNIMPORTANT = 1 << 13, //unimportant lights still need to be updated

        VIEW_CHANGE_2D = ROTATE | ZOOM | LEVEL_CHANGED, //all of these invalidate the scroll buffers.
        ARCH_CHANGED = WALL_CHANGED | FLOOR_CHANGED | ROOM_CHANGED,
        LIGHTING_ANY = LIGHTING_CHANGED | OUTDOORS_LIGHTING_CHANGED,
        ALL = 0x0FFF
    }

    /*
    public enum BlueprintDamageType
    {
        OBJECT_GRAPHIC_REFRESH, //also happens when the object moves
        OBJECT_RETURN_TO_STATIC, //handled internally now
        FLOOR_CHANGED,
        WALL_CHANGED,
        WALL_CUT_CHANGED,
        LIGHTING_CHANGED,
        OUTDOORS_LIGHTING_CHANGED,
    }
    */
}
