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
        /// <summary>The game's own "have a baby?" question came up (from there on it's The Sims 1's usual baby).</summary>
        Asked,
        /// <summary>The roll for a baby failed this time.</summary>
        NoLuck,
        /// <summary>The roll succeeded, but the game's own baby check said no (mood, the kiss turned down, a full house...).</summary>
        GameDeclined,
    }

    /// <summary>
    /// "Try for Baby", as in The Sims 2, for The Sims 1 (Simitone).
    ///
    /// The Sims 1 has no Try for Baby: a couple living together can be asked "have a baby?" at random after a kiss
    /// (or a hug, or play in a Vibromatic bed). This adds a "Try for Baby" interaction to Sims that does the game's own
    /// kiss with a chance (ChancePercent) of a baby. When the roll succeeds, the game's own baby check is steered to
    /// pass, so the rest is exactly The Sims 1's baby: its question, the baby in a bassinet and the naming. When it fails,
    /// the check is steered to fail, so the kiss can't bring a baby by luck either.
    ///
    /// Nothing in the game's files is changed. At run time, the game's code is searched for the dialog that asks about a
    /// baby (a yes/no dialog whose text says "baby"), and for the interactions that lead to it (the kiss). The dice
    /// rolls ("random number") in the code between the kiss and that dialog are the ones steered, and only while a Sim
    /// is trying for a baby. The interaction lives in a copy of the Sims' interaction table held in memory, so the
    /// Sims' files (which the game writes back when it saves) never get it. Everything found is written to the log.
    /// </summary>
    public static class VMTryForBaby
    {
        public const string InteractionName = "Try for Baby";
        public static bool Enabled = true;
        /// <summary>Chance of a baby from each try, in percent (as in The Sims 2, half the time).</summary>
        public static int ChancePercent = 50;
        /// <summary>Daily relationship both Sims need with each other.</summary>
        public static int RelationshipNeeded = 50;
        /// <summary>The Sims 1's household limit.</summary>
        public static int MaxFamily = 8;

        /// <summary>Called when a try ends: (vm, the Sim who asked, their partner, outcome).</summary>
        public static Action<VM, VMAvatar, VMAvatar, VMTryForBabyOutcome> OnResult;
        public static Action<string> Log = (msg) => Console.WriteLine("[baby] " + msg);

        private class Setup
        {
            public TTAB Table;
            public TTAs Strings;
            public TTABInteraction Entry;
        }

        private class Attempt
        {
            public VM VM;
            public VMQueuedAction Action;
            public VMAvatar Initiator;
            public VMAvatar Partner;
            public bool Lucky;
            public bool Asked;
            public int Steered;
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

        private class Found
        {
            public TTABInteraction Trigger;
            public string TriggerName;
        }

        private static Dictionary<TTAB, Setup> Setups = new Dictionary<TTAB, Setup>();
        private static HashSet<TTAB> OurTables = new HashSet<TTAB>();
        private static HashSet<TTABInteraction> OurEntries = new HashSet<TTABInteraction>();
        private static Dictionary<GameIffResource, Found> Searched = new Dictionary<GameIffResource, Found>();
        private static HashSet<VMRoutine> SteeredRoutines = new HashSet<VMRoutine>();
        private static HashSet<VMRoutine> AskRoutines = new HashSet<VMRoutine>();
        private static List<Attempt> Attempts = new List<Attempt>();

        // ---- the interaction ----

        /// <summary>Gives a Sim the Try for Baby interaction (a copy of their interaction table with it added).</summary>
        public static void Prepare(VM vm, VMEntity entity)
        {
            if (!Enabled || vm == null || !vm.TS1) return;
            var ava = entity as VMAvatar;
            if (ava?.TreeTable == null || OurTables.Contains(ava.TreeTable)) return;
            var original = ava.TreeTable;
            var found = Search(vm, ava);
            Setup setup;
            if (!Setups.TryGetValue(original, out setup))
            {
                setup = (found == null) ? null : MakeSetup(original, ava.TreeTableStrings, found);
                Setups[original] = setup;
            }
            if (setup == null) return;
            ava.TreeTable = setup.Table;
            ava.TreeTableStrings = setup.Strings;
        }

        /// <summary>False for Try for Baby when this couple can't try (other interactions are always allowed).</summary>
        public static bool Allowed(VM vm, VMEntity caller, VMEntity callee, TTABInteraction entry)
        {
            if (entry == null || !OurEntries.Contains(entry)) return true;
            return CanTry(vm, caller as VMAvatar, callee as VMAvatar);
        }

        public static bool CanTry(VM vm, VMAvatar a, VMAvatar b)
        {
            if (vm == null || !vm.TS1 || a == null || b == null || a == b) return false;
            if (a.GetPersonData(VMPersonDataVariable.PersonsAge) < 18 || b.GetPersonData(VMPersonDataVariable.PersonsAge) < 18) return false;
            if (a.GetPersonData(VMPersonDataVariable.Gender) == b.GetPersonData(VMPersonDataVariable.Gender)) return false;
            var family = vm.TS1State?.CurrentFamily;
            if (family?.FamilyGUIDs == null || family.FamilyGUIDs.Length >= MaxFamily) return false;
            if (!family.FamilyGUIDs.Contains(a.Object.OBJ.GUID) || !family.FamilyGUIDs.Contains(b.Object.OBJ.GUID)) return false;
            if (Daily(a, b) < RelationshipNeeded || Daily(b, a) < RelationshipNeeded) return false;
            if (Attempts.Any(x => x.VM == vm && (Involves(x, a) || Involves(x, b)))) return false;
            return true;
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

        private static Setup MakeSetup(TTAB original, TTAs strings, Found found)
        {
            if (strings == null) return null;
            var trigger = found.Trigger;
            var count = strings.LanguageSets[0]?.Strings.Length ?? 0;
            var index = (uint)Math.Max(count, original.Interactions.Length == 0 ? 0 : (int)original.Interactions.Max(x => x.TTAIndex) + 1);
            if (index > 255)
            {
                Log("no room for another interaction on Sims");
                return null;
            }

            var motives = new TTABMotiveEntry[trigger.MotiveEntries?.Length ?? 0];
            for (int i = 0; i < motives.Length; i++) motives[i].MotiveIndex = i; //no advertising: autonomy never picks it
            var entry = new TTABInteraction
            {
                ActionFunction = trigger.ActionFunction,
                TestFunction = trigger.TestFunction,
                MotiveEntries = motives,
                Flags = trigger.Flags,
                Flags2 = trigger.Flags2,
                TTAIndex = index,
                AttenuationCode = trigger.AttenuationCode,
                AttenuationValue = trigger.AttenuationValue,
                AutonomyThreshold = trigger.AutonomyThreshold,
                JoiningIndex = trigger.JoiningIndex,
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
            Log($"added \"{InteractionName}\" to Sims (interaction {index}, using \"{found.TriggerName}\")");
            return new Setup { Table = table, Strings = names, Entry = entry };
        }

        // ---- finding the game's baby question ----

        private static Found Search(VM vm, VMAvatar ava)
        {
            var res = ava.Object?.Resource;
            if (res == null) return null;
            Found found;
            if (Searched.TryGetValue(res, out found)) return found;
            found = null;
            try
            {
                found = RunSearch(vm, ava);
            }
            catch (Exception e)
            {
                Log("couldn't search the game's code for its baby question: " + e);
            }
            Searched[res] = found;
            return found;
        }

        private static Found RunSearch(VM vm, VMAvatar ava)
        {
            var globals = vm.Context.Globals?.Resource;
            var semi = ava.SemiGlobal;
            var local = ava.Object.Resource;

            //every routine the Sim's code can reach: globals (IDs below 4096), the Sim's own (4096-8191), semi-globals (8192+)
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
            AddAll(globals);

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

            //dialogs that talk about a baby
            var asks = new List<(Node node, string text, VMDialogType type)>();
            foreach (var node in nodes.Values)
            {
                foreach (var inst in node.Bhav.Instructions)
                {
                    if (inst.Opcode != 36 && inst.Opcode != 38 && inst.Opcode != 39) continue;
                    var op = new VMDialogOperand();
                    try { op.Read(inst.Operand); } catch { continue; }
                    STR table = null;
                    if (inst.Opcode == 36) table = node.Resource.Get<STR>(301);
                    else if (inst.Opcode == 38) table = globals?.Get<STR>(301);
                    else table = semi?.Get<STR>(301);
                    var text = table?.GetString(Math.Max(0, op.MessageStringID - 1)) ?? "";
                    if (text.IndexOf("baby", StringComparison.OrdinalIgnoreCase) >= 0) asks.Add((node, text, op.Type));
                }
            }
            if (asks.Count == 0)
            {
                Log("the game's code has no dialog about a baby (only English text is searched): Try for Baby is off");
                return null;
            }

            //the Sim's interactions, and the routines each one reaches
            var tableNames = ava.TreeTableStrings;
            var triggers = new List<(TTABInteraction entry, string name, Dictionary<Node, Node> parents, Node root)>();
            foreach (var entry in ava.TreeTable.Interactions)
            {
                Node root;
                if (!nodes.TryGetValue(entry.ActionFunction, out root)) continue;
                var parents = new Dictionary<Node, Node> { { root, null } };
                var queue = new Queue<Node>();
                queue.Enqueue(root);
                while (queue.Count > 0)
                {
                    var n = queue.Dequeue();
                    foreach (var c in n.Calls)
                    {
                        if (parents.ContainsKey(c)) continue;
                        parents[c] = n;
                        queue.Enqueue(c);
                    }
                }
                triggers.Add((entry, tableNames?.GetString((int)entry.TTAIndex) ?? "", parents, root));
            }

            int NameScore(string name)
            {
                var lower = (name ?? "").ToLowerInvariant();
                if (lower.Contains("kiss")) return 3;
                if (lower.Contains("hug")) return 2;
                return 1;
            }
            int AskScore((Node node, string text, VMDialogType type) ask)
            {
                var lower = ask.text.ToLowerInvariant();
                var score = 0;
                if (ask.type == VMDialogType.YesNo || ask.type == VMDialogType.YesNoCancel) score += 4;
                if (lower.Contains("have")) score += 2;
                if (lower.Contains("adopt")) score -= 6;
                if (lower.Contains("social worker") || lower.Contains("taken") || lower.Contains("away")) score -= 4;
                var reachers = triggers.Where(t => t.parents.ContainsKey(ask.node)).ToList();
                if (reachers.Any(t => NameScore(t.name) > 1)) score += 6;
                else if (reachers.Count > 0) score += 3;
                return score;
            }

            foreach (var ask in asks.Take(12))
                Log($"baby dialog in {ask.node.Name} ({ask.type}): \"{Clip(ask.text)}\"");
            var best = asks.OrderByDescending(AskScore).First();
            var askNode = best.node;

            var reaching = triggers.Where(t => t.parents.ContainsKey(askNode))
                .OrderByDescending(t => NameScore(t.name)).ThenBy(t => PathTo(t.parents, askNode).Count).ToList();
            (TTABInteraction entry, string name, Dictionary<Node, Node> parents, Node root) trigger;
            List<Node> path = null;
            if (reaching.Count > 0)
            {
                trigger = reaching[0];
                path = PathTo(trigger.parents, askNode);
            }
            else
            {
                //the question may come from the partner's side of the social (pushed to them, not called): use the kiss
                var kiss = triggers.Where(t => NameScore(t.name) > 1).OrderByDescending(t => NameScore(t.name)).ThenBy(t => t.name.Length).ToList();
                if (kiss.Count == 0)
                {
                    Log($"found the baby question in {askNode.Name}, but no kiss or hug to start it from: Try for Baby is off");
                    return null;
                }
                trigger = kiss[0];
            }

            //the dice to steer: in the routines between the kiss and the question (not the kiss itself, unless it asks
            //directly), or else in the question's routine and the routines that call it
            var steer = new List<Node>();
            if (path != null)
            {
                steer.AddRange(path.Count <= 2 ? path : path.Skip(1));
            }
            else
            {
                steer.Add(askNode);
                steer.AddRange(askNode.Callers);
            }
            foreach (var n in steer) if (n.Routine != null) SteeredRoutines.Add(n.Routine);
            if (askNode.Routine != null) AskRoutines.Add(askNode.Routine);

            Log($"the baby question: {askNode.Name}: \"{Clip(best.text)}\"");
            Log($"started from \"{trigger.name}\"" + ((path != null) ? ": " + string.Join(" > ", path.Select(x => x.Name)) : " (the question isn't called from it directly)"));
            Log("dice steered in: " + string.Join(", ", steer.Select(x => x.Name)));
            return new Found { Trigger = trigger.entry, TriggerName = trigger.name };
        }

        private static List<Node> PathTo(Dictionary<Node, Node> parents, Node target)
        {
            var path = new List<Node>();
            for (var n = target; n != null; n = parents[n]) path.Insert(0, n);
            return path;
        }

        private static string Clip(string text)
        {
            text = (text ?? "").Replace('\n', ' ').Replace('\r', ' ');
            return text.Length > 90 ? text.Substring(0, 90) + "..." : text;
        }

        // ---- a try in progress ----

        private static bool Involves(Attempt attempt, VMEntity entity)
        {
            return entity != null && (entity == attempt.Initiator || entity == attempt.Partner);
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

        /// <summary>A Sim starts an interaction: if it's Try for Baby, roll for the baby.</summary>
        public static void OnActionStart(VMEntity entity, VMQueuedAction action)
        {
            if (!IsTry(action)) return;
            var a = entity as VMAvatar;
            var b = action.Callee as VMAvatar;
            if (a == null || b == null || Attempts.Any(x => x.Action == action)) return;
            var vm = entity.Thread.Context.VM;
            var lucky = (int)vm.Context.NextRandom(100) < ChancePercent;
            Attempts.Add(new Attempt { VM = vm, Action = action, Initiator = a, Partner = b, Lucky = lucky });
            Log($"{a.Name} and {b.Name} try for a baby ({ChancePercent}% chance): " + (lucky ? "lucky" : "not this time"));
        }

        /// <summary>
        /// A "random number" in the game's code: while a couple is trying for a baby, the rolls in the routines between
        /// the kiss and the baby question come out as the try's roll did (0 to pass, the top of the range to fail).
        /// </summary>
        public static int? SteerRandom(VMStackFrame frame, int range)
        {
            if (Attempts.Count == 0 || frame.Routine == null || !SteeredRoutines.Contains(frame.Routine)) return null;
            var attempt = Find(frame);
            if (attempt == null || attempt.Asked) return null;
            attempt.Steered++;
            var value = attempt.Lucky ? 0 : Math.Max(0, range - 1);
            Log($"roll in {(frame.Routine.Rti?.Name ?? "").TrimEnd('\0')} #{frame.Routine.ID}: {value} (of {range})");
            return value;
        }

        /// <summary>The game shows a dialog: if it's the baby question during a try, the try worked.</summary>
        public static void OnDialog(VMStackFrame frame, VMDialogOperand operand, STR table)
        {
            if (Attempts.Count == 0) return;
            var attempt = Find(frame);
            if (attempt == null || attempt.Asked) return;
            var text = table?.GetString(Math.Max(0, operand.MessageStringID - 1)) ?? "";
            if (AskRoutines.Contains(frame.Routine) || text.IndexOf("baby", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                attempt.Asked = true;
                Log($"the game asked about a baby (in {(frame.Routine?.Rti?.Name ?? "").TrimEnd('\0')} #{frame.Routine?.ID}): \"{Clip(text)}\"");
            }
        }

        /// <summary>Each tick: tries whose interaction has ended are reported.</summary>
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
                Attempts.RemoveAt(i--);
                var outcome = attempt.Asked ? VMTryForBabyOutcome.Asked
                    : attempt.Lucky ? VMTryForBabyOutcome.GameDeclined : VMTryForBabyOutcome.NoLuck;
                Log($"try for a baby ended: {outcome} ({attempt.Steered} roll(s) steered)");
                OnResult?.Invoke(vm, attempt.Initiator, attempt.Partner, outcome);
            }
        }
    }
}
