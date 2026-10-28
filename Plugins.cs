using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Paddock
{
    /// <summary>What a plugin can reach. Grow this as your own capabilities need it.</summary>
    public interface IPaddockHost
    {
        FenceConfig AddDesktopFence(string title, params RuleDef[] rules);
        FenceConfig AddPortal(string title, string folder);
        string ActiveProfile { get; }
        void SwitchProfile(string name);
        void ToggleQuickHide();
        void RefreshAll();
    }

    /// <summary>Implement this in a DLL dropped into %APPDATA%\Paddock\plugins.</summary>
    public interface IPaddockPlugin
    {
        string Name { get; }
        void Initialize(IPaddockHost host);
    }

    public static class PluginLoader
    {
        public static void LoadAll(IPaddockHost host)
        {
            var dir = Path.Combine(ConfigStore.Dir, "plugins");
            Directory.CreateDirectory(dir);
            foreach (var dll in Directory.GetFiles(dir, "*.dll"))
            {
                try
                {
                    var asm = Assembly.LoadFrom(dll);
                    foreach (var t in asm.GetTypes().Where(t => typeof(IPaddockPlugin).IsAssignableFrom(t) && !t.IsAbstract))
                        ((IPaddockPlugin)Activator.CreateInstance(t)!).Initialize(host);
                }
                catch (Exception ex)
                {
                    File.AppendAllText(Path.Combine(ConfigStore.Dir, "plugin-errors.log"),
                        $"{DateTime.Now:s} {Path.GetFileName(dll)}: {ex.Message}{Environment.NewLine}");
                }
            }
        }
    }
}
