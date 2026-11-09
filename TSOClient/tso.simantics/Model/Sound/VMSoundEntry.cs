using FSO.HIT;

namespace FSO.SimAntics.Model.Sound
{
    public class VMSoundEntry
    {
        public HITSound Sound;
        public bool Pan;
        public bool Zoom;
        public bool Loop;
        public string Name;
        public bool VolumeChecked; //whether its first volume has been checked, to note one that starts silent
    }
}
