using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace CampusNetGuard
{
    public sealed class PortalBrowserSession : IDisposable
    {
        private const int DefaultTimeoutMs = 30000;
        private const int PageReadyTimeoutMs = 30000;
        private const string NativeResultPrefix = "__CNG_NATIVE_OK__";

        private Process browserProcess;
        private string profileDirectory;
        private CdpConnection cdp;
        private bool disposed;

        private PortalBrowserSession()
        {
        }

        public static bool IsBrowserAvailable()
        {
            string path;
            return TryFindBrowser(out path);
        }

        public static PortalBrowserSession Start(out string error)
        {
            error = "";
            string browserPath;
            if (!TryFindBrowser(out browserPath))
            {
                error = "未找到 Google Chrome 或 Microsoft Edge。";
                return null;
            }

            PortalBrowserSession session = new PortalBrowserSession();
            try
            {
                string browserStartError = "";
                session.profileDirectory = Path.Combine(Path.GetTempPath(),
                    "CampusNetGuard-browser-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(session.profileDirectory);

                ProcessStartInfo info = new ProcessStartInfo();
                info.FileName = browserPath;
                info.Arguments = BuildStartArguments(session.profileDirectory);
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                info.WindowStyle = ProcessWindowStyle.Hidden;
                info.RedirectStandardOutput = true;
                info.RedirectStandardError = true;

                StringBuilder recentProcessError = new StringBuilder();
                object processErrorSync = new object();
                session.browserProcess = new Process();
                session.browserProcess.StartInfo = info;
                session.browserProcess.OutputDataReceived += delegate(object sender, DataReceivedEventArgs args) { };
                session.browserProcess.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs args)
                {
                    if (args == null || string.IsNullOrEmpty(args.Data))
                        return;
                    lock (processErrorSync)
                    {
                        recentProcessError.AppendLine(args.Data);
                        if (recentProcessError.Length > 4000)
                            recentProcessError.Remove(0, recentProcessError.Length - 3000);
                    }
                };

                if (!session.browserProcess.Start())
                {
                    error = "浏览器进程启动失败。";
                    session.Dispose();
                    return null;
                }
                session.browserProcess.BeginOutputReadLine();
                session.browserProcess.BeginErrorReadLine();

                int debugPort;
                string debugPath;
                if (!session.WaitForDevToolsPort(out debugPort, out debugPath, out browserStartError))
                {
                    lock (processErrorSync)
                        browserStartError = AppendBrowserError(browserStartError, recentProcessError.ToString());
                    error = browserStartError;
                    session.Dispose();
                    return null;
                }

                string targetJson;
                string targetError;
                if (!CreateBrowserTarget(debugPort, out targetJson, out targetError))
                {
                    error = targetError;
                    session.Dispose();
                    return null;
                }

                string webSocketUrl = ReadJsonString(targetJson, "webSocketDebuggerUrl");
                if (string.IsNullOrEmpty(webSocketUrl))
                {
                    error = "浏览器没有返回调试连接。";
                    session.Dispose();
                    return null;
                }

                session.cdp = new CdpConnection();
                if (!session.cdp.Connect(webSocketUrl, out targetError))
                {
                    error = targetError;
                    session.Dispose();
                    return null;
                }

                string enableError;
                if (!session.cdp.SendNoResult("Page.enable", "{}", DefaultTimeoutMs, out enableError) ||
                    !session.cdp.SendNoResult("Runtime.enable", "{}", DefaultTimeoutMs, out enableError))
                {
                    error = enableError;
                    session.Dispose();
                    return null;
                }

                return session;
            }
            catch (Exception ex)
            {
                error = "浏览器初始化失败: " + SafeMessage(ex.Message);
                session.Dispose();
                return null;
            }
        }

        public bool LoadPage(string url, out string content, out string finalUrl, out string error)
        {
            content = "";
            finalUrl = url ?? "";
            error = "";
            if (disposed || cdp == null)
            {
                error = "浏览器会话不可用。";
                return false;
            }

            try
            {
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                Dictionary<string, object> parameters = new Dictionary<string, object>();
                parameters["url"] = url ?? "";
                string response = cdp.Send("Page.navigate", serializer.Serialize(parameters), DefaultTimeoutMs);
                string commandError;
                if (ExtractCdpError(response, out commandError))
                {
                    error = commandError;
                    return false;
                }

                if (!WaitForDocumentReady(out error))
                    return false;

                if (!cdp.EvaluateString("document.documentElement.outerHTML", false,
                    out content, out error))
                    return false;

                string location;
                if (cdp.EvaluateString("location.href", false, out location, out error) &&
                    !string.IsNullOrEmpty(location))
                    finalUrl = location;

                if (string.IsNullOrEmpty(content))
                {
                    error = "浏览器返回了空页面。";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = "浏览器加载门户失败: " + SafeMessage(ex.Message);
                return false;
            }
        }

        public bool SubmitLogin(string username, string password, string fallbackFormBody,
            out string content, out string error)
        {
            content = "";
            error = "";
            if (disposed || cdp == null)
            {
                error = "浏览器会话不可用。";
                return false;
            }

            string evaluationError = "";
            string result = "";
            try
            {
                if (cdp.EvaluateString(BuildNativeLoginExpression(username, password), true,
                    out result, out evaluationError))
                {
                    if (result != null && result.StartsWith(NativeResultPrefix, StringComparison.Ordinal))
                    {
                        content = result.Substring(NativeResultPrefix.Length);
                        return true;
                    }
                    if (string.Equals(result, "__CNG_NATIVE_HTTP_ERROR__", StringComparison.Ordinal))
                    {
                        error = "门户认证请求没有返回内容。";
                        return false;
                    }
                    if (string.Equals(result, "__CNG_NATIVE_PRELOGIN_FAILED__", StringComparison.Ordinal))
                    {
                        error = "门户客户端校验未通过，可能需要人工处理。";
                        return false;
                    }
                    if (result != null && result.StartsWith("__CNG_NATIVE_JS_ERROR__", StringComparison.Ordinal))
                    {
                        error = "门户页面脚本执行失败，可能需要人工处理。";
                        return false;
                    }
                }

                string fetchError = "";
                if (cdp.EvaluateString(BuildFetchExpression(fallbackFormBody), true,
                    out result, out fetchError))
                {
                    if (result != null && result.StartsWith("__CNG_FETCH_ERROR__", StringComparison.Ordinal))
                    {
                        error = "浏览器认证请求失败。";
                        return false;
                    }
                    content = result ?? "";
                    return !string.IsNullOrEmpty(content);
                }

                error = string.IsNullOrEmpty(fetchError) ? evaluationError : fetchError;
                if (string.IsNullOrEmpty(error))
                    error = "浏览器认证请求失败。";
                return false;
            }
            catch (Exception ex)
            {
                error = "浏览器认证请求失败: " + SafeMessage(ex.Message);
                return false;
            }
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;

            if (cdp != null)
            {
                string ignoredError; try { cdp.SendNoResult("Browser.close", "{}", 1500, out ignoredError); }
                catch { }
                try { cdp.Dispose(); }
                catch { }
                cdp = null;
            }

            if (browserProcess != null)
            {
                try
                {
                    if (!browserProcess.HasExited)
                    {
                        if (!browserProcess.WaitForExit(2000))
                            browserProcess.Kill();
                    }
                }
                catch { }
                try { browserProcess.Dispose(); }
                catch { }
                browserProcess = null;
            }

            TryDeleteProfile();
        }

        private bool WaitForDevToolsPort(out int port, out string debugPath, out string error)
        {
            port = 0;
            debugPath = "";
            error = "等待浏览器调试端口超时。";
            string activePortFile = Path.Combine(profileDirectory, "DevToolsActivePort");
            DateTime deadline = DateTime.UtcNow.AddSeconds(12);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    if (browserProcess != null && browserProcess.HasExited)
                    {
                        error = "浏览器进程提前退出。";
                        return false;
                    }
                    if (File.Exists(activePortFile))
                    {
                        string[] lines = File.ReadAllLines(activePortFile);
                        int parsedPort;
                        if (lines.Length >= 2 && int.TryParse(lines[0].Trim(), out parsedPort) && parsedPort > 0)
                        {
                            port = parsedPort;
                            debugPath = lines[1].Trim();
                            return true;
                        }
                    }
                }
                catch
                {
                }
                Thread.Sleep(150);
            }
            return false;
        }

        private bool WaitForDocumentReady(out string error)
        {
            error = "";
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(PageReadyTimeoutMs);
            string lastError = "";
            const string readinessScript =
                "(function(){var s=document.readyState||'';return s+'|'+" +
                "(!!document.getElementById('form1'))+'|'+" +
                "(!!(document.getElementById('id_userName')||document.querySelector('[name=\"userName\"]')))+'|'+" +
                "(!!(document.getElementById('id_userPwd')||document.querySelector('[name=\"userPwd\"]')));})()";
            while (DateTime.UtcNow < deadline)
            {
                string state;
                if (cdp.EvaluateString(readinessScript, false, out state, out lastError))
                {
                    if (IsPortalReadyState(state))
                        return true;
                }
                Thread.Sleep(200);
            }
            error = string.IsNullOrEmpty(lastError) ? "等待门户页面加载超时。" : lastError;
            return false;
        }

        public static bool IsPortalReadyState(string state)
        {
            if (string.IsNullOrEmpty(state))
                return false;
            string[] parts = state.Split('|');
            if (parts.Length < 4)
                return false;
            bool documentReady = string.Equals(parts[0], "complete", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(parts[0], "interactive", StringComparison.OrdinalIgnoreCase);
            return documentReady &&
                string.Equals(parts[1], "True", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(parts[2], "True", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(parts[3], "True", StringComparison.OrdinalIgnoreCase);
        }

        private void TryDeleteProfile()
        {
            if (string.IsNullOrEmpty(profileDirectory))
                return;
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    if (Directory.Exists(profileDirectory))
                        Directory.Delete(profileDirectory, true);
                    profileDirectory = null;
                    return;
                }
                catch
                {
                    Thread.Sleep(250);
                }
            }
        }

        private static bool CreateBrowserTarget(int port, out string targetJson, out string error)
        {
            targetJson = "";
            error = "";
            string url = "http://127.0.0.1:" + port.ToString() + "/json/new?about:blank";
            string putError;
            if (TryRequestJson(url, "PUT", out targetJson, out putError))
                return true;
            string getError;
            if (TryRequestJson(url, "GET", out targetJson, out getError))
                return true;

            string listJson;
            string listError;
            string listUrl = "http://127.0.0.1:" + port.ToString() + "/json/list";
            if (TryRequestJson(listUrl, "GET", out listJson, out listError) &&
                TrySelectFirstTarget(listJson, out targetJson))
                return true;

            error = string.IsNullOrEmpty(putError) ? getError : putError;
            if (string.IsNullOrEmpty(error))
                error = listError;
            if (string.IsNullOrEmpty(error))
                error = "无法创建浏览器页面。";
            return false;
        }

        private static bool TrySelectFirstTarget(string listJson, out string targetJson)
        {
            targetJson = "";
            if (string.IsNullOrEmpty(listJson))
                return false;
            try
            {
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                object[] targets = serializer.DeserializeObject(listJson) as object[];
                if (targets == null)
                    return false;
                foreach (object item in targets)
                {
                    Dictionary<string, object> target = item as Dictionary<string, object>;
                    if (target == null || !target.ContainsKey("webSocketDebuggerUrl") ||
                        target["webSocketDebuggerUrl"] == null)
                        continue;
                    target["webSocketDebuggerUrl"] = Convert.ToString(target["webSocketDebuggerUrl"]);
                    targetJson = serializer.Serialize(target);
                    return true;
                }
            }
            catch
            {
            }
            return false;
        }

        private static bool TryRequestJson(string url, string method, out string content, out string error)
        {
            content = "";
            error = "";
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = method;
                request.Proxy = null;
                request.Timeout = 5000;
                request.ReadWriteTimeout = 5000;
                request.KeepAlive = false;
                if (string.Equals(method, "PUT", StringComparison.OrdinalIgnoreCase))
                    request.ContentLength = 0;
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (Stream stream = response.GetResponseStream())
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true))
                    content = reader.ReadToEnd();
                return !string.IsNullOrEmpty(content);
            }
            catch (Exception ex)
            {
                error = SafeMessage(ex.Message);
                return false;
            }
        }

        private static string BuildStartArguments(string profileDirectory)
        {
            StringBuilder builder = new StringBuilder();
            // This is a short-lived, isolated browser profile. The compatibility
            // flags avoid a GPU-process startup crash seen with current Chrome
            // and Edge builds in this Windows environment.
            builder.Append("--headless=new ");
            builder.Append("--disable-gpu ");
            builder.Append("--disable-gpu-sandbox ");
            builder.Append("--no-sandbox ");
            builder.Append("--no-first-run ");
            builder.Append("--disable-extensions ");
            builder.Append("--disable-background-networking ");
            builder.Append("--disable-default-apps ");
            builder.Append("--disable-sync ");
            builder.Append("--metrics-recording-only ");
            builder.Append("--remote-allow-origins=* ");
            builder.Append("--remote-debugging-port=0 ");
            builder.Append("--user-data-dir=");
            builder.Append(QuoteArgument(profileDirectory));
            builder.Append(" about:blank");
            return builder.ToString();
        }

        private static bool TryFindBrowser(out string path)
        {
            path = "";
            List<string> candidates = new List<string>();
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            AddBrowserCandidate(candidates, programFiles, @"Google\Chrome\Application\chrome.exe");
            AddBrowserCandidate(candidates, programFilesX86, @"Google\Chrome\Application\chrome.exe");
            AddBrowserCandidate(candidates, localAppData, @"Google\Chrome\Application\chrome.exe");
            AddBrowserCandidate(candidates, programFiles, @"Microsoft\Edge\Application\msedge.exe");
            AddBrowserCandidate(candidates, programFilesX86, @"Microsoft\Edge\Application\msedge.exe");
            AddBrowserCandidate(candidates, localAppData, @"Microsoft\Edge\Application\msedge.exe");
            foreach (string candidate in candidates)
            {
                if (!string.IsNullOrEmpty(candidate) && File.Exists(candidate))
                {
                    path = candidate;
                    return true;
                }
            }
            return false;
        }

        private static void AddBrowserCandidate(List<string> candidates, string root, string relative)
        {
            if (string.IsNullOrEmpty(root))
                return;
            candidates.Add(Path.Combine(root, relative));
        }

        private static string BuildNativeLoginExpression(string username, string password)
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            return "(async function(){try{" +
                "var u=document.getElementById('id_userName');if(u)u.value=" + serializer.Serialize(username ?? "") + ";" +
                "var p=document.getElementById('id_userPwd');if(p)p.value=" + serializer.Serialize(password ?? "") + ";" +
                "if(typeof f_preLoginForInputPanel==='function'){var ok=false;try{ok=f_preLoginForInputPanel();}catch(e){return '__CNG_NATIVE_JS_ERROR__'+(e&&e.message?e.message:e);}if(!ok)return '__CNG_NATIVE_PRELOGIN_FAILED__';}" +
                "if(typeof $!=='function'||!$('#form1').length)return '__CNG_NATIVE_NO_JQUERY__';" +
                "var ifEmailAuth=$('#if_email_auth').length===0?false:$('#if_email_auth').val();" +
                "return await new Promise(function(resolve){$.ajax({url:'/portal/pws?t=li&ifEmailAuth='+ifEmailAuth,type:'POST',cache:false,async:true,dataType:'text',data:$('#form1').serializeArray(),success:function(data){resolve('" + NativeResultPrefix + "'+(data||''));},error:function(){resolve('__CNG_NATIVE_HTTP_ERROR__');}});});" +
                "}catch(e){return '__CNG_NATIVE_JS_ERROR__'+(e&&e.message?e.message:e);}})()";
        }

        private static string BuildFetchExpression(string formBody)
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            return "(async function(){try{var r=await fetch('/portal/pws?t=li&ifEmailAuth=false',{method:'POST',credentials:'same-origin',cache:'no-store',headers:{'Content-Type':'application/x-www-form-urlencoded; charset=UTF-8','X-Requested-With':'XMLHttpRequest'},body:" +
                serializer.Serialize(formBody ?? "") +
                "});return await r.text();}catch(e){return '__CNG_FETCH_ERROR__'+(e&&e.message?e.message:e);}})()";
        }

        private static string ReadJsonString(string json, string key)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key))
                return "";
            try
            {
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                Dictionary<string, object> values = serializer.DeserializeObject(json) as Dictionary<string, object>;
                if (values == null || !values.ContainsKey(key) || values[key] == null)
                    return "";
                return Convert.ToString(values[key]);
            }
            catch
            {
                return "";
            }
        }

        private static bool ExtractCdpError(string response, out string error)
        {
            error = "";
            try
            {
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                Dictionary<string, object> values = serializer.DeserializeObject(response) as Dictionary<string, object>;
                if (values == null || !values.ContainsKey("error"))
                    return false;
                Dictionary<string, object> errorObject = values["error"] as Dictionary<string, object>;
                if (errorObject != null && errorObject.ContainsKey("message"))
                    error = Convert.ToString(errorObject["message"]);
                if (string.IsNullOrEmpty(error))
                    error = "浏览器命令执行失败。";
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string DescribeException(Exception ex)
        {
            StringBuilder builder = new StringBuilder();
            for (Exception current = ex; current != null && builder.Length < 700; current = current.InnerException)
            {
                if (builder.Length > 0)
                    builder.Append(" -> ");
                builder.Append(current.GetType().Name);
                builder.Append(": ");
                builder.Append(SafeMessage(current.Message));
            }
            return builder.ToString();
        }
        private static string SafeMessage(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "";
            string clean = value.Replace("\r", " ").Replace("\n", " ").Trim();
            return clean.Length > 180 ? clean.Substring(0, 180) : clean;
        }

        private static string AppendBrowserError(string first, string second)
        {
            if (string.IsNullOrEmpty(first))
                first = "浏览器初始化失败。";
            if (string.IsNullOrEmpty(second))
                return first;
            string clean = SafeMessage(second);
            return string.IsNullOrEmpty(clean) ? first : first + " " + clean;
        }

        private static string QuoteArgument(string value)
        {
            return "\"" + (value ?? "").Replace("\"", "\\\"") + "\"";
        }

        private sealed class CdpConnection : IDisposable
        {
            private readonly JavaScriptSerializer serializer = new JavaScriptSerializer();
            private ClientWebSocket socket;
            private int nextId;
            private bool disposed;

            public bool Connect(string url, out string error)
            {
                error = "";
                try
                {
                    socket = new ClientWebSocket();
                    try { socket.Options.Proxy = null; } catch { }
                    CancellationTokenSource source = new CancellationTokenSource(8000);
                    try
                    {
                        socket.ConnectAsync(new Uri(url), source.Token).GetAwaiter().GetResult();
                    }
                    finally
                    {
                        source.Dispose();
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    error = "连接浏览器调试端口失败: " + DescribeException(ex);
                    return false;
                }
            }

            public bool SendNoResult(string method, string parameters, int timeoutMs, out string error)
            {
                string response;
                error = "";
                try
                {
                    response = Send(method, parameters, timeoutMs);
                    return !ExtractCdpError(response, out error);
                }
                catch (Exception ex)
                {
                    error = SafeMessage(ex.Message);
                    return false;
                }
            }

            public string Send(string method, string parameters, int timeoutMs)
            {
                if (socket == null || socket.State != WebSocketState.Open)
                    throw new InvalidOperationException("浏览器调试连接未打开。");

                int id = ++nextId;
                string request = "{\"id\":" + id.ToString() + ",\"method\":" +
                    serializer.Serialize(method ?? "") + ",\"params\":" +
                    (string.IsNullOrEmpty(parameters) ? "{}" : parameters) + "}";
                SendText(request, timeoutMs);

                DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                while (DateTime.UtcNow < deadline)
                {
                    int remaining = (int)Math.Max(1, (deadline - DateTime.UtcNow).TotalMilliseconds);
                    string response = ReceiveText(remaining);
                    int responseId;
                    if (TryReadResponseId(response, out responseId) && responseId == id)
                        return response;
                }
                throw new TimeoutException("等待浏览器响应超时。");
            }

            public bool EvaluateString(string expression, bool awaitPromise,
                out string value, out string error)
            {
                value = "";
                error = "";
                try
                {
                    Dictionary<string, object> parameters = new Dictionary<string, object>();
                    parameters["expression"] = expression ?? "";
                    parameters["returnByValue"] = true;
                    parameters["awaitPromise"] = awaitPromise;
                    parameters["userGesture"] = false;
                    string response = Send("Runtime.evaluate", serializer.Serialize(parameters), DefaultTimeoutMs);
                    if (ExtractCdpError(response, out error))
                        return false;

                    Dictionary<string, object> root = serializer.DeserializeObject(response) as Dictionary<string, object>;
                    if (root == null || !root.ContainsKey("result"))
                    {
                        error = "浏览器没有返回脚本结果。";
                        return false;
                    }
                    Dictionary<string, object> result = root["result"] as Dictionary<string, object>;
                    if (result == null)
                    {
                        error = "浏览器脚本结果格式无效。";
                        return false;
                    }
                    if (result.ContainsKey("exceptionDetails"))
                    {
                        error = "浏览器执行门户脚本失败。";
                        return false;
                    }
                    if (!result.ContainsKey("result"))
                    {
                        value = "";
                        return true;
                    }
                    Dictionary<string, object> remote = result["result"] as Dictionary<string, object>;
                    if (remote != null && remote.ContainsKey("value") && remote["value"] != null)
                        value = Convert.ToString(remote["value"]);
                    return true;
                }
                catch (Exception ex)
                {
                    error = SafeMessage(ex.Message);
                    return false;
                }
            }

            public void Dispose()
            {
                if (disposed)
                    return;
                disposed = true;
                if (socket != null)
                {
                    try
                    {
                        if (socket.State == WebSocketState.Open)
                        {
                            CancellationTokenSource source = new CancellationTokenSource(1200);
                            try
                            {
                                socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "close", source.Token)
                                    .GetAwaiter().GetResult();
                            }
                            finally
                            {
                                source.Dispose();
                            }
                        }
                    }
                    catch { }
                    try { socket.Dispose(); }
                    catch { }
                    socket = null;
                }
            }

            private void SendText(string text, int timeoutMs)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(text);
                CancellationTokenSource source = new CancellationTokenSource(timeoutMs);
                try
                {
                    socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, source.Token)
                        .GetAwaiter().GetResult();
                }
                finally
                {
                    source.Dispose();
                }
            }

            private string ReceiveText(int timeoutMs)
            {
                CancellationTokenSource source = new CancellationTokenSource(timeoutMs);
                try
                {
                    using (MemoryStream stream = new MemoryStream())
                    {
                        byte[] buffer = new byte[8192];
                        WebSocketReceiveResult result;
                        do
                        {
                            result = socket.ReceiveAsync(new ArraySegment<byte>(buffer), source.Token)
                                .GetAwaiter().GetResult();
                            if (result.MessageType == WebSocketMessageType.Close)
                                throw new WebSocketException("浏览器调试连接已关闭。");
                            stream.Write(buffer, 0, result.Count);
                        }
                        while (!result.EndOfMessage);
                        return Encoding.UTF8.GetString(stream.ToArray());
                    }
                }
                finally
                {
                    source.Dispose();
                }
            }

            private bool TryReadResponseId(string response, out int id)
            {
                id = 0;
                try
                {
                    Dictionary<string, object> values = serializer.DeserializeObject(response) as Dictionary<string, object>;
                    if (values == null || !values.ContainsKey("id"))
                        return false;
                    id = Convert.ToInt32(values["id"]);
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }
    }
}
