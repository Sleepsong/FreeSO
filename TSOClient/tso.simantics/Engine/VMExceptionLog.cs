using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace FSO.SimAntics.Engine
{
    /// <summary>
    /// Appends SimAntics exceptions to a text file with the full .NET stack trace and every VM stack frame.
    /// The in-game dialog shows only the first line of each and is rate-limited per thread, so this file is what
    /// to attach to bug reports. Does nothing until a frontend sets <see cref="Path"/>.
    /// </summary>
    public static class VMExceptionLog
    {
        /// <summary>Log file to append to, or null to disable logging.</summary>
        public static string Path;

        /// <summary>Written at the top of each session's entries (e.g. frontend version and launch arguments).</summary>
        public static string SessionHeader = "";

        private const long RotateBytes = 8 * 1024 * 1024; //checked once per session; the old log is kept as *.old
        private const int FullReportsPerSignature = 3; //after that, repeats are only counted
        private const int RepeatSummaryEvery = 50;

        private static readonly Dictionary<string, int> Counts = new Dictionary<string, int>();
        private static readonly object Lock = new object();
        private static bool SessionStarted;

        public static void Write(Exception e, VMThread thread, VMEntity entity)
        {
            var path = Path;
            if (path == null) return;
            try
            {
                lock (Lock)
                {
                    var stack = thread.Stack;
                    var signature = Signature(e, stack);
                    Counts.TryGetValue(signature, out var count);
                    Counts[signature] = ++count;

                    var output = new StringBuilder();
                    StartSession(output, path);

                    if (count <= FullReportsPerSignature)
                    {
                        AppendReport(output, e, thread, entity, stack, count);
                    }
                    else if (count % RepeatSummaryEvery == 0)
                    {
                        output.AppendLine($"[{DateTime.Now:HH:mm:ss}] repeated {count} times: {signature}");
                    }

                    if (output.Length > 0) File.AppendAllText(path, output.ToString());
                }
            }
            catch
            {
                //logging must never take the game down.
            }
        }

        private const int MaxNotesPerSession = 200;
        private static int NotesWritten;

        /// <summary>
        /// Records a problem the VM recovered from without an exception (e.g. state that couldn't be loaded).
        /// Identical notes are written once per session.
        /// </summary>
        public static void WriteNote(VMEntity entity, string message)
        {
            var path = Path;
            if (path == null) return;
            try
            {
                lock (Lock)
                {
                    var line = Safe(() => entity?.ToString()) + " " + message;
                    if (NotesWritten >= MaxNotesPerSession || Counts.ContainsKey("note:" + line)) return;
                    Counts["note:" + line] = 1;
                    NotesWritten++;

                    var output = new StringBuilder();
                    StartSession(output, path);
                    output.AppendLine($"[{DateTime.Now:HH:mm:ss}] note: {line}");
                    File.AppendAllText(path, output.ToString());
                }
            }
            catch
            {
                //logging must never take the game down.
            }
        }

        private static void StartSession(StringBuilder output, string path)
        {
            if (SessionStarted) return;
            SessionStarted = true;
            Rotate(path);
            output.AppendLine();
            output.AppendLine($"===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} {SessionHeader} =====");
        }

        private static void Rotate(string path)
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length < RotateBytes) return;
            var old = path + ".old";
            if (File.Exists(old)) File.Delete(old);
            File.Move(path, old);
        }

        private static string Signature(Exception e, List<VMStackFrame> stack)
        {
            var top = (stack.Count > 0) ? DescribeRoutine(stack[stack.Count - 1]) : "(empty stack)";
            return $"{e.GetType().Name}: {e.Message} | {FirstSourceLine(e)} | {top}";
        }

        private static void AppendReport(StringBuilder output, Exception e, VMThread thread, VMEntity entity, List<VMStackFrame> stack, int count)
        {
            output.AppendLine($"[{DateTime.Now:HH:mm:ss}] {(thread.IsCheck ? "check tree" : "thread")} of {Safe(() => entity?.ToString())} (occurrence {count})");
            output.AppendLine($"  {e.GetType().FullName}: {e.Message}");
            output.AppendLine($"  VM stack, innermost first ({stack.Count} frames):");
            for (int i = stack.Count - 1; i >= 0; i--)
            {
                var frame = stack[i];
                output.Append($"    [{i}] {frame.GetType().Name} {DescribeRoutine(frame)}");
                output.Append($" caller={Safe(() => frame.Caller?.ToString())} callee={Safe(() => frame.Callee?.ToString())}");
                output.Append($" stackobj={Safe(() => frame.StackObject?.ToString())}");
                output.AppendLine($" owner={Safe(() => (frame.CodeOwner == null) ? null : "0x" + frame.CodeOwner.OBJ.GUID.ToString("X8"))} actiontree={frame.ActionTree}");
            }
            output.AppendLine("  .NET exception:");
            foreach (var line in e.ToString().Split('\n'))
            {
                output.AppendLine("    " + line.TrimEnd('\r'));
            }
            output.AppendLine();
        }

        private static string DescribeRoutine(VMStackFrame frame)
        {
            var routine = frame.Routine;
            if (routine == null) return "<no routine>";
            var name = Safe(() => routine.Rti?.Name?.TrimEnd('\0'));
            var opcode = (frame.InstructionPointer < (routine.Instructions?.Length ?? 0))
                ? $" opcode={routine.Instructions[frame.InstructionPointer].Opcode}"
                : " (past end)";
            return $"'{name}' id=0x{routine.ID:X4} ip={frame.InstructionPointer}{opcode}";
        }

        private static string FirstSourceLine(Exception e)
        {
            foreach (var line in (e.StackTrace ?? "").Split('\n'))
            {
                if (line.Contains(".cs")) return line.Trim();
            }
            return "(no source line)";
        }

        private static string Safe(Func<string> describe)
        {
            try
            {
                return describe() ?? "null";
            }
            catch (Exception)
            {
                return "?";
            }
        }
    }
}
