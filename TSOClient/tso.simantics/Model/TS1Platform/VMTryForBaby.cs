using FSO.Content;
using FSO.Files.Formats.IFF.Chunks;
using FSO.SimAntics.Engine;
using FSO.SimAntics.Model;
using FSO.SimAntics.Primitives;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FSO.SimAntics.Model.TS1Platform
{
    public enum VMTryForBabyOutcome
    {
        /// <summary>A baby: the game made it and asked for its name.</summary>
        Baby,
        /// <summary>Twins: the game made two babies.</summary>
        Twins,
        /// <summary>The roll for a baby failed this time.</summary>
        NoLuck,
        /// <summary>The roll succeeded, but the game's own baby code never got as far as the baby (mood, a full house...).</summary>
        GameDeclined,
    }

    /// <summary>
    /// "Try for Baby", as in The Sims 2, for The Sims 1 (Simitone).
    ///
    /// The Sims 1's WooHoo is "Play" in the Vibromatic Heart Bed (Livin' Large), and the game can ask "have a baby?" at
    /// random after it (or after a kiss). This adds "Try for Baby" next to the bed's WooHoo: the same WooHoo, but with The
    /// Sims 2's odds. Half the time there's a baby, and one baby in ten is twins. On a baby the game's own baby code
    /// runs without asking: its question is answered "yes" for you, the baby arrives in its bassinet, and the game asks
    /// for its name. For twins it runs a second time. Otherwise there's no baby, even by the game's own luck.
    ///
    /// Nothing in the game's files is changed. At run time the game's code is searched for the dialog that asks about a
    /// baby (a yes/no dialog whose text says "baby"), and for the interactions that lead to it. While a couple is trying,
    /// the dice rolls ("random number") between those interactions and the question come out as the try's roll did,
    /// and the question is answered for them. The new interaction lives in a copy of the bed's interaction table held
    /// in memory. Everything found and every try is written to the log ("[baby]").
    /// </summary>
    public static class VMTryForBaby
    {
        public const string InteractionName = "Try for Baby";
        public static bool Enabled = true;
        /// <summary>Chance of a baby from each try, in percent.</summary>
        public static int ChancePercent = 50;
        /// <summary>Chance that a baby is twins, in percent.</summary>
        public static int TwinsPercent = 10;
        /// <summary>Daily relationship both Sims need with each other.</summary>
        public static int RelationshipNeeded = 50;
        /// <summary>The Sims 1's household limit.</summary>
        public static int MaxFamily = 8;

        /// <summary>Called when a try ends: (vm, the Sim who started it, their partner if known, outcome).</summary>
        public static Action<VM, VMAvatar, VMAvatar, VMTryForBabyOutcome> OnResult;
        public static Action<string> Log = (msg) => Console.WriteLine("[baby] " + msg);

        private class Setup
        {
            public TTAB Table;
            public TTAs Strings;
        }

        private class Attempt
        {
            public VM VM;
            public VMQueuedAction Action;
            public VMAvatar Initiator;
            public VMAvatar Partner;
            public VMEntity Bed;
            public bool Lucky;
            public bool Twins;
            public int Babies;
            public bool Refused;
            public int Steered;
            public VMQueuedAction TwinAction;
            public VMEntity TwinOwner;
            public short[] TwinTemps;
        }

        private class Node
        {
            public ushort ID;
            public BHAV Bhav;
            public VMRoutine Routine;
            public GameIffResource Resource;
            public List<Node> Calls = new List<Node>();
            public List<Node> Callers = new List<Node>();
            public string Name => $"{(Bhav.ChunkLabel ?? "").TrimEnd('\0')} #{ID}";
        }

        private class Ask
        {
            public Node Node;
            public string Text;
            public VMDialogType Type;
        }

        private static Dictionary<TTAB, Setup> Setups = new Dictionary<TTAB, Setup>();
        private static HashSet<TTAB> OurTables = new HashSet<TTAB>();
        private static HashSet<TTABInteraction> OurEntries = new HashSet<TTABInteraction>();
        private static HashSet<GameIffResource> SearchedPeople = new HashSet<GameIffResource>();
        private static HashSet<VMRoutine> SteeredRoutines = new HashSet<VMRoutine>();
        private static HashSet<VMRoutine> AskRoutines = new HashSet<VMRoutine>();
        private static List<Attempt> Attempts = new List<Attempt>();

        // ---- setting up ----

        /// <summary>
        /// A pie menu is about to be built for callee: search the caller's (a Sim's) code for the baby question once, and
        /// give a bed with WooHoo the Try for Baby interaction (a copy of its interaction table with it added).
        /// </summary>
        public static void Prepare(VM vm, VMEntity callee, VMEntity caller)
        {
            if (!Enabled || vm == null || !vm.TS1) return;
            var person = caller as VMAvatar;
            if (person?.Object?.Resource != null && SearchedPeople.Add(person.Object.Resource))
            {
                try { SearchPerson(vm, person); }
                catch (Exception e) { Log("couldn't search a Sim's code for the baby question: " + e); }
            }
            if (callee == null || callee is VMAvatar || callee.TreeTable == null || OurTables.Contains(callee.TreeTable)) return;

            var original = callee.TreeTable;
            Setup setup;
            if (!Setups.TryGetValue(original, out setup))
            {
                setup = null;
                try { setup = SetUpBed(vm, callee); }
                catch (Exception e) { Log($"couldn't set up Try for Baby on {callee}: " + e); }
                Setups[original] = setup;
            }
            if (setup == null) return;
            callee.TreeTable = setup.Table;
            callee.TreeTableStrings = setup.Strings;
        }

        private static void SearchPerson(VM vm, VMAvatar person)
        {
            var nodes = Graph(vm, person.Object.Resource, person.SemiGlobal);
            var asks = FindAsks(vm, nodes, person.SemiGlobal);
            if (asks.Count == 0)
            {
                Log("no dialog about a baby in the Sims' code (only English text is searched)");
                return;
            }
            //(every Sim has the same code, so only the first Sim's search is logged)
            var quiet = asks.All(x => x.Node.Routine != null && AskRoutines.Contains(x.Node.Routine));
            if (!quiet) foreach (var ask in asks.Take(12)) Log($"baby dialog in {ask.Node.Name} ({ask.Type}): \"{Clip(ask.Text)}\"");
            //where the Sims' own interactions (the kiss) reach the question, steer the dice in between
            foreach (var entry in person.TreeTable.Interactions)
            {
                var name = person.TreeTableStrings?.GetString((int)entry.TTAIndex) ?? "";
                foreach (var ask in asks)
                {
                    var path = PathFrom(nodes, entry.ActionFunction, ask.Node);
                    if (path == null) continue;
                    Steer(path, ask);
                    if (!quiet) Log($"\"{name}\" leads to the baby question: " + string.Join(" > ", path.Select(x => x.Name)));
                }
            }
            foreach (var ask in asks) Steer(null, ask);
        }

        private static Setup SetUpBed(VM vm, VMEntity obj)
        {
            var strings = obj.TreeTableStrings;
            if (strings == null) return null;
            var label = ((obj.Object?.OBJ?.ChunkLabel ?? "") + " " + (obj.Object?.Resource?.Get<CTSS>(obj.Object.OBJ.CatalogStringsID)?.GetString(0) ?? "")).ToLowerInvariant();
            var isBed = label.Contains("bed");

            var woohoo = obj.TreeTable.Interactions.Where(x =>
            {
                var n = (strings.GetString((int)x.TTAIndex) ?? "").ToLowerInvariant();
                return n.Contains("woohoo") || n.Contains("woo hoo") || n.Contains("play");
            }).ToList();
            if (woohoo.Count == 0) return null;

            //WooHoo that reaches the baby question in the bed's own code, or a bed's "Play" (the question may be asked
            //from the Sims' side, which the Sims' search covers)
            var nodes = Graph(vm, obj.Object.Resource, obj.SemiGlobal);
            var asks = FindAsks(vm, nodes, obj.SemiGlobal);
            TTABInteraction chosen = null;
            foreach (var entry in woohoo)
            {
                foreach (var ask in asks)
                {
                    var path = PathFrom(nodes, entry.ActionFunction, ask.Node);
                    if (path == null) continue;
                    Steer(path, ask);
                    Log($"{label.Trim()}: \"{strings.GetString((int)entry.TTAIndex)}\" leads to the baby question: " + string.Join(" > ", path.Select(x => x.Name)));
                    if (chosen == null) chosen = entry;
                }
            }
            if (chosen == null && isBed) chosen = woohoo[0];
            if (chosen == null) return null;
            foreach (var ask in asks) Steer(null, ask);

            var setup = MakeSetup(obj.TreeTable, strings, chosen);
            if (setup != null) Log($"added \"{InteractionName}\" to {label.Trim()}, next to \"{strings.GetString((int)chosen.TTAIndex)}\"");
            return setup;
        }

        private static Setup MakeSetup(TTAB original, TTAs strings, TTABInteraction woohoo)
        {
            var count = strings.LanguageSets[0]?.Strings.Length ?? 0;
            var index = (uint)Math.Max(count, original.Interactions.Length == 0 ? 0 : (int)original.Interactions.Max(x => x.TTAIndex) + 1);
            if (index > 255) return null;

            var motives = new TTABMotiveEntry[woohoo.MotiveEntries?.Length ?? 0];
            for (int i = 0; i < motives.Length; i++) motives[i].MotiveIndex = i; //no advertising: autonomy never picks it
            var entry = new TTABInteraction
            {
                ActionFunction = woohoo.ActionFunction,
                TestFunction = woohoo.TestFunction,
                MotiveEntries = motives,
                Flags = woohoo.Flags,
                Flags2 = woohoo.Flags2,
                TTAIndex = index,
                AttenuationCode = woohoo.AttenuationCode,
                AttenuationValue = woohoo.AttenuationValue,
                AutonomyThreshold = woohoo.AutonomyThreshold,
                JoiningIndex = woohoo.JoiningIndex,
            };

            var table = new TTAB
            {
                ChunkID = original.ChunkID,
                ChunkLabel = original.ChunkLabel,
                ChunkType = original.ChunkType,
                ChunkParent = original.ChunkParent,
                ChunkProcessed = true,
                Interactions = original.Interactions.Concat(new[] { entry }).ToArray(),
                InteractionByIndex = new Dictionary<uint, TTABInteraction>(original.InteractionByIndex),
                AutoInteractions = original.AutoInteractions,
            };
            table.InteractionByIndex[index] = entry;

            var names = new TTAs
            {
                ChunkID = strings.ChunkID,
                ChunkLabel = strings.ChunkLabel,
                ChunkType = strings.ChunkType,
                ChunkParent = strings.ChunkParent,
                ChunkProcessed = true,
            };
            for (int i = 0; i < names.LanguageSets.Length && i < strings.LanguageSets.Length; i++)
            {
                var src = strings.LanguageSets[i]?.Strings ?? new STRItem[0];
                if (src.Length == 0) continue;
                var copy = new STRItem[Math.Max(src.Length, (int)index + 1)];
                Array.Copy(src, copy, src.Length);
                for (int j = src.Length; j < copy.Length; j++) copy[j] = new STRItem { LanguageCode = src[0].LanguageCode, Value = "", Comment = "" };
                copy[index] = new STRItem { LanguageCode = src[0].LanguageCode, Value = InteractionName, Comment = "" };
                names.LanguageSets[i] = new STRLanguageSet { Strings = copy };
            }

            OurTables.Add(table);
            OurEntries.Add(entry);
            return new Setup { Table = table, Strings = names };
        }

        // ---- searching the game's code ----

        /// <summary>Every routine an object's code can reach: globals (IDs below 4096), its own (4096-8191), semi-globals (8192+).</summary>
        private static Dictionary<ushort, Node> Graph(VM vm, GameIffResource local, GameIffResource semi)
        {
            var nodes = new Dictionary<ushort, Node>();
            void AddAll(GameIffResource source)
            {
                var list = source?.List<BHAV>();
                if (list == null) return;
                foreach (var bhav in list)
                {
                    if (nodes.ContainsKey(bhav.ChunkID)) continue;
                    nodes[bhav.ChunkID] = new Node { ID = bhav.ChunkID, Bhav = bhav, Resource = source, Routine = source.GetRoutine(bhav.ChunkID) as VMRoutine };
                }
            }
            AddAll(local);
            AddAll(semi);
            AddAll(vm.Context.Globals?.Resource);
            foreach (var node in nodes.Values)
            {
                foreach (var inst in node.Bhav.Instructions)
                {
                    Node target;
                    if (inst.Opcode >= 256 && nodes.TryGetValue(inst.Opcode, out target) && !node.Calls.Contains(target))
                    {
                        node.Calls.Add(target);
                        target.Callers.Add(node);
                    }
                }
            }
            return nodes;
        }

        /// <summary>Yes/no dialogs whose text talks about a baby (not adoption or the social worker).</summary>
        private static List<Ask> FindAsks(VM vm, Dictionary<ushort, Node> nodes, GameIffResource semi)
        {
            var globals = vm.Context.Globals?.Resource;
            var asks = new List<Ask>();
            foreach (var node in nodes.Values)
            {
                foreach (var inst in node.Bhav.Instructions)
                {
                    if (inst.Opcode != 36 && inst.Opcode != 38 && inst.Opcode != 39) continue;
                    var op = new VMDialogOperand();
                    try { op.Read(inst.Operand); } catch { continue; }
                    if (op.Type != VMDialogType.YesNo && op.Type != VMDialogType.YesNoCancel) continue;
                    STR table;
                    if (inst.Opcode == 36) table = node.Resource.Get<STR>(301);
                    else if (inst.Opcode == 38) table = globals?.Get<STR>(301);
                    else table = semi?.Get<STR>(301);
                    var text = table?.GetString(Math.Max(0, op.MessageStringID - 1)) ?? "";
                    if (IsBabyQuestion(text)) asks.Add(new Ask { Node = node, Text = text, Type = op.Type });
                }
            }
            return asks;
        }

        private static bool IsBabyQuestion(string text)
        {
            var lower = (text ?? "").ToLowerInvariant();
            return lower.Contains("baby") && !lower.Contains("adopt") && !lower.Contains("social worker");
        }

        private static List<Node> PathFrom(Dictionary<ushort, Node> nodes, ushort start, Node target)
        {
            Node root;
            if (!nodes.TryGetValue(start, out root)) return null;
            var parents = new Dictionary<Node, Node> { { root, null } };
            var queue = new Queue<Node>();
            queue.Enqueue(root);
            while (queue.Count > 0)
            {
                var n = queue.Dequeue();
                if (n == target)
                {
                    var path = new List<Node>();
                    for (var p = n; p != null; p = parents[p]) path.Insert(0, p);
                    return path;
                }
                foreach (var c in n.Calls)
                {
                    if (parents.ContainsKey(c)) continue;
                    parents[c] = n;
                    queue.Enqueue(c);
                }
            }
            return null;
        }

        /// <summary>
        /// The dice to steer: the routines between an interaction and the question (not the interaction itself, unless
        /// it asks directly), or else the question's routine and the routines that call it.
        /// </summary>
        private static void Steer(List<Node> path, Ask ask)
        {
            var steer = new List<Node>();
            if (path != null) steer.AddRange(path.Count <= 2 ? path : path.Skip(1));
            else
            {
                steer.Add(ask.Node);
                steer.AddRange(ask.Node.Callers);
            }
            foreach (var n in steer) if (n.Routine != null) SteeredRoutines.Add(n.Routine);
            if (ask.Node.Routine != null) AskRoutines.Add(ask.Node.Routine);
        }

        private static string Clip(string text)
        {
            text = (text ?? "").Replace('\n', ' ').Replace('\r', ' ');
            return text.Length > 90 ? text.Substring(0, 90) + "..." : text;
        }

        // ---- who can try ----

        /// <summary>False for Try for Baby when this Sim can't try (other interactions are always allowed).</summary>
        public static bool Allowed(VM vm, VMEntity caller, VMEntity callee, TTABInteraction entry)
        {
            if (entry == null || !OurEntries.Contains(entry)) return true;
            var sim = caller as VMAvatar;
            if (sim == null || !vm.TS1) return false;
            if (Attempts.Any(x => x.VM == vm && (x.Initiator == sim || x.Partner == sim))) return false;
            //joining a try already going on in this bed, or starting one with someone they could have a baby with
            var joining = Attempts.FirstOrDefault(x => x.VM == vm && SameObject(x.Bed, callee) && x.Partner == null);
            if (joining != null) return CanCouple(vm, sim, joining.Initiator);
            return vm.Entities.OfType<VMAvatar>().Any(other => CanCouple(vm, sim, other));
        }

        /// <summary>The Sims 2's rules for Try for Baby, with The Sims 1's household limit.</summary>
        public static bool CanCouple(VM vm, VMAvatar a, VMAvatar b)
        {
            if (vm == null || !vm.TS1 || a == null || b == null || a == b || a.Dead || b.Dead) return false;
            if (a.GetPersonData(VMPersonDataVariable.PersonsAge) < 18 || b.GetPersonData(VMPersonDataVariable.PersonsAge) < 18) return false;
            if (a.GetPersonData(VMPersonDataVariable.Gender) == b.GetPersonData(VMPersonDataVariable.Gender)) return false;
            var family = vm.TS1State?.CurrentFamily;
            if (family?.FamilyGUIDs == null || family.FamilyGUIDs.Length >= MaxFamily) return false;
            if (!family.FamilyGUIDs.Contains(a.Object.OBJ.GUID) || !family.FamilyGUIDs.Contains(b.Object.OBJ.GUID)) return false;
            return Daily(a, b) >= RelationshipNeeded && Daily(b, a) >= RelationshipNeeded;
        }

        private static int Daily(VMAvatar from, VMAvatar to)
        {
            var neighbourhood = FSO.Content.Content.Get()?.Neighborhood;
            var me = neighbourhood?.GetNeighborByID(from.GetPersonData(VMPersonDataVariable.NeighborId));
            List<short> values = null;
            if (me?.Relationships != null && me.Relationships.TryGetValue(to.GetPersonData(VMPersonDataVariable.NeighborId), out values) && values.Count > 0)
                return values[0];
            return 0;
        }

        private static bool SameObject(VMEntity a, VMEntity b)
        {
            if (a == null || b == null) return false;
            return a == b || (a.MultitileGroup != null && a.MultitileGroup == b.MultitileGroup);
        }

        // ---- a try in progress ----

        private static bool Involves(Attempt attempt, VMEntity entity)
        {
            return entity != null && (entity == attempt.Initiator || entity == attempt.Partner || SameObject(entity, attempt.Bed));
        }

        private static Attempt Find(VMStackFrame frame)
        {
            if (Attempts.Count == 0) return null;
            VMEntity stackObject = null;
            try { stackObject = frame.StackObject; } catch { }
            return Attempts.FirstOrDefault(x => x.VM == frame.VM && (Involves(x, frame.Caller) || Involves(x, frame.Callee) || Involves(x, stackObject)));
        }

        private static bool IsTry(VMQueuedAction action)
        {
            if (OurEntries.Count == 0 || action?.Callee?.TreeTable == null || action.InteractionNumber < 0) return false;
            TTABInteraction entry;
            return action.Callee.TreeTable.InteractionByIndex.TryGetValue((uint)action.InteractionNumber, out entry) && OurEntries.Contains(entry);
        }

        /// <summary>A Sim starts an interaction: Try for Baby rolls for the baby (once per couple), twins restore their state.</summary>
        public static void OnActionStart(VMEntity entity, VMQueuedAction action)
        {
            if (Attempts.Count > 0)
            {
                var twin = Attempts.FirstOrDefault(x => x.TwinAction == action);
                if (twin != null)
                {
                    if (twin.TwinTemps != null) Array.Copy(twin.TwinTemps, entity.Thread.TempRegisters, Math.Min(twin.TwinTemps.Length, entity.Thread.TempRegisters.Length));
                    Log("the second twin is on the way");
                    return;
                }
            }
            if (!IsTry(action)) return;
            var sim = entity as VMAvatar;
            if (sim == null || Attempts.Any(x => x.Action == action)) return;
            var vm = entity.Thread.Context.VM;

            var joining = Attempts.FirstOrDefault(x => x.VM == vm && SameObject(x.Bed, action.Callee) && x.Partner == null && x.Initiator != sim);
            if (joining != null)
            {
                joining.Partner = sim;
                Log($"{sim.Name} joins {joining.Initiator.Name} in trying for a baby");
                return;
            }
            var lucky = (int)vm.Context.NextRandom(100) < ChancePercent;
            var twins = lucky && (int)vm.Context.NextRandom(100) < TwinsPercent;
            Attempts.Add(new Attempt { VM = vm, Action = action, Initiator = sim, Bed = action.Callee, Lucky = lucky, Twins = twins });
            Log($"{sim.Name} tries for a baby ({ChancePercent}% chance, {TwinsPercent}% of babies twins): " + (twins ? "twins" : lucky ? "a baby" : "not this time"));
        }

        /// <summary>
        /// A "random number" in the game's code: while a couple is trying for a baby, the rolls in the routines between
        /// WooHoo (or a kiss) and the baby question come out as the try's roll did (0 to pass, the top of the range to fail).
        /// </summary>
        public static int? SteerRandom(VMStackFrame frame, int range)
        {
            if (Attempts.Count == 0 || frame.Routine == null || !SteeredRoutines.Contains(frame.Routine)) return null;
            var attempt = Find(frame);
            if (attempt == null) return null;
            var wanted = attempt.Lucky && attempt.Babies < (attempt.Twins ? 2 : 1);
            attempt.Steered++;
            var value = wanted ? 0 : Math.Max(0, range - 1);
            Log($"roll in {(frame.Routine.Rti?.Name ?? "").TrimEnd('\0')} #{frame.Routine.ID}: {value} (of {range})");
            return value;
        }

        /// <summary>
        /// The game is about to show a dialog. If it's the baby question during a try, it's answered for the couple (yes
        /// for a baby, no otherwise) instead of being shown: returns the answer, or null to show the dialog as usual.
        /// </summary>
        public static bool? AnswerDialog(VMStackFrame frame, VMDialogOperand operand, STR table)
        {
            if (Attempts.Count == 0) return null;
            if (operand.Type != VMDialogType.YesNo && operand.Type != VMDialogType.YesNoCancel) return null;
            var attempt = Find(frame);
            if (attempt == null) return null;
            var text = table?.GetString(Math.Max(0, operand.MessageStringID - 1)) ?? "";
            if (!AskRoutines.Contains(frame.Routine) && !IsBabyQuestion(text)) return null;
            var where = $"{(frame.Routine?.Rti?.Name ?? "").TrimEnd('\0')} #{frame.Routine?.ID}";

            var wanted = attempt.Lucky && attempt.Babies < (attempt.Twins ? 2 : 1);
            if (!wanted)
            {
                attempt.Refused = true;
                Log($"the game asked about a baby (in {where}), answered no: \"{Clip(text)}\"");
                return false;
            }
            attempt.Babies++;
            Log($"the game asked about a baby (in {where}), answered yes ({attempt.Babies} of {(attempt.Twins ? 2 : 1)}): \"{Clip(text)}\"");

            if (attempt.Twins && attempt.Babies == 1 && attempt.TwinAction == null && frame.Routine != null)
            {
                //the second twin: the same baby code again, queued to run after this
                var owner = frame.Caller; //(the Sim whose thread runs the baby code)
                VMEntity stackObject = null;
                try { stackObject = frame.StackObject; } catch { }
                attempt.TwinOwner = owner;
                attempt.TwinTemps = (short[])frame.Thread.TempRegisters.Clone();
                attempt.TwinAction = new VMQueuedAction
                {
                    Callee = frame.Callee,
                    IconOwner = frame.Callee,
                    CodeOwner = frame.CodeOwner,
                    ActionRoutine = frame.Routine,
                    StackObject = stackObject,
                    Args = (short[])frame.Args?.Clone(),
                    Name = InteractionName,
                    Priority = (short)VMQueuePriority.UserDriven,
                    Flags = 0,
                };
                owner.Thread.EnqueueAction(attempt.TwinAction);
            }
            return true;
        }

        /// <summary>Each tick: tries whose interactions (and second twin) have ended are reported.</summary>
        public static void Tick(VM vm)
        {
            for (int i = 0; i < Attempts.Count; i++)
            {
                var attempt = Attempts[i];
                if (attempt.VM != vm)
                {
                    Attempts.RemoveAt(i--); //from a lot that's gone
                    continue;
                }
                if (!attempt.Initiator.Dead && attempt.Initiator.Thread?.Queue.Contains(attempt.Action) == true) continue;
                if (attempt.TwinAction != null && attempt.TwinOwner?.Dead == false && attempt.TwinOwner.Thread?.Queue.Contains(attempt.TwinAction) == true) continue;
                Attempts.RemoveAt(i--);
                VMTryForBabyOutcome outcome;
                if (attempt.Babies >= 2) outcome = VMTryForBabyOutcome.Twins;
                else if (attempt.Babies == 1) outcome = VMTryForBabyOutcome.Baby;
                else if (attempt.Lucky) outcome = VMTryForBabyOutcome.GameDeclined;
                else outcome = VMTryForBabyOutcome.NoLuck;
                Log($"try for a baby ended: {outcome} ({attempt.Steered} roll(s) steered)");
                OnResult?.Invoke(vm, attempt.Initiator, attempt.Partner, outcome);
            }
        }
    }
}
