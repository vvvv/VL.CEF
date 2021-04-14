﻿using System;
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
                    CefRuntime.Load();

                    var cefSettings = new CefSettings();
                    cefSettings.WindowlessRenderingEnabled = true;
                    cefSettings.PackLoadingDisabled = false;
                    cefSettings.MultiThreadedMessageLoop = true;
                    cefSettings.BrowserSubprocessPath = typeof(WebRendererApp).Assembly.Location;
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

        // Taken from https://github.com/cefsharp/CefSharp/blob/6f6ca94ea9bc12b332e6fdb96c5b40b95b50dff5/CefSharp/WebBrowserExtensions.cs
        /// <summary>
        /// Loads html as Data Uri
        /// See https://developer.mozilla.org/en-US/docs/Web/HTTP/Basics_of_HTTP/Data_URIs for details
        /// If base64Encode is false then html will be Uri encoded
        /// </summary>
        public static void LoadString(this CefFrame frame, string html, bool base64Encode = false)
        {
            if (base64Encode)
            {
                var base64EncodedHtml = Convert.ToBase64String(Encoding.UTF8.GetBytes(html));
                frame.LoadUrl("data:text/html;base64," + base64EncodedHtml);
            }
            else
            {
                var uriEncodedHtml = Uri.EscapeDataString(html);
                frame.LoadUrl("data:text/html," + uriEncodedHtml);
            }
        }
    }
}
