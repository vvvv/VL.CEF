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
        static string resolvedRendererPath;
        static int initCount;

        static CefExtensions()
        {
            const string rendererAssemblyName = "VL.CEF.Renderer";
            const string rendererExeName = $"{rendererAssemblyName}.exe";
            const string renderer = "renderer";

            var thisDirectory = Path.GetDirectoryName(typeof(CefExtensions).Assembly.Location);
            // In standalone the webrenderer is in the renderer subfolder
            var standalonePath = Path.Combine(thisDirectory, renderer, rendererExeName);
            if (File.Exists(standalonePath))
            {
                resolvedRendererPath = standalonePath;
            }
            else
            {
                // Check if we're in package (different folder layout)
                var rendererExeAssembly = Assembly.Load(rendererAssemblyName);
                if (rendererExeAssembly is null)
                    throw new FileNotFoundException($"Couldn't find {rendererAssemblyName}");

                var packagePath = Path.Combine(Path.GetDirectoryName(rendererExeAssembly.Location), "..", "..", renderer, rendererExeName);
                if (File.Exists(packagePath))
                {
                    resolvedRendererPath = packagePath;
                }
            }

            if (resolvedRendererPath is null)
                throw new FileNotFoundException("Can't find VL.CEF.Renderer.exe");

            // Ensure native libs can be found
            var browserDir = Path.GetDirectoryName(resolvedRendererPath);
            var pathVariable = Environment.GetEnvironmentVariable("PATH");
            if (!pathVariable.Contains(browserDir))
                Environment.SetEnvironmentVariable("PATH", $"{pathVariable};{browserDir}");
        }

        public static IResourceProvider<IDisposable> GetRuntimeProvider()
        {
            return ResourceProvider.New(() =>
            {
                if (Interlocked.Increment(ref initCount) == 1)
                {
                    CefRuntime.Load();

                    var cefSettings = new CefSettings();
                    cefSettings.WindowlessRenderingEnabled = true;
                    cefSettings.PackLoadingDisabled = false;
                    cefSettings.MultiThreadedMessageLoop = true;
                    cefSettings.BrowserSubprocessPath = resolvedRendererPath;
                    cefSettings.CommandLineArgsDisabled = false;
                    //// We do not meet the requirements - see cef_sandbox_win.h
                    //cefSettings.NoSandbox = true;
#if DEBUG
                    cefSettings.LogSeverity = CefLogSeverity.Error;
#else
            cefSettings.LogSeverity = CefLogSeverity.Disable;
#endif

                    var args = Environment.GetCommandLineArgs();
                    var mainArgs = new CefMainArgs(args);
                    CefRuntime.Initialize(mainArgs, cefSettings, application: null, windowsSandboxInfo: default);

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
    }
}
