using FSO.SimAntics.NetPlay.Drivers;
using System.IO;
using System.Linq;

namespace FSO.SimAntics.NetPlay.Model.Commands
{
    public class VMNetDirectControlToggleCommand : VMNetCommandBodyAbstract
    {
        public bool Enable;

        public override bool Execute(VM vm, VMAvatar avatar)
        {
            if (avatar == null) return false;
            SetEnabled(vm, avatar, Enable);
            return true;
        }

        /// <summary>
        /// Turns direct control on or off for an avatar, queueing or cancelling its direct control action.
        /// </summary>
        public static void SetEnabled(VM vm, VMAvatar avatar, bool enable)
        {
            //TS1 keeps the flag at runtime only, so it can't leak into TS1 save data.
            if (vm.TS1) avatar.DirectControlEnabled = enable;
            else avatar.SetPersonData(SimAntics.Model.VMPersonDataVariable.UnusedAndDoNotUse2, (short)(enable ? 32767 : 0));
            // Set avatar variable that controls whether direct control should be enabled.

            if (!enable)
            {
                var dcInteraction = avatar.Thread.Queue.FirstOrDefault(action => action.Flags.HasFlag(Files.Formats.IFF.Chunks.TTABFlags.FSODirectControl));

                if (dcInteraction != null)
                {
                    avatar.Thread.CancelAction(dcInteraction.UID);
                }

                avatar.Avatar.HeadSeekWeight = 0f;
            }
            else
            {
                avatar.Thread.EnsureDirectControlAction();
            }
        }

        public override bool Verify(VM vm, VMAvatar caller)
        {
            if (!base.Verify(vm, caller))
            {
                return false;
            }
            if (vm.AllowDirectControl) return true;

            var enable = vm.Tuning.GetTuning("aprilfools", 0, 2023) ?? 0;

            if (enable == 0 && Enable) return false;

            if (enable == 2 && !Enable)
            {
                var server = (vm.Driver as VMServerDriver);
                server.SendGenericMessage(caller.PersistID, "3D Only", "This property can only be joined in 3D mode. Launch your game with the -3d option.");
                server.DisconnectClient(caller.PersistID);
                return false;
            }

            return true;
        }

        #region VMSerializable Members

        public override void SerializeInto(BinaryWriter writer)
        {
            base.SerializeInto(writer);

            writer.Write(Enable);
        }

        public override void Deserialize(BinaryReader reader)
        {
            base.Deserialize(reader);

            Enable = reader.ReadBoolean();
        }

        #endregion
    }
}
