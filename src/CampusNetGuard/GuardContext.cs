using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace CampusNetGuard
{
    public sealed class GuardContext : ApplicationContext
    {
        private const int EthernetFallbackDelayMilliseconds = 10000;
        private static readonly TimeSpan CampusContextLifetime = TimeSpan.FromMinutes(30);
        private readonly SettingsStore store = new SettingsStore();
        private readonly AppSettings settings;
        private string password;
        private readonly NotifyIcon icon;
        private readonly ContextMenuStrip menu;
        private readonly ToolStripMenuItem statusItem;
        private readonly ToolStripMenuItem autoItem;
        private readonly ToolStripMenuItem checkItem;
        private readonly ToolStripMenuItem recoveryItem;
        private readonly ToolStripMenuItem recoveryAutoItem;
        private readonly ToolStripMenuItem recoveryManualItem;
        private readonly ToolStripMenuItem recoveryKeepItem;
        private readonly ToolStripMenuItem settingsItem;
        private readonly ToolStripMenuItem logsItem;
        private readonly ToolStripMenuItem wifiRadioItem;
        private readonly ToolStripMenuItem startupItem;
        private readonly ToolStripMenuItem exitItem;
        private readonly System.Windows.Forms.Timer timer;
        private readonly System.Threading.Timer fallbackTimer;
        private readonly System.Threading.Timer pauseTimer;
        private readonly SynchronizationContext synchronizationContext;
        private readonly Control dispatcher;
        private readonly object stateLock = new object();

        private StatusForm statusForm;
        private int checkInProgress;
        private int checkPending;
        private int fallbackCheckPending;
        private int pauseResumePending;
        private GuardState state = GuardState.Starting;
        private string statusDetail = "正在启动";
        private bool disposed;
        private bool lastProbeOnline;
        private bool lastCampusContext;
        private DateTime? lastCampusContextUtc;
        private bool credentialBlocked;
        private bool manualActionBlocked;
        private bool manualCheckOverride;
        private int manualCheckRequested;
        private bool wifiFallbackActive;
        private bool ethernetWasUp;
        private bool notifiedWifiMissing;
        private bool notifiedWifiConnectFailure;
        private bool notifiedWifiRadioUnavailable;
        private bool wifiRadioEnableAttempted;
        private int failureCount;
        private DateTime? nextAttemptUtc;
        private DateTime? nextBrowserFallbackUtc;
        private DateTime? ethernetDownSince;
        private DateTime? ethernetStableSince;
        private DateTime? lastCheckUtc;
        private string lastAuthMessage = "尚未尝试";
        private string currentSsid = "";

        public GuardContext()
        {
            settings = store.Load();
            if (!string.IsNullOrEmpty(settings.LogDirectory) &&
                !AppPaths.TrySetLogDirectory(settings.LogDirectory))
            {
                settings.LogDirectory = "";
                AppPaths.TrySetLogDirectory(AppPaths.DefaultLogDirectoryPath);
            }
            password = store.UnprotectPassword(settings.ProtectedPassword);
            lastCampusContext = false;
            synchronizationContext = SynchronizationContext.Current;
            Form marshalForm = new Form();
            dispatcher = marshalForm;
            marshalForm.ShowInTaskbar = false;
            marshalForm.FormBorderStyle = FormBorderStyle.None;
            marshalForm.StartPosition = FormStartPosition.Manual;
            marshalForm.Location = new Point(-32000, -32000);
            marshalForm.Size = new Size(1, 1);
            try { marshalForm.Show(); marshalForm.Hide(); } catch { }

            if (settings.StartWithWindows && !StartupManager.IsEnabled())
            {
                if (StartupManager.SetEnabled(true))
                    Logger.Info("已按设置启用开机启动。");
                else
                    Logger.Warn("无法启用开机启动，请稍后从托盘菜单重试。");
            }

            icon = new NotifyIcon();
            icon.Icon = SystemIcons.Shield;
            icon.Text = "校园网助手";
            icon.Visible = true;
            icon.DoubleClick += delegate { ShowStatusWindow(); };

            menu = new ContextMenuStrip();
            statusItem = new ToolStripMenuItem("查看状态");
            statusItem.Click += delegate { ShowStatusWindow(); };
            autoItem = new ToolStripMenuItem("自动重连");
            autoItem.Click += delegate { ToggleAutoReconnect(); };
            checkItem = new ToolStripMenuItem("立即检测");
            checkItem.Click += delegate { CheckNow(); };
            recoveryItem = new ToolStripMenuItem("有线恢复策略");
            recoveryAutoItem = new ToolStripMenuItem("稳定 60 秒后自动切回有线");
            recoveryAutoItem.Click += delegate { SetReturnPolicy(EthernetReturnPolicy.AutoAfter60Seconds); };
            recoveryManualItem = new ToolStripMenuItem("保留 Wi-Fi，手动切回有线");
            recoveryManualItem.Click += delegate { SetReturnPolicy(EthernetReturnPolicy.KeepWifiManual); };
            recoveryKeepItem = new ToolStripMenuItem("保持 Wi-Fi，不自动切回");
            recoveryKeepItem.Click += delegate { SetReturnPolicy(EthernetReturnPolicy.KeepWifiNoAuto); };
            recoveryItem.DropDownItems.Add(recoveryAutoItem);
            recoveryItem.DropDownItems.Add(recoveryManualItem);
            recoveryItem.DropDownItems.Add(recoveryKeepItem);
            settingsItem = new ToolStripMenuItem("账号设置...");
            settingsItem.Click += delegate { ShowCredentialsWindow(); };
            logsItem = new ToolStripMenuItem("查看日志");
            logsItem.Click += delegate { OpenLogsFolder(); };
            ToolStripMenuItem logLocationItem = new ToolStripMenuItem("日志保存位置...");
            logLocationItem.Click += delegate { ShowLogLocationDialog(); };
            wifiRadioItem = new ToolStripMenuItem("有线断开时尝试开启 Wi-Fi");
            wifiRadioItem.Click += delegate { ToggleAutoEnableWifi(); };
            startupItem = new ToolStripMenuItem("开机启动");
            startupItem.Click += delegate { ToggleStartup(); };
            exitItem = new ToolStripMenuItem("退出");
            exitItem.Click += delegate { ExitApplication(); };

            menu.Items.Add(statusItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(autoItem);
            menu.Items.Add(checkItem);
            menu.Items.Add(recoveryItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(settingsItem);
            menu.Items.Add(logsItem);
            menu.Items.Add(logLocationItem);
            menu.Items.Add(wifiRadioItem);
            menu.Items.Add(startupItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exitItem);
            icon.ContextMenuStrip = menu;

            NetworkChange.NetworkAddressChanged += OnNetworkChanged;
            NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;

            fallbackTimer = new System.Threading.Timer(delegate
            {
                Interlocked.Exchange(ref fallbackCheckPending, 0);
                if (!disposed)
                    QueueCheck();
            }, null, Timeout.Infinite, Timeout.Infinite);

            pauseTimer = new System.Threading.Timer(delegate
            {
                Interlocked.Exchange(ref pauseResumePending, 0);
                if (!disposed)
                    QueueCheck();
            }, null, Timeout.Infinite, Timeout.Infinite);

            timer = new System.Windows.Forms.Timer();
            timer.Interval = 30000;
            timer.Tick += delegate { QueueCheck(); };
            timer.Start();
            UpdateTray();
            Logger.Info("校园网助手启动，账号: " + Logger.MaskAccount(settings.Username));
            if (string.IsNullOrEmpty(settings.Username) || string.IsNullOrEmpty(password))
                Notify("需要配置账号", "请右键托盘图标，打开“账号设置”填写校园网账号和密码。", ToolTipIcon.Info);
            QueueCheck();
        }

        public AppSettings Settings
        {
            get { return settings; }
        }

        public bool AutoReconnectEnabled
        {
            get { lock (stateLock) { return settings.AutoReconnectEnabled; } }
        }

        public bool StartWithWindows
        {
            get { return settings.StartWithWindows; }
        }

        public bool HasSavedPassword
        {
            get { return !string.IsNullOrEmpty(settings.ProtectedPassword); }
        }

        public bool AutoEnableWifi
        {
            get { return settings.AutoEnableWifi; }
        }

        public bool CanRestoreWired
        {
            get
            {
                NetworkSnapshot ethernet = NetworkInspector.FindPhysicalEthernet();
                return ethernet != null && ethernet.IsUp;
            }
        }

        public void CheckNow()
        {
            Interlocked.Exchange(ref manualCheckRequested, 1);
            QueueCheck();
        }

        public void ToggleAutoReconnect()
        {
            settings.AutoReconnectEnabled = !settings.AutoReconnectEnabled;
            if (settings.AutoReconnectEnabled)
            {
                bool keepCredentialBlock = credentialBlocked;
                bool keepManualActionBlock = manualActionBlocked;
                ResetFailures();
                credentialBlocked = keepCredentialBlock;
                manualActionBlocked = keepManualActionBlock;
                statusDetail = credentialBlocked
                    ? "账号或密码错误，自动认证已停止，请修改账号设置。"
                    : (manualActionBlocked
                        ? "门户需要人工验证，自动认证已停止。"
                        : "自动重连已启用");
            }
            else
            {
                CancelEthernetFallbackCheck();
                CancelPauseResumeCheck();
                ethernetDownSince = null;
                statusDetail = "自动重连已暂停";
            }
            store.Save(settings);
            SetState(settings.AutoReconnectEnabled
                ? (credentialBlocked ? GuardState.CredentialError :
                    (manualActionBlocked ? GuardState.ManualActionRequired : GuardState.Waiting))
                : GuardState.UserPaused, statusDetail);
            UpdateTray();
            if (settings.AutoReconnectEnabled)
                QueueCheck();
        }

        public void SetReturnPolicy(EthernetReturnPolicy policy)
        {
            settings.ReturnPolicy = (int)policy;
            store.Save(settings);
            statusDetail = "有线恢复策略已更新";
            UpdateTray();
            QueueCheck();
        }

        public void ToggleAutoEnableWifi()
        {
            settings.AutoEnableWifi = !settings.AutoEnableWifi;
            store.Save(settings);
            wifiRadioEnableAttempted = false;
            notifiedWifiRadioUnavailable = false;
            statusDetail = settings.AutoEnableWifi
                ? "已允许在有线断开时尝试开启 Wi-Fi。"
                : "已禁止自动开启 Wi-Fi。";
            UpdateTray();
            QueueCheck();
        }

        public bool SaveCredentials(string username, string newPassword, bool clearPassword, bool startupEnabled)
        {
            settings.Username = username ?? "";
            if (clearPassword)
            {
                settings.ProtectedPassword = "";
                password = "";
            }
            else if (!string.IsNullOrEmpty(newPassword))
            {
                try
                {
                    settings.ProtectedPassword = store.ProtectPassword(newPassword);
                    password = newPassword;
                }
                catch (Exception ex)
                {
                    Logger.Error("无法使用 DPAPI 加密密码: " + ex.Message);
                    Notify("无法安全保存密码", "Windows 当前无法加密保存密码，本次设置未保存。请重新登录后再试。",
                        ToolTipIcon.Error);
                    return false;
                }
            }
            settings.StartWithWindows = startupEnabled;
            if (!store.Save(settings))
            {
                Notify("无法保存账号设置", "请检查当前用户的数据目录是否可写，然后重试。", ToolTipIcon.Error);
                return false;
            }
            StartupManager.SetEnabled(startupEnabled);
            credentialBlocked = false;
            manualActionBlocked = false;
            Interlocked.Exchange(ref manualCheckRequested, 0);
            ResetFailures();
            statusDetail = "账号设置已保存";
            UpdateTray();
            QueueCheck();
            return true;
        }

        public void RestoreWiredNow()
        {
            NetworkSnapshot ethernet = NetworkInspector.FindPhysicalEthernet();
            if (ethernet == null || !ethernet.IsUp)
            {
                Notify("有线尚未恢复", "当前没有检测到可用的有线网络。", ToolTipIcon.Warning);
                return;
            }
            NetworkSnapshot wifi = NetworkInspector.FindWifi();
            if (wifi != null && wifi.IsUp)
                WifiManager.Disconnect(wifi.Name);
            wifiFallbackActive = false;
            ethernetStableSince = null;
            statusDetail = "已请求切回有线网络";
            UpdateTray();
            QueueCheck();
        }

        public void ShowStatusWindow()
        {
            if (statusForm == null || statusForm.IsDisposed)
                statusForm = new StatusForm(this);
            statusForm.UpdateFromContext();
            statusForm.Show();
            statusForm.Activate();
        }

        public void ShowCredentialsWindow()
        {
            using (CredentialsForm form = new CredentialsForm(this))
                form.ShowDialog();
        }

        public void OpenLogsFolder()
        {
            try
            {
                Process.Start("explorer.exe", "\"" + AppPaths.LogDirectory + "\"");
            }
            catch (Exception ex)
            {
                Logger.Warn("打开日志目录失败: " + ex.Message);
                Notify("无法打开日志", "请手动打开: " + AppPaths.LogDirectory, ToolTipIcon.Warning);
            }
        }

        public void ShowLogLocationDialog()
        {
            string oldDirectory = AppPaths.LogDirectory;
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "选择校园网助手的日志保存文件夹";
                dialog.ShowNewFolderButton = true;
                try
                {
                    dialog.SelectedPath = oldDirectory;
                }
                catch
                {
                    dialog.SelectedPath = AppPaths.DefaultLogDirectoryPath;
                }

                if (dialog.ShowDialog() != DialogResult.OK)
                    return;

                string newDirectory = AppPaths.NormalizeLogDirectory(dialog.SelectedPath);
                if (string.IsNullOrEmpty(newDirectory))
                {
                    Notify("日志位置无效", "请选择一个可用的文件夹。", ToolTipIcon.Warning);
                    return;
                }
                if (string.Equals(newDirectory, oldDirectory, StringComparison.OrdinalIgnoreCase))
                    return;

                DialogResult moveChoice = MessageBox.Show(
                    "是否将已有日志也移动到新文件夹？\n\n选择“否”只修改以后日志的位置，旧日志保留原处。",
                    "日志保存位置",
                    MessageBoxButtons.YesNoCancel,
                    MessageBoxIcon.Question);
                if (moveChoice == DialogResult.Cancel)
                    return;

                if (!AppPaths.TrySetLogDirectory(newDirectory))
                {
                    Notify("无法使用该日志位置", "程序无法创建或写入该文件夹，请换一个位置。", ToolTipIcon.Error);
                    return;
                }

                settings.LogDirectory = newDirectory;
                if (!store.Save(settings))
                {
                    settings.LogDirectory = oldDirectory;
                    AppPaths.TrySetLogDirectory(oldDirectory);
                    Notify("日志位置未保存", "设置文件写入失败，已保留原来的日志位置。", ToolTipIcon.Error);
                    return;
                }

                if (moveChoice == DialogResult.Yes)
                    MoveExistingLogs(oldDirectory, newDirectory);

                statusDetail = "日志保存位置已更新";
                Logger.Info("日志保存位置已更新: " + newDirectory);
                Notify("日志位置已更新",
                    "以后的新日志会保存到：\n" + newDirectory +
                    (moveChoice == DialogResult.Yes ? "\n旧日志也已尝试移动。" : "\n旧日志保留在原位置。"),
                    ToolTipIcon.Info);
                UpdateTray();
            }
        }

        private static void MoveExistingLogs(string sourceDirectory, string destinationDirectory)
        {
            try
            {
                if (!Directory.Exists(sourceDirectory) ||
                    string.Equals(Path.GetFullPath(sourceDirectory), Path.GetFullPath(destinationDirectory),
                        StringComparison.OrdinalIgnoreCase))
                    return;

                foreach (string sourceFile in Directory.GetFiles(sourceDirectory, "app-*.log"))
                {
                    string destinationFile = GetAvailableLogPath(
                        destinationDirectory, Path.GetFileName(sourceFile));
                    try
                    {
                        File.Copy(sourceFile, destinationFile, false);
                        try
                        {
                            File.Delete(sourceFile);
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn("旧日志已复制，但无法删除原文件: " + ex.Message);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn("移动旧日志失败: " + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("移动旧日志失败: " + ex.Message);
            }
        }

        private static string GetAvailableLogPath(string directory, string fileName)
        {
            string candidate = Path.Combine(directory, fileName);
            if (!File.Exists(candidate))
                return candidate;

            string stem = Path.GetFileNameWithoutExtension(fileName);
            string extension = Path.GetExtension(fileName);
            for (int index = 1; ; index++)
            {
                candidate = Path.Combine(directory, stem + "-" + index.ToString(CultureInfo.InvariantCulture) + extension);
                if (!File.Exists(candidate))
                    return candidate;
            }
        }

        public void ExitApplication()
        {
            if (disposed)
                return;
            disposed = true;
            timer.Stop();
            timer.Dispose();
            if (fallbackTimer != null)
                fallbackTimer.Dispose();
            if (pauseTimer != null)
                pauseTimer.Dispose();
            NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
            NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
            icon.Visible = false;
            icon.Dispose();
            menu.Dispose();
            if (dispatcher != null)
                dispatcher.Dispose();
            ExitThread();
        }

        public string GetStatusText()
        {
            NetworkSnapshot ethernet = NetworkInspector.FindPhysicalEthernet();
            NetworkSnapshot wifi = NetworkInspector.FindWifi();
            StringBuilder builder = new StringBuilder();
            builder.AppendLine("运行状态: " + StateLabel(state));
            builder.AppendLine("自动重连: " + (settings.AutoReconnectEnabled ? "已启用" : "已暂停"));
            builder.AppendLine("有线网络: " + Describe(ethernet));
            builder.AppendLine("无线网络: " + Describe(wifi) + (string.IsNullOrEmpty(currentSsid) ? "" : "，SSID=" + currentSsid));
            builder.AppendLine("互联网: " + (lastProbeOnline ? "在线" : "未确认或不在线"));
            builder.AppendLine("校园环境: " + (IsRecentCampusContext() ? "已识别（最近确认）" : "未知或非校园网络"));
            builder.AppendLine("计划暂停: " + (IsScheduledPause(DateTime.Now)
                ? "是（学校计划断网时段，仅暂停自动重连）"
                : "否"));
            builder.AppendLine("自动开启 Wi-Fi: " + (settings.AutoEnableWifi ? "允许" : "禁止"));
            builder.AppendLine("日志目录: " + AppPaths.LogDirectory);
            builder.AppendLine("最近认证: " + lastAuthMessage);
            builder.AppendLine("下次重试: " + (nextAttemptUtc.HasValue
                ? nextAttemptUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                : "无"));
            builder.AppendLine("恢复策略: " + ReturnPolicyLabel(settings.GetReturnPolicy()));
            builder.AppendLine("最后检查: " + (lastCheckUtc.HasValue
                ? lastCheckUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                : "尚未完成"));
            builder.AppendLine();
            builder.Append("说明: " + statusDetail);
            return builder.ToString();
        }

        private void QueueCheck()
        {
            if (disposed)
                return;
            if (Interlocked.CompareExchange(ref checkInProgress, 1, 0) != 0)
            {
                Interlocked.Exchange(ref checkPending, 1);
                return;
            }
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    DoCheck();
                }
                catch (Exception ex)
                {
                    Logger.Error("后台检测异常: " + ex);
                    SetState(GuardState.Waiting, "检测过程中出现错误，稍后重试。");
                }
                finally
                {
                    Interlocked.Exchange(ref checkInProgress, 0);
                    manualCheckOverride = false;
                    if (Interlocked.Exchange(ref checkPending, 0) != 0)
                        QueueCheck();
                }
            });
        }

        private void ScheduleEthernetFallbackCheck()
        {
            if (disposed)
                return;
            if (Interlocked.CompareExchange(ref fallbackCheckPending, 1, 0) != 0)
                return;
            try
            {
                fallbackTimer.Change(EthernetFallbackDelayMilliseconds, Timeout.Infinite);
            }
            catch (ObjectDisposedException)
            {
                Interlocked.Exchange(ref fallbackCheckPending, 0);
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref fallbackCheckPending, 0);
                Logger.Warn("安排 Wi-Fi 回退检查失败: " + ex.Message);
            }
        }

        private void CancelEthernetFallbackCheck()
        {
            Interlocked.Exchange(ref fallbackCheckPending, 0);
            if (fallbackTimer == null)
                return;
            try
            {
                fallbackTimer.Change(Timeout.Infinite, Timeout.Infinite);
            }
            catch
            {
            }
        }

        private void SchedulePauseResumeCheck()
        {
            if (disposed)
                return;
            DateTime now = DateTime.Now;
            if (!IsScheduledPause(now))
            {
                CancelPauseResumeCheck();
                return;
            }
            if (Interlocked.CompareExchange(ref pauseResumePending, 1, 0) != 0)
                return;
            try
            {
                DateTime resume = now.Date.AddHours(6);
                if (resume <= now)
                    resume = resume.AddDays(1);
                int delay = (int)Math.Max(1000, Math.Min((double)int.MaxValue, (resume - now).TotalMilliseconds));
                pauseTimer.Change(delay, Timeout.Infinite);
            }
            catch (ObjectDisposedException)
            {
                Interlocked.Exchange(ref pauseResumePending, 0);
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref pauseResumePending, 0);
                Logger.Warn("安排计划暂停恢复检查失败: " + ex.Message);
            }
        }

        private void CancelPauseResumeCheck()
        {
            Interlocked.Exchange(ref pauseResumePending, 0);
            if (pauseTimer == null)
                return;
            try
            {
                pauseTimer.Change(Timeout.Infinite, Timeout.Infinite);
            }
            catch
            {
            }
        }

        private void DoCheck()
        {
            lastCheckUtc = DateTime.UtcNow;
            manualCheckOverride = Interlocked.Exchange(ref manualCheckRequested, 0) != 0;
            DateTime now = DateTime.Now;
            if (IsScheduledPause(now))
            {
                CancelEthernetFallbackCheck();
                SchedulePauseResumeCheck();
                SetState(GuardState.ScheduledPaused, ScheduleRules.GetPauseDescription());
                return;
            }
            CancelPauseResumeCheck();
            if (!settings.AutoReconnectEnabled)
            {
                CancelEthernetFallbackCheck();
                SetState(GuardState.UserPaused, "自动重连已手动暂停。");
                return;
            }

            NetworkSnapshot ethernet = NetworkInspector.FindPhysicalEthernet();
            NetworkSnapshot wifi = NetworkInspector.FindWifi();
            currentSsid = (wifi != null && wifi.IsUp) ? WifiManager.GetConnectedSsid() : "";
            ethernetWasUp = ethernet != null && ethernet.IsUp;
            string activeProfile = string.IsNullOrEmpty(settings.WifiProfile) ? "gdei-WiFi-S" : settings.WifiProfile;
            if (!string.IsNullOrEmpty(currentSsid))
            {
                if (string.Equals(currentSsid, activeProfile, StringComparison.OrdinalIgnoreCase))
                {
                    lastCampusContext = true;
                    lastCampusContextUtc = DateTime.UtcNow;
                }
                else
                {
                    lastCampusContext = false;
                    lastCampusContextUtc = null;
                }
            }
            if (ethernet == null || !ethernet.IsUp)
            {
                ethernetStableSince = null;
                lastProbeOnline = false;
            }

            if (ethernet != null && ethernet.IsUp)
            {
                CancelEthernetFallbackCheck();
                ethernetDownSince = null;
                if (ethernet.Gateway != null)
                {
                    if (NetworkInspector.IsCampusGateway(ethernet.Gateway))
                    {
                        lastCampusContext = true;
                        lastCampusContextUtc = DateTime.UtcNow;
                        if (!string.Equals(settings.LastCampusGateway, ethernet.Gateway.ToString(),
                            StringComparison.OrdinalIgnoreCase))
                        {
                            settings.LastCampusGateway = ethernet.Gateway.ToString();
                            store.Save(settings);
                        }
                    }
                    else
                    {
                        lastCampusContext = false;
                        lastCampusContextUtc = null;
                    }
                }

                if (!ethernet.HasIPv4)
                {
                    SetState(GuardState.Waiting, "有线已连接，正在等待获取 IP 地址。");
                    return;
                }

                bool online = ProbeInternet(ethernet.IPv4);
                lastProbeOnline = online;
                if (online)
                {
                    HandleEthernetOnline(ethernet, wifi);
                    return;
                }

                if (!IsCampusContext(ethernet))
                {
                    SetState(GuardState.EnvironmentUnknown, "当前网络不是校园网或校园门户不可达，已停止自动认证。");
                    return;
                }
                if (credentialBlocked)
                {
                    SetState(GuardState.CredentialError, "账号或密码错误，自动认证已停止，请修改账号设置。");
                    return;
                }
                if (manualActionBlocked && !manualCheckOverride)
                {
                    SetState(GuardState.ManualActionRequired, "门户要求验证码或二次认证，自动认证已停止；请手动完成验证后点击“立即检测”。");
                    return;
                }
                if (!CanAttempt())
                {
                    SetState(GuardState.Waiting, NextAttemptDescription("等待下一次有线认证"));
                    return;
                }

                SetState(GuardState.Authenticating, "正在重新认证有线校园网...");
                string[] candidates = new string[]
                {
                    "https://nac.gdei.edu.cn/portal/2021/login.jsp?nasip=192.168.104.2",
                    "http://nac.gdei.edu.cn/portal/2021/login.jsp?nasip=192.168.104.2"
                };
                AuthResult result = AuthenticatePortal(candidates, ethernet.IPv4);
                HandleAuthResult(result, ethernet.IPv4, false);
                return;
            }

            if (ethernetDownSince == null)
            {
                ethernetDownSince = DateTime.UtcNow;
                ScheduleEthernetFallbackCheck();
                SetState(GuardState.Waiting, "检测到有线网卡断开，等待 10 秒确认后再启动 Wi-Fi 回退。");
                return;
            }
            if (DateTime.UtcNow - ethernetDownSince.Value < TimeSpan.FromSeconds(10))
            {
                int remaining = 10 - (int)(DateTime.UtcNow - ethernetDownSince.Value).TotalSeconds;
                if (remaining < 0) remaining = 0;
                ScheduleEthernetFallbackCheck();
                SetState(GuardState.Waiting, "有线网卡持续断开，约 " + remaining + " 秒后尝试 Wi-Fi。");
                return;
            }

            CancelEthernetFallbackCheck();
            HandleWifiFallback(wifi);
        }

        private void HandleEthernetOnline(NetworkSnapshot ethernet, NetworkSnapshot wifi)
        {
            ResetFailures();
            manualActionBlocked = false;
            if (wifiFallbackActive && wifi != null && wifi.IsUp)
            {
                EthernetReturnPolicy policy = settings.GetReturnPolicy();
                if (policy == EthernetReturnPolicy.AutoAfter60Seconds)
                {
                    if (!ethernetStableSince.HasValue)
                        ethernetStableSince = DateTime.UtcNow;
                    double stableSeconds = (DateTime.UtcNow - ethernetStableSince.Value).TotalSeconds;
                    if (stableSeconds >= 60)
                    {
                        WifiManager.Disconnect(wifi.Name);
                        wifiFallbackActive = false;
                        ethernetStableSince = null;
                        SetState(GuardState.Online, "有线校园网已稳定 60 秒，已切回有线。");
                        return;
                    }
                    SetState(GuardState.Online, "有线已恢复，等待稳定 60 秒后切回有线。");
                    return;
                }
                if (policy == EthernetReturnPolicy.KeepWifiManual || policy == EthernetReturnPolicy.KeepWifiNoAuto)
                {
                    SetState(GuardState.Online, "有线已恢复；当前策略保留 Wi-Fi，不自动断开。");
                    return;
                }
            }
            SetState(GuardState.Online, "校园网在线（有线 " + DescribeShort(ethernet) + "）。");
        }

        private void HandleWifiFallback(NetworkSnapshot wifi)
        {
            string profile = settings.WifiProfile;
            if (string.IsNullOrEmpty(profile))
                profile = "gdei-WiFi-S";

            bool connectedProfile = wifi != null && wifi.IsUp && wifi.HasIPv4 &&
                string.Equals(currentSsid, profile, StringComparison.OrdinalIgnoreCase);
            if (connectedProfile)
            {
                ProcessConnectedWifi(wifi, profile);
                return;
            }

            if (nextAttemptUtc.HasValue && DateTime.UtcNow < nextAttemptUtc.Value)
            {
                SetState(GuardState.Waiting, NextAttemptDescription("等待下一次 Wi-Fi 连接"));
                return;
            }

            if (wifi == null)
            {
                if (!notifiedWifiMissing)
                {
                    notifiedWifiMissing = true;
                    Notify("未检测到无线网卡", "请检查 Windows 无线网卡是否已启用。", ToolTipIcon.Warning);
                }
                SetState(GuardState.EnvironmentUnknown, "未检测到无线网卡，无法执行 Wi-Fi 回退。");
                return;
            }

            string radioMessage;
            WifiRadioAvailability radioState = WifiManager.GetRadioAvailability(wifi, out radioMessage);
            if (radioState == WifiRadioAvailability.SoftwareOff ||
                radioState == WifiRadioAvailability.HardwareOff)
            {
                if (WifiRadioPolicy.ShouldAttemptEnable(settings.AutoEnableWifi, radioState) &&
                    !wifiRadioEnableAttempted)
                {
                    wifiRadioEnableAttempted = true;
                    if (WifiManager.TryEnableSoftwareRadio(wifi, out radioMessage))
                    {
                        Logger.Info(radioMessage);
                        radioState = WifiManager.GetRadioAvailability(wifi, out radioMessage);
                    }
                    else
                    {
                        Logger.Warn(radioMessage);
                    }
                }

                if (radioState != WifiRadioAvailability.On)
                {
                    string unavailable = string.IsNullOrEmpty(radioMessage)
                        ? WifiRadioPolicy.DescribeUnavailable(radioState)
                        : radioMessage;
                    if (settings.AutoEnableWifi &&
                        radioState == WifiRadioAvailability.SoftwareOff &&
                        !wifiRadioEnableAttempted)
                    {
                        unavailable += " 可在托盘中开启“自动开启 Wi-Fi”后重试。";
                    }
                    if (!notifiedWifiRadioUnavailable)
                    {
                        notifiedWifiRadioUnavailable = true;
                        Notify("Wi-Fi 无线电未开启", unavailable, ToolTipIcon.Warning);
                    }
                    SetState(GuardState.EnvironmentUnknown,
                        unavailable + " 已暂停 Wi-Fi 回退，等待网络状态变化或用户开启 Wi-Fi。");
                    return;
                }
            }

            bool campusEvidence = IsRecentCampusContext() || WifiManager.IsSsidVisible(profile);
            if (!campusEvidence)
            {
                SetState(GuardState.EnvironmentUnknown, "未检测到校园网特征，且未发现 " + profile + "，已暂停 Wi-Fi 回退。");
                return;
            }
            if (!WifiManager.IsProfileSaved(profile))
            {
                SetState(GuardState.EnvironmentUnknown, "Windows 中没有保存 " + profile + " 的无线配置。");
                return;
            }
            if (!CanAttempt())
            {
                SetState(GuardState.Waiting, NextAttemptDescription("等待下一次 Wi-Fi 连接"));
                return;
            }

            SetState(GuardState.WifiFallback, "有线断开，正在连接 Wi-Fi " + profile + "...");
            if (!WifiManager.Connect(profile, wifi.Name))
            {
                if (!notifiedWifiConnectFailure)
                {
                    notifiedWifiConnectFailure = true;
                    Notify("Wi-Fi 回退失败", "无法连接 " + profile + "，请确认无线网卡和无线开关已开启。", ToolTipIcon.Warning);
                }
                ScheduleFailure("无法连接 " + profile + "。");
                SetState(GuardState.Waiting, NextAttemptDescription("等待重试 Wi-Fi 连接"));
                return;
            }

            if (!WifiManager.WaitForIp(profile, 30000))
            {
                ScheduleFailure("已发出 Wi-Fi 连接请求，但没有取得可用 IP。");
                SetState(GuardState.Waiting, NextAttemptDescription("等待重试 Wi-Fi 连接"));
                return;
            }

            NetworkSnapshot connected = NetworkInspector.FindWifi();
            currentSsid = WifiManager.GetConnectedSsid();
            if (connected == null || !connected.HasIPv4)
            {
                ScheduleFailure("Wi-Fi 已连接但网络尚未就绪。");
                SetState(GuardState.Waiting, NextAttemptDescription("等待重试 Wi-Fi 连接"));
                return;
            }
            ProcessConnectedWifi(connected, profile);
        }

        private void ProcessConnectedWifi(NetworkSnapshot wifi, string profile)
        {
            bool online = ProbeInternet(wifi.IPv4);
            lastProbeOnline = online;
            if (online)
            {
                wifiFallbackActive = true;
                ResetFailures();
                manualActionBlocked = false;
                SetState(GuardState.Online, "Wi-Fi " + profile + " 已连接并恢复上网。");
                return;
            }

            if (credentialBlocked)
            {
                SetState(GuardState.CredentialError, "账号或密码错误，自动认证已停止，请修改账号设置。");
                return;
            }
            if (manualActionBlocked && !manualCheckOverride)
            {
                SetState(GuardState.ManualActionRequired, "门户要求验证码或二次认证，自动认证已停止；请手动完成验证后点击“立即检测”。");
                return;
            }
            if (!CanAttempt())
            {
                SetState(GuardState.Waiting, NextAttemptDescription("等待下一次 Wi-Fi 认证"));
                return;
            }

            SetState(GuardState.Authenticating, "正在认证 Wi-Fi 校园门户...");
            string[] candidates = BuildWifiCandidates(wifi.IPv4);
            AuthResult result = AuthenticatePortal(candidates, wifi.IPv4);
            HandleAuthResult(result, wifi.IPv4, true);
        }

        private void HandleAuthResult(AuthResult result, IPAddress localAddress, bool wifi)
        {
            lastAuthMessage = result == null ? "认证结果为空。" : Logger.Sanitize(result.Message, settings.Username);
            if (result != null && result.Outcome == AuthOutcome.Success)
            {
                bool online = ProbeInternet(localAddress);
                lastProbeOnline = online;
                if (online)
                {
                    ResetFailures();
                    credentialBlocked = false;
                    manualActionBlocked = false;
                    if (wifi)
                    {
                        wifiFallbackActive = true;
                        SetState(GuardState.Online, "Wi-Fi 校园认证成功，网络已恢复。");
                    }
                    else
                    {
                        SetState(GuardState.Online, "有线校园认证成功，网络已恢复。");
                    }
                    return;
                }
                ScheduleFailure("门户认证成功，但互联网探测仍未通过。");
                SetState(GuardState.Waiting, NextAttemptDescription("等待重新验证网络"));
                return;
            }

            if (result != null &&
                (result.Outcome == AuthOutcome.TransientFailure ||
                 result.Outcome == AuthOutcome.FormParseFailure))
            {
                bool onlineAfterAttempt = ProbeInternet(localAddress);
                lastProbeOnline = onlineAfterAttempt;
                if (onlineAfterAttempt)
                {
                    ResetFailures();
                    credentialBlocked = false;
                    manualActionBlocked = false;
                    lastAuthMessage = "门户响应格式未识别，但互联网已恢复，按认证成功处理。";
                    if (wifi)
                    {
                        wifiFallbackActive = true;
                        SetState(GuardState.WifiFallback, "Wi-Fi 门户响应格式未识别，但互联网已恢复。");
                    }
                    else
                    {
                        SetState(GuardState.Online, "有线门户响应格式未识别，但互联网已恢复。");
                    }
                    return;
                }
            }

            if (result != null && result.Outcome == AuthOutcome.ManualActionRequired)
            {
                manualActionBlocked = true;
                string manualMessage = Logger.Sanitize(result.Message, settings.Username);
                lastAuthMessage = manualMessage;
                SetState(GuardState.ManualActionRequired, manualMessage + " 自动认证已停止，请手动完成验证后点击“立即检测”。");
                Notify("校园网需要人工验证", manualMessage + " 请打开校园网门户手动完成验证码或二次认证。", ToolTipIcon.Warning);
                return;
            }

            if (result != null && result.Outcome == AuthOutcome.RejectedCredentials)
            {
                credentialBlocked = true;
                string rejectedMessage = Logger.Sanitize(result.Message, settings.Username);
                lastAuthMessage = rejectedMessage;
                SetState(GuardState.CredentialError, rejectedMessage + " 自动认证已停止。");
                Notify("校园网认证失败", rejectedMessage + " 请在账号设置中修改凭据。", ToolTipIcon.Error);
                return;
            }

            string message = result == null ? "认证失败。" : Logger.Sanitize(result.Message, settings.Username);
            lastAuthMessage = message;
            ScheduleFailure(message);
            if (failureCount == 3)
                Notify("校园网重连持续失败", message + " 程序会继续每 30 秒尝试。", ToolTipIcon.Warning);
            SetState(GuardState.Waiting, NextAttemptDescription("认证失败，等待重试: " + message));
        }

        private AuthResult AuthenticatePortal(string[] candidates, IPAddress localAddress)
        {
            DateTime now = DateTime.UtcNow;
            bool allowBrowserFallback = !nextBrowserFallbackUtc.HasValue ||
                now >= nextBrowserFallbackUtc.Value;
            AuthResult result = PortalAuthenticator.Authenticate(candidates, localAddress,
                settings.Username, password, allowBrowserFallback);
            if (result != null && result.BrowserFallbackAttempted)
            {
                nextBrowserFallbackUtc = now.AddSeconds(
                    RetryPolicy.BrowserFallbackThrottleSeconds);
            }
            return result;
        }

        private string[] BuildWifiCandidates(IPAddress localAddress)
        {
            List<string> candidates = new List<string>();
            string captured;
            if (PortalAuthenticator.DiscoverCaptivePortal(localAddress, out captured) &&
                !string.IsNullOrEmpty(captured))
                candidates.Add(captured);
            candidates.Add("http://nac.gdei.edu.cn/portal/2021/login.jsp");
            candidates.Add("https://nac.gdei.edu.cn/portal/2021/login.jsp");
            return candidates.ToArray();
        }

        private bool ProbeInternet(IPAddress localAddress)
        {
            if (localAddress == null)
                return false;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                CookieContainer cookies = new CookieContainer();
                HttpResult result = HttpHelper.Get("http://www.msftconnecttest.com/connecttest.txt",
                    localAddress, cookies, false);
                if (result != null && result.IsSuccess &&
                    result.Content.IndexOf("Microsoft Connect Test", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
                if (attempt == 0)
                    Thread.Sleep(5000);
            }
            return false;
        }

        private bool IsCampusContext(NetworkSnapshot snapshot)
        {
            if (snapshot != null && snapshot.Gateway != null)
                return NetworkInspector.IsCampusGateway(snapshot.Gateway);
            return IsRecentCampusContext();
        }

        private bool IsRecentCampusContext()
        {
            if (!lastCampusContext || !lastCampusContextUtc.HasValue)
                return false;
            return DateTime.UtcNow - lastCampusContextUtc.Value <= CampusContextLifetime;
        }

        private bool CanAttempt()
        {
            return !nextAttemptUtc.HasValue || DateTime.UtcNow >= nextAttemptUtc.Value;
        }

        private void ScheduleFailure(string message)
        {
            DateTime now = DateTime.UtcNow;
            RetryDecision decision = RetryPolicy.PlanFailure(now, failureCount);
            failureCount = decision.FailureCount;
            nextAttemptUtc = decision.NextAttemptUtc;
            Logger.Warn("自动重连失败，第 " + failureCount + " 次，等待 " +
                decision.DelaySeconds + " 秒: "
                + Logger.Sanitize(message, settings.Username));
        }

        private void ResetFailures()
        {
            failureCount = 0;
            nextAttemptUtc = null;
            nextBrowserFallbackUtc = null;
        }

        private string NextAttemptDescription(string prefix)
        {
            if (nextAttemptUtc.HasValue)
                return prefix + "，最早 " + nextAttemptUtc.Value.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "。";
            return prefix + "。";
        }

        private bool IsScheduledPause(DateTime now)
        {
            return ScheduleRules.IsPaused(now);
        }

        private void SetState(GuardState newState, string detail)
        {
            Post(delegate
            {
                GuardState previousState = state;
                bool recovering = GuardStateTransitions.IsRecovery(previousState, newState);
                state = newState;
                statusDetail = detail ?? "";
                UpdateTray();
                if (statusForm != null && !statusForm.IsDisposed)
                    statusForm.UpdateFromContext();
                if (recovering)
                {
                    Logger.Info("校园网已恢复: " + Logger.Sanitize(detail ?? "", settings.Username));
                    if (settings.NotificationsEnabled && !IsScheduledPause(DateTime.Now))
                        Notify("校园网已恢复", "网络连接已恢复。", ToolTipIcon.Info);
                }
            });
        }

        private void Post(Action action)
        {
            if (action == null)
                return;
            try
            {
                if (synchronizationContext != null)
                    synchronizationContext.Post(delegate { action(); }, null);
                else if (dispatcher != null && dispatcher.IsHandleCreated)
                    dispatcher.BeginInvoke(action);
                else
                    action();
            }
            catch
            {
                try { action(); } catch { }
            }
        }

        private void UpdateTray()
        {
            if (icon == null)
                return;
            autoItem.Checked = settings.AutoReconnectEnabled;
            autoItem.Text = settings.AutoReconnectEnabled ? "自动重连（已启用）" : "自动重连（已暂停）";
            recoveryAutoItem.Checked = settings.GetReturnPolicy() == EthernetReturnPolicy.AutoAfter60Seconds;
            recoveryManualItem.Checked = settings.GetReturnPolicy() == EthernetReturnPolicy.KeepWifiManual;
            recoveryKeepItem.Checked = settings.GetReturnPolicy() == EthernetReturnPolicy.KeepWifiNoAuto;
            wifiRadioItem.Checked = settings.AutoEnableWifi;
            wifiRadioItem.Text = settings.AutoEnableWifi
                ? "有线断开时尝试开启 Wi-Fi（已启用）"
                : "有线断开时尝试开启 Wi-Fi（已禁用）";
            startupItem.Checked = StartupManager.IsEnabled();
            string text = "校园网助手 - " + StateLabel(state);
            if (text.Length > 63)
                text = text.Substring(0, 60) + "...";
            icon.Text = text;
        }

        private void Notify(string title, string message, ToolTipIcon iconType)
        {
            Post(delegate { ShowNotification(title, message, iconType); });
        }

        private void ShowNotification(string title, string message, ToolTipIcon iconType)
        {
            if (disposed || !settings.NotificationsEnabled || IsScheduledPause(DateTime.Now))
                return;
            try
            {
                icon.BalloonTipTitle = title ?? "校园网助手";
                icon.BalloonTipText = message ?? "";
                icon.BalloonTipIcon = iconType;
                icon.ShowBalloonTip(5000);
            }
            catch
            {
            }
        }

        private void ToggleStartup()
        {
            bool enabled = !StartupManager.IsEnabled();
            if (!StartupManager.SetEnabled(enabled))
            {
                Notify("无法修改开机启动", "请检查当前用户注册表权限。", ToolTipIcon.Warning);
                return;
            }
            settings.StartWithWindows = enabled;
            store.Save(settings);
            statusDetail = enabled ? "已开启开机启动。" : "已关闭开机启动。";
            UpdateTray();
            Notify(enabled ? "已开启开机启动" : "已关闭开机启动",
                "电脑登录后" + (enabled ? "会自动启动校园网助手。" : "不会自动启动校园网助手。"),
                ToolTipIcon.Info);
        }

        private void OnNetworkChanged(object sender, EventArgs e)
        {
            wifiRadioEnableAttempted = false;
            notifiedWifiRadioUnavailable = false;
            QueueCheck();
        }

        private void OnNetworkAvailabilityChanged(object sender, NetworkAvailabilityEventArgs e)
        {
            wifiRadioEnableAttempted = false;
            notifiedWifiRadioUnavailable = false;
            QueueCheck();
        }

        private string Describe(NetworkSnapshot snapshot)
        {
            if (snapshot == null)
                return "未检测到";
            string name = string.IsNullOrEmpty(snapshot.Name) ? snapshot.Description : snapshot.Name;
            if (!snapshot.IsUp)
                return name + "（已断开）";
            if (snapshot.IPv4 != null)
                return name + "（" + snapshot.IPv4.ToString() + "）";
            return name + "（已连接，等待 IP）";
        }

        private string DescribeShort(NetworkSnapshot snapshot)
        {
            if (snapshot == null)
                return "未检测到";
            if (snapshot.IPv4 != null)
                return snapshot.IPv4.ToString();
            return snapshot.IsUp ? "已连接" : "未连接";
        }

        private string ReturnPolicyLabel(EthernetReturnPolicy policy)
        {
            if (policy == EthernetReturnPolicy.KeepWifiManual)
                return "保留 Wi-Fi，手动切回有线";
            if (policy == EthernetReturnPolicy.KeepWifiNoAuto)
                return "保持 Wi-Fi，不自动切回";
            return "稳定 60 秒后自动切回有线";
        }

        private string StateLabel(GuardState value)
        {
            switch (value)
            {
                case GuardState.Online:
                    return "在线";
                case GuardState.Waiting:
                    return "等待网络";
                case GuardState.Authenticating:
                    return "正在认证";
                case GuardState.WifiFallback:
                    return "Wi-Fi 回退";
                case GuardState.ScheduledPaused:
                    return "学校计划断网暂停";
                case GuardState.UserPaused:
                    return "手动暂停";
                case GuardState.EnvironmentUnknown:
                    return "非校园环境";
                case GuardState.CredentialError:
                    return "凭据错误";
                case GuardState.ManualActionRequired:
                    return "需要人工处理";
                default:
                    return "正在启动";
            }
        }
    }
}
