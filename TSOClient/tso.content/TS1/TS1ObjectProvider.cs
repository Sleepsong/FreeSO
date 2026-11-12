using FSO.Common.Utils;
using FSO.Content.Framework;
using FSO.Content.Interfaces;
using FSO.Files.Formats.IFF;
using FSO.Files.Formats.IFF.Chunks;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FSO.Content.TS1
{
    public class TS1ObjectProvider : AbstractObjectProvider, IObjectCatalog
    {
        private TS1SubProvider<IffFile> GameObjects;
        private static List<ObjectCatalogItem>[] ItemsByCategory;
        private static Dictionary<uint, ObjectCatalogItem> ItemsByGUID;
        public HashSet<uint> PersonGUIDs = new HashSet<uint>();

        public TS1ObjectProvider(Content contentManager, TS1Provider provider) : base(contentManager)
        {
            GameObjects = new TS1SubProvider<IffFile>(provider, ".iff");
        }

        public void Init()
        {
            GameObjects.Init();

            Entries = new Dictionary<ulong, GameObjectReference>();
            Cache = new TimedReferenceCache<ulong, GameObject>();

            ItemsByGUID = new Dictionary<uint, ObjectCatalogItem>();
            ItemsByCategory = new List<ObjectCatalogItem>[30];
            for (int i = 0; i < 30; i++) ItemsByCategory[i] = new List<ObjectCatalogItem>();

            var scanTimer = System.Diagnostics.Stopwatch.StartNew();
            var allIffs = GameObjects.ListGeneric();

            //what each file gave last time, so only new or changed files are opened (see CatalogIndex)
            var useIndex = !CatalogIndex.Disabled;
            var language = (int)STR.DefaultLangCode;
            var saved = useIndex ? CatalogIndex.Load(ContentManager.TS1BasePath, language) : null;
            var current = new CatalogIndex(ContentManager.TS1BasePath, language);
            var stamps = new Dictionary<string, (long, long)?>();
            int reused = 0, scanned = 0;

            foreach (var iff in allIffs)
            {
                var source = GameObjectSource.Far;
                string filename = Path.GetFileName(iff.ToString().Replace('\\', '/'));
                string container = null; //the file that would change if this one did: itself, or the .far it's in
                if (iff is FileContentReference<object>)
                {
                    //if we're in downloads, remember the real filename and set as standalone
                    //for easy editing in volcanic (not patching)
                    var fileIff = iff as FileContentReference<object>;
                    container = fileIff.Filename;
                    if (fileIff.Filename.Contains("Downloads"))
                    {
                        filename = fileIff.Filename;
                        source = GameObjectSource.Standalone;
                    }
                }
                else if (iff is Far1ProviderEntry<object>) container = ((Far1ProviderEntry<object>)iff).Archive?.FilePath;

                CatalogIndex.Record record = null;
                string key = null;
                (long, long)? stamp = null;
                if (useIndex && container != null)
                {
                    key = CatalogIndex.Key(container, iff.ToString());
                    if (!stamps.TryGetValue(container, out stamp)) stamps[container] = stamp = CatalogIndex.Stamp(container);
                    if (stamp != null && saved.Records.TryGetValue(key, out var old) && old.Size == stamp.Value.Item1 && old.Ticks == stamp.Value.Item2)
                        record = old;
                }

                if (record != null) reused++;
                else
                {
                    IffFile file = null;
                    try
                    {
                        file = (IffFile)iff.GetThrowawayGeneric();
                    }
                    catch (Exception ex)
                    {
                        // Log failed object file loads
                        string failedFilename = Path.GetFileName(iff.ToString().Replace('\\', '/'));
                        Content.FailedContentFiles.Add(new TS1BCFProvider.FailedFileInfo
                        {
                            Filename = failedFilename,
                            ErrorMessage = $"Failed to load object file: {ex.Message}",
                            ErrorType = ex.GetType().Name
                        });
                        continue;
                    }

                    if (file == null) continue;
                    scanned++;
                    file.MarkThrowaway();
                    record = ScanFile(file, filename, out bool complete);
                    //a file that had problems is scanned every launch, so its problems keep being reported
                    if (key != null && stamp != null && complete)
                    {
                        record.Size = stamp.Value.Item1;
                        record.Ticks = stamp.Value.Item2;
                    }
                    else key = null;
                }

                if (key != null) current.Records[key] = record;
                foreach (var obj in record.Objects)
                {
                    Entries[obj.GUID] = new GameObjectReference(this)
                    {
                        FileName = filename,
                        ID = obj.GUID,
                        Name = obj.Name,
                        Source = source,
                        Group = obj.Group,
                        SubIndex = obj.SubIndex,
                        GlobalSimObject = obj.Global
                    };
                    if (obj.Person) PersonGUIDs.Add(obj.GUID);
                    if (obj.InCatalog)
                    {
                        ItemsByCategory[obj.Item.Category].Add(obj.Item);
                        ItemsByGUID[obj.GUID] = obj.Item;
                    }
                }
            }

            //save when anything changed: files scanned, or files gone since last time
            if (useIndex && (scanned > 0 || saved.Records.Count != current.Records.Count)) current.Save();
            LastScanReport = (useIndex ? $"catalog index: {reused} files reused, {scanned} scanned" : "catalog index: off")
                + $", {scanTimer.ElapsedMilliseconds} ms";

            var globalSims = Entries.Values.Where(x => x.GlobalSimObject);
            ControllerObjects.Clear();
            ControllerObjects.AddRange(globalSims);

            ContentManager.Neighborhood.LoadCharacters(false);
        }

        /// <summary>How the last object scan went (files reused from the catalog index, files scanned, time), for logs and tests.</summary>
        public static string LastScanReport = "";

        /// <summary>
        /// What one object file gives the scan: each of its objects, and whether it appears in the catalog and under what
        /// name. complete is false if something went wrong, so the result shouldn't be kept in the index.
        /// </summary>
        private static CatalogIndex.Record ScanFile(IffFile file, string filename, out bool complete)
        {
            complete = true;
            var record = new CatalogIndex.Record();
            var objects = file.List<OBJD>();
            if (objects == null) return record;
            foreach (var obj in objects)
            {
                var entry = new CatalogIndex.Object
                {
                    GUID = obj.GUID,
                    Name = obj.ChunkLabel,
                    Group = (short)obj.MasterID,
                    SubIndex = obj.SubIndex,
                    Global = obj.Global == 1 && obj.ObjectType != OBJDType.GiftToken,
                    Person = obj.ObjectType == OBJDType.Person
                };
                record.Objects.Add(entry);

                //does this object appear in the catalog?
                bool passesCatalogCheck = (obj.FunctionFlags > 0 || obj.BuildModeType > 0) && obj.Disabled == 0 &&
                    (obj.IsMultiTile || obj.NumGraphics > 0) && (obj.MasterID == 0 || obj.SubIndex == -1);

                if (passesCatalogCheck)
                {
                    try
                    {
                        //todo: more than one of these set? no normal game objects do this
                        //todo: room sort
                        var cat = (sbyte)Math.Log(obj.FunctionFlags, 2);
                        if (obj.FunctionFlags == 0) cat = (sbyte)(obj.BuildModeType + 7);
                        var item = new ObjectCatalogItem()
                        {
                            Category = (sbyte)(cat), //0-7 buy categories. 8-15 build mode categories
                            RoomSort = (byte)obj.RoomFlags,
                            GUID = obj.GUID,
                            DisableLevel = 0,
                            Price = obj.Price,
                            Name = obj.ChunkLabel,
                            //the name the catalog shows ("" if the object has none), read here so opening a category
                            //doesn't have to load every object to get it
                            CatalogName = CatalogNameOf(file, obj),

                            Subsort = (byte)obj.FunctionSubsort,
                            CommunitySort = (byte)obj.CommunitySubsort,
                            DowntownSort = (byte)obj.DTSubsort,
                            MagictownSort = (byte)obj.MTSubsort,
                            StudiotownSort = (byte)obj.STSubsort,
                            VacationSort = (byte)obj.VacationSubsort
                        };
                        if (item.Category < 0 || item.Category >= ItemsByCategory.Length) throw new IndexOutOfRangeException("category " + item.Category);
                        entry.Item = item;
                        entry.InCatalog = true;
                    }
                    catch (Exception ex)
                    {
                        // Log catalog item creation failures
                        complete = false;
                        Content.FailedContentFiles.Add(new TS1BCFProvider.FailedFileInfo
                        {
                            Filename = filename,
                            ErrorMessage = $"Failed to add object '{obj.ChunkLabel}' to catalog: {ex.Message}",
                            ErrorType = "CatalogError"
                        });
                    }
                }
            }
            return record;
        }

        private static string CatalogNameOf(IffFile file, OBJD obj)
        {
            try
            {
                return file.Get<CTSS>(obj.CatalogStringsID)?.GetString(0) ?? "";
            }
            catch
            {
                return "";
            }
        }

        /// <summary>A short fingerprint of everything the scan produced, so tests can check two scans agree.</summary>
        public string ScanFingerprint()
        {
            var text = new System.Text.StringBuilder();
            foreach (var kv in Entries.OrderBy(x => x.Key))
            {
                var e = kv.Value;
                text.Append(kv.Key).Append('|').Append(e.FileName).Append('|').Append(e.Name).Append('|').Append((int)e.Source).Append('|')
                    .Append(e.Group).Append('|').Append(e.SubIndex).Append('|').Append(e.GlobalSimObject).Append('\n');
            }
            foreach (var guid in PersonGUIDs.OrderBy(x => x)) text.Append("person ").Append(guid).Append('\n');
            for (int c = 0; c < ItemsByCategory.Length; c++)
            {
                foreach (var i in ItemsByCategory[c])
                {
                    text.Append("item ").Append(c).Append('|').Append(i.GUID).Append('|').Append(i.Price).Append('|').Append(i.Name).Append('|').Append(i.CatalogName)
                        .Append('|').Append(i.RoomSort).Append(i.Subsort).Append(i.DowntownSort).Append(i.VacationSort).Append(i.CommunitySort)
                        .Append(i.StudiotownSort).Append(i.MagictownSort).Append('\n');
                }
            }
            using (var sha = System.Security.Cryptography.SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(text.ToString()))).Replace("-", "").Substring(0, 16)
                    + " (" + Entries.Count + " objects, " + ItemsByGUID.Count + " in catalog)";
        }

        protected override Func<string, GameObjectResource> GenerateResource(GameObjectReference reference)
        {
            return (fname) =>
            {
                /** Better set this up! **/
                IffFile iff = null;

                if (reference.Source == GameObjectSource.Far)
                {
                    iff = GameObjects.Get(reference.FileName.ToLower());
                    iff.InitHash();
                    if (iff != null) iff.RuntimeInfo.Path = reference.FileName;
                }
                else
                {
                    //unused
                    iff = new IffFile(reference.FileName, reference.Source == GameObjectSource.User);
                    iff.InitHash();
                    iff.RuntimeInfo.Path = reference.FileName;
                    iff.RuntimeInfo.State = IffRuntimeState.Standalone;
                }

                if (iff != null)
                {
                    if (iff != null && iff.RuntimeInfo.State == IffRuntimeState.PIFFPatch)
                    {
                        //OBJDs may have changed due to patch. Remove all file references
                        ResetFile(iff);
                    }
                    iff.RuntimeInfo.UseCase = IffUseCase.Object;
                }

                return new GameObjectResource(iff, null, null, reference.FileName, ContentManager);
            };
        }

        public List<ObjectCatalogItem> All()
        {
            var result = new List<ObjectCatalogItem>();
            foreach (var cat in ItemsByCategory)
            {
                result.AddRange(cat);
            }
            return result;
        }

        public List<ObjectCatalogItem> GetItemsByCategory(sbyte category)
        {
            return ItemsByCategory[category];
        }

        public ObjectCatalogItem? GetItemByGUID(uint guid)
        {
            ObjectCatalogItem item;
            if (ItemsByGUID.TryGetValue(guid, out item))
                return item;
            else return null;
        }

        public List<uint> GetUntradableGUIDs()
        {
            return new List<uint>();
        }
    }
}
