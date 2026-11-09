using FSO.Common;
using FSO.Common.Utils;
using FSO.Files;
using FSO.Files.Formats.IFF.Chunks;
using FSO.Files.RC;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FSO.Content
{
    public class RCMeshProvider
    {
        public GraphicsDevice GD;
        public HashSet<string> CacheFiles;
        public HashSet<string> ReplaceFiles;

        /// <summary>
        /// Replacement meshes and textures (Content/MeshReplace, and MeshReplace in the user folder, which wins), by file
        /// name in any case. The names asked for come from the game's own object file names, whose case varies between
        /// installs, and Linux file names are case-sensitive: a remesh pack made on Windows would otherwise load its
        /// meshes and miss their textures.
        /// </summary>
        private Dictionary<string, string> ReplacePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Notes about replacement meshes that couldn't be used, for a log (the frontend sets it, e.g. with -logs).
        /// Each mesh is noted once.
        /// </summary>
        public static Action<string> Note;

        public RCMeshProvider(GraphicsDevice gd)
        {
            GD = gd;
            DGRP3DGeometry.ReplTextureProvider = GetTex;

            var repldir = Path.Combine(FSOEnvironment.ContentDir, "MeshReplace/");
            var userrepl = Path.Combine(FSOEnvironment.UserDir, "MeshReplace/");
            var dir = Path.Combine(FSOEnvironment.UserDir, "MeshCache/");
            try
            {
                Directory.CreateDirectory(dir);
                Directory.CreateDirectory(repldir);
            } catch
            {
            }
            CacheFiles = new HashSet<string>(Directory.GetFiles(dir).Select(x => Path.GetFileName(x).ToLowerInvariant()));
            AddReplacements(repldir);
            AddReplacements(userrepl);
            ReplaceFiles = new HashSet<string>(ReplacePaths.Keys.Select(x => x.ToLowerInvariant()));
        }

        private void AddReplacements(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                foreach (var file in Directory.GetFiles(dir)) ReplacePaths[Path.GetFileName(file)] = file;
            }
            catch
            {
            }
        }
        public Dictionary<DGRP, DGRP3DMesh> Cache = new Dictionary<DGRP, DGRP3DMesh>();
        public HashSet<DGRP> IgnoreRCCache = new HashSet<DGRP>();
        public ConcurrentDictionary<string, MTEX> ReplacementTex = new ConcurrentDictionary<string, MTEX>();
        public Dictionary<string, DGRP3DMesh> NameCache = new Dictionary<string, DGRP3DMesh>();

        public DGRP3DMesh Get(DGRP dgrp, OBJD obj)
        {
            DGRP3DMesh result = null;
            var repldir = Path.Combine(FSOEnvironment.ContentDir, "MeshReplace/");
            var dir = Path.Combine(FSOEnvironment.UserDir, "MeshCache/");
            if (!Cache.TryGetValue(dgrp, out result))
            {
                //does it exist in replacements
                var name = obj.ChunkParent.Filename.Replace('.', '_').ToLowerInvariant() + "_" + dgrp.ChunkID + ".fsom";
                if (ReplacePaths.TryGetValue(name, out var replPath))
                {
                    try
                    {
                        result = new DGRP3DMesh(dgrp, replPath, GD);
                    }
                    catch (Exception e)
                    {
                        Note?.Invoke(name + " couldn't be read (" + e.Message + "); the object keeps its own look.");
                        result = null;
                    }
                    //a replacement missing textures draws without those parts, or not at all: keep the object's own look
                    //instead. (Loaded in the background, the textures aren't known yet; Simitone loads in the foreground.)
                    if (result != null && AssetStreaming.LoadingType == AssetStreamingMode.None && result.MissingTextures.Count > 0)
                    {
                        Note?.Invoke(name + " is missing " + string.Join(", ", result.MissingTextures.Distinct())
                            + " (looked for in MeshReplace); the object keeps its own look.");
                        result = null;
                    }
                }

                if (result == null)
                {
                    //does it exist in iff
                    try
                    {
                        result = dgrp.ChunkParent.Get<FSOM>(dgrp.ChunkID)?.Get(dgrp, GD);
                    }
                    catch (Exception)
                    {
                        result = null;
                    }
                }

                if (CacheFiles.Contains(name))
                {
                    if (result == null && !IgnoreRCCache.Contains(dgrp))
                    {
                        //does it exist in rc cache
                        try
                        {
                            result = new DGRP3DMesh(dgrp, Path.Combine(dir, name), GD);
                        }
                        catch (Exception)
                        {
                            result = null;
                        }
                    }
                } else
                {

                }

                //create it anew
                if (result == null)
                {
                    result = new DGRP3DMesh(dgrp, obj, GD, dir);
                    CacheFiles.Add(name);
                }
                Cache[dgrp] = result;
            }
            return result;
        }

        public DGRP3DMesh Get(string name)
        {
            DGRP3DMesh result = null;
            var repldir = Path.Combine(FSOEnvironment.ContentDir, "3D/");
            if (!NameCache.TryGetValue(name, out result))
            {
                //does it exist in replacements
                try
                {
                    result = new DGRP3DMesh(null, Path.Combine(repldir, name), GD);
                }
                catch (Exception)
                {
                    result = null;
                }
                NameCache[name] = result;
            }
            return result;
        }

        public void ClearCache(DGRP dgrp)
        {
            //todo: dispose old?
            IgnoreRCCache.Add(dgrp);
            Cache.Remove(dgrp);
        }

        public void Replace(DGRP dgrp, DGRP3DMesh mesh)
        {
            //todo: dispose old?

            var name = dgrp.ChunkParent.Filename.Replace('.', '_').ToLowerInvariant() + "_" + dgrp.ChunkID + ".fsom";
            var repldir = Path.Combine(FSOEnvironment.ContentDir, "MeshReplace/");
            ReplaceFiles.Add(name);
            ReplacePaths[name] = Path.Combine(repldir, name);
            mesh.SaveDirectory = repldir;
            mesh.Save();

            Cache[dgrp] = mesh;
        }

        public DGRP3DTextureSource? GetTex(string name)
        {
            MTEX result = null;
            // TODO: Could have load the same texture multiple times due to a race condition?
            if (!ReplacementTex.TryGetValue(name, out result))
            {
                string path;
                if (name.StartsWith("FSO_"))
                {
                    path = Path.Combine(FSOEnvironment.ContentDir, "3D/", name.Substring(4));
                }
                else if (!ReplacePaths.TryGetValue(name, out path)) path = null; //from the meshreplace folders, in any case
                try
                {
                    if (path != null && File.Exists(path))
                    {
                        result = new MTEX(File.OpenRead(path));
                    }
                }
                catch (Exception)
                {
                    result = null;
                }
                ReplacementTex[name] = result;
            }

            return DGRP3DTextureSource.WithDecoded(result, GD);
        }
    }
}
