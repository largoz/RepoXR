using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace RepoXR.Patches;

/// <summary>
/// Compatibility check for a new game version, started with <c>--repoxr-patch-check</c>.
///
/// Applies every patch class, the VR ones too when no headset is present, without the failure dialog, then reports
/// the classes that failed to apply and every RepoXR transpiler that left its method's IL unchanged: a CodeMatcher
/// whose anchor is gone fails silently, so "applied" alone says nothing about a transpiler. The report goes to
/// <c>BepInEx/repoxr-patch-check.txt</c> and the game quits.
///
/// A transpiler reported as unchanged is not proven broken (some only act under a condition), and one reported as
/// changed is not proven right (it may have matched the wrong place): both are leads for reading the IL.
/// </summary>
internal static class PatchCheck
{
    public const string Flag = "--repoxr-patch-check";

    private static readonly string[] HarmonyIds = ["io.daxcess.repoxr", "io.daxcess.repoxr-universal"];

    public static bool Enabled { get; } =
        Environment.GetCommandLineArgs().Contains(Flag, StringComparer.OrdinalIgnoreCase);

    private static readonly List<string> applied = [];
    private static readonly List<(string type, string error)> failed = [];

    public static void RecordApplied(Type type) => applied.Add(type.FullName!);

    public static void RecordFailed(Type type, Exception e) =>
        failed.Add((type.FullName!, $"{e.Message} {e.InnerException?.Message}".Trim()));

    /// <summary>
    /// Write the report and quit. <paramref name="abortReason"/> is set when the mod stopped before patching
    /// </summary>
    public static void Finish(string? abortReason = null)
    {
        var report = new StringBuilder();
        report.AppendLine($"RepoXR {Plugin.PLUGIN_VERSION} patch check");
        report.AppendLine($"game {Plugin.GameVersion} ({Environment.GetEnvironmentVariable("REPO_BRANCH") ?? "?"}), " +
                          $"Unity {Application.unityVersion}, VR started: {Plugin.Flags.HasFlag(Flags.VR)}");

        if (abortReason != null)
        {
            report.AppendLine($"ABORTED before patching: {abortReason}");
            Write(report);
            return;
        }

        int transpilers = 0, unchanged = 0, broken = 0, methods = 0;
        var lines = new List<string>();

        var getSorted = AccessTools.Method("HarmonyLib.PatchFunctions:GetSortedPatchMethods");

        foreach (var method in Harmony.GetAllPatchedMethods().ToList())
        {
            var info = Harmony.GetPatchInfo(method);
            if (info == null || !info.Owners.Any(HarmonyIds.Contains))
                continue;

            methods++;

            var patches = info.Transpilers.ToArray();
            if (patches.Length == 0)
                continue;

            var sorted = (List<MethodInfo>)getSorted.Invoke(null, [method, patches, false]);
            List<string>? previous = null;

            for (var count = 0; count <= sorted.Count; count++)
            {
                List<string> current;
                try
                {
                    current = PatchProcessor.GetCurrentInstructions(method, count).Select(Render).ToList();
                }
                catch (Exception e)
                {
                    broken++;
                    lines.Add($"ERROR      {Describe(method)} <- {Name(sorted[Math.Max(count - 1, 0)])}: " +
                              $"{e.GetType().Name}: {e.Message}");
                    break;
                }

                if (previous != null)
                {
                    var transpiler = sorted[count - 1];
                    transpilers++;

                    if (current.SequenceEqual(previous))
                    {
                        unchanged++;
                        lines.Add($"UNCHANGED  {Describe(method)} <- {Name(transpiler)}");
                    }
                    else
                    {
                        var differing = current.Zip(previous, (a, b) => a == b ? 0 : 1).Sum() +
                                        Math.Abs(current.Count - previous.Count);
                        lines.Add($"changed    {Describe(method)} <- {Name(transpiler)} " +
                                  $"({previous.Count} -> {current.Count} instructions, {differing} differ)");
                    }
                }

                previous = current;
            }
        }

        report.AppendLine($"patch classes: {applied.Count} applied, {failed.Count} failed");
        report.AppendLine($"patched methods: {methods}; transpilers: {transpilers} run, {unchanged} unchanged, " +
                          $"{broken} threw");
        report.AppendLine();

        foreach (var (type, error) in failed)
            report.AppendLine($"FAILED     {type}: {error}");

        foreach (var line in lines.OrderBy(l => l.StartsWith("changed") ? 1 : 0).ThenBy(l => l))
            report.AppendLine(line);

        Write(report);
    }

    private static void Write(StringBuilder report)
    {
        var path = Path.Combine(Paths.BepInExRootPath, "repoxr-patch-check.txt");
        File.WriteAllText(path, report.ToString());

        Logger.LogInfo($"Patch check written to {path}:\n{report}");

        // Application.Quit is ignored this early in startup (seen on Unity 6000.3): end the process instead
        System.Diagnostics.Process.GetCurrentProcess().Kill();
    }

    private static string Render(CodeInstruction instruction)
    {
        var operand = instruction.operand switch
        {
            null => "",
            Label => "<label>",
            Label[] labels => $"<{labels.Length} labels>",
            LocalBuilder local => $"local {local.LocalIndex} {local.LocalType}",
            MethodBase m => m.FullDescription(),
            FieldInfo f => $"{f.DeclaringType?.FullName}::{f.Name}",
            Type t => t.FullName ?? t.Name,
            var other => other.ToString()
        };

        return $"{instruction.opcode} {operand}";
    }

    private static string Describe(MethodBase method) => $"{method.DeclaringType?.FullName}::{method.Name}";

    private static string Name(MethodInfo patch) => $"{patch.DeclaringType?.FullName}.{patch.Name}";
}
