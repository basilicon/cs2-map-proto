using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Godot;

namespace Cs2MapProto.Plugins;

/// <summary>Finds plugins/&lt;Name&gt;/&lt;Name&gt;.dll next to the executable (exported builds) or in the project
/// folder (running from Godot), loads each into the editor's own load context so it shares the editor's types,
/// and creates its IEditorPlugin types. A plugin that fails to load or initialize is skipped with an error.</summary>
public static class PluginLoader
{
    public static IEnumerable<string> SearchDirs()
    {
        var dirs = new List<string> { Path.Combine(Path.GetDirectoryName(OS.GetExecutablePath()) ?? ".", "plugins") };
        if (OS.HasFeature("editor")) dirs.Insert(0, ProjectSettings.GlobalizePath("res://plugins"));
        return dirs.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists);
    }

    public static List<IEditorPlugin> LoadAll(IEditorHost host)
    {
        var loaded = new List<IEditorPlugin>();
        var context = AssemblyLoadContext.GetLoadContext(typeof(IEditorPlugin).Assembly) ?? AssemblyLoadContext.Default;
        var ids = new HashSet<string>();

        foreach (var root in SearchDirs())
        foreach (var dir in Directory.GetDirectories(root))
        {
            var dll = Path.Combine(dir, Path.GetFileName(dir) + ".dll");
            if (!File.Exists(dll)) continue;
            try
            {
                // The plugin's own dependencies (NuGet packages etc.) are looked up next to it.
                context.Resolving += (ctx, name) =>
                {
                    var dep = Path.Combine(dir, name.Name + ".dll");
                    return File.Exists(dep) ? ctx.LoadFromAssemblyPath(dep) : null;
                };
                var asm = context.LoadFromAssemblyPath(dll);
                foreach (var type in SafeTypes(asm).Where(t => typeof(IEditorPlugin).IsAssignableFrom(t) && !t.IsAbstract && t.IsPublic))
                {
                    var plugin = (IEditorPlugin)Activator.CreateInstance(type);
                    if (!ids.Add(plugin.Id)) { GD.PrintErr($"Plugin id '{plugin.Id}' is loaded twice; skipping {dll}"); continue; }
                    plugin.Initialize(host);
                    loaded.Add(plugin);
                    GD.Print($"Plugin loaded: {plugin.Name} ({plugin.Id}) from {dll}");
                }
            }
            catch (Exception e) { GD.PrintErr($"Plugin {dll} failed: {e}"); }
        }
        return loaded;
    }

    static IEnumerable<Type> SafeTypes(Assembly asm)
    {
        try { return asm.GetTypes(); }
        catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null); }
    }
}
