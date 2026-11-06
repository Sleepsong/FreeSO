using System.Collections.Generic;
using System.Linq;
using System.IO;
using FSO.Common.Rendering.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework;
using FSO.Content.Model;
using FSO.Vitaboy.Model;
using FSO.Files.RC;

namespace FSO.Vitaboy
{
    /// <summary>
    /// The base class for all avatars in the game.
    /// </summary>
    public abstract class Avatar : _3DComponent
    {
        // Debug logging for censorship system
        private static string _logPath;
        private static object _logLock = new object();
        public static bool CensorDebugEnabled = true;
        private static bool _logInitialized = false;

        // DEBUG: Set to true to force censorship on ALL body parts for testing
        // This will make ALL avatars show the mosaic blur - just for testing!
        public static bool FORCE_CENSOR_TEST = false;
        /// <summary>Set while drawing avatars' depth (software depth): skip the censor overlay, which isn't depth.</summary>
        public static bool DepthPass;

        private static void InitLog()
        {
            if (_logInitialized) return;
            _logInitialized = true;

            // Try desktop first, fall back to current directory
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            if (string.IsNullOrEmpty(desktop) || !Directory.Exists(desktop))
            {
                desktop = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }
            if (string.IsNullOrEmpty(desktop) || !Directory.Exists(desktop))
            {
                desktop = ".";
            }
            _logPath = Path.Combine(desktop, "simitone_censor_debug.log");

            try
            {
                File.AppendAllText(_logPath, $"\n\n=== Simitone Censor Debug Log Started {DateTime.Now} ===\n");
                File.AppendAllText(_logPath, $"Log path: {_logPath}\n");
            }
            catch { }
        }

        public List<AvatarBindingInstance> Bindings = new List<AvatarBindingInstance>();
        public static Effect Effect;
        public Skeleton Skeleton { get; set; }
        public Skeleton BaseSkeleton { get; set; }
        public List<Vector2> LightPositions;
        public DGRP3DMesh HeadObject;
        public float HeadObjectRotation;
        public float HeadObjectSpeedyVel;
        public bool HideHead;
        protected Matrix[] SkelBones;

        /// <summary>
        /// Virtual property for skin tone access in censorship rendering.
        /// Override in SimAvatar to return the actual skin tone.
        /// </summary>
        protected virtual AppearanceType? SkinToneForCensor => null;

        public static void setVitaboyEffect(Effect e) {
            Effect = e;
        }

        /// <summary>
        /// Creates a new Avatar instance.
        /// </summary>
        /// <param name="skel">A Skeleton instance.</param>
        public Avatar(Skeleton skel)
        {
            this.Skeleton = skel?.Clone();
            this.BaseSkeleton = skel?.Clone(); //keep a copy we can revert back to
        }

        public Avatar(Avatar old)
        {
            this.BaseSkeleton = old.BaseSkeleton.Clone();
            this.Skeleton = old.BaseSkeleton.Clone();
            for (int i = 0; i < old.Bindings.Count(); i++)
            {
                AvatarBindingInstance oldb = old.Bindings[i];
                Bindings.Add(new AvatarBindingInstance()
                {
                    Mesh = oldb.Mesh,
                    Texture = oldb.Texture,
                    CensorFlagBits = oldb.CensorFlagBits
                });
            }
            for (int i = 0; i < old.Accessories.Count(); i++)
            {
                this.Accessories.Add(old.Accessories.Keys.ElementAt(i), old.Accessories.Values.ElementAt(i));
            } //just shallow copy the binding and accessory list, as the data inside isn't going to change any time soon...
        }

        private Dictionary<Appearance, AvatarAppearanceInstance> Accessories = new Dictionary<Appearance, AvatarAppearanceInstance>();
        
        /// <summary>
        /// Adds an accessory to this avatar.
        /// </summary>
        /// <param name="apr">The Appearance instance of the accessory.</param>
        public void AddAccessory(Appearance apr)
        {
            if (Accessories.ContainsKey(apr))
            {
                return;
            }

            var add = AddAppearance(apr, null);
            Accessories.Add(apr, add);
        }

        /// <summary>
        /// Remove an accessory from this avatar.
        /// </summary>
        /// <param name="apr">The Appearance of the accessory to remove.</param>
        public void RemoveAccessory(Appearance apr)
        {
            if (apr == null) return;
            if (Accessories.ContainsKey(apr))
            {
                RemoveAppearance(Accessories[apr], true);
                Accessories.Remove(apr);
            }
        }

        private bool GPUMode;
        private GraphicsDevice GPUDevice;
        public void StoreOnGPU(GraphicsDevice device)
        {
            GPUMode = true;
            GPUDevice = device;
            lock (Bindings)
            {
                foreach (var binding in Bindings)
                {
                    binding.Mesh.StoreOnGPU(device);
                    binding.Texture.Get(device);
                }
            }
        }

        private string UniformName(string name)
        {
            return name.ToLowerInvariant().Replace("lgt", "").Replace("med", "").Replace("drk", "");
        }

        /// <summary>
        /// Adds an Appearance instance to this avatar.
        /// </summary>
        /// <param name="appearance">The Appearance instance to add.</param>
        /// <returns>An AvatarAppearanceInstance instance.</returns>
        protected AvatarAppearanceInstance AddAppearance(Appearance appearance, string texOverride)
        {
            var result = new AvatarAppearanceInstance();
            result.Original = appearance;
            result.Bindings = new List<AvatarBindingInstance>();

            int i = 0;
            int replaced = 0;
            var realBindings = appearance.Bindings.Select(bindingReference =>
                bindingReference.RealBinding ?? FSO.Content.Content.Get().AvatarBindings?.Get(bindingReference.TypeID, bindingReference.FileID)).ToList();

            if (Content.Content.Get().TS1)
            {
                foreach (var binding in realBindings)
                {
                    if (binding == null) { i++; continue; }
                    var mesh = Content.Content.Get().AvatarMeshes.Get(binding.MeshName);
                    if (texOverride != null &&
                            (UniformName(mesh.TextureName.ToLowerInvariant()).EndsWith(UniformName(texOverride.ToLowerInvariant()))
                            || mesh.TextureName.ToLowerInvariant() == "x"))
                    {
                        replaced = i;
                    }
                    i++;
                }

                if (texOverride != null)
                {
                    realBindings[replaced] = realBindings[replaced].TS1Copy();
                    realBindings[replaced].TextureName = texOverride;
                }
            }

            foreach (var binding in realBindings)
            {
                if (binding == null) { continue; }
                result.Bindings.Add(AddBinding(binding));
            }

            return result;
        }

        /// <summary>
        /// Removes an Appearance instance from this avatar.
        /// </summary>
        /// <param name="appearance">The Appearance instance to remove.</param>
        /// <param name="dispose">Should the appearance be disposed?</param>
        public void RemoveAppearance(AvatarAppearanceInstance appearance, bool dispose)
        {
            lock (Bindings)
            {
                if (appearance == null) return;
                foreach (var binding in appearance.Bindings)
                {
                    RemoveBinding(binding, dispose);
                }
            }
        }

        /// <summary>
        /// Adds a Binding instance to this avatar.
        /// </summary>
        /// <param name="binding">The Binding instance to add.</param>
        /// <returns>An AvatarBindingInstance instance.</returns>
        protected AvatarBindingInstance AddBinding(Binding binding)
        {
            var content = FSO.Content.Content.Get();
            var instance = new AvatarBindingInstance();
            if (binding.MeshName != null)
            {
                instance.Mesh = content.AvatarMeshes.Get(binding.MeshName);
                instance.Texture = content.AvatarTextures.Get(binding.TextureName ?? instance.Mesh.TextureName);
            }
            else
            {
                instance.Mesh = content.AvatarMeshes.Get(binding.MeshTypeID, binding.MeshFileID);
                instance.Texture = content.AvatarTextures.Get(binding.TextureTypeID, binding.TextureFileID);
            }

            /*if (instance.Mesh != null)
            {
                //We make a copy so we can modify it, most of the variables
                //are kept as pointers because we only change a few locals
                //per sim, the rest are global
                instance.Mesh = instance.Mesh.Clone();
            }*/

            instance.Mesh.Prepare(Skeleton.RootBone);
            instance.CensorFlagBits = binding.CensorFlagBits; // Preserve censorship flags for rendering

            /*if (GPUMode)
            {
                instance.Mesh.StoreOnGPU(GPUDevice);
                instance.Texture.Get(GPUDevice);
            }*/

            lock (Bindings)
            {
                Bindings.Add(instance);
            }
            return instance;
        }

        /// <summary>
        /// Removes a Binding instance from this avatar.
        /// </summary>
        /// <param name="instance">The Binding instance to remove.</param>
        /// <param name="dispose">Should the binding be disposed?</param>
        protected void RemoveBinding(AvatarBindingInstance instance, bool dispose)
        {
            lock (Bindings)
            {
                Bindings.Remove(instance);
            }
        }

        /// <summary>
        /// Initializes this Avatar instance.
        /// </summary>
        public override void Initialize()
        {
            base.Initialize();
        }

        /// <summary>
        /// When the skeleton changes (for example due to an animation) this
        /// method will recompute the meshes to adhere to the new skeleton positions.
        /// </summary>
        public void ReloadSkeleton()
        {
            if (Skeleton == null) return;
            Skeleton.ComputeBonePositions(Skeleton.RootBone, Matrix.Identity);
            SkelBones = new Matrix[Skeleton.Bones.Length];
            for (int i = 0; i < Skeleton.Bones.Length; i++)
            {
                SkelBones[i] = Skeleton.Bones[i].AbsoluteMatrix;
            }
        }

        /// <summary>
        /// Updates this Avatar instance.
        /// </summary>
        /// <param name="state">An UpdateState instance.</param>
        public override void Update(FSO.Common.Rendering.Framework.Model.UpdateState state)
        {
        }

        public static int DefaultTechnique = 0;
        public Vector4 AmbientLight = Vector4.One;

        // Censorship pixelation effect resources
        private static SpriteBatch _censorSpriteBatch;

        // Per-skin-tone mosaic textures for censorship
        private static Dictionary<AppearanceType, Texture2D> _mosaicTextures = new Dictionary<AppearanceType, Texture2D>();

        private static Texture2D GetMosaicTexture(GraphicsDevice device, AppearanceType skinTone)
        {
            if (_mosaicTextures.TryGetValue(skinTone, out var tex) && !tex.IsDisposed)
                return tex;

            // Color palettes per skin tone (base + variations, opaque)
            Color[] colors = skinTone switch
            {
                AppearanceType.Light => new Color[] {
                    new Color(255, 224, 196),  // light base
                    new Color(255, 210, 180),
                    new Color(248, 216, 188),
                    new Color(255, 200, 170)
                },
                AppearanceType.Medium => new Color[] {
                    new Color(224, 172, 132),  // medium base
                    new Color(210, 160, 120),
                    new Color(200, 150, 110),
                    new Color(215, 165, 125)
                },
                AppearanceType.Dark => new Color[] {
                    new Color(140, 90, 60),    // dark base
                    new Color(130, 80, 50),
                    new Color(150, 95, 65),
                    new Color(120, 75, 45)
                },
                _ => new Color[] {  // fallback = Dark
                    new Color(140, 90, 60),
                    new Color(130, 80, 50),
                    new Color(150, 95, 65),
                    new Color(120, 75, 45)
                }
            };

            // Generate 64x64 texture with 8x8 grid
            int size = 64, gridSize = 8;
            var data = new Color[size * size];

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int gridX = x / (size / gridSize);
                    int gridY = y / (size / gridSize);
                    int colorIndex = (gridX * 3 + gridY * 7) % colors.Length;
                    data[y * size + x] = colors[colorIndex];
                }
            }

            var texture = new Texture2D(device, size, size);
            texture.SetData(data);
            _mosaicTextures[skinTone] = texture;
            return texture;
        }

        /// <summary>
        /// Draws the meshes making up this Avatar instance.
        /// </summary>
        /// <param name="device">A GraphicsDevice instance.</param>
        public override void Draw(Microsoft.Xna.Framework.Graphics.GraphicsDevice device)
        {
            Effect.CurrentTechnique = Effect.Techniques[DefaultTechnique];
            Effect.Parameters["View"].SetValue(View);
            Effect.Parameters["Projection"].SetValue(Projection);
            Effect.Parameters["World"].SetValue(World);
            Effect.Parameters["AmbientLight"].SetValue(AmbientLight);

            DrawGeometry(device, Effect);
        }

        public void DrawHeadOnly(Microsoft.Xna.Framework.Graphics.GraphicsDevice device, Effect effect, Matrix headMatrix)
        {
            //Effect.CurrentTechnique = Effect.Techniques[0];
            if (SkelBones == null) ReloadSkeleton();
            var matrixCopy = new Matrix[SkelBones.Length];
            // Locate the head bone and inject the custom matrix.

            for (int i = 0; i < Skeleton.Bones.Length; i++)
            {
                var bone = Skeleton.Bones[i];

                if (bone.Name == "HEAD") matrixCopy[i] = headMatrix;
            }

            effect.Parameters["SkelBindings"].SetValue(matrixCopy);

            lock (Bindings)
            {
                foreach (var pass in effect.CurrentTechnique.Passes)
                {
                    foreach (var binding in Bindings)
                    {
                        if (binding.Texture != null)
                        {
                            var tex = binding.Texture.Get(device);
                            effect.Parameters["MeshTex"].SetValue(tex);
                        }
                        else
                        {
                            effect.Parameters["MeshTex"].SetValue((Texture2D)null);
                        }
                        pass.Apply();
                        binding.Mesh.Draw(device);
                    }
                }
            }
        }

        private static int _lastLoggedCensorFlags = -1;
        private static DateTime _lastCensorLog = DateTime.MinValue;

        public void DrawGeometry(Microsoft.Xna.Framework.Graphics.GraphicsDevice device, Effect effect, int censorshipFlags = 0)
        {
            // DEBUG: Force censorship test mode - censor ALL body parts
            if (FORCE_CENSOR_TEST)
            {
                censorshipFlags = 0xFF; // All flags on - Pelvis, Spine, Head, Hands, Feet, FullBody
            }

            // Debug log censorship flags (throttled to avoid spam)
            if (censorshipFlags != 0 && (censorshipFlags != _lastLoggedCensorFlags || (DateTime.Now - _lastCensorLog).TotalSeconds > 2))
            {
                _lastLoggedCensorFlags = censorshipFlags;
                _lastCensorLog = DateTime.Now;
            }

            if (SkelBones == null) ReloadSkeleton();
            effect.Parameters["SkelBindings"].SetValue(SkelBones);

            // Check if censorship is active (either from game or forced for testing)
            bool showCensorBlur = censorshipFlags != 0 || FORCE_CENSOR_TEST;

            lock (Bindings)
            {
                // Draw all meshes normally
                foreach (var pass in effect.CurrentTechnique.Passes)
                {
                    foreach (var binding in Bindings)
                    {
                        if (HideHead && binding.Mesh.BoneBindings.Any(meshBind => meshBind.BoneName == "HEAD"))
                        {
                            continue;
                        }

                        if (binding.Texture != null)
                        {
                            var tex = binding.Texture.Get(device);
                            effect.Parameters["MeshTex"].SetValue(tex);
                        }
                        else
                        {
                            effect.Parameters["MeshTex"].SetValue((Texture2D)null);
                        }
                        pass.Apply();
                        binding.Mesh.Draw(device);
                    }
                }

                // If censorship is active, draw mosaic overlay at pelvis position
                if (showCensorBlur && !DepthPass)
                {
                    DrawCensoredMeshesPixelated(device, effect, censorshipFlags);
                }
            }

            //skip drawing shadows if we're drawing id
            if (LightPositions == null || effect.CurrentTechnique == effect.Techniques[1]) return;

            if (ShadBuf == null)
            {
                var shadVerts = new ShadowVertex[]
                {
                new ShadowVertex(new Vector3(-1, 0, -1), 25),
                new ShadowVertex(new Vector3(-1, 0, 1), 25),
                new ShadowVertex(new Vector3(1, 0, 1), 25),
                new ShadowVertex(new Vector3(1, 0, -1), 25),

                new ShadowVertex(new Vector3(-1, 0, -1), 19),
                new ShadowVertex(new Vector3(-1, 0, 1), 19),
                new ShadowVertex(new Vector3(1, 0, 1), 19),
                new ShadowVertex(new Vector3(1, 0, -1), 19)
                };
                for (int i = 0; i < shadVerts.Length; i++) shadVerts[i].Position *= 1f;
                int[] shadInd = new int[] { 2, 1, 0, 2, 0, 3, 6, 5, 4, 6, 4, 7 };

                ShadBuf = new VertexBuffer(device, typeof(ShadowVertex), shadVerts.Length, BufferUsage.None);
                ShadBuf.SetData(shadVerts);
                ShadIBuf = new IndexBuffer(device, IndexElementSize.ThirtyTwoBits, shadInd.Length, BufferUsage.None);
                ShadIBuf.SetData(shadInd);
            }

            foreach (var light in LightPositions)
            {
                //effect.Parameters["FloorHeight"].SetValue((float)(Math.Floor(Position.Y/2.95)*2.95 + 0.05));
                effect.Parameters["LightPosition"].SetValue(light);
                var oldTech = effect.CurrentTechnique;
                effect.CurrentTechnique = effect.Techniques[4];
                effect.CurrentTechnique.Passes[0].Apply();
                device.DepthStencilState = DepthStencilState.DepthRead;
                device.SetVertexBuffer(ShadBuf);
                device.Indices = ShadIBuf;
                device.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, 4);
                effect.CurrentTechnique = oldTech;
                device.DepthStencilState = DepthStencilState.Default;
            }

            DrawHeadObject(device, effect);
        }

        /// <summary>
        /// Draws a simple procedural mosaic at the pelvis position.
        /// Uses fixed sizes based on zoom level for consistent appearance.
        /// </summary>
        private void DrawCensoredMeshesPixelated(GraphicsDevice device, Effect effect, int censorshipFlags)
        {
            // Get skin tone (default to Dark if unknown, per user request)
            var skinTone = SkinToneForCensor ?? AppearanceType.Dark;
            var mosaicTexture = GetMosaicTexture(device, skinTone);

            if (_censorSpriteBatch == null || _censorSpriteBatch.IsDisposed)
                _censorSpriteBatch = new SpriteBatch(device);

            // Get pelvis screen position
            var viewMatrix = effect.Parameters["View"].GetValueMatrix();
            var projMatrix = effect.Parameters["Projection"].GetValueMatrix();
            var worldMatrix = effect.Parameters["World"].GetValueMatrix();

            Vector3 pelvisWorld = Vector3.Zero;
            if (Skeleton != null)
            {
                var pelvisBone = Skeleton.GetBone("PELVIS");
                if (pelvisBone != null)
                    pelvisWorld = Vector3.Transform(pelvisBone.AbsolutePosition, worldMatrix);
            }

            var viewport = device.Viewport;
            var screenPos = viewport.Project(pelvisWorld, projMatrix, viewMatrix, Matrix.Identity);

            if (screenPos.Z < 0 || screenPos.Z > 1) return;

            // Fixed size - simple and reliable
            // Use projection diagonal to estimate zoom (works for both 2D and 3D)
            int censorSize = 50; // default
            bool isPerspective = Math.Abs(projMatrix.M34) > 0.0001f;

            if (!isPerspective)
            {
                // 2D orthographic - use projection scale
                float scale = Math.Abs(projMatrix.M11);
                if (scale > 0.015f) censorSize = 70;       // Near
                else if (scale > 0.008f) censorSize = 50;  // Medium
                else censorSize = 35;                       // Far
            }

            int censorWidth = censorSize;
            int censorHeight = (int)(censorSize * 1.4f);
            int halfWidth = censorWidth / 2;
            int halfHeight = censorHeight / 2;
            var destRect = new Rectangle(
                (int)screenPos.X - halfWidth,
                (int)screenPos.Y - halfHeight,
                censorWidth,
                censorHeight
            );

            _censorSpriteBatch.Begin(SpriteSortMode.Immediate, BlendState.AlphaBlend,
                SamplerState.PointClamp, DepthStencilState.None, RasterizerState.CullNone);
            _censorSpriteBatch.Draw(mosaicTexture, destRect, Color.White);
            _censorSpriteBatch.End();
        }

        public void DrawHeadObject(GraphicsDevice device, Effect effect)
        {
            //0: high reflective
            //1: light reflective
            //2: outline
            if (effect.Techniques[1] == effect.CurrentTechnique) return;
            var headObj = HeadObject;
            if (headObj == null) return;
            var oldTech = effect.CurrentTechnique;
            effect.CurrentTechnique = effect.Techniques[6];
            device.RasterizerState = RasterizerState.CullClockwise;

            var trans = Matrix.Invert(effect.Parameters["View"].GetValueMatrix()).Translation;
            effect.Parameters["HOCameraPosition"].SetValue(trans);
            effect.Parameters["World"].SetValue(Matrix.CreateScale(1.33f) * Matrix.CreateRotationY(HeadObjectRotation) * Matrix.CreateTranslation(Skeleton.GetBone("HEAD").AbsoluteMatrix.Translation + new Vector3(0, 3.25f, 0)) * effect.Parameters["World"].GetValueMatrix());

            for (int i=0; i<headObj.Geoms.Count; i++)
            {
                var multi = 0.5f / (headObj.Geoms.Count - 1);
                var geom = headObj.Geoms[i];
                foreach (var item in geom)
                {
                    effect.Parameters["MeshTex"].SetValue(item.Key);
                    effect.Parameters["HOToonSpecThresh"].SetValue(0.5f);
                    effect.Parameters["HOToonSpecColor"].SetValue(new Vector3(multi * ((headObj.Geoms.Count-1) - i)));

                    effect.CurrentTechnique.Passes[0].Apply();
                    //if (i != 1) device.RasterizerState = RasterizerState.CullClockwise;
                    device.SetVertexBuffer(item.Value.Verts);
                    device.Indices = item.Value.Indices;
                    device.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, item.Value.PrimCount);
                    //if (i != 1) device.RasterizerState = RasterizerState.CullCounterClockwise;
                }
            }
            device.RasterizerState = RasterizerState.CullCounterClockwise;
            effect.CurrentTechnique = oldTech;
        }

        // TODO: memory leak
        private static VertexBuffer ShadBuf;
        private static IndexBuffer ShadIBuf;

        public override void DeviceReset(GraphicsDevice Device)
        {
        }
    }

    /// <summary>
    /// Holds bindings for an avatar.
    /// </summary>
    public class AvatarAppearanceInstance
    {
        public Appearance Original;
        public List<AvatarBindingInstance> Bindings;
    }

    /// <summary>
    /// Holds a mesh and texture for an avatar.
    /// </summary>
    public class AvatarBindingInstance
    {
        public Mesh Mesh;
        public ITextureRef Texture;
        public int CensorFlagBits; // Which body parts this mesh covers (for censorship blur)
    }
}
