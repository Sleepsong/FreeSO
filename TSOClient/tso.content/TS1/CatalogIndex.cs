using FSO.Common;
using FSO.Content.Interfaces;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace FSO.Content.TS1
{
    /// <summary>
    /// A saved copy of what the object scan (TS1ObjectProvider.Init) finds in each object file, so a launch only opens the
    /// files that are new or have changed. Opening and parsing every object file is most of the scan, and grows with
    /// custom content.
    ///
    /// A record is what one file gave: its objects, their catalog entries and catalog names. It's reused while the file
    /// it came from (the loose .iff, or the .far holding it) has the same size and modified time. The index is dropped
    /// and rebuilt if it is damaged, from another version, for another install, or built with another language (the
    /// catalog names depend on it). Delete the file, or set SIMITONE_NO_CATALOG_INDEX, to go back to a full scan.
    /// </summary>
    public class CatalogIndex
    {
        public const int FormatVersion = 1;
        private const string Magic = "SCIX";

        public class Object
        {
            public uint GUID;
            public string Name; //the chunk label
            public short Group, SubIndex;
            public bool Global, Person;
            public bool InCatalog;
            public ObjectCatalogItem Item; //when InCatalog
        }

        public class Record
        {
            public long Size, Ticks; //of the file this came from, when it was scanned
            public List<Object> Objects = new List<Object>();
        }

        public readonly Dictionary<string, Record> Records = new Dictionary<string, Record>();
        private readonly string Install;
        private readonly int Language;

        public CatalogIndex(string install, int language)
        {
            Install = install ?? "";
            Language = language;
        }

        public static bool Disabled => Environment.GetEnvironmentVariable("SIMITONE_NO_CATALOG_INDEX") != null;

        /// <summary>One index per install, so switching installs doesn't throw the other's away.</summary>
        public static string PathFor(string install)
        {
            uint hash = 2166136261;
            foreach (var c in (install ?? "").Replace('\\', '/').TrimEnd('/').ToLowerInvariant()) hash = (hash ^ c) * 16777619;
            return System.IO.Path.Combine(FSOEnvironment.UserDir, "CatalogIndex-" + hash.ToString("x8") + ".bin");
        }

        public static string Key(string container, string member) => container + "\n" + member;

        /// <summary>The size and modified time that say whether a file has changed, or null if it can't be read.</summary>
        public static (long, long)? Stamp(string path)
        {
            try
            {
                var info = new FileInfo(path);
                return info.Exists ? ((long, long)?)(info.Length, info.LastWriteTimeUtc.Ticks) : null;
            }
            catch { return null; }
        }

        /// <summary>The saved index, or an empty one if there's none or it can't be trusted.</summary>
        public static CatalogIndex Load(string install, int language)
        {
            var index = new CatalogIndex(install, language);
            try
            {
                var path = PathFor(install);
                if (!File.Exists(path)) return index;
                using (var reader = new BinaryReader(File.OpenRead(path), Encoding.UTF8))
                {
                    if (reader.ReadString() != Magic || reader.ReadInt32() != FormatVersion) return index;
                    if (reader.ReadString() != index.Install || reader.ReadInt32() != language) return index;
                    var count = reader.ReadInt32();
                    var loaded = new Dictionary<string, Record>(count);
                    for (int i = 0; i < count; i++)
                    {
                        var key = reader.ReadString();
                        var record = new Record { Size = reader.ReadInt64(), Ticks = reader.ReadInt64() };
                        var objects = reader.ReadInt32();
                        for (int j = 0; j < objects; j++) record.Objects.Add(ReadObject(reader));
                        loaded[key] = record;
                    }
                    if (reader.ReadString() != Magic) return index; //cut short
                    foreach (var kv in loaded) index.Records[kv.Key] = kv.Value;
                }
            }
            catch
            {
                index.Records.Clear(); //damaged: start again
            }
            return index;
        }

        /// <summary>Saves the index. A failure is not an error: the next launch just scans again.</summary>
        public void Save()
        {
            try
            {
                var path = PathFor(Install);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                var temp = path + ".tmp";
                using (var writer = new BinaryWriter(File.Create(temp), Encoding.UTF8))
                {
                    writer.Write(Magic);
                    writer.Write(FormatVersion);
                    writer.Write(Install);
                    writer.Write(Language);
                    writer.Write(Records.Count);
                    foreach (var kv in Records)
                    {
                        writer.Write(kv.Key);
                        writer.Write(kv.Value.Size);
                        writer.Write(kv.Value.Ticks);
                        writer.Write(kv.Value.Objects.Count);
                        foreach (var obj in kv.Value.Objects) WriteObject(writer, obj);
                    }
                    writer.Write(Magic);
                }
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
            }
            catch (Exception e)
            {
                Console.WriteLine("Couldn't save the catalog index: " + e.Message);
            }
        }

        private static void WriteString(BinaryWriter w, string s)
        {
            w.Write(s != null);
            if (s != null) w.Write(s);
        }

        private static string ReadString(BinaryReader r) => r.ReadBoolean() ? r.ReadString() : null;

        private static void WriteObject(BinaryWriter w, Object o)
        {
            w.Write(o.GUID);
            WriteString(w, o.Name);
            w.Write(o.Group);
            w.Write(o.SubIndex);
            w.Write(o.Global);
            w.Write(o.Person);
            w.Write(o.InCatalog);
            if (!o.InCatalog) return;
            var item = o.Item;
            w.Write(item.Category);
            w.Write(item.Price);
            WriteString(w, item.Name);
            WriteString(w, item.CatalogName);
            w.Write(item.RoomSort);
            w.Write(item.Subsort);
            w.Write(item.DowntownSort);
            w.Write(item.VacationSort);
            w.Write(item.CommunitySort);
            w.Write(item.StudiotownSort);
            w.Write(item.MagictownSort);
        }

        private static Object ReadObject(BinaryReader r)
        {
            var o = new Object
            {
                GUID = r.ReadUInt32(),
                Name = ReadString(r),
                Group = r.ReadInt16(),
                SubIndex = r.ReadInt16(),
                Global = r.ReadBoolean(),
                Person = r.ReadBoolean(),
                InCatalog = r.ReadBoolean()
            };
            if (!o.InCatalog) return o;
            o.Item = new ObjectCatalogItem
            {
                GUID = o.GUID,
                Category = r.ReadSByte(),
                Price = r.ReadUInt32(),
                Name = ReadString(r),
                CatalogName = ReadString(r),
                RoomSort = r.ReadByte(),
                Subsort = r.ReadByte(),
                DowntownSort = r.ReadByte(),
                VacationSort = r.ReadByte(),
                CommunitySort = r.ReadByte(),
                StudiotownSort = r.ReadByte(),
                MagictownSort = r.ReadByte()
            };
            return o;
        }
    }
}
