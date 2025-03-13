using SharpDX.Direct3D11;
using Stride.Core.IO;
using Stride.Engine;
using Stride.Graphics;
using Stride.Input;
using Stride.Rendering;
using Stride.Rendering.Images;
using Stride.Shaders.Compiler;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reactive.Disposables;
using System.Text;
using VL.Core;
using VL.Lib.Basics.Resources;
using VL.Stride;
using VL.Stride.Input;
using Xilium.CefGlue;
using System.Reflection;
using SharpDX.DXGI;
using Device1 = SharpDX.Direct3D11.Device1;
using Device = SharpDX.Direct3D11.Device;
using System.Collections.Concurrent;
using VL.Core.Utils;
using SharpDX;
using DataBox = Stride.Graphics.DataBox;
using System.Threading;
using System.Diagnostics;

namespace VL.CEF
{
    public sealed partial class StrideRenderHandler : RendererBase
    {
        struct CopyOperation(SharpDX.DXGI.Resource Resource, uint FenceValue)
        {
            public SharpDX.DXGI.Resource Resource = Resource;
            public uint FenceValue = FenceValue;
        }

        private readonly object syncRoot = new object();
        private readonly IResourceHandle<Game> gameHandle;
        private readonly WebBrowser browser;
        private readonly Device5 producerDevice;
        private readonly DeviceContext4 producerContext;
        private readonly Fence copyFence;
        private readonly nint copyFenceHandle;
        private readonly Fence copyFenceStride;
        private readonly AutoResetEvent m = new(false);
        private readonly AutoResetEvent m2 = new(false);
        private uint copyFenceValue;
        private readonly BlockingCollection<CopyOperation> queue = new(boundedCapacity: 2);
        private Texture surface;
        private bool needsConversion;

        public StrideRenderHandler(WebBrowser browser)
        {
            this.browser = browser;

            using var producerDevice = new Device(SharpDX.Direct3D.DriverType.Hardware);
            this.producerDevice = producerDevice.QueryInterface<Device5>();
            this.producerContext = producerDevice.ImmediateContext.QueryInterface<DeviceContext4>();
            this.copyFence = new Fence(this.producerDevice, copyFenceValue, FenceFlags.Shared);
            this.copyFenceHandle = copyFence.CreateSharedHandle(default, 0x10000000, null);

            gameHandle = ServiceRegistry.Current.GetGameHandle();

            var renderContext = RenderContext.GetShared(gameHandle.Resource.Services);
            Initialize(renderContext);

            {
                var strideDevice = SharpDXInterop.GetNativeDevice(renderContext.GraphicsDevice) as Device;
                using var strideDevice5 = strideDevice.QueryInterface<Device5>();
                this.copyFenceStride = strideDevice5.OpenSharedFence(copyFenceHandle);
            }

            browser.Paint += OnPaint;
            browser.AcceleratedPaint += OnAcceleratedPaint;
        }

        protected override void Destroy()
        {
            browser.Paint -= OnPaint;
            browser.AcceleratedPaint -= OnAcceleratedPaint;

            surface?.Dispose();
            base.Destroy();
            gameHandle.Dispose();
        }

        void OnPaint(CefPaintElementType type, CefRectangle[] cefRects, IntPtr buffer, int width, int height)
        {
            var data = new DataBox(buffer, width * 4, width * height * 4);
            var format = PixelFormat.B8G8R8A8_UNorm;
            if (GraphicsDevice.ColorSpace == ColorSpace.Linear)
                format = PixelFormat.B8G8R8A8_UNorm_SRgb;

            var surface = Texture.New2D(GraphicsDevice, width, height, 1, format, new[] { data }, usage: GraphicsResourceUsage.Immutable);
            lock (syncRoot)
            {
                this.surface?.Dispose();
                this.surface = surface;
            }

            needsConversion = false;
        }

        void OnAcceleratedPaint(CefPaintElementType type, CefRectangle[] dirtyRects, CefAcceleratedPaintInfo info)
        {
            try
            {
                var sharedHandle = info.SharedTextureHandle;
                if (sharedHandle == IntPtr.Zero)
                    return;

                using var texture = producerDevice.OpenSharedResource1<Texture2D>(sharedHandle);
                if (texture is null)
                    return;

                using var targetTexture = new Texture2D(producerDevice, new()
                {
                    ArraySize = texture.Description.ArraySize,
                    BindFlags = BindFlags.ShaderResource,
                    CpuAccessFlags = CpuAccessFlags.None,
                    Format = texture.Description.Format,
                    Height = texture.Description.Height,
                    Width = texture.Description.Width,
                    MipLevels = texture.Description.MipLevels,
                    OptionFlags = ResourceOptionFlags.Shared,
                    SampleDescription = new(1, 0),
                    Usage = ResourceUsage.Default
                });
                producerContext.CopyResource(texture, targetTexture);
                var fenceValue = ++copyFenceValue;
                producerContext.Signal(copyFence, fenceValue);
                copyFence.SetEventOnCompletion(fenceValue, m2.Handle);
                //producerContext.Flush();
                var resource = targetTexture.QueryInterface<SharpDX.DXGI.Resource>();
                if (queue.TryAddSafe(new(resource, fenceValue), millisecondsTimeout: 100))
                    m2.WaitOne();
                else
                    resource.Dispose();

                needsConversion = true;
            }
            catch (Exception e)
            {
                RuntimeGraph.ReportException(e);
            }
        }

        protected override void DrawCore(RenderDrawContext context)
        {
            // Subscribe to input events - in case we have many sinks we assume that there's only one input source active
            var inputSource = context.RenderContext.Tags.Get(InputExtensions.WindowInputSource);
            if (inputSource != lastInputSource)
            {
                lastInputSource = inputSource;
                inputSubscription.Disposable = SubscribeToInputSource(inputSource, context);
            }

            var commandList = context.CommandList;

            // Ensure we render in the proper size
            browser.Size = commandList.Viewport.Size;

            if (queue.TryTake(out var copyOperation))
            {
                surface?.Dispose();

                var device = (Device)SharpDXInterop.GetNativeDevice(GraphicsDevice);
                //copyFenceStride.SetEventOnCompletion(copyOperation.FenceValue, m.Handle);
                //m.WaitOne();
                var texture2D = device.OpenSharedResource<Texture2D>(copyOperation.Resource.SharedHandle);
                copyOperation.Resource.Dispose();
                surface = SharpDXInterop.CreateTextureFromNative(GraphicsDevice, texture2D, takeOwnership: false);
                // Undo the ref count increment of an internal QueryInterface call
                (texture2D as IUnknown).Release();
            }
            else
            {
                Trace.TraceInformation("No copy operation");
            }

            lock (syncRoot)
            {
                var renderTarget = commandList.RenderTarget;
                if (surface != null && renderTarget != null)
                {
                    if (needsConversion)
                    {
                        shader.SetInput(surface);
                        shader.SetOutput(renderTarget);
                        shader.Draw(context);
                    }
                    else
                    {
                        context.GraphicsContext.DrawTexture(surface, BlendStates.AlphaBlend);
                    }
                }
            }
        }
        private readonly SerialDisposable inputSubscription = new SerialDisposable();
        private IInputSource lastInputSource;

        protected override void InitializeCore()
        {
            base.InitializeCore();

            // Setup our own effect compiler
            using var fileProvider = new InMemoryFileProvider(EffectSystem.FileProvider);
            fileProvider.Register("shaders/WebRendererShader.sdsl", GetShaderSource());
            using var compiler = new EffectCompiler(fileProvider);
            compiler.SourceDirectories.Add("shaders");

            // Switch effect compiler
            var currentCompiler = EffectSystem.Compiler;
            EffectSystem.Compiler = compiler;
            try
            {
                shader = ToLoadAndUnload(new ImageEffectShader("WebRendererShader"));
                // The incoming texture uses premultiplied alpha
                shader.BlendState = BlendStates.AlphaBlend;
                shader.EffectInstance.UpdateEffect(GraphicsDevice);
            }
            finally
            {
                EffectSystem.Compiler = currentCompiler;
            }

            string GetShaderSource()
            {
                return @"
shader WebRendererShader : ImageEffectShader
{
stage override float4 Shading()
{
    float4 color = Texture0.Sample(PointSampler, streams.TexCoord);

    // The render pipeline expects a linear color space
    return float4(ToLinear(color.r), ToLinear(color.g), ToLinear(color.b), color.a);
}

// There're faster approximations, see http://chilliant.blogspot.com/2012/08/srgb-approximations-for-hlsl.html
float ToLinear(float C_srgb)
{
    if (C_srgb <= 0.04045)
        return C_srgb / 12.92;
    else
        return pow(abs((C_srgb + 0.055) / 1.055), 2.4);
}
};
";
            }
        }
        ImageEffectShader shader;

        // See https://github.com/vvvv/VL.Devices.DeckLink/blob/master/src/VL.Devices.DeckLink/VideoIn.cs
        class InMemoryFileProvider : VirtualFileProviderBase, IDisposable
        {
            readonly Dictionary<string, byte[]> inMemory = new Dictionary<string, byte[]>();
            readonly HashSet<string> tempFiles = new HashSet<string>();
            readonly IVirtualFileProvider virtualFileProvider;

            public InMemoryFileProvider(IVirtualFileProvider baseFileProvider) : base(baseFileProvider.RootPath)
            {
                virtualFileProvider = baseFileProvider;
            }

            public void Register(string url, string content)
            {
                inMemory[url] = Encoding.Default.GetBytes(content);

                // The effect system assumes there is a /path - doesn't do a FileExists check first :/
                var path = GetTempFileName();
                tempFiles.Add(path);
                File.WriteAllText(path, content);
                inMemory[$"{url}/path"] = Encoding.Default.GetBytes(path);
            }

            // Don't use Path.GetTempFileName() where we can run into overflow issue (had it during development)
            // https://stackoverflow.com/questions/18350699/system-io-ioexception-the-file-exists-when-using-system-io-path-gettempfilena
            private string GetTempFileName()
            {
                return Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            }

            public new void Dispose()
            {
                try
                {
                    foreach (var f in tempFiles)
                        File.Delete(f);
                }
                catch (Exception)
                {
                    // Ignore
                }
                base.Dispose();
            }

            public override bool FileExists(string url)
            {
                if (inMemory.ContainsKey(url))
                    return true;

                return virtualFileProvider.FileExists(url);
            }

            public override Stream OpenStream(string url, VirtualFileMode mode, VirtualFileAccess access, VirtualFileShare share = VirtualFileShare.Read, StreamFlags streamFlags = StreamFlags.None)
            {
                if (inMemory.TryGetValue(url, out var bytes))
                {
                    return new MemoryStream(bytes);
                }

                return virtualFileProvider.OpenStream(url, mode, access, share, streamFlags);
            }
        }
    }
}
