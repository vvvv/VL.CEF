using SharpDX.Direct3D11;
using Stride.Core.IO;
using Stride.Core.Mathematics;
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
using VL.Stride.Input;
using Xilium.CefGlue;

namespace VL.CEF
{
    public sealed partial class StrideRenderHandler : RendererBase, IRenderHandler
    {
        private readonly object syncRoot = new object();
        private readonly IResourceHandle<Game> gameHandle;
        private WebRenderer webRenderer;

        public StrideRenderHandler(NodeContext nodeContext)
        {
            var gameProvider = nodeContext.Factory.CreateService<IResourceProvider<Game>>(nodeContext);
            gameHandle = gameProvider?.GetHandle() ?? throw new ArgumentNullException(nameof(nodeContext), "The game service wasn't found");
            var renderContext = RenderContext.GetShared(gameHandle.Resource.Services);
            Initialize(renderContext);
        }

        public bool UseAcceleratedPaint => SharpDXInterop.GetNativeDevice(GraphicsDevice) is Device;

        public void Initialize(WebRenderer webRenderer)
        {
            this.webRenderer = webRenderer;
        }

        protected override void Destroy()
        {
            surface?.Dispose();
            base.Destroy();
            gameHandle.Dispose();
        }

        public void OnPaint(CefPaintElementType type, CefRectangle[] cefRects, IntPtr buffer, int width, int height)
        {
            // TODO: Implement me!
        }

        public void OnAcceleratedPaint(CefPaintElementType type, CefRectangle[] dirtyRects, IntPtr sharedHandle)
        {
            var d3dDevice = SharpDXInterop.GetNativeDevice(GraphicsDevice) as Device;
            if (d3dDevice is null)
                return;

            var d3dTexture = d3dDevice.OpenSharedResource<Texture2D>(sharedHandle);
            if (d3dTexture is null)
                return;

            var strideTexture = SharpDXInterop.CreateTextureFromNative(GraphicsDevice, d3dTexture, takeOwnership: true);
            lock (syncRoot)
            {
                surface?.Dispose();
                surface = strideTexture;
            }
        }
        Texture surface;

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
            webRenderer.Size = commandList.Viewport.Size;

            lock (syncRoot)
            {
                var renderTarget = commandList.RenderTarget;
                if (surface != null && renderTarget != null)
                {
                    shader.SetInput(surface);
                    shader.SetOutput(renderTarget);
                    shader.Draw(context);
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
    float2 samplePosition = float2(streams.TexCoord.x, 1 - streams.TexCoord.y);
    float4 color = Texture0.Sample(PointSampler, samplePosition);

    // The render pipeline expects a linear color space
    return float4(ToLinear(color.r), ToLinear(color.g), ToLinear(color.b), color.a);
}

// There're faster approximations, see http://chilliant.blogspot.com/2012/08/srgb-approximations-for-hlsl.html
float ToLinear(float C_srgb)
{
    if (C_srgb <= 0.04045)
        return C_srgb / 12.92;
    else
        return pow((C_srgb + 0.055) / 1.055, 2.4);
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
