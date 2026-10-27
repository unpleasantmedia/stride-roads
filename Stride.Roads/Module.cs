using System.Reflection;
using Stride.Core;
using Stride.Core.Reflection;

namespace Stride.Roads;

internal static class Module
{
    /// <summary>
    /// Registers this assembly with the tools, as the engine's plugins do, so the editor lists its components (Road,
    /// Road point, Road network) where components are added.
    /// </summary>
    [ModuleInitializer]
    public static void Initialize()
    {
        AssemblyRegistry.Register(typeof(Module).GetTypeInfo().Assembly, AssemblyCommonCategories.Assets);
    }
}
