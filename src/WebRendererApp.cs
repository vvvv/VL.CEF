using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Disposables;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using VL.Core;
using Xilium.CefGlue;

namespace VL.CEF
{
    public class WebRendererApp : CefApp
    {
        const string CmdAssemblySearchPathSwitch = "assembly-search-path";

        // Main entry point when called by CEF
        [STAThread]
        public static int Main(string[] args)
        {
            CefRuntime.EnableHighDpiSupport();
            CefRuntime.Load();

            var app = new WebRendererApp();
            var mainArgs = new CefMainArgs(args);
            var exitCode = CefRuntime.ExecuteProcess(mainArgs, app, IntPtr.Zero);
            if (exitCode != -1)
                return exitCode;

            CefRuntime.Shutdown();
            return 0;
        }

        private static readonly Dictionary<CefThreadId, CefTaskScheduler> TaskSchedulers = new Dictionary<CefThreadId, CefTaskScheduler>();

        public static TaskScheduler GetTaskScheduler(CefThreadId threadId)
        {
            CefTaskScheduler result;
            if (!TaskSchedulers.TryGetValue(threadId, out result))
            {
                result = new CefTaskScheduler(threadId);
                TaskSchedulers.Add(threadId, result);
            }
            return result;
        }

        class CefTaskScheduler : TaskScheduler
        {
            class TaskWrapper : CefTask
            {
                public readonly CefTaskScheduler Scheduler;
                public readonly Task Task;
                private bool Removed;

                public TaskWrapper(CefTaskScheduler scheduler, Task task)
                {
                    Scheduler = scheduler;
                    Task = task;
                }

                protected override void Execute()
                {
                    Scheduler.TryExecuteTask(Task);
                    lock (Scheduler.Tasks)
                        Removed = Scheduler.Tasks.Remove(this);
                }

                protected override void Dispose(bool disposing)
                {
                    if (!Removed)
                    {
                        lock (Scheduler.Tasks)
                            Scheduler.Tasks.Remove(this);
                    }
                    base.Dispose(disposing);
                }
            }

            private readonly CefThreadId ThreadId;
            private readonly LinkedList<TaskWrapper> Tasks = new LinkedList<TaskWrapper>();

            public CefTaskScheduler(CefThreadId threadId)
            {
                ThreadId = threadId;
            }

            protected override IEnumerable<Task> GetScheduledTasks()
            {
                lock (Tasks)
                {
                    foreach (var taskWrapper in Tasks)
                        yield return taskWrapper.Task;
                }
            }

            protected override void QueueTask(Task task)
            {
                lock (Tasks)
                {
                    var t = new TaskWrapper(this, task);
                    if (CefRuntime.PostTask(ThreadId, t))
                        Tasks.AddLast(t);
                }
            }

            protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
            {
                return false;
            }
        }

        class BrowserProcessHandler : CefBrowserProcessHandler
        {
            protected override void OnRenderProcessThreadCreated(CefListValue extraInfo)
            {
                base.OnRenderProcessThreadCreated(extraInfo);
            }

            protected override void OnBeforeChildProcessLaunch(CefCommandLine commandLine)
            {
                if (!commandLine.HasSwitch(CmdAssemblySearchPathSwitch))
                {
                    var searchPath = Path.GetDirectoryName(typeof(IVLObject).Assembly.Location);
                    commandLine.AppendSwitch(CmdAssemblySearchPathSwitch, searchPath);
                }
                base.OnBeforeChildProcessLaunch(commandLine);
            }
        }

        class RenderProcessHandler : CefRenderProcessHandler
        {
            class CustomCallbackHandler : CefV8Handler
            {
                public const string ReportDocumentSize = "vvvvReportDocumentSize";

                private readonly CefBrowser Browser;
                private readonly CefFrame Frame;
                public CustomCallbackHandler(CefBrowser browser, CefFrame frame)
                {
                    Browser = browser;
                    Frame = frame;
                }

                protected override bool Execute(string name, CefV8Value obj, CefV8Value[] arguments, out CefV8Value returnValue, out string exception)
                {
                    var message = default(CefProcessMessage);
                    try
                    {
                        CefListValue args;
                        switch (name)
                        {
                            case ReportDocumentSize:
                                message = CefProcessMessage.Create("document-size-response");
                                message.SetFrameIdentifier(Frame.Identifier);
                                using (args = message.Arguments)
                                {
                                    args.SetInt(2, arguments[0].GetIntValue());
                                    args.SetInt(3, arguments[1].GetIntValue());
                                    Frame.SendProcessMessage(CefProcessId.Browser, message);
                                }
                                returnValue = null;
                                exception = null;
                                return true;
                            default:
                                returnValue = null;
                                exception = null;
                                return false;
                        }
                    }
                    finally
                    {
                        message?.Dispose();
                    }
                }
            }

            protected override void OnRenderThreadCreated(CefListValue extraInfo)
            {
                base.OnRenderThreadCreated(extraInfo);
            }

            protected override bool OnProcessMessageReceived(CefBrowser browser, CefFrame frame, CefProcessId sourceProcess, CefProcessMessage message)
            {
                long identifier;
                switch (message.Name)
                {
                    case "document-size-request":
                        HandleDocumentSizeRequest(browser, sourceProcess, frame);
                        return true;
                    default:
                        return base.OnProcessMessageReceived(browser, frame, sourceProcess, message);
                }
            }

            protected override void OnContextCreated(CefBrowser browser, CefFrame frame, CefV8Context context)
            {
                if (frame.IsMain)
                {
                    // Retrieve the context's window object and install the "vvvvReportDocumentSize" function
                    // used to tell the node about the document size as well as the "vvvvSend" function
                    // used to tell the node about variables computed inside the frame.
                    using (var window = context.GetGlobal())
                    {
                        var handler = new CustomCallbackHandler(browser, frame);
                        var reportDocumentSizeFunc = CefV8Value.CreateFunction(CustomCallbackHandler.ReportDocumentSize, handler);
                        window.SetValue(CustomCallbackHandler.ReportDocumentSize, reportDocumentSizeFunc, CefV8PropertyAttribute.None);
                    }
                }
                base.OnContextCreated(browser, frame, context);
            }

            private void HandleDocumentSizeRequest(CefBrowser browser, CefProcessId sourceProcess, CefFrame frame)
            {
                var scheduler = GetTaskScheduler(CefThreadId.Renderer);
                Task.Factory.StartNew(
                    () =>
                    {
                        var js = new StringBuilder();
                        js.AppendLine("var body = document.body;");
                        js.AppendLine("var width = body.scrollWidth;");
                        js.AppendLine("var height = body.scrollHeight;");
                        js.AppendLine(string.Format("window.{0}(width, height);", CustomCallbackHandler.ReportDocumentSize));
                        frame.ExecuteJavaScript(js.ToString(), string.Empty, 0);
                    },
                    CancellationToken.None,
                    TaskCreationOptions.None,
                    scheduler);
            }
        }

        //protected override void OnRegisterCustomSchemes(CefSchemeRegistrar registrar)
        //{
        //    registrar.AddCustomScheme(SchemeHandlerFactory.SCHEME_NAME, CefSchemeOptions.Local | CefSchemeOptions.CspBypassing);
        //    base.OnRegisterCustomSchemes(registrar);
        //}

        protected override void OnBeforeCommandLineProcessing(string processType, CefCommandLine commandLine)
        {
            if (commandLine.HasSwitch(CmdAssemblySearchPathSwitch))
            {
                var searchPath = commandLine.GetSwitchValue(CmdAssemblySearchPathSwitch);
                AppDomain.CurrentDomain.AssemblyResolve += (sender, resolveEventArgs) =>
                {
                    var assemblyName = new AssemblyName(resolveEventArgs.Name);
                    var assemblyFileName = assemblyName.Name + ".dll";
                    var assemblyLocation = Path.Combine(searchPath, assemblyFileName);
                    if (File.Exists(assemblyLocation))
                    {
                        return Assembly.Load(AssemblyName.GetAssemblyName(assemblyLocation));
                    }
                    return null;
                };

            }
            if (string.IsNullOrEmpty(processType))
            {
                commandLine.AppendSwitch("disable-smooth-scrolling");
                commandLine.AppendSwitch("enable-system-flash");
            }
            base.OnBeforeCommandLineProcessing(processType, commandLine);
        }

        protected override CefBrowserProcessHandler GetBrowserProcessHandler()
        {
            return new BrowserProcessHandler();
        }

        protected override CefRenderProcessHandler GetRenderProcessHandler()
        {
            return new RenderProcessHandler();
        }
    }
}
