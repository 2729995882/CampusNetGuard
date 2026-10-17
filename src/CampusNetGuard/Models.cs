using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace CampusNetGuard
{
    public enum GuardState
    {
        Starting,
        Online,
        Waiting,
        Authenticating,
        WifiFallback,
        ScheduledPaused,
        UserPaused,
        EnvironmentUnknown,
        CredentialError,
        ManualActionRequired
    }

    public enum EthernetReturnPolicy
    {
        AutoAfter60Seconds = 0,
        KeepWifiManual = 1,
        KeepWifiNoAuto = 2
    }

    public static class ScheduleRules
    {
        public static string GetPauseDescription()
        {
            return "学校计划断网时段（01:00-06:00），仅暂停自动重连，不会断开网络。";
        }

        public static bool IsPaused(DateTime now)
        {
            bool scheduledDay = now.DayOfWeek == DayOfWeek.Sunday ||
                now.DayOfWeek == DayOfWeek.Monday ||
                now.DayOfWeek == DayOfWeek.Tuesday ||
                now.DayOfWeek == DayOfWeek.Wednesday ||
                now.DayOfWeek == DayOfWeek.Thursday;
            if (!scheduledDay)
                return false;
            return now.TimeOfDay >= TimeSpan.FromHours(1) &&
                now.TimeOfDay < TimeSpan.FromHours(6);
        }
    }

    public sealed class RetryDecision
    {
        public DateTime NextAttemptUtc;
        public int DelaySeconds;
        public int FailureCount;
    }

    public static class RetryPolicy
    {
        public const int NormalRetryDelaySeconds = 30;
        public const int BrowserFallbackThrottleSeconds = 120;

        public static RetryDecision PlanFailure(DateTime nowUtc, int currentFailureCount)
        {
            return new RetryDecision
            {
                NextAttemptUtc = nowUtc.AddSeconds(NormalRetryDelaySeconds),
                DelaySeconds = NormalRetryDelaySeconds,
                FailureCount = currentFailureCount + 1
            };
        }
    }

    public static class GuardStateTransitions
    {
        public static bool IsRecovery(GuardState previous, GuardState next)
        {
            return next == GuardState.Online &&
                previous != GuardState.Online &&
                previous != GuardState.Starting;
        }
    }

    public sealed class AppSettings
    {
        public bool AutoReconnectEnabled = true;
        public bool StartWithWindows = true;
        public bool NotificationsEnabled = true;
        public bool AutoEnableWifi = true;
        public string Username = "";
        public string ProtectedPassword = "";
        public int ReturnPolicy = (int)EthernetReturnPolicy.AutoAfter60Seconds;
        public string WifiProfile = "gdei-WiFi-S";
        public string LastCampusGateway = "";
        public string LogDirectory = "";

        public EthernetReturnPolicy GetReturnPolicy()
        {
            if (ReturnPolicy < 0 || ReturnPolicy > 2)
                return EthernetReturnPolicy.AutoAfter60Seconds;
            return (EthernetReturnPolicy)ReturnPolicy;
        }
    }

    public static class AppPaths
    {
        private static readonly object LogDirectorySync = new object();
        private static readonly string DefaultLogDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CampusNetGuard",
            "logs");
        private static string logDirectory = DefaultLogDirectory;

        public static readonly string BaseDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CampusNetGuard");
        public static readonly string SettingsFile = Path.Combine(BaseDirectory, "settings.xml");

        public static string DefaultLogDirectoryPath
        {
            get { return DefaultLogDirectory; }
        }

        public static string LogDirectory
        {
            get
            {
                lock (LogDirectorySync)
                    return logDirectory;
            }
        }

        public static string NormalizeLogDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;
            try
            {
                string expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
                if (string.IsNullOrWhiteSpace(expanded))
                    return null;
                return Path.GetFullPath(expanded);
            }
            catch
            {
                return null;
            }
        }

        public static bool TrySetLogDirectory(string path)
        {
            string normalized = NormalizeLogDirectory(path);
            if (string.IsNullOrEmpty(normalized))
                return false;
            try
            {
                Directory.CreateDirectory(normalized);
            }
            catch
            {
                return false;
            }
            lock (LogDirectorySync)
                logDirectory = normalized;
            return true;
        }

        static AppPaths()
        {
            try
            {
                Directory.CreateDirectory(BaseDirectory);
                Directory.CreateDirectory(DefaultLogDirectory);
            }
            catch
            {
                // The program will still run with in-memory defaults.
            }
        }
    }

    public static class Logger
    {
        private static readonly object Sync = new object();

        public static void Info(string message)
        {
            Write("INFO", message);
        }

        public static void Warn(string message)
        {
            Write("WARN", message);
        }

        public static void Error(string message)
        {
            Write("ERROR", message);
        }

        public static string MaskAccount(string account)
        {
            if (string.IsNullOrEmpty(account))
                return "(empty)";
            if (account.Length <= 2)
                return account.Substring(0, 1) + "****";
            return account.Substring(0, 2) + "****";
        }

        public static string Sanitize(string message, string account)
        {
            if (string.IsNullOrEmpty(message))
                return message ?? "";
            string sanitized = message;
            if (!string.IsNullOrEmpty(account))
                sanitized = sanitized.Replace(account, MaskAccount(account));
            sanitized = Regex.Replace(sanitized,
                @"(?i)(password|passwd|pwd|userPwd|密码)\s*[=:：]\s*(?:""[^""]*""|'[^']*'|[^&\s,;]+)", "$1=***");
            return sanitized;
        }

        private static void Write(string level, string message)
        {
            if (message == null) message = "";
            lock (Sync)
            {
                try
                {
                    Directory.CreateDirectory(AppPaths.LogDirectory);
                    string file = Path.Combine(AppPaths.LogDirectory,
                        "app-" + DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");
                    string line = string.Format(CultureInfo.InvariantCulture,
                        "[{0:yyyy-MM-dd HH:mm:ss}] [{1}] {2}{3}",
                        DateTime.Now, level, message, Environment.NewLine);
                    File.AppendAllText(file, line, new UTF8Encoding(false));
                }
                catch
                {
                    // Logging must never terminate the guard.
                }
            }
        }
    }

    public sealed class SettingsStore
    {
        private readonly object sync = new object();

        public AppSettings Load()
        {
            AppSettings settings = new AppSettings();
            try
            {
                if (!File.Exists(AppPaths.SettingsFile))
                    return settings;

                XmlDocument document = new XmlDocument();
                document.Load(AppPaths.SettingsFile);
                XmlNode root = document.SelectSingleNode("/CampusNetGuard");
                if (root == null)
                    return settings;

                settings.AutoReconnectEnabled = ReadBool(root, "AutoReconnectEnabled", settings.AutoReconnectEnabled);
                settings.StartWithWindows = ReadBool(root, "StartWithWindows", settings.StartWithWindows);
                settings.NotificationsEnabled = ReadBool(root, "NotificationsEnabled", settings.NotificationsEnabled);
                settings.AutoEnableWifi = ReadBool(root, "AutoEnableWifi", settings.AutoEnableWifi);
                settings.Username = ReadText(root, "Username", settings.Username);
                settings.ProtectedPassword = ReadText(root, "ProtectedPassword", settings.ProtectedPassword);
                settings.ReturnPolicy = ReadInt(root, "ReturnPolicy", settings.ReturnPolicy);
                settings.WifiProfile = ReadText(root, "WifiProfile", settings.WifiProfile);
                settings.LastCampusGateway = ReadText(root, "LastCampusGateway", settings.LastCampusGateway);
                settings.LogDirectory = ReadText(root, "LogDirectory", settings.LogDirectory);
            }
            catch (Exception ex)
            {
                Logger.Warn("读取设置失败，使用默认设置: " + ex.Message);
            }
            return settings;
        }

        public bool Save(AppSettings settings)
        {
            lock (sync)
            {
                try
                {
                    Directory.CreateDirectory(AppPaths.BaseDirectory);
                    XmlDocument document = new XmlDocument();
                    XmlElement root = document.CreateElement("CampusNetGuard");
                    document.AppendChild(root);
                    Add(root, document, "AutoReconnectEnabled", settings.AutoReconnectEnabled.ToString());
                    Add(root, document, "StartWithWindows", settings.StartWithWindows.ToString());
                    Add(root, document, "NotificationsEnabled", settings.NotificationsEnabled.ToString());
                    Add(root, document, "AutoEnableWifi", settings.AutoEnableWifi.ToString());
                    Add(root, document, "Username", settings.Username ?? "");
                    Add(root, document, "ProtectedPassword", settings.ProtectedPassword ?? "");
                    Add(root, document, "ReturnPolicy", settings.ReturnPolicy.ToString(CultureInfo.InvariantCulture));
                    Add(root, document, "WifiProfile", settings.WifiProfile ?? "gdei-WiFi-S");
                    Add(root, document, "LastCampusGateway", settings.LastCampusGateway ?? "");
                    Add(root, document, "LogDirectory", settings.LogDirectory ?? "");
                    document.Save(AppPaths.SettingsFile);
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.Error("保存设置失败: " + ex.Message);
                    return false;
                }
            }
        }

        public string ProtectPassword(string password)
        {
            if (string.IsNullOrEmpty(password))
                return "";
            byte[] clear = Encoding.UTF8.GetBytes(password);
            byte[] protectedBytes = ProtectedData.Protect(clear, null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(protectedBytes);
        }

        public string UnprotectPassword(string protectedBase64)
        {
            if (string.IsNullOrEmpty(protectedBase64))
                return "";
            try
            {
                byte[] protectedBytes = Convert.FromBase64String(protectedBase64);
                byte[] clear = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(clear);
            }
            catch (Exception ex)
            {
                Logger.Warn("无法解密已保存密码: " + ex.Message);
                return "";
            }
        }

        private static void Add(XmlElement root, XmlDocument document, string name, string value)
        {
            XmlElement element = document.CreateElement(name);
            element.InnerText = value;
            root.AppendChild(element);
        }

        private static string ReadText(XmlNode root, string name, string fallback)
        {
            XmlNode node = root.SelectSingleNode(name);
            return node == null ? fallback : node.InnerText;
        }

        private static bool ReadBool(XmlNode root, string name, bool fallback)
        {
            bool value;
            return bool.TryParse(ReadText(root, name, fallback.ToString()), out value) ? value : fallback;
        }

        private static int ReadInt(XmlNode root, string name, int fallback)
        {
            int value;
            return int.TryParse(ReadText(root, name, fallback.ToString(CultureInfo.InvariantCulture)),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? value : fallback;
        }
    }
}
