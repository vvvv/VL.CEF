#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Disposables;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Stride.Core.Mathematics;
using VL.Lib.Basics.Resources;
using Xilium.CefGlue;

namespace VL.CEF
{
    public static class CefExtensions
    {
        static string? resolvedRendererPath;
        static int initCount;

        static CefExtensions()
        {
            foreach (var rendererExe in GetPotentialRendererExeLocations())
            {
                if (File.Exists(rendererExe))
                {
                    resolvedRendererPath = Path.GetFullPath(rendererExe);
                    break;
                }
            }

            static IEnumerable<string> GetPotentialRendererExeLocations()
            {
                // Exported app?
                var processDir = Path.GetDirectoryName(Environment.ProcessPath);
                if (processDir != null)
                    yield return Path.Combine(processDir, "renderer", "VL.CEF.Renderer.exe");

                var assemblyDir = Path.GetDirectoryName(typeof(CefExtensions).Assembly.Location);
                if (assemblyDir != null)
                {
                    // Source package?
                    yield return Path.Combine(assemblyDir, "..", "..", "..", "VL.CEF.Renderer", "bin", "VL.CEF.Renderer.exe");
                    // Installed package?
                    yield return Path.Combine(assemblyDir, "..", "..", "renderer", "VL.CEF.Renderer.exe");

                }
            }
        }

        public static IResourceProvider<IDisposable> GetRuntimeProvider()
        {
            return ResourceProvider.New(() =>
            {
                if (resolvedRendererPath is null)
                    throw new FileNotFoundException("Can't find VL.CEF.Renderer.exe");

                if (Interlocked.Increment(ref initCount) == 1)
                {
                    CefRuntime.Load(Path.GetDirectoryName(resolvedRendererPath));

                    var cefSettings = new CefSettings();
                    cefSettings.WindowlessRenderingEnabled = true;
                    cefSettings.MultiThreadedMessageLoop = true;
                    cefSettings.BrowserSubprocessPath = resolvedRendererPath;
                    cefSettings.CommandLineArgsDisabled = false;
                    //// We do not meet the requirements - see cef_sandbox_win.h
                    //cefSettings.NoSandbox = true;
#if DEBUG
                    cefSettings.LogSeverity = CefLogSeverity.Verbose;
#else
            cefSettings.LogSeverity = CefLogSeverity.Disable;
#endif

                    var args = Environment.GetCommandLineArgs();
                    var mainArgs = new CefMainArgs(args);
                    CefRuntime.Initialize(mainArgs, cefSettings, application: new App(), windowsSandboxInfo: default);

                    var schemeName = "cef";
                    if (!CefRuntime.RegisterSchemeHandlerFactory(SchemeHandlerFactory.SCHEME_NAME, null, new SchemeHandlerFactory()))
                        throw new Exception(string.Format("Couldn't register custom scheme factory for '{0}'.", schemeName));

                    //return Disposable.Create(() => CefRuntime.Shutdown());
                }
                else
                {
                }
                return Disposable.Empty;
            }).ShareInParallel();
        }

        public static Vector2 DeviceToLogical(this Vector2 v, float scaleFactor)
        {
            return v / scaleFactor;
        }

        public static Vector2 LogicalToDevice(this Vector2 v, float scaleFactor)
        {
            return v * scaleFactor;
        }

        //public static Vector2 ToScreenSpace(this Vector2 v, in Viewport viewport, in Matrix view, in Matrix projection)
        //{
        //    return viewport.Project((Vector3)v, view, projection, Matrix.Identity).XY();
        //}

        class App : CefApp
        {
            protected override void OnBeforeCommandLineProcessing(string processType, CefCommandLine commandLine)
            {
                // Enable auto play (https://github.com/vvvv/VL.CEF/issues/12)
                commandLine.AppendSwitch("autoplay-policy", "no-user-gesture-required");
                //enable camera
                commandLine.AppendSwitch("enable-media-stream", "true");

                base.OnBeforeCommandLineProcessing(processType, commandLine);
            }
        }
    }
}
