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
        private const int MaxLinesPerSession = 80000;
        private const int ReportsPerUnimplementedCall = 3; //per call and routine; after that, repeats are only counted
        private const int ActionTraceLines = 5000; //an interaction the player picked away from home
        private const int ObjectTraceLines = 1500; //an object one of those created (a cab, a car...)
        private const int CheckTraceLines = 400; //a check routine that left an interaction out of a pie menu
        private const int StepsPerInstruction = 2; //a loop is written twice per instruction and outcome, then skipped

        /// <summary>The thread whose script step is running while the trace follows it (objects it creates are followed too).</summary>
        public static VMThread Following;
        /// <summary>Set while a pie menu's check routines are re-run with the trace on.</summary>
        public static bool TracingChecks;
        private static int CheckBudget;
        private static int CheckRun;
        private static StringBuilder Capture; //check traces go under their interaction in the pie menu entry

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

        private static readonly StringBuilder Pending = new StringBuilder();
        private static int PendingLines;
        private static DateTime LastFlush;

        public static void Write(string line)
        {
            var path = Path;
            if (path == null) return;
            try
            {
                lock (Lock)
                {
                    if (Capture != null)
                    {
                        Capture.Append('\n').Append(line);
                        return;
                    }
                    if (LinesWritten >= MaxLinesPerSession) return;
                    StartSession(Pending, path);
                    var parts = line.Split('\n');
                    for (int i = 0; i < parts.Length; i++)
                    {
                        Pending.AppendLine((i == 0) ? $"[{DateTime.Now:HH:mm:ss}] {parts[i]}" : parts[i]);
                        LinesWritten++;
                    }
                    if (LinesWritten >= MaxLinesPerSession) Pending.AppendLine($"[{DateTime.Now:HH:mm:ss}] (the log is full for this session: {MaxLinesPerSession} lines)");
                    //a followed script writes many lines in one tick: they go to the file together
                    PendingLines += parts.Length;
                    if (PendingLines >= 500 || (DateTime.UtcNow - LastFlush).TotalMilliseconds > 500) FlushLocked(path);
                }
            }
            catch
            {
                //logging must never take the game down.
            }
        }

        /// <summary>Writes out anything still buffered. The frontend calls this every frame.</summary>
        public static void Flush()
        {
            var path = Path;
            if (path == null || PendingLines == 0) return;
            try
            {
                lock (Lock) FlushLocked(path);
            }
            catch
            {
                //logging must never take the game down.
            }
        }

        private static void FlushLocked(string path)
        {
            LastFlush = DateTime.UtcNow;
            if (Pending.Length == 0) return;
            File.AppendAllText(path, Pending.ToString());
            Pending.Clear();
            PendingLines = 0;
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
            var temps = frame.Thread?.TempRegisters;
            var after = (temps == null) ? $"temp0 {temp0}, temp1 {temp1}" : $"temp0 {temp0} -> {temps[0]}, temp1 {temp1} -> {temps[1]}";
            if (!missing)
            {
                //the same call from the same place with the same values is written once (objects check the zoning constantly)
                lock (Lock)
                {
                    var once = "travel:" + (int)call + "|" + where + "|" + after + "|" + result;
                    if (Seen.ContainsKey(once)) return;
                    Seen[once] = 1;
                }
            }
            else
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

            Write($"TS1 call {(int)call} {call}{(missing ? " (NOT IMPLEMENTED in FreeSO: does nothing)" : "")} -> {result}\n" +
                $"  from {where}\n" +
                $"  caller {Name(frame.Caller)}, stack object {Name(frame.StackObject)}; {after}" +
                ((frame.Thread == null) ? "" : Globals(frame.VM)) +
                DisassembleOnce(frame));
        }

        /// <summary>A routine's code, the first time it's mentioned in a session (there's no TS1 content to read it from otherwise).</summary>
        private static string DisassembleOnce(VMStackFrame frame)
        {
            var routine = frame?.Routine;
            if (routine?.Instructions == null) return "";
            lock (Lock)
            {
                var key = $"code:{routine.ID}|{frame.CodeOwner?.OBJ?.GUID}";
                if (Seen.ContainsKey(key)) return "";
                Seen[key] = 1;
            }
            var output = new StringBuilder($"\n  code of '{RoutineName(routine)}' 0x{routine.ID:X4} ({routine.Arguments} args, {routine.Locals} locals):");
            for (int i = 0; i < routine.Instructions.Length; i++)
            {
                var instruction = routine.Instructions[i];
                output.Append($"\n    [{i}] {Describe(frame, instruction)}  T->{Pointer(instruction.TruePointer)} F->{Pointer(instruction.FalsePointer)}");
            }
            return output.ToString();
        }

        private static string Pointer(byte target)
        {
            switch (target)
            {
                case 253: return "error";
                case 254: return "return true";
                case 255: return "return false";
                default: return target.ToString();
            }
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
            var traceChecks = AwayFromHome(vm);
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

                    if (flagsOnly != null && (full == null || hidden) && traceChecks)
                        verdict += TraceCheck(caller, action);
                }
                lines.Add($"{label}: {verdict}");
            }
        }

        /// <summary>Runs an interaction's check routine again with the trace on, and returns its steps.</summary>
        private static string TraceCheck(VMEntity caller, VMQueuedAction action)
        {
            var steps = new StringBuilder();
            lock (Lock)
            {
                Capture = steps;
                TracingChecks = true;
                CheckBudget = CheckTraceLines;
                CheckRun++;
            }
            try
            {
                caller.ObjectData[(int)VMStackObjectVariable.HideInteraction] = 0;
                caller.Thread.CheckAction(action);
            }
            catch (Exception e)
            {
                steps.Append($"\n(the check threw {e.GetType().Name}: {e.Message})");
            }
            finally
            {
                lock (Lock)
                {
                    Capture = null;
                    TracingChecks = false;
                }
                caller.ObjectData[(int)VMStackObjectVariable.HideInteraction] = 0;
            }
            return string.Concat(steps.ToString().Split('\n').Where(x => x.Length > 0).Select(x => "\n      " + x));
        }

        /// <summary>Whether the family is on a lot other than its home: only there are interactions and checks traced.</summary>
        public static bool AwayFromHome(VM vm)
        {
            try
            {
                var family = vm?.TS1State?.CurrentFamily;
                return family != null && vm.GetGlobalValue(10) != family.HouseNumber;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>The player picked interactions (away from home): their scripts are traced step by step.</summary>
        public static void Follow(VM vm, VMEntity caller, VMEntity callee, IEnumerable<VMQueuedAction> actions)
        {
            if (!Enabled || !AwayFromHome(vm)) return;
            try
            {
                foreach (var action in actions)
                {
                    action.TraceLines = ActionTraceLines;
                    Write($"following '{action.Name}' on {Name(callee)} for {Name(caller)} (its script, step by step)" + Globals(vm));
                }
            }
            catch (Exception) { }
        }

        /// <summary>An interaction queued by a script the trace is following is followed too (a call that sends the Sim on).</summary>
        public static void Queued(VMEntity entity, VMQueuedAction action)
        {
            if (Following == null || action.TraceLines > 0) return;
            action.TraceLines = ActionTraceLines;
            Write($"{Who(Following)} queued '{action.Name}' on {Name(action.Callee)} for {Name(entity)}: following it too");
        }

        /// <summary>An object created by a script the trace is following: its own scripts are followed for a while.</summary>
        public static void Created(VMEntity entity)
        {
            if (!Enabled || entity == null) return;
            try
            {
                entity.TraceBudget = ObjectTraceLines;
                Write($"{Who(Following)} created {Name(entity)} (object {entity.ObjectID}, GUID 0x{entity.Object?.OBJ?.GUID:X8}): following its scripts too");
            }
            catch (Exception) { }
        }

        /// <summary>For other notes: which followed script was running, if any.</summary>
        public static string Context()
        {
            var thread = Following;
            if (thread == null) return "";
            var top = thread.Stack.LastOrDefault();
            return $" (while {Who(thread)} ran {Routine(top)})";
        }

        /// <summary>One step of a followed script: the instruction it ran (decoded), what it returned, and the temps after.</summary>
        public static void Step(VMThread thread, VMStackFrame frame, int ip, VMInstruction instruction, VMPrimitiveExitCode result)
        {
            try
            {
                if (instruction == null || !Spend(thread, frame, $"{frame.Routine?.ID}|{frame.CodeOwner?.OBJ?.GUID}|{ip}|{result}")) return;
                string what;
                if (instruction.Opcode >= 256)
                {
                    var callee = thread.Stack.LastOrDefault();
                    what = (callee != null && callee != frame && result == VMPrimitiveExitCode.CONTINUE)
                        ? $"call '{RoutineName(callee.Routine)}' 0x{instruction.Opcode:X4} with args ({string.Join(", ", callee.Args?.Take(4) ?? new short[0])})"
                        : $"call 0x{instruction.Opcode:X4}: not found, so the script stops with an error";
                    result = VMPrimitiveExitCode.CONTINUE;
                }
                else
                {
                    what = Describe(frame, instruction) + $" -> {result}";
                }
                var temps = thread.TempRegisters;
                var locals = (frame.Locals != null && frame.Locals.Length > 0) ? $" locals {string.Join(",", frame.Locals.Take(8))}" : "";
                Write($"{Who(thread)} '{RoutineName(frame.Routine)}' 0x{frame.Routine?.ID:X4} [{ip}] {what}" +
                    $" | temps {temps[0]},{temps[1]},{temps[2]},{temps[3]}{locals}" +
                    ((ip == 0 && frame.Args != null) ? $" | args {string.Join(",", frame.Args.Take(4))}" : ""));
            }
            catch (Exception) { }
        }

        /// <summary>A followed routine returned to its caller.</summary>
        public static void Returned(VMThread thread, VMStackFrame frame, VMPrimitiveExitCode result)
        {
            try
            {
                if (!Spend(thread, frame, $"ret|{frame.Routine?.ID}|{frame.CodeOwner?.OBJ?.GUID}|{result}")) return;
                Write($"{Who(thread)} '{RoutineName(frame.Routine)}' 0x{frame.Routine?.ID:X4} returned {result}");
            }
            catch (Exception) { }
        }

        /// <summary>A followed Sim finished routing (to an object, a spot, a car...).</summary>
        public static void Routed(VMThread thread, VMRoutingFrame frame, VMPrimitiveExitCode result)
        {
            try
            {
                if (!Spend(thread, frame, $"route|{frame.Routine?.ID}|{frame.CodeOwner?.OBJ?.GUID}|{result}")) return;
                Write($"{Who(thread)} routing for '{RoutineName(frame.Routine)}' 0x{frame.Routine?.ID:X4} finished: {result}");
            }
            catch (Exception) { }
        }

        private static readonly Dictionary<string, int> Steps = new Dictionary<string, int>();

        /// <summary>Counts a followed step against its budget, and skips loops after a couple of rounds.</summary>
        private static bool Spend(VMThread thread, VMStackFrame frame, string step)
        {
            string scope;
            if (thread.IsCheck) scope = "c" + CheckRun;
            else if (thread.Owner.TraceBudget > 0 && !(frame.ActionTree && thread.ActiveAction?.TraceLines > 0)) scope = "o" + thread.Owner.ObjectID;
            else scope = "a" + thread.Owner.ObjectID + ":" + thread.ActiveAction?.UID;

            lock (Lock)
            {
                if (Steps.Count > 200000) Steps.Clear();
                var key = scope + "|" + step;
                Steps.TryGetValue(key, out var count);
                Steps[key] = ++count;
                if (count > StepsPerInstruction) return false;
            }

            int left;
            if (thread.IsCheck) left = --CheckBudget;
            else if (scope[0] == 'o') left = --thread.Owner.TraceBudget;
            else left = --thread.ActiveAction.TraceLines;
            if (left == 0) Write($"{Who(thread)}: trace limit reached, no more steps written for this {(scope[0] == 'c' ? "check" : scope[0] == 'o' ? "object" : "interaction")}");
            return left >= 0;
        }

        private static string Who(VMThread thread)
        {
            if (thread == null) return "?";
            return (thread.IsCheck ? "check " : "") + Name(thread.Owner);
        }

        /// <summary>An instruction in words: the primitive's name and its operand's fields, or the routine it calls.</summary>
        private static string Describe(VMStackFrame frame, VMInstruction instruction)
        {
            var opcode = instruction.Opcode;
            if (opcode >= 256)
            {
                var callee = ResolveCall(frame, opcode);
                var args = (instruction.Operand as VMSubRoutineOperand)?.Arguments;
                return $"call '{(callee == null ? "(not found)" : RoutineName(callee))}' 0x{opcode:X4}" +
                    (args == null ? "" : $" args ({string.Join(", ", args)})");
            }
            var name = (opcode < VMContext.Primitives.Length) ? VMContext.Primitives[opcode]?.Name : null;
            return $"{name ?? "primitive " + opcode} {OperandText(instruction.Operand)}";
        }

        private static VMRoutine ResolveCall(VMStackFrame frame, ushort opcode)
        {
            try
            {
                if (opcode >= 8192) return (VMRoutine)frame.ScopeResource.SemiGlobal?.GetRoutine(opcode);
                if (opcode >= 4096) return (VMRoutine)frame.ScopeResource.GetRoutine(opcode);
                return (VMRoutine)frame.Global.Resource.GetRoutine(opcode);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static readonly Dictionary<Type, System.Reflection.MemberInfo[]> OperandMembers = new Dictionary<Type, System.Reflection.MemberInfo[]>();

        /// <summary>An operand's public fields and properties that aren't zero or empty, as name=value.</summary>
        private static string OperandText(VMPrimitiveOperand operand)
        {
            if (operand == null) return "";
            var type = operand.GetType();
            System.Reflection.MemberInfo[] members;
            lock (Lock)
            {
                if (!OperandMembers.TryGetValue(type, out members))
                {
                    members = type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance).Cast<System.Reflection.MemberInfo>()
                        .Concat(type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                            .Where(x => x.CanRead && x.GetIndexParameters().Length == 0))
                        .ToArray();
                    OperandMembers[type] = members;
                }
            }
            var parts = new List<string>();
            foreach (var member in members)
            {
                object value;
                try
                {
                    value = (member is System.Reflection.FieldInfo field) ? field.GetValue(operand) : ((System.Reflection.PropertyInfo)member).GetValue(operand);
                }
                catch (Exception)
                {
                    continue;
                }
                if (value == null) continue;
                string text;
                if (value is Array array)
                {
                    if (array.Length == 0 || array.Length > 16) continue;
                    text = "[" + string.Join(",", array.Cast<object>()) + "]";
                }
                else
                {
                    text = value.ToString();
                    if (text == "0" || text == "False" || text == "") continue;
                }
                parts.Add($"{member.Name}={text}");
            }
            return "{" + string.Join(" ", parts) + "}";
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
            Flush();
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
