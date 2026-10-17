using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace CampusNetGuard
{
    public sealed class NetworkSnapshot
    {
        public NetworkInterface Interface;
        public string Name = "";
        public string Description = "";
        public bool IsUp;
        public IPAddress IPv4;
        public IPAddress Gateway;

        public bool HasIPv4
        {
            get { return IPv4 != null; }
        }
    }

    public static class NetworkInspector
    {
        private static readonly string[] VirtualKeywords = new string[]
        {
            "tap", "vpn", "virtual", "zerotier", "radmin", "meta", "hyper-v",
            "vmware", "virtualbox", "loopback", "tunnel", "pseudo", "npcap",
            "wintun", "wireguard", "bridge"
        };

        public static NetworkSnapshot FindPhysicalEthernet()
        {
            NetworkSnapshot best = null;
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (!IsEthernet(ni) || IsVirtual(ni))
                    continue;
                NetworkSnapshot current = Snapshot(ni);
                if (best == null || (current.IsUp && !best.IsUp))
                    best = current;
            }
            return best;
        }

        public static NetworkSnapshot FindWifi()
        {
            NetworkSnapshot best = null;
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.NetworkInterfaceType != NetworkInterfaceType.Wireless80211 || IsVirtual(ni))
                    continue;
                NetworkSnapshot current = Snapshot(ni);
                if (best == null || (current.IsUp && !best.IsUp))
                    best = current;
            }
            return best;
        }

        public static NetworkSnapshot Snapshot(NetworkInterface ni)
        {
            NetworkSnapshot snapshot = new NetworkSnapshot();
            snapshot.Interface = ni;
            snapshot.Name = ni.Name ?? "";
            snapshot.Description = ni.Description ?? "";
            try
            {
                snapshot.IsUp = ni.OperationalStatus == OperationalStatus.Up;
                IPInterfaceProperties properties = ni.GetIPProperties();
                foreach (UnicastIPAddressInformation address in properties.UnicastAddresses)
                {
                    if (address.Address.AddressFamily != AddressFamily.InterNetwork)
                        continue;
                    if (IPAddress.IsLoopback(address.Address) || IsApipa(address.Address))
                        continue;
                    snapshot.IPv4 = address.Address;
                    break;
                }
                foreach (GatewayIPAddressInformation gateway in properties.GatewayAddresses)
                {
                    if (gateway.Address.AddressFamily != AddressFamily.InterNetwork)
                        continue;
                    if (gateway.Address.Equals(IPAddress.Any))
                        continue;
                    snapshot.Gateway = gateway.Address;
                    break;
                }
            }
            catch
            {
                snapshot.IsUp = false;
            }
            return snapshot;
        }

        public static bool IsCampusGateway(IPAddress address)
        {
            if (address == null)
                return false;
            byte[] bytes = address.GetAddressBytes();
            return bytes.Length == 4 && bytes[0] == 10 && bytes[1] == 0 && bytes[2] == 104;
        }

        public static bool IsCampusHostResolvable()
        {
            try
            {
                IPAddress[] addresses = Dns.GetHostAddresses("nac.gdei.edu.cn");
                return addresses != null && addresses.Length > 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsEthernet(NetworkInterface ni)
        {
            string type = ni.NetworkInterfaceType.ToString();
            return type.IndexOf("Ethernet", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsVirtual(NetworkInterface ni)
        {
            string all = ((ni.Name ?? "") + " " + (ni.Description ?? "")).ToLowerInvariant();
            foreach (string keyword in VirtualKeywords)
            {
                if (all.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        private static bool IsApipa(IPAddress address)
        {
            byte[] bytes = address.GetAddressBytes();
            return bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254;
        }
    }

    public sealed class HttpResult
    {
        public int StatusCode;
        public string Content = "";
        public string FinalUrl = "";
        public string Location = "";
        public string Error = "";
        public bool IsSuccess;
    }

    public static class HttpHelper
    {
        public const string BrowserUserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

        static HttpHelper()
        {
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            }
            catch
            {
            }
            ServicePointManager.Expect100Continue = false;
        }

        public static HttpResult Get(string url, IPAddress bindAddress, CookieContainer cookies, bool allowRedirect)
        {
            return Execute(url, "GET", null, bindAddress, cookies, allowRedirect, null);
        }

        public static HttpResult PostForm(string url, string formBody, string referer, IPAddress bindAddress,
            CookieContainer cookies)
        {
            return Execute(url, "POST", formBody, bindAddress, cookies, false, referer);
        }

        private static HttpResult Execute(string url, string method, string body, IPAddress bindAddress,
            CookieContainer cookies, bool allowRedirect, string referer)
        {
            HttpResult result = new HttpResult();
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = method;
                request.Proxy = null;
                request.UserAgent = BrowserUserAgent;
                request.Accept = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8";
                request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                request.AllowAutoRedirect = allowRedirect;
                request.Timeout = 15000;
                request.ReadWriteTimeout = 15000;
                request.KeepAlive = false;
                if (cookies != null)
                    request.CookieContainer = cookies;
                if (!string.IsNullOrEmpty(referer))
                    request.Referer = referer;
                if (bindAddress != null)
                {
                    request.ServicePoint.BindIPEndPointDelegate = delegate(ServicePoint servicePoint,
                        IPEndPoint remoteEndPoint, int retryCount)
                    {
                        return new IPEndPoint(bindAddress, 0);
                    };
                }
                if (body != null)
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(body);
                    request.ContentType = "application/x-www-form-urlencoded; charset=UTF-8";
                    request.ContentLength = bytes.Length;
                    using (Stream stream = request.GetRequestStream())
                    {
                        stream.Write(bytes, 0, bytes.Length);
                    }
                }

                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    result.StatusCode = (int)response.StatusCode;
                    result.IsSuccess = response.StatusCode == HttpStatusCode.OK;
                    result.FinalUrl = response.ResponseUri == null ? url : response.ResponseUri.ToString();
                    result.Location = response.Headers["Location"] ?? "";
                    using (Stream stream = response.GetResponseStream())
                    using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true))
                    {
                        result.Content = reader.ReadToEnd();
                    }
                }
            }
            catch (WebException ex)
            {
                result.Error = ex.Message;
                if (ex.Response != null)
                {
                    try
                    {
                        using (HttpWebResponse response = (HttpWebResponse)ex.Response)
                        {
                            result.StatusCode = (int)response.StatusCode;
                            result.FinalUrl = response.ResponseUri == null ? url : response.ResponseUri.ToString();
                    result.Location = response.Headers["Location"] ?? "";
                            using (Stream stream = response.GetResponseStream())
                            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true))
                            {
                                result.Content = reader.ReadToEnd();
                            }
                        }
                    }
                    catch
                    {
                    }
                }
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
            }
            return result;
        }
    }

    public sealed class CommandResult
    {
        public int ExitCode = -1;
        public string Output = "";
        public string Error = "";
        public bool TimedOut;
    }

    public static class CommandRunner
    {
        public static CommandResult Run(string fileName, string arguments, int timeoutMs)
        {
            CommandResult result = new CommandResult();
            Process process = null;
            try
            {
                ProcessStartInfo info = new ProcessStartInfo(fileName, arguments);
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                info.RedirectStandardOutput = true;
                info.RedirectStandardError = true;
                info.StandardOutputEncoding = Encoding.UTF8;
                info.StandardErrorEncoding = Encoding.UTF8;
                process = Process.Start(info);
                string standardOutput = process.StandardOutput.ReadToEnd();
                string standardError = process.StandardError.ReadToEnd();
                if (!process.WaitForExit(timeoutMs))
                {
                    result.TimedOut = true;
                    try { process.Kill(); } catch { }
                    result.Error = "命令超时";
                    return result;
                }
                result.ExitCode = process.ExitCode;
                result.Output = standardOutput ?? "";
                result.Error = standardError ?? "";
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
            }
            finally
            {
                if (process != null)
                    process.Dispose();
            }
            return result;
        }
    }

    public static class WifiManager
    {
        public static bool IsAdapterPresent()
        {
            return NetworkInspector.FindWifi() != null;
        }

        public static bool IsProfileSaved(string profile)
        {
            CommandResult result = CommandRunner.Run("netsh",
                "wlan show profiles name=\"" + EscapeArgument(profile) + "\"", 8000);
            if (result.TimedOut || result.ExitCode != 0)
                return false;
            return ContainsProfileName(result.Output, profile);
        }

        public static bool IsSsidVisible(string profile)
        {
            CommandResult result = CommandRunner.Run("netsh", "wlan show networks mode=bssid", 12000);
            return !result.TimedOut && result.ExitCode == 0 && result.Output.IndexOf(profile, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static string GetConnectedSsid()
        {
            CommandResult result = CommandRunner.Run("netsh", "wlan show interfaces", 8000);
            if (result.TimedOut || result.ExitCode != 0)
                return "";
            string[] lines = result.Output.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.StartsWith("SSID", StringComparison.OrdinalIgnoreCase) &&
                    !line.StartsWith("BSSID", StringComparison.OrdinalIgnoreCase))
                {
                    int colon = line.IndexOf(':');
                    if (colon >= 0)
                        return line.Substring(colon + 1).Trim();
                }
            }
            return "";
        }

        public static bool Connect(string profile, string interfaceName)
        {
            string args = "wlan connect name=\"" + EscapeArgument(profile) + "\" ssid=\"" +
                EscapeArgument(profile) + "\" interface=\"" + EscapeArgument(interfaceName) + "\"";
            CommandResult result = CommandRunner.Run("netsh", args, 15000);
            if (result.TimedOut)
            {
                Logger.Warn("连接 Wi-Fi 超时: " + profile);
                return false;
            }
            if (result.ExitCode != 0)
            {
                Logger.Warn("连接 Wi-Fi 失败: " + result.Output + " " + result.Error);
                return false;
            }
            return true;
        }

        public static bool WaitForIp(string profile, int timeoutMs)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                NetworkSnapshot wifi = NetworkInspector.FindWifi();
                if (wifi != null && wifi.IsUp && wifi.HasIPv4 &&
                    string.Equals(GetConnectedSsid(), profile, StringComparison.OrdinalIgnoreCase))
                    return true;
                Thread.Sleep(1000);
            }
            return false;
        }

        public static void Disconnect(string interfaceName)
        {
            CommandRunner.Run("netsh", "wlan disconnect interface=\"" + EscapeArgument(interfaceName) + "\"", 8000);
        }

        private static bool ContainsProfileName(string output, string profile)
        {
            if (string.IsNullOrEmpty(output))
                return false;
            string[] lines = output.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string line in lines)
            {
                if (line.IndexOf(profile, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        private static string EscapeArgument(string value)
        {
            return (value ?? "").Replace("\"", "\\\"");
        }
    }

    public static class StartupManager
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "CampusNetGuard";

        public static bool IsEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (key == null)
                        return false;
                    string value = key.GetValue(ValueName) as string;
                    return !string.IsNullOrEmpty(value) &&
                        string.Equals(Unquote(value), ApplicationExecutable(), StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                return false;
            }
        }

        public static bool SetEnabled(bool enabled)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (key == null)
                        return false;
                    if (enabled)
                        key.SetValue(ValueName, "\"" + ApplicationExecutable() + "\"");
                    else
                        key.DeleteValue(ValueName, false);
                }
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn("设置开机启动失败: " + ex.Message);
                return false;
            }
        }

        public static string ApplicationExecutable()
        {
            return System.Reflection.Assembly.GetEntryAssembly().Location;
        }

        private static string Unquote(string value)
        {
            if (value == null)
                return "";
            value = value.Trim();
            if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
                return value.Substring(1, value.Length - 2);
            return value;
        }
    }
}
