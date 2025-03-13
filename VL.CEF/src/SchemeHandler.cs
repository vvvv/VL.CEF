using System;
using System.Text;
using Xilium.CefGlue;
using System.IO;
using System.Collections.Specialized;
using System.Collections.Generic;

namespace VL.CEF
{
    internal sealed class SchemeHandler : CefResourceHandler
    {

        private Stream stream;

        private long responseLength;
        private int status;
        private string statusText;
        private string mimeType;


        private void Close()
        {
            if (this.stream != null)
            {
                this.stream.Dispose();
                this.stream = null;
            }
            this.responseLength = 0;
            this.status = 0;
            this.statusText = null;
            this.mimeType = null;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Close();
            base.Dispose(disposing);
        }

        protected override bool Open(CefRequest request, out bool handleRequest, CefCallback callback)
        {
            // Backwards compatibility. ProcessRequest will be called.
            callback.Dispose();
            handleRequest = false;
            return false;
        }

        [Obsolete]
        protected override bool ProcessRequest(CefRequest request, CefCallback callback)
        {
            var urlString = request.Url;

            string errorMessage = null;
            int errorStatus = 0;
            string errorStatusText = null;

            try
            {
                var uri = new Uri(urlString);
                var path = uri.Host + uri.AbsolutePath; // ignore host

                switch (uri.AbsolutePath)
                {
                    case "plugins":
                        // TODO: The plugin visitor API is now gone and should probably
                        // be replaced with an ExtensionHandler that tracks
                        // added extensions and keeps a list of them.  
                        // Old code below for reference:

                        // var visitor = new WebPluginVisitor();
                        // CefRuntime.VisitWebPluginInfo(visitor);
                        // var s = new StringBuilder();
                        // foreach (var plugin in visitor.Plugins)
                        // {
                        //     s.AppendLine(string.Format("Name: {0}", plugin.Name));
                        //     s.AppendLine(string.Format("Description: {0}", plugin.Description));
                        //     s.AppendLine(string.Format("Version: {0}", plugin.Version));
                        //     s.AppendLine(string.Format("Path: {0}", plugin.Path));
                        //     s.AppendLine();
                        // }
                        // this.stream = new MemoryStream(Encoding.UTF8.GetBytes(s.ToString()), false);                     
                        break;
                    default:
                        throw new Exception();
                }

                if (this.stream != null)
                {
                    // found
                    this.responseLength = -1;
                    this.status = 200;
                    this.statusText = "OK";
                    this.mimeType = "text/plain";
                    callback.Continue();
                    return true;
                }
            }
            catch (Exception ex)
            {
                errorStatus = 500;
                errorStatusText = "Internal Error";
                errorMessage = "<!doctype html><html><body><h1>Internal Error!</h1><pre>" + ex.ToString() + "</pre></body></html>";
            }

            // not found or error while processing request
            errorMessage = errorMessage ?? "<!doctype html><html><body><h1>Not Found!</h1><p>The requested url [" + urlString + "] was not found!</p></body></html>";
            var bytes = Encoding.UTF8.GetBytes(errorMessage);
            this.stream = new MemoryStream(bytes, false);

            this.responseLength = -1;
            this.status = errorStatus != 0 ? errorStatus : 404;
            this.statusText = errorStatusText ?? "Not Found";
            this.mimeType = "text/html";
            callback.Continue();
            return true;
        }

        protected override void Cancel()
        {
            this.Close();
        }

        protected override bool Skip(long bytesToSkip, out long bytesSkipped, CefResourceSkipCallback callback)
        {
            bytesSkipped = (long)CefErrorCode.Failed;
            return false;
        }

        protected override void GetResponseHeaders(CefResponse response, out long responseLength, out string redirectUrl)
        {
            responseLength = this.responseLength;
            redirectUrl = null;

            if (responseLength != -1)
            {
                var headers = new NameValueCollection(StringComparer.InvariantCultureIgnoreCase);
                headers.Add("Content-Length", responseLength.ToString());
                response.SetHeaderMap(headers);
            }

            response.Status = this.status;
            response.StatusText = this.statusText;
            response.MimeType = this.mimeType;
        }

        protected override bool Read(Stream response, int bytesToRead, out int bytesRead, CefResourceReadCallback callback)
        {
            // Backwards compatibility. ReadResponse will be called.
            callback.Dispose();
            bytesRead = -1;
            return false;
        }

        [Obsolete]
        protected override bool ReadResponse(Stream stream, int bytesToRead, out int bytesRead, CefCallback callback)
        {
            byte[] buffer = new byte[bytesToRead];
            var readed = this.stream.Read(buffer, 0, buffer.Length);
            if (readed > 0)
            {
                stream.Write(buffer, 0, readed);
                bytesRead = readed;
                return true;
            }
            else
            {
                this.Close();
                bytesRead = 0;
                return false;
            }
        }
    }
}
