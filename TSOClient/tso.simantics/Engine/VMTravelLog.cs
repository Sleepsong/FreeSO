using FSO.Files.Formats.IFF.Chunks;
using FSO.SimAntics.Model;
using FSO.SimAntics.Primitives;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace FSO.SimAntics.Engine
{
    /// <summary>
    /// A trace of TS1 travel between lots, for finding why an expansion pack's way home (Magic Town, Studio Town...) does
    /// nothing: the TS1 calls that FreeSO doesn't implement (with the routine that made them), every call on the travel
    /// path with its temps, town map dialogs and their answers, lot switches, and, for a pie menu the player opens,
    /// each interaction that was left out and why. Also dumps every Sim's script state on request, for a Sim stuck in
    /// an interaction. Does nothing until a frontend sets <see cref="Path"/>.
    /// </summary>
    public static class VMTravelLog
    {
        /// <summary>Log file to append to, or null to disable the trace.</summary>
        public static string Path;
        public static bool Enabled => Path != null;

        private const long RotateBytes = 4 * 1024 * 1024; //checked once per session; the old log is kept as *.old
        private const int MaxLinesPerSession = 20000;
        private const int ReportsPerUnimplementedCall = 3; //per call and routine; after that, repeats are only counted

        private static readonly object Lock = new object();
        private static readonly Dictionary<string, int> Seen = new Dictionary<string, int>();
        private static bool SessionStarted;
        private static int LinesWritten;

        /// <summary>The TS1 calls FreeSO leaves out, or only stubs (they return true and do nothing else).</summary>
        public static readonly HashSet<VMGenericTS1CallMode> NotImplemented = new HashSet<VMGenericTS1CallMode>
        {
            VMGenericTS1CallMode.HouseTutorialComplete,
            VMGenericTS1CallMode.PullDownTaxiDialog,
            VMGenericTS1CallMode.MakeNewNeighbor,
            VMGenericTS1CallMode.FamilyTutorialComplete,
            VMGenericTS1CallMode.ArchitectureTutorialComplete,
            VMGenericTS1CallMode.GetDistanceToCameraInTemp0,
            VMGenericTS1CallMode.MyRoutingFootprintEqualsTemp0,
            VMGenericTS1CallMode.ChangeNormalOutfit,
            VMGenericTS1CallMode.SelectDowntownLot,
            VMGenericTS1CallMode.GetDowntownTimeFromSOInventory,
            VMGenericTS1CallMode.SaveSimPersistentData,
            VMGenericTS1CallMode.PromoteFameIfNeeded,
            VMGenericTS1CallMode.TakeTaxiHook,
            VMGenericTS1CallMode.DemoteFameIfNeeded,
            VMGenericTS1CallMode.CancelPieMenu,
            VMGenericTS1CallMode.GetTokensFromString,
            VMGenericTS1CallMode.ChildToAdult,
            VMGenericTS1CallMode.PetToAdult,
            VMGenericTS1CallMode.HeadFlush,
            VMGenericTS1CallMode.FamilySpellsIntoController,
        };

        /// <summary>The TS1 calls on the way between lots: each one is written, with its temps before and after.</summary>
        public static readonly HashSet<VMGenericTS1CallMode> TravelCalls = new HashSet<VMGenericTS1CallMode>
        {
            VMGenericTS1CallMode.ChangeToLotInTemp0,
            VMGenericTS1CallMode.BuildTheDowntownSimAndPlaceObjIDInTemp0,
            VMGenericTS1CallMode.SpawnDowntownDateOfPersonInTemp0,
            VMGenericTS1CallMode.SpawnTakeBackHomeDataOfPersonInTemp0,
            VMGenericTS1CallMode.SpawnInventorySimDataEffects,
            VMGenericTS1CallMode.BuildVacationFamilyPutFamilyNumInTemp0,
            VMGenericTS1CallMode.ReturnNumberOfAvaiableVacationLotsInTemp0,
            VMGenericTS1CallMode.ReturnZoningTypeOfLotInTemp0,
            VMGenericTS1CallMode.MakeTemp0SelectedSim,
        };

        public static void Write(string line)
        {
            var path = Path;
            if (path == null) return;
            try
            {
                lock (Lock)
                {
                    if (LinesWritten >= MaxLinesPerSession) return;
                    var output = new StringBuilder();
                    StartSession(output, path);
                    var parts = line.Split('\n');
                    for (int i = 0; i < parts.Length; i++)
                    {
                        output.AppendLine((i == 0) ? $"[{DateTime.Now:HH:mm:ss}] {parts[i]}" : parts[i]);
                        LinesWritten++;
                    }
                    File.AppendAllText(path, output.ToString());
                }
            }
            catch
            {
                //logging must never take the game down.
            }
        }

        /// <summary>After a TS1 generic call (primitive 0x2A) has run.</summary>
        public static void TS1Call(VMStackFrame frame, VMGenericTS1CallMode call, short temp0, short temp1, VMPrimitiveExitCode result)
        {
            if (!Enabled) return;
            try
            {
                WriteCall(frame, call, temp0, temp1, result);
            }
            catch (Exception)
            {
                //logging must never take the game down.
            }
        }

        private static void WriteCall(VMStackFrame frame, VMGenericTS1CallMode call, short temp0, short temp1, VMPrimitiveExitCode result)
        {
            var missing = NotImplemented.Contains(call);
            if (!missing && !TravelCalls.Contains(call)) return;

            var where = Where(frame);
            if (missing)
            {
                var key = "call:" + (int)call + "|" + where;
                int count;
                lock (Lock)
                {
                    Seen.TryGetValue(key, out count);
                    Seen[key] = ++count;
                }
                if (count > ReportsPerUnimplementedCall)
                {
                    if (count % 50 == 0) Write($"TS1 call {(int)call} {call}: {count} times from {where}");
                    return;
                }
            }

            var temps = frame.Thread?.TempRegisters;
            var after = (temps == null) ? $"temp0 {temp0}, temp1 {temp1}" : $"temp0 {temp0} -> {temps[0]}, temp1 {temp1} -> {temps[1]}";
            Write($"TS1 call {(int)call} {call}{(missing ? " (NOT IMPLEMENTED in FreeSO: does nothing)" : "")} -> {result}\n" +
                $"  from {where}\n" +
                $"  caller {Name(frame.Caller)}, stack object {Name(frame.StackObject)}; {after}" +
                ((frame.Thread == null) ? "" : Globals(frame.VM)));
        }

        /// <summary>A town map dialog the scripts asked for, or the answer it got.</summary>
        public static void Dialog(VMStackFrame frame, VMDialogType type, string what)
        {
            if (!Enabled) return;
            try
            {
                Write($"dialog {type}: {what}\n  from {Where(frame)}, caller {Name(frame.Caller)}");
            }
            catch (Exception)
            {
                //logging must never take the game down.
            }
        }

        /// <summary>
        /// Explains a pie menu the player opened: every interaction the object (and the global table) has, and whether
        /// it was shown, or why not. Runs the check trees a second time, as opening the menu again would.
        /// </summary>
        public static void PieMenu(VM vm, VMEntity obj, VMEntity caller, int shown)
        {
            if (!Enabled || vm == null || obj == null || caller?.Thread == null) return;
            try
            {
                var output = new StringBuilder();
                output.Append($"pie menu on {Name(obj)} (GUID 0x{obj.Object?.OBJ?.GUID:X8}) for {Name(caller)}: {shown} shown" +
                    $" | house {vm.GetGlobalValue(10)}" + Globals(vm));
                var lines = new List<string>();
                Explain(vm, obj, caller, false, lines);
                Explain(vm, obj, caller, true, lines);
                foreach (var line in lines) output.Append("\n  " + line);

                //the same menu on the same object, unchanged, is written once
                var key = "pie:" + output.ToString().Substring(output.ToString().IndexOf(':'));
                lock (Lock)
                {
                    if (Seen.ContainsKey(key)) return;
                    Seen[key] = 1;
                }
                Write(output.ToString());
            }
            catch (Exception e)
            {
                Write($"pie menu on {Name(obj)}: couldn't explain it ({e.GetType().Name}: {e.Message})");
            }
        }

        private static void Explain(VM vm, VMEntity obj, VMEntity caller, bool global, List<string> lines)
        {
            var table = global ? vm.Context.GlobalTreeTable : obj.TreeTable;
            var strings = global ? vm.Context.GlobalTTAs : obj.TreeTableStrings;
            if (table?.Interactions == null) return;
            foreach (var ia in table.Interactions)
            {
                var name = strings?.GetString((int)ia.TTAIndex) ?? "(no name)";
                var label = $"{(global ? "global " : "")}#{ia.TTAIndex} '{name}'";
                string verdict;

                var action = obj.GetAction((int)ia.TTAIndex, caller, vm.Context, global);
                if (action == null)
                {
                    verdict = (obj.GetRoutineWithOwner(ia.ActionFunction, vm.Context) == null)
                        ? $"left out: its action routine 0x{ia.ActionFunction:X4} isn't in the installed content"
                        : $"left out: its check routine 0x{ia.TestFunction:X4} isn't in the installed content";
                }
                else
                {
                    var check = action.CheckRoutine;
                    action.CheckRoutine = null;
                    var flagsOnly = caller.Thread.CheckAction(action);
                    action.CheckRoutine = check;

                    caller.ObjectData[(int)VMStackObjectVariable.HideInteraction] = 0;
                    var full = (flagsOnly == null) ? null : caller.Thread.CheckAction(action);
                    var hidden = caller.ObjectData[(int)VMStackObjectVariable.Hidden] == 1
                        || caller.ObjectData[(int)VMStackObjectVariable.HideInteraction] == 1;

                    if (flagsOnly == null) verdict = $"left out by its flags ({action.Flags}): visitor, age, pet or debug";
                    else if (full == null) verdict = $"left out: check routine '{RoutineName(check)}' 0x{check?.ID:X4} said no";
                    else if (hidden) verdict = $"hidden: check routine '{RoutineName(check)}' 0x{check?.ID:X4} hid it";
                    else if (caller.Position == FSO.LotView.Model.LotTilePos.OUT_OF_WORLD) verdict = "left out: the Sim is out of the world";
                    else verdict = "shown" + ((full.Count > 0) ? $" ({string.Join(", ", full.Select(x => x.Name))})" : "");
                    caller.ObjectData[(int)VMStackObjectVariable.HideInteraction] = 0;
                }
                lines.Add($"{label}: {verdict}");
            }
        }

        /// <summary>Every Sim's script stack, interaction queue and animation, for a Sim stuck in an interaction.</summary>
        public static string DumpSims(VM vm, string reason)
        {
            if (vm == null) return "no lot";
            try
            {
                return WriteDump(vm, reason);
            }
            catch (Exception e)
            {
                var text = $"Sims on the lot ({reason}): couldn't dump them ({e.GetType().Name}: {e.Message})";
                Write(text);
                return text;
            }
        }

        private static string WriteDump(VM vm, string reason)
        {
            var output = new StringBuilder();
            output.Append($"Sims on the lot ({reason}) | house {vm.GetGlobalValue(10)}, tick {vm.Scheduler?.CurrentTickID}" + Globals(vm));
            var related = new HashSet<VMEntity>();
            foreach (var avatar in vm.Context.ObjectQueries.Avatars.ToList())
            {
                DumpEntity(output, avatar, related);
            }
            //and the objects they're using, whose own scripts may be what's stuck
            foreach (var obj in related.Where(x => !(x is VMAvatar)).Distinct().ToList())
            {
                DumpEntity(output, obj, null);
            }
            var text = output.ToString();
            Write(text);
            return text;
        }

        private static void DumpEntity(StringBuilder output, VMEntity entity, HashSet<VMEntity> related)
        {
            var thread = entity.Thread;
            output.Append($"\n  {Name(entity)} (object {entity.ObjectID}, GUID 0x{entity.Object?.OBJ?.GUID:X8}) at {entity.Position}");
            if (entity is VMAvatar avatar)
            {
                var anims = avatar.Animations?.Select(a => $"{a.Anim?.Name} frame {a.CurrentFrame:0}{(a.EndReached ? " (ended)" : "")}{(a.Loop ? " loop" : "")}");
                output.Append($"\n    animations: {((anims == null || !anims.Any()) ? "none" : string.Join(", ", anims))}");
                var carry = avatar.CarryAnimationState?.Anim?.Name;
                if (carry != null) output.Append($", carrying {carry}");
            }
            if (thread == null)
            {
                output.Append("\n    no thread");
                return;
            }
            output.Append($"\n    blocked on: {thread.BlockingState?.GetType().Name ?? "nothing"}{(thread.Interrupt ? ", interrupt pending" : "")}");
            output.Append($"\n    stack, innermost first ({thread.Stack.Count} frames):");
            for (int i = thread.Stack.Count - 1; i >= 0; i--)
            {
                var frame = thread.Stack[i];
                output.Append($"\n      [{i}] {Routine(frame)} callee {Name(frame.Callee)}, stack object {Name(frame.StackObject)}");
                if (related != null && frame.StackObject != null && frame.StackObject != entity) related.Add(frame.StackObject);
                if (related != null && frame.Callee != null && frame.Callee != entity) related.Add(frame.Callee);
            }
            output.Append($"\n    queue ({thread.Queue.Count}):");
            for (int i = 0; i < thread.Queue.Count; i++)
            {
                var action = thread.Queue[i];
                output.Append($"\n      {(i == 0 ? "> " : "  ")}'{action.Name}' on {Name(action.Callee)}, priority {action.Priority}, mode {action.Mode}, flags {action.Flags}");
                if (related != null && action.Callee != null && action.Callee != entity) related.Add(action.Callee);
            }
        }

        private static string Globals(VM vm)
        {
            if (vm == null) return "";
            try
            {
                var cross = Content.Content.Get()?.Neighborhood?.GameState;
                return $" | globals: selected sim {vm.GetGlobalValue(3)}, family {vm.GetGlobalValue(9)}, house {vm.GetGlobalValue(10)}, transit {vm.GetGlobalValue(34)}" +
                    (cross == null ? "" : $" | trip: transit info {cross.LotTransitInfo}, sim GUID 0x{cross.DowntownSimGUID:X8}, family {cross.ActiveFamily?.ChunkID}");
            }
            catch (Exception)
            {
                return "";
            }
        }

        private static string Where(VMStackFrame frame)
        {
            if (frame == null) return "?";
            var owner = frame.CodeOwner?.OBJ;
            return $"{Routine(frame)} of {owner?.ChunkLabel ?? "?"} (0x{owner?.GUID:X8})";
        }

        private static string Routine(VMStackFrame frame)
        {
            var routine = frame?.Routine;
            if (routine == null) return "<no routine>";
            var opcode = (frame.InstructionPointer < (routine.Instructions?.Length ?? 0))
                ? $" opcode {routine.Instructions[frame.InstructionPointer].Opcode}"
                : " (past end)";
            return $"'{RoutineName(routine)}' 0x{routine.ID:X4} at {frame.InstructionPointer}{opcode}";
        }

        private static string RoutineName(VMRoutine routine)
        {
            try
            {
                return routine?.Rti?.Name?.TrimEnd('\0') ?? "?";
            }
            catch (Exception)
            {
                return "?";
            }
        }

        private static string Name(VMEntity entity)
        {
            if (entity == null) return "none";
            try
            {
                return entity.ToString();
            }
            catch (Exception)
            {
                return "object " + entity.ObjectID;
            }
        }

        private static void StartSession(StringBuilder output, string path)
        {
            if (SessionStarted) return;
            SessionStarted = true;
            try
            {
                var file = new FileInfo(path);
                if (file.Exists && file.Length >= RotateBytes)
                {
                    var old = path + ".old";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(path, old);
                }
            }
            catch (Exception) { }
            output.AppendLine();
            output.AppendLine($"===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} {VMExceptionLog.SessionHeader} =====");
        }
    }
}
