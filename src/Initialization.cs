using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using System.Linq;
using VL.Core;
using VL.Core.CompilerServices;
using VL.Lib.Basics.Resources;

[assembly: AssemblyInitializer(typeof(VL.CEF.Initialization))]

namespace VL.CEF
{
    public sealed class Initialization : AssemblyInitializer<Initialization>
    {
        protected override void RegisterServices(IVLFactory factory)
        {
            if (!factory.HasService<NodeContext, IResourceProvider<Device>>())
            {
                factory.RegisterService<NodeContext, IResourceProvider<Device>>(nodeContext =>
                {
                    // One per entry point
                    var key = nodeContext.Path.Stack.Last();
                    return ResourceProvider.NewPooled(key, _ => new Device(DriverType.Hardware, DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport));
                });
            }
        }
    }
}
