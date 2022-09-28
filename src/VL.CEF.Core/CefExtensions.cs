using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Disposables;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering;
using VL.Lib.Basics.Resources;
using Xilium.CefGlue;

namespace VL.CEF
{
    static class CefExtensions
    {
        static int initCount;

        public static IResourceProvider<IDisposable> GetRuntimeProvider()
        {
            return ResourceProvider.New(() =>
            {
                if (Interlocked.Increment(ref initCount) == 1)
                {
                    var browserPath = typeof(WebRendererApp).Assembly.Location;
                    // Check if we're in package (different folder layout)
                    var toolsPath = Path.Combine(Path.GetDirectoryName(browserPath), "..", "..", "renderer", Path.GetFileName(browserPath));
                    if (File.Exists(toolsPath))
                    {
                        browserPath = toolsPath;

                        // Ensure native libs can be found if we're in package format
                        var browserDir = Path.GetDirectoryName(browserPath);
                        var pathVariable = Environment.GetEnvironmentVariable("PATH");
                        if (!pathVariable.Contains(browserDir))
                            Environment.SetEnvironmentVariable("PATH", $"{pathVariable};{browserDir}");
                    }

                    CefRuntime.Load();

                    var cefSettings = new CefSettings();
                    cefSettings.WindowlessRenderingEnabled = true;
                    cefSettings.PackLoadingDisabled = false;
                    cefSettings.MultiThreadedMessageLoop = true;
                    cefSettings.BrowserSubprocessPath = browserPath;
                    cefSettings.CommandLineArgsDisabled = false;
                    cefSettings.IgnoreCertificateErrors = true;
                    //// We do not meet the requirements - see cef_sandbox_win.h
                    //cefSettings.NoSandbox = true;
#if DEBUG
                    cefSettings.LogSeverity = CefLogSeverity.Error;
#else
            cefSettings.LogSeverity = CefLogSeverity.Disable;
#endif

                    var args = Environment.GetCommandLineArgs();
                    var mainArgs = new CefMainArgs(args);
                    CefRuntime.Initialize(mainArgs, cefSettings, new WebRendererApp(), IntPtr.Zero);

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

        public static Vector2 ToScreenSpace(this Vector2 v, in Viewport viewport, in Matrix view, in Matrix projection)
        {
            return viewport.Project((Vector3)v, view, projection, Matrix.Identity).XY();
        }
    }
}
