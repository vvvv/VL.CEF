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
                    CefRuntime.Load();

                    var cefSettings = new CefSettings();
                    cefSettings.WindowlessRenderingEnabled = true;
                    cefSettings.PackLoadingDisabled = false;
                    cefSettings.MultiThreadedMessageLoop = true;
                    cefSettings.BrowserSubprocessPath = Assembly.GetExecutingAssembly().Location;
                    cefSettings.CommandLineArgsDisabled = false;
                    cefSettings.IgnoreCertificateErrors = true;
                    //// We do not meet the requirements - see cef_sandbox_win.h
                    //cefSettings.NoSandbox = true;
#if DEBUG
                    cefSettings.LogSeverity = CefLogSeverity.Error;
#else
            cefSettings.LogSeverity = CefLogSeverity.Disable;
#endif

                    // Check if we're in package (different folder layout)
                    var filename = Path.GetFileName(cefSettings.BrowserSubprocessPath);
                    var toolsPath = Path.Combine(Path.GetDirectoryName(cefSettings.BrowserSubprocessPath), "..", "..", "runtimes", "win-x64", "native", filename);
                    if (File.Exists(toolsPath))
                    {
                        cefSettings.BrowserSubprocessPath = toolsPath;
                    }

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

        public static void SetFrameIdentifier(this CefProcessMessage message, long identifier, int index = 0)
        {
            int low, high;
            MakeInt(identifier, out low, out high);
            var arguments = message.Arguments;
            arguments.SetInt(index, low);
            arguments.SetInt(index + 1, high);
        }

        public static long GetFrameIdentifier(this CefProcessMessage message, int index = 0)
        {
            var arguments = message.Arguments;
            var low = arguments.GetInt(index);
            var high = arguments.GetInt(index + 1);
            return MakeLong(low, high);
        }

        private static long MakeLong(int low, int high)
        {
            return (long)(high << 32) | (long)(low);
        }

        private static void MakeInt(long value, out int low, out int high)
        {
            low = (int)(value);
            high = (int)(value >> 32);
        }

        public static Vector2 DeviceToLogical(this Vector2 v, float scaleFactor)
        {
            return new Vector2(v.X / scaleFactor, v.Y / scaleFactor);
        }

        public static Vector2 LogicalToDevice(this Vector2 v, float scaleFactor)
        {
            return new Vector2(v.X * scaleFactor, v.Y * scaleFactor);
        }
    }
}
