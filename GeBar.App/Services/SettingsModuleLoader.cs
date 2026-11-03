using GeBar.UI;
using System.Reflection;
using System.Runtime.Loader;

namespace GeBar.App;

internal sealed class SettingsModuleLoader : IDisposable
{
    private ModuleContext? _context;
    public ISettingsModule? Module { get; private set; }

    public void Load(string path, FileLogger logger)
    {
        if (!File.Exists(path)) return;
        try
        {
            _context = new ModuleContext();
            using var stream = new MemoryStream(File.ReadAllBytes(path));
            Assembly assembly = _context.LoadFromStream(stream);
            if (assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(attribute => attribute.Key == "ModuleApi")?.Value != "1")
                throw new NotSupportedException("Unsupported settings module API.");
            Type type = assembly.GetExportedTypes().Single(type => typeof(ISettingsModule).IsAssignableFrom(type) && !type.IsAbstract);
            Module = (ISettingsModule?)Activator.CreateInstance(type, logger) ?? throw new InvalidOperationException("The settings module could not be created.");
            logger.Info($"Settings module loaded: {Module.Name} {Module.Version}");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or BadImageFormatException or ReflectionTypeLoadException
            or TypeLoadException or MissingMethodException or TargetInvocationException or InvalidOperationException or NotSupportedException)
        {
            logger.Error("Cannot load settings module", error);
            Dispose();
        }
    }

    public void Dispose()
    {
        Module?.Dispose();
        Module = null;
        _context?.Unload();
        _context = null;
    }

    private sealed class ModuleContext() : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name) => name.Name switch
        {
            "GeBar.Core" => typeof(GeBar.Core.HidDeviceProfile).Assembly,
            "GeBar.UI" => typeof(ISettingsModule).Assembly,
            "GeBar.Hid" => typeof(GeBar.Hid.HidDeviceScanner).Assembly,
            "HidSharp" => typeof(HidSharp.HidDevice).Assembly,
            _ => null
        };
    }
}
