using System.IO;

namespace FSO.SimAntics.NetPlay.Model.Commands
{
    public class VMNetChangeControlCmd : VMNetCommandBodyAbstract
    {
        public short TargetID;

        public override bool Execute(VM vm, VMAvatar caller)
        {
            //switch this client's controlled sim to another
            //only used for TS1.

            //not suitable for networking right now. If you want to do anything weird with ts1 multiplayer, needs these additions:
            // - block stealing sims controlled by others
            // - block assuming control of npcs (this is enforced by simitone ui)
            // - multiplayer handling of "selected sim" global

            var target = vm.GetObjectById(TargetID);
            if (target == null || target is VMGameObject) return false;

            if (caller != null && caller.PersistID == ActorUID)
            {
                //direct control belongs to whoever controls the sim. Once this sim is let go it can't be addressed
                //(its PersistID becomes 0), so end it here rather than leave it walking on its own.
                if (caller.DirectControlEnabled && caller != target) VMNetDirectControlToggleCommand.SetEnabled(vm, caller, false);

                //relinquish previous control
                vm.Context.ObjectQueries.RemoveAvatarPersist(caller.PersistID);
                caller.PersistID = 0;
            }

            target.PersistID = ActorUID;
            vm.Context.ObjectQueries.RegisterAvatarPersist((VMAvatar)target, target.PersistID);
            vm.SetGlobalValue(3, target.ObjectID);

            if (VM.UseWorld)
            {
                vm.Context.World.CenterTo(target.WorldUI);
            }

            return true;
        }

        public override bool Verify(VM vm, VMAvatar caller)
        {
            return vm.TS1;
        }

        #region VMSerializable Members

        public override void SerializeInto(BinaryWriter writer)
        {
            base.SerializeInto(writer);
            writer.Write(TargetID);
        }

        public override void Deserialize(BinaryReader reader)
        {
            base.Deserialize(reader);
            TargetID = reader.ReadInt16();
        }

        #endregion
    }
}
