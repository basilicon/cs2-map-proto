using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Cs2MapProto.Export;

/// <summary>Locates CS2 Workshop Tools binaries from a .vmap path inside
/// ...\Counter-Strike Global Offensive\content\csgo_addons\&lt;addon&gt;\maps\ and runs them.</summary>
public static class Cs2Tools
{
    public static string FindBinDir(string vmapPath)
    {
        if (string.IsNullOrWhiteSpace(vmapPath)) return null;
        var dir = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(vmapPath)) ?? "");
        while (dir != null)
        {
            var bin = Path.Combine(dir.FullName, "game", "bin", "win64");
            if (File.Exists(Path.Combine(bin, "resourcecompiler.exe"))) return bin;
            dir = dir.Parent;
        }
        return null;
    }

    public static string Tool(string vmapPath, string exe)
    {
        var bin = FindBinDir(vmapPath);
        return bin == null ? null : Path.Combine(bin, exe);
    }

    /// <summary>True when the path sits in content\csgo_addons\&lt;addon&gt;\maps, which the compiler requires.</summary>
    public static bool IsInAddon(string vmapPath, out string addon)
    {
        addon = null;
        var parts = Path.GetFullPath(vmapPath).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        int i = Array.FindIndex(parts, p => p.Equals("csgo_addons", StringComparison.OrdinalIgnoreCase));
        if (i < 1 || i + 2 >= parts.Length || !parts[i - 1].Equals("content", StringComparison.OrdinalIgnoreCase)) return false;
        addon = parts[i + 1];
        return parts.Skip(i + 2).First().Equals("maps", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Compiles the map (world + physics, no lighting bake: fast iteration). Blocking; call off the main thread.</summary>
    public static (bool ok, string summary) Compile(string vmapPath)
    {
        var rc = Tool(vmapPath, "resourcecompiler.exe");
        if (rc == null) return (false, "resourcecompiler.exe not found above the .vmap path (is it inside CS2's content\\csgo_addons?)");
        if (!IsInAddon(vmapPath, out _)) return (false, "The .vmap must be in content\\csgo_addons\\<addon>\\maps to compile.");

        var psi = new ProcessStartInfo(rc)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(rc)!,
        };
        foreach (var a in new[] { "-i", Path.GetFullPath(vmapPath), "-world", "-phys", "-fshallow", "-nompi", "-nop4", "-unbufferedio", "-noassert" })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(10 * 60 * 1000)) { try { p.Kill(); } catch { } return (false, "resourcecompiler timed out"); }
        string log = stdout.Result + stderr.Result;
        File.WriteAllText(Path.ChangeExtension(vmapPath, ".compile.log"), log);
        var okLine = log.Split('\n').LastOrDefault(l => l.Contains(" compiled, ") && l.Contains(" failed"))?.Trim() ?? "";
        bool ok = p.ExitCode == 0 && okLine.Contains(" 0 failed");
        return (ok, ok ? $"Compiled: {okLine}  -> in CS2 (with Workshop Tools) run: map {Path.GetFileNameWithoutExtension(vmapPath)}"
                       : $"Compile failed ({okLine}); see {Path.ChangeExtension(vmapPath, ".compile.log")}");
    }
}
