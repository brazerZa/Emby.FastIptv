// Loads the built plugin against a real Emby server's system/ directory and forces every type to
// load. A missing interface member (e.g. ILiveStream.AddConsumer on 4.10, or the ConsumerCount
// setter on 4.9) compiles fine but throws TypeLoadException here — the same failure Emby hits
// when it loads the plugin.
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: LoadCheck <plugin.dll> <emby system dir>");
    return 2;
}

var plugin = Path.GetFullPath(args[0]);
var embyDir = Path.GetFullPath(args[1]);
var ctx = new AssemblyLoadContext("loadcheck");
ctx.Resolving += (c, name) =>
{
    var path = Path.Combine(embyDir, name.Name + ".dll");
    return File.Exists(path) ? c.LoadFromAssemblyPath(path) : null;
};

try
{
    var asm = ctx.LoadFromAssemblyPath(plugin);
    var types = asm.GetTypes();
    foreach (var t in types)
        RuntimeHelpers.RunClassConstructor(t.TypeHandle);
    var controller = ctx.Assemblies.FirstOrDefault(a => a.GetName().Name == "MediaBrowser.Controller");
    Console.WriteLine($"OK: {types.Length} types loaded against MediaBrowser.Controller {controller?.GetName().Version}");
    return 0;
}
catch (ReflectionTypeLoadException e)
{
    foreach (var le in e.LoaderExceptions)
        Console.WriteLine("FAIL: " + le?.Message);
    return 1;
}
catch (Exception e)
{
    Console.WriteLine("FAIL: " + e.Message);
    return 1;
}
