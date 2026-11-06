using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace FSO.Common.Utils
{
    public class PPXDepthEngine
    {
        private static GraphicsDevice GD;
        private static RenderTarget2D BackbufferDepth;
        private static RenderTarget2D Backbuffer;
        private static SpriteBatch SB;
        public static int SSAA = 1;
        public static int MSAA = 0;

        public static void InitGD(GraphicsDevice gd)
        {
            GD = gd;
            SB = new SpriteBatch(gd);
        }
        public static void InitScreenTargets()
        {
            if (GD == null) return;
            ReleaseInherited(BackbufferDepth);
            if (BackbufferDepth != null) BackbufferDepth.Dispose();
            BackbufferDepth = null;
            if (Backbuffer != null) Backbuffer.Dispose();
            var scale = 1;//FSOEnvironment.DPIScaleFactor;
            if (!FSOEnvironment.Enable3D)
                BackbufferDepth = CreateRenderTarget(GD, 1, MSAA, SurfaceFormat.Color, SSAA * GD.Viewport.Width / scale, SSAA * GD.Viewport.Height / scale, DepthFormat.None);
            Backbuffer = CreateRenderTarget(GD, 1, MSAA, SurfaceFormat.Color, SSAA * GD.Viewport.Width / scale, SSAA * GD.Viewport.Height / scale, DepthFormat.Depth24Stencil8);
            //share now, before either is drawn to: a target's framebuffer is built (with its buffers) the first time it's used
            if (FSOEnvironment.SoftwareDepth && BackbufferDepth != null) InheritDepthStencil(BackbufferDepth, Backbuffer);
        }

        private static RenderTarget2D ActiveColor;
        private static RenderTarget2D ActiveDepth;
        private static int StencilValue;

        public static void SetPPXTarget(RenderTarget2D color, RenderTarget2D depth, bool clear)
        {
            SetPPXTarget(color, depth, clear, ColorExtensions.TransparentBlack);
        }

        public static void SetPPXTarget(RenderTarget2D color, RenderTarget2D depth, bool clear, Color clearColor)
        {
            if (color == null && depth == null && Backbuffer != null) color = Backbuffer;
            ActiveColor = color;
            if (color == Backbuffer && depth == null && BackbufferDepth != null) depth = BackbufferDepth;
            // one-pass software depth reads the depth image as draw buffer 1, so targets drawn without one
            // (thumbnails, object picking) get a scratch one, cleared with them
            if (FSOEnvironment.FramebufferFetch && depth == null && color != null) depth = ScratchDepth(color.Width, color.Height);
            ActiveDepth = depth;

            if (FSOEnvironment.SoftwareDepth && color != null && depth != null) InheritDepthStencil(depth, color);
            var gd = GD;
            gd.SetRenderTarget(color); //can have null subresource when switching to 2d with supersampling enabled, which is odd since the texture is not disposed
            if (clear)
            {
                StencilValue = 1;

                gd.Clear(clearColor);// FSO.Common.Rendering.Framework.GameScreen.ClearColor);
                if (depth != null)
                {
                    gd.SetRenderTarget(depth);
                    gd.Clear(Color.White);
                }
            }
            if (FSOEnvironment.UseMRT)
            {
                if (depth != null) gd.SetRenderTargets(color, depth);
            }
        }

        // Software depth needs a colour target and its depth target to share one depth-stencil buffer: the colour
        // pass marks the pixels it drew in the stencil, and the depth pass writes depth only where they're marked.
        // FreeSO's own MonoGame had RenderTarget2D.InheritDepthStencil for this. Stock MonoGame (OpenGL) keeps the
        // buffers in internal properties, read when a target's framebuffer is first built, so they're set here
        // before the depth target is first drawn to. Without this the depth pass wrote depth over whole sprites.
        private static PropertyInfo RTDepthBuffer, RTStencilBuffer;
        private static bool RTReflectTried;
        private static int InheritLogs;
        // targets sharing another's buffers -> their own buffers (CreateRenderTarget gives every target one), put back
        // before disposal so MonoGame deletes those and not the shared ones
        private static Dictionary<RenderTarget2D, Tuple<int, int>> Inherited = new Dictionary<RenderTarget2D, Tuple<int, int>>();

        public static void InheritDepthStencil(RenderTarget2D target, RenderTarget2D source)
        {
            if (!RTReflectTried)
            {
                RTReflectTried = true;
                var iface = typeof(RenderTarget2D).GetInterfaces().FirstOrDefault(x => x.Name == "IRenderTarget");
                RTDepthBuffer = iface?.GetProperty("GLDepthBuffer");
                RTStencilBuffer = iface?.GetProperty("GLStencilBuffer");
            }
            if (RTDepthBuffer == null || RTStencilBuffer == null || target == null || source == null || target == source) return;
            try
            {
                var depth = (int)RTDepthBuffer.GetValue(source);
                var stencil = (int)RTStencilBuffer.GetValue(source);
                var targetDepth = (int)RTDepthBuffer.GetValue(target);
                if (targetDepth == depth && (int)RTStencilBuffer.GetValue(target) == stencil) return; //already shared
                if (target.Width != source.Width || target.Height != source.Height || depth == 0 || Inherited.ContainsKey(target))
                {
                    if (InheritLogs++ < 8) Console.WriteLine($"[2d] can't share depth-stencil: {source.Width}x{source.Height} (depth {depth}) with {target.Width}x{target.Height} (depth {targetDepth})");
                    return;
                }
                Inherited[target] = Tuple.Create(targetDepth, (int)RTStencilBuffer.GetValue(target));
                RTDepthBuffer.SetValue(target, depth);
                RTStencilBuffer.SetValue(target, stencil);
            }
            catch (Exception e)
            {
                if (InheritLogs++ < 8) Console.WriteLine("[2d] depth-stencil sharing failed: " + e.Message);
            }
        }

        /// <summary>Call before disposing a target: one that shares another's depth-stencil buffer lets the owner delete it.</summary>
        public static void ReleaseInherited(RenderTarget2D target)
        {
            Tuple<int, int> own;
            if (target == null || !Inherited.TryGetValue(target, out own)) return;
            Inherited.Remove(target);
            RTDepthBuffer.SetValue(target, own.Item1);
            RTStencilBuffer.SetValue(target, own.Item2);
        }

        public static RenderTarget2D GetBackbuffer()
        {
            return Backbuffer;
        }

        /// <summary>Effects whose shaders read the depth image as draw buffer 1 (framebuffer fetch).</summary>
        public static HashSet<Effect> FetchEffects = new HashSet<Effect>();
        /// <summary>Called after binding colour + depth together, to draw into both (MonoGame doesn't call glDrawBuffers on GLES).</summary>
        public static Action<int> DrawBuffersHook;
        private static RenderTargetBinding[] BoundTargets = new RenderTargetBinding[8];
        private static Dictionary<Point, RenderTarget2D> ScratchDepths = new Dictionary<Point, RenderTarget2D>();

        private static RenderTarget2D ScratchDepth(int width, int height)
        {
            var size = new Point(width, height);
            RenderTarget2D result;
            if (!ScratchDepths.TryGetValue(size, out result) || result.IsDisposed)
            {
                result = new RenderTarget2D(GD, width, height, false, SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
                ScratchDepths[size] = result;
            }
            return result;
        }

        private static bool FetchTargetsBound(RenderTarget2D color, RenderTarget2D depth)
        {
            var gd = GD;
            if (gd.RenderTargetCount != 2) return false;
            gd.GetRenderTargets(BoundTargets);
            return BoundTargets[0].RenderTarget == color && BoundTargets[1].RenderTarget == depth;
        }

        /// <summary>
        /// Back to drawing into the colour target alone, after one-pass software depth draws (which leave colour + depth
        /// bound, to avoid switching targets for every sprite). For drawing with shaders that don't keep draw buffer 1.
        /// </summary>
        public static void EndFetch()
        {
            if (ActiveColor != null && ActiveDepth != null && FetchTargetsBound(ActiveColor, ActiveDepth)) GD.SetRenderTarget(ActiveColor);
        }

        public delegate void RenderPPXProcedureDelegate(bool depthPass);
        public static void RenderPPXDepth(Effect effect, bool forceDepth,
            RenderPPXProcedureDelegate proc)
        {
            var color = ActiveColor;
            var depth = ActiveDepth;
            var gd = GD;
            if (FSOEnvironment.SoftwareDepth && depth != null && color != null && FetchEffects.Contains(effect))
            {
                // one pass: the shaders test against and write the depth image (draw buffer 1) themselves. Colour +
                // depth stay bound afterwards, so the next sprite doesn't switch targets.
                if (!FetchTargetsBound(color, depth))
                {
                    gd.SetRenderTargets(color, depth);
                    DrawBuffersHook?.Invoke(2);
                }
                var oldDS = gd.DepthStencilState;
                gd.DepthStencilState = forceDepth ? DepthStencilState.Default : DepthStencilState.None;
                effect.Parameters["depthOutMode"]?.SetValue(false);
                effect.Parameters["depthMap"]?.SetValue((Texture2D)null);
                proc(false);
                gd.DepthStencilState = oldDS;
                return;
            }
            if (FSOEnvironment.SoftwareDepth && depth != null)
            {
                var oldDS = gd.DepthStencilState;
                //completely special case.
                gd.SetRenderTarget(color);
                gd.DepthStencilState = new DepthStencilState
                {
                    StencilEnable = true,
                    StencilFunction = CompareFunction.Always,
                    StencilFail = StencilOperation.Keep,
                    StencilPass = StencilOperation.Replace,
                    CounterClockwiseStencilPass = StencilOperation.Replace,
                    StencilDepthBufferFail = StencilOperation.Keep,
                    DepthBufferEnable = forceDepth, //(ActiveColor == null),
                    DepthBufferWriteEnable = forceDepth, //(ActiveColor == null),
                    ReferenceStencil = StencilValue,
                    TwoSidedStencilMode = true
                };
                effect.Parameters["depthMap"].SetValue(depth);
                effect.Parameters["depthOutMode"].SetValue(false);
                proc(false);

                //now draw the depth using the depth test information we got previously.

                //unbind depth map since we are writing to it
                effect.Parameters["depthMap"].SetValue((Texture2D)null);
                effect.Parameters["depthOutMode"].SetValue(true);
                gd.SetRenderTarget(depth);
                gd.DepthStencilState = new DepthStencilState
                {
                    StencilEnable = true,
                    StencilFunction = CompareFunction.Equal,
                    DepthBufferEnable = forceDepth,
                    DepthBufferWriteEnable = forceDepth,
                    ReferenceStencil = StencilValue,
                };
                proc(true);

                gd.DepthStencilState = oldDS;
                StencilValue++; //can increment up to 254 times. Assume we're not going to be rendering that much between clears.
                if (StencilValue > 255) StencilValue = 1;
                gd.SetRenderTarget(color);
                effect.Parameters["depthOutMode"].SetValue(false);
            }
            else if (!FSOEnvironment.UseMRT && depth != null)
            {
                //draw color then draw depth
                gd.SetRenderTarget(color);
                proc(false);
                effect.Parameters["depthOutMode"].SetValue(true);
                gd.SetRenderTarget(depth);
                proc(true);
                effect.Parameters["depthOutMode"].SetValue(false);
            }
            else
            {
                //mrt already bound. draw in both.
                proc(false);
            }
        }

        public static Action<GraphicsDevice, RenderTarget2D> SSAAFunc;
        public static bool WithOpacity = true;

        public static void DrawBackbuffer(float opacity, float scale)
        {
            if (Backbuffer == null) return; //this gfx mode does not use a rendertarget backbuffer
            if (SSAA > 1)
            {
                SSAAFunc(GD, Backbuffer);
            }
            else
            {
                if (!WithOpacity)
                {
                    SB.Begin(blendState: BlendState.Opaque);
                    opacity = 1;
                }
                else
                    SB.Begin(blendState: BlendState.AlphaBlend);
                SB.Draw(Backbuffer, new Vector2(Backbuffer.Width * (1 - scale) / 2, Backbuffer.Height * (1 - scale) / 2), null, Color.White * opacity, 0f, new Vector2(), scale,
                    SpriteEffects.None, 0);
                SB.End();
            }
        }

        public static Point GetWidthHeight()
        {
            return new Point(Backbuffer.Width, Backbuffer.Height);
        }

        public static RenderTarget2D CreateRenderTarget(GraphicsDevice device, int numberLevels, int multisample, SurfaceFormat surface, int width, int height, DepthFormat dformat)
        {
            //apparently in xna4, there is no way to check device format... (it looks for the closest format if desired is not supported) need to look into if this affects anything.

            /*MultiSampleType type = device.PresentationParameters.MultiSampleType;

            // If the card can't use the surface format
            if (!GraphicsAdapter.DefaultAdapter.CheckDeviceFormat(
                DeviceType.Hardware,
                GraphicsAdapter.DefaultAdapter.CurrentDisplayMode.Format,
                TextureUsage.None,
                QueryUsages.None,
                ResourceType.RenderTarget,
                surface))
            {
                // Fall back to current display format
                surface = device.DisplayMode.Format;
            }
            // Or it can't accept that surface format 
            // with the current AA settings
            else if (!GraphicsAdapter.DefaultAdapter.CheckDeviceMultiSampleType(
                DeviceType.Hardware, surface,
                device.PresentationParameters.IsFullScreen, type))
            {
                // Fall back to no antialiasing
                type = MultiSampleType.None;
            }*/

            /*int width, height;

            // See if we can use our buffer size as our texture
            CheckTextureSize(device.PresentationParameters.BackBufferWidth,
                device.PresentationParameters.BackBufferHeight,
                out width, out height);*/

            // Create our render target
            return new RenderTarget2D(device,
                width, height, (numberLevels > 1), surface,
                DepthFormat.Depth24Stencil8, multisample, RenderTargetUsage.PreserveContents);
        }
    }
}
