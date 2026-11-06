using FSO.Content.Interfaces;
using FSO.Files.HIT;
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework.Audio;
using FSO.Content.Framework;
using System.Text.RegularExpressions;
using System.IO;
using FSO.Content.Codecs;
using FSO.Content.Model;
using FSO.Common.Utils;

namespace FSO.Content.TS1
{
    public class TS1Audio : IAudioProvider
    {
        private Content ContentManager;

        private Dictionary<uint, Track> TracksById = new Dictionary<uint, Track>();
        private Dictionary<uint, Hitlist> HitlistsById = new Dictionary<uint, Hitlist>();
        private Dictionary<uint, Patch> PatchesById = new Dictionary<uint, Patch>();

        private TS1SubProvider<DecodedSFX> WAVSounds;
        private TS1SubProvider<DecodedSFX> MP3Sounds;
        private TS1SubProvider<DecodedSFX> XASounds;
        private TS1SubProvider<DecodedSFX> UTKSounds;

        /** Audio Cache **/
        public Dictionary<Patch, SoundEffect> SFXCache = new Dictionary<Patch, SoundEffect>();

        /// <summary>
        /// Notes about sounds that couldn't be played, for a log (the frontend sets it, e.g. with -logs). These failures
        /// are otherwise silent. Each message is noted once.
        /// </summary>
        public static Action<string> Note;

        /// <summary>
        /// A line for every sound the game asks for, and what came of it, for a sound log (the frontend sets it, e.g. with
        /// -soundlog). Off unless set: games ask for a lot of sounds.
        /// </summary>
        public static Action<string> Trace;
        private static HashSet<string> Noted = new HashSet<string>();

        public static void NoteOnce(string message)
        {
            if (Note == null) return;
            lock (Noted)
            {
                if (!Noted.Add(message)) return;
            }
            Note(message);
        }

        private Dictionary<string, HITEventRegistration> _Events = new Dictionary<string, HITEventRegistration>();
        public Dictionary<string, HITEventRegistration> Events
        {
            get
            {
                return _Events;
            }
        }

        private Dictionary<string, string> _StationPaths = new Dictionary<string, string>
        {
            {IIS(6685), "Music/Stations/Beach/"},
            {IIS(269), "Music/Stations/Classica/"},
            {IIS(260), "Music/Stations/Country/"},
            {IIS(6681), "Music/Stations/CountryD/"},
            {IIS(6684), "Music/Stations/Disco/"},
            {IIS(65536), "Music/Stations/EZ/"},
            {IIS(65537), "Music/Stations/EZExp/"},
            {IIS(270), "Music/Stations/Latin/"},
            {IIS(6682), "Music/Stations/Rap/"},
            {IIS(6683), "Music/Stations/Rave/"},
            {IIS(280), "Music/Stations/Rock/"},

            {IIS(22900), "Music/Stations/UnlCheap/"},
            {IIS(22901), "Music/Stations/UnlSpeak/"}, //unlesked expensive?
            {IIS(22902), "Music/Stations/UnlSpeak/"}, //unleashed ui
            {IIS(100100), "Music/Stations/Superstar/"}, //superstar speaker
// These ones aren't radio stations - they're UI music
            {"KBUI", "Music/Modes/Build/"},
            {"KBUY", "Music/Modes/Buy/"},
            {"KDTN", "Music/Modes/dt_nhood/"},
            {"KLOD", "Music/Modes/Load/"},
            {"K_MM", "Music/Modes/MakinMagic/"},
            {"KMMB", "Music/Modes/MM_Build/"},
            {"KMM2", "Music/Modes/MM_Buy/"},
            {"KMMC", "Music/Modes/MM_credits/"},
            {"KNHD", "Music/Modes/Nhood/"},
            {"KNUS", "Music/Modes/NhoodUS/"},
            {"KSST", "Music/Modes/Superstar/"},
            {"KVAC", "Music/Modes/Vacation/"},
//tv
            { IIS(28), "SoundData/TVStations/TV_Action/" },
            { IIS(29), "SoundData/TVStations/TV_Comedy_cartoon/" },
            { IIS(27), "SoundData/TVStations/TV_Mystery/" },
            { IIS(26), "SoundData/TVStations/TV_Romance/" },

            { IIS(103000), "ExpansionPack6/Sound/TVStations/Animal/" },
            { IIS(103001), "ExpansionPack6/Sound/TVStations/Entertainment/" },
            { IIS(103002), "ExpansionPack6/Sound/TVStations/News/" },
            { IIS(103003), "ExpansionPack6/Sound/TVStations/Sports/" },

        };

        private Dictionary<int, string> _MusicModes = new Dictionary<int, string>
        {
            { 0, "" }, //live
            { 1, "KBUY" }, //buy
            { 2, "KBUI" }, //build
            { 3, "KNUS" }, //nhood1
            { 5, "KLOD" }, //load
            { 6, "KNUS" }, //credits
            { 7, "" }, //options
            { 8, "KBUI" }, //family
            { 9, "" }, //fade
            { 10, "KDTN" }, //downtown 
            { 11, "KVAC" }, //vacation
            { 12, IIS(22902) }, //unleashed
            { 13, IIS(100100) }, //superstar
            { 14, "KSST" }, //superstar transition
            { 15, "K_MM" }, //magictown
            { 16, "KMMC" }, //magic credits
            { 17, "KMMB" }, //magic build
            { 18, "KMM2" } //magic buy
        };

        public Dictionary<string, string> StationPaths
        {
            get
            {
                return _StationPaths;
            }
        }

        public Dictionary<int, string> MusicModes
        {
            get
            {
                return _MusicModes;
            }
        }

        private static string IIS(uint id)
        {
            return new string(new char[] { (char)(id & 0xFF), (char)((id >> 8) & 0xFF), (char)((id >> 16) & 0xFF), (char)((id >> 24) & 0xFF) });
        }

        public TS1Audio(Content contentManager)
        {
            this.ContentManager = contentManager;
            WAVSounds = new TS1SubProvider<DecodedSFX>(ContentManager.TS1Global, ".wav");
            MP3Sounds = new TS1SubProvider<DecodedSFX>(ContentManager.TS1Global, ".mp3");
            XASounds = new TS1SubProvider<DecodedSFX>(ContentManager.TS1Global, ".xa");
            UTKSounds = new TS1SubProvider<DecodedSFX>(ContentManager.TS1Global, ".utk");
        }

        public void Init()
        {
            //somewhat different from TSO audio - need to scan content for .hot files, then load them all
            //hot contains EVT, tracks, hitlists and patches...
            //...which are added to our global database.

            WAVSounds.Init();
            MP3Sounds.Init();
            XASounds.Init();
            UTKSounds.Init();

            var FilePattern = new Regex(@".*\.hot");

            List<string> matchedFiles = new List<string>();
            foreach (var file in ContentManager.TS1AllFiles)
            {
                if (FilePattern.IsMatch(file.Replace('\\', '/')))
                {
                    matchedFiles.Add(file);
                }
            }
            //every .hsm first: a .hot can name labels that another file's .hsm defines
            var hsms = new Dictionary<string, HSM>();
            var allNames = new Dictionary<string, int>();
            foreach (var file in matchedFiles)
            {
                var cFile = Path.Combine(ContentManager.TS1BasePath, file);
                var bPath = cFile.Substring(0, cFile.Length - 4); //path without .hot extension
                var hsm = new HSM(PathCaseTools.Insensitive(bPath + ".hsm"));
                hsms[file] = hsm;
                foreach (var name in hsm.Constants)
                {
                    if (!allNames.ContainsKey(name.Key)) allNames.Add(name.Key, name.Value);
                }
            }

            foreach (var file in matchedFiles)
            {
                //load associated HIT, HSM
                var cFile = Path.Combine(ContentManager.TS1BasePath, file);
                var bPath = cFile.Substring(0, cFile.Length - 4); //path without .hot extension

                var hsm = hsms[file];
                var hit = new HITFile(PathCaseTools.Insensitive(bPath + ".hit"));
                var hot = new Hot(cFile, hsm, allNames);

                var group = new HITResourceGroup() { hsm = hsm, hit = hit, hot = hot };

                foreach (var trk in hot.Tracks)
                {
                    if (TracksById.ContainsKey(trk.Key) && TracksById[trk.Key].SubroutineID != trk.Value.SubroutineID) { }
                    TracksById[trk.Key] = trk.Value;
                }

                foreach (var patch in hot.Patches)
                {
                    if (PatchesById.ContainsKey(patch.Key) && patch.Value.Filename != PatchesById[patch.Key].Filename) { }
                    PatchesById[patch.Key] = patch.Value;
                }

                foreach (var hls in hot.Hitlists)
                {
                    if (HitlistsById.ContainsKey(hls.Key) && HitlistsById[hls.Key].IDs.Count != hls.Value.IDs.Count) { }
                    HitlistsById[hls.Key] = hls.Value;
                }

                foreach (var evt in hot.Events)
                {
                    _Events[evt.Key] = new HITEventRegistration()
                    {
                        Name = evt.Value.Name,
                        EventType = (FSO.Files.HIT.HITEvents)evt.Value.EventType,
                        TrackID = evt.Value.TrackID,
                        ResGroup = group
                    };
                }
            }
            var musics = Events.Where(x => x.Value.EventType == HITEvents.kSetMusicMode).Select(x => x.Key + ": " + x.Value.TrackID).ToList();
            var stations = Events.Where(x => x.Value.EventType == HITEvents.kTurnOnTV).Select(x => x.Key + ": " + x.Value.TrackID.ToString()).ToList();
                //.Select(x => x.Key + ": " + new string(new char[] { (char)(x.Value.TrackID & 0xFF), (char)((x.Value.TrackID >> 8) & 0xFF), (char)((x.Value.TrackID >> 16) & 0xFF), (char)((x.Value.TrackID >> 24) & 0xFF) })).ToList();
        }

        //A sound file's tracks can use hitlists, tracks and sounds that another sound file defines (a Sim's voice lines:
        //the track picks a line from a hitlist). Only the event's own file was looked in, so those played nothing. Its
        //own file still comes first; the tables of every file, loaded in Init, are the fallback.

        public Hitlist GetHitlist(uint InstanceID, HITResourceGroup group)
        {
            Hitlist result = null;
            if (!group.hot.Hitlists.TryGetValue(InstanceID, out result)) HitlistsById.TryGetValue(InstanceID, out result);
            return result;
        }

        public Track GetTrack(uint value, uint fallback, HITResourceGroup group)
        {
            Track result = null;
            if (!group.hot.Tracks.TryGetValue(value, out result)) TracksById.TryGetValue(value, out result);
            return result;
        }

        /// <summary>Where a hitlist is defined, for the sound log: the event's own sound file, another, or none.</summary>
        public string HitlistSource(uint InstanceID, HITResourceGroup group)
        {
            if (group?.hot?.Hitlists?.ContainsKey(InstanceID) == true) return "in its own sound file";
            if (HitlistsById.ContainsKey(InstanceID)) return "in another sound file";
            return "nowhere";
        }

        public SoundEffect GetSFX(Patch patch)
        {
            if (patch == null) return null;
            if (SFXCache.ContainsKey(patch)) return SFXCache[patch];

            DecodedSFX aud;
            SoundEffect sfx;
            try
            {
                aud = GetAudioFrom(patch.Filename);
                if (aud != null)
                {
                    using (var stream = new MemoryStream(aud.Data)) sfx = SoundEffect.FromStream(stream);
                }
                else sfx = null;
            }
            catch (Exception e)
            {
                NoteOnce(patch.Filename + " couldn't be decoded (" + e.GetType().Name + ": " + e.Message + ").");
                SFXCache.Add(patch, null);
                return null;
            }
            if (aud != null)
            {
                SFXCache.Add(patch, sfx);
                switch (aud.Filetype)
                {
                    case 2:
                        sfx.Name = "VOX";
                        break;
                    case 3:
                        sfx.Name = "MUSIC";
                        break;
                    default:
                        sfx.Name = "FX";
                        break;
                }
                return sfx; //remember to clear the sfx cache between lots!
            }
            else
            {
                Console.WriteLine(patch.Filename+" was null");
                NoteOnce(patch.Filename + " wasn't found in the game's sound files.");
                return null;// pinnedArray; //we couldn't find anything! can't return null so do this... not the best idea tbh
            }
        }

        private DecodedSFX GetAudioFrom(string fileName)
        {
            if (fileName == null) return null;
            fileName = fileName.ToLowerInvariant();
            var ext = Path.GetExtension(fileName);
            var fname = Path.GetFileName(fileName);
            switch (ext)
            {
                case ".wav":
                    return WAVSounds.Get(fname);
                case ".mp3":
                    return MP3Sounds.Get(fname);
                case ".xa":
                    return XASounds.Get(fname);
                case ".utk":
                    return UTKSounds.Get(fname);
            }
            Console.WriteLine("what... "+ext);
            NoteOnce(fileName + " has a sound type the game doesn't know (" + ext + ").");
            return null;
        }

        public Patch GetPatch(uint id, HITResourceGroup group)
        {
            Patch result = null;
            if (!group.hot.Patches.TryGetValue(id, out result)) PatchesById.TryGetValue(id, out result);
            return result;
        }
    }
}
