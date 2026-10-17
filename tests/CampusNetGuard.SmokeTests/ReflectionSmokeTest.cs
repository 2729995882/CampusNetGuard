using System;
using System.Collections;
using System.IO;
using System.Net;
using System.Reflection;
using System.Collections.Generic;
using System.Runtime.Serialization;

internal static class ReflectionSmokeTest
{
    private static int failures;
    private static Assembly campus;

    private static void Assert(bool condition, string name)
    {
        Console.WriteLine((condition ? "PASS " : "FAIL ") + name);
        if (!condition) failures++;
    }

    private static object Static(string typeName, string methodName, params object[] args)
    {
        Type type = campus.GetType("CampusNetGuard." + typeName, true);
        MethodInfo method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
        return method.Invoke(null, args);
    }

    private static void Main(string[] args)
    {
        string assemblyPath = args != null && args.Length > 0
            ? args[0]
            : @"D:\Codex\2026-09-28\referenced-chatgpt-conversation-this-is-an\outputs\CampusNetGuard.exe";
        campus = Assembly.LoadFrom(assemblyPath);

        Assert(!(bool)Static("ScheduleRules", "IsPaused", new DateTime(2026, 9, 27, 0, 59, 59)), "周日 00:59:59 不暂停");
        Assert((bool)Static("ScheduleRules", "IsPaused", new DateTime(2026, 9, 27, 1, 0, 0)), "周日 01:00:00 暂停");
        Assert((bool)Static("ScheduleRules", "IsPaused", new DateTime(2026, 9, 27, 5, 59, 59)), "周日 05:59:59 暂停");
        Assert(!(bool)Static("ScheduleRules", "IsPaused", new DateTime(2026, 9, 27, 6, 0, 0)), "周日 06:00:00 不暂停");
        Assert(!(bool)Static("ScheduleRules", "IsPaused", new DateTime(2026, 10, 2, 1, 0, 0)), "周五 01:00:00 不暂停");
        Assert(!(bool)Static("ScheduleRules", "IsPaused", new DateTime(2026, 10, 3, 1, 0, 0)), "周六 01:00:00 不暂停");
        Type scheduleRules = campus.GetType("CampusNetGuard.ScheduleRules", true);
        MethodInfo pauseDescription = scheduleRules.GetMethod("GetPauseDescription",
            BindingFlags.Public | BindingFlags.Static);
        Assert(pauseDescription != null, "计划暂停说明方法存在");
        if (pauseDescription != null)
        {
            string description = Convert.ToString(pauseDescription.Invoke(null, null));
            Assert(description.Contains("学校计划断网") && description.Contains("不会断开网络"),
                "计划暂停说明明确不会主动断网");
        }

        Type retryPolicy = campus.GetType("CampusNetGuard.RetryPolicy", false);
        Assert(retryPolicy != null, "普通失败重试策略类型存在");
        MethodInfo planFailure = retryPolicy == null ? null :
            retryPolicy.GetMethod("PlanFailure", BindingFlags.Public | BindingFlags.Static);
        Assert(planFailure != null, "普通失败重试计划存在");
        if (planFailure != null)
        {
            DateTime retryNow = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);
            object retryDecision = planFailure.Invoke(null, new object[] { retryNow, 7 });
            Type retryDecisionType = retryDecision.GetType();
            DateTime nextAttempt = (DateTime)retryDecisionType.GetField("NextAttemptUtc").GetValue(retryDecision);
            int retrySeconds = (int)retryDecisionType.GetField("DelaySeconds").GetValue(retryDecision);
            int nextFailureCount = (int)retryDecisionType.GetField("FailureCount").GetValue(retryDecision);
            Assert(retrySeconds == 30, "普通认证失败固定等待 30 秒");
            Assert(nextAttempt == retryNow.AddSeconds(30), "下次重试时间严格为 30 秒后");
            Assert(nextFailureCount == 8, "连续失败次数继续累计而不截断");
        }
        FieldInfo browserThrottle = retryPolicy == null ? null :
            retryPolicy.GetField("BrowserFallbackThrottleSeconds", BindingFlags.Public | BindingFlags.Static);
        Assert(browserThrottle != null && (int)browserThrottle.GetValue(null) == 120,
            "浏览器回退最多每 2 分钟一次");

        Type wifiRadioState = campus.GetType("CampusNetGuard.WifiRadioAvailability", false);
        Assert(wifiRadioState != null && Enum.IsDefined(wifiRadioState, "SoftwareOff") &&
            Enum.IsDefined(wifiRadioState, "HardwareOff"), "Wi-Fi 无线电状态区分软硬件关闭");
        Type wifiRadioPolicy = campus.GetType("CampusNetGuard.WifiRadioPolicy", false);
        MethodInfo shouldAttemptRadioEnable = wifiRadioPolicy == null ? null :
            wifiRadioPolicy.GetMethod("ShouldAttemptEnable", BindingFlags.Public | BindingFlags.Static);
        Assert(shouldAttemptRadioEnable != null, "Wi-Fi 自动开启策略存在");
        if (shouldAttemptRadioEnable != null && wifiRadioState != null)
        {
            object softwareOff = Enum.Parse(wifiRadioState, "SoftwareOff");
            object hardwareOff = Enum.Parse(wifiRadioState, "HardwareOff");
            Assert((bool)shouldAttemptRadioEnable.Invoke(null, new object[] { true, softwareOff }),
                "软件 Wi-Fi 开关关闭时允许自动开启");
            Assert(!(bool)shouldAttemptRadioEnable.Invoke(null, new object[] { false, softwareOff }),
                "关闭自动开启设置后不修改 Wi-Fi 开关");
            Assert(!(bool)shouldAttemptRadioEnable.Invoke(null, new object[] { true, hardwareOff }),
                "硬件 Wi-Fi 开关关闭时不盲目尝试");
        }

        Assert((bool)Static("NetworkInspector", "IsCampusGateway", IPAddress.Parse("10.0.104.2")), "校园网关 10.0.104.2");
        Assert(!(bool)Static("NetworkInspector", "IsCampusGateway", IPAddress.Parse("192.168.1.1")), "非校园网关 192.168.1.1");
        Assert(!(bool)Static("NetworkInspector", "IsCampusGateway", IPAddress.Parse("10.0.103.9")), "非校园网关 10.0.103.9");

        string html = "<form id=\"form1\"><input name=\"wlanacname\" value=\"ac1\"><input name=\"nasip\" value=\"10.0.104.2\"><input name=\"userName\"><input name=\"userPwd\"></form>";
        IEnumerable fields = (IEnumerable)Static("HtmlFormParser", "Parse", html);
        bool hasWlan = false, hasNas = false, hasUser = false, hasPwd = false;
        foreach (object field in fields)
        {
            string name = (string)field.GetType().GetField("Name").GetValue(field);
            hasWlan |= name == "wlanacname";
            hasNas |= name == "nasip";
            hasUser |= name == "userName";
            hasPwd |= name == "userPwd";
        }
        Assert(hasWlan && hasNas && hasUser && hasPwd, "动态表单字段解析");

        string sanitized = (string)Static("Logger", "Sanitize", "账号=202612345 密码=secret userPwd=abc123", "202612345");
        Assert(sanitized.Contains("20****"), "账号脱敏");
        Assert(!sanitized.Contains("密码=secret") && !sanitized.Contains("userPwd=abc123"), "密码与提交字段脱敏");

        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Static;
        Type context = campus.GetType("CampusNetGuard.GuardContext", true);
        Assert(context.GetField("pauseTimer", flags) != null, "暂停恢复定时器存在");
        Assert(context.GetMethod("SchedulePauseResumeCheck", flags) != null, "暂停恢复调度逻辑存在");
        Assert(context.GetMethod("ProcessConnectedWifi", flags) != null, "Wi-Fi 门户处理逻辑存在");
        Type program = campus.GetType("CampusNetGuard.Program", true);
        Assert(program.GetField("singleInstanceMutex", BindingFlags.NonPublic | BindingFlags.Static) != null, "单实例保护存在");

        Type portal = campus.GetType("CampusNetGuard.PortalAuthenticator", true);
        MethodInfo challengeCheck = portal.GetMethod("HasManualChallenge", flags);
        MethodInfo credentialCheck = portal.GetMethod("LooksLikeCredentialError", flags);
        MethodInfo payloadDecode = portal.GetMethod("TryDecodePayload", flags);
        Type formParser = campus.GetType("CampusNetGuard.HtmlFormParser", true);
        MethodInfo parseForm = formParser.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static);
        string visibleChallengeHtml = "<form id='form1'><input type='text' name='verifyCode'></form>";
        object visibleChallengeFields = parseForm.Invoke(null, new object[] { visibleChallengeHtml });
        Assert((bool)challengeCheck.Invoke(null, new object[] { visibleChallengeHtml, visibleChallengeFields }), "可见验证码页面安全识别");

        string hiddenTemplateHtml = "<form id='form1'><input type='hidden' name='userName'><input type='hidden' id='id_i_p_info_verifyCode' value='验证码'></form>";
        object hiddenTemplateFields = parseForm.Invoke(null, new object[] { hiddenTemplateHtml });
        Assert(!(bool)challengeCheck.Invoke(null, new object[] { hiddenTemplateHtml, hiddenTemplateFields }), "隐藏验证码模板不误判");

        string portalDomFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "chrome-dom3.txt");
        if (File.Exists(portalDomFile))
        {
            string portalDom = File.ReadAllText(portalDomFile);
            object portalDomFields = parseForm.Invoke(null, new object[] { portalDom });
            Assert(!(bool)challengeCheck.Invoke(null, new object[] { portalDom, portalDomFields }), "真实门户普通登录页不误判");
        }
        Assert((bool)credentialCheck.Invoke(null, new object[] { "63032", "" }), "门户 63032 按凭据错误处理");
        object[] decodeArgs = new object[] { "%7B%22errorNumber%22%3A%2263032%22%7D", null };
        Assert((bool)payloadDecode.Invoke(null, decodeArgs) && ((string)decodeArgs[1]).Contains("63032"), "门户响应 URL 解码");

        Type guardState = campus.GetType("CampusNetGuard.GuardState", true);
        Type authOutcome = campus.GetType("CampusNetGuard.AuthOutcome", true);
        Assert(Enum.IsDefined(guardState, "ManualActionRequired"), "人工处理状态存在");
        Assert(Enum.IsDefined(authOutcome, "ManualActionRequired"), "人工验证认证结果存在");
        Assert(context.GetField("manualActionBlocked", flags) != null, "人工验证锁定字段存在");

        object uninitializedContext = FormatterServices.GetUninitializedObject(context);
        FieldInfo credentialBlocked = context.GetField("credentialBlocked", flags);
        FieldInfo manualActionBlocked = context.GetField("manualActionBlocked", flags);
        FieldInfo failureCount = context.GetField("failureCount", flags);
        FieldInfo nextAttemptUtc = context.GetField("nextAttemptUtc", flags);
        credentialBlocked.SetValue(uninitializedContext, true);
        manualActionBlocked.SetValue(uninitializedContext, true);
        failureCount.SetValue(uninitializedContext, 3);
        nextAttemptUtc.SetValue(uninitializedContext, (DateTime?)DateTime.UtcNow.AddMinutes(5));
        context.GetMethod("ResetFailures", flags).Invoke(uninitializedContext, null);
        Assert((bool)credentialBlocked.GetValue(uninitializedContext), "重置普通失败不会解除凭据锁定");
        Assert((bool)manualActionBlocked.GetValue(uninitializedContext), "重置普通失败不会解除人工验证锁定");
        Assert((int)failureCount.GetValue(uninitializedContext) == 0, "重置普通失败会清空退避计数");

        object retryContext = FormatterServices.GetUninitializedObject(context);
        nextAttemptUtc.SetValue(retryContext, null);
        failureCount.SetValue(retryContext, 100);
        MethodInfo canAttempt = context.GetMethod("CanAttempt", flags);
        Assert((bool)canAttempt.Invoke(retryContext, null), "普通失败不再受每小时 6 次限制");

        Type guardTransitions = campus.GetType("CampusNetGuard.GuardStateTransitions", false);
        MethodInfo isRecovery = guardTransitions == null ? null :
            guardTransitions.GetMethod("IsRecovery", BindingFlags.Public | BindingFlags.Static);
        Assert(isRecovery != null, "网络恢复状态判定存在");
        Type guardStateType = campus.GetType("CampusNetGuard.GuardState", true);
        if (isRecovery != null)
        {
            object waiting = Enum.Parse(guardStateType, "Waiting");
            object wifiFallback = Enum.Parse(guardStateType, "WifiFallback");
            object online = Enum.Parse(guardStateType, "Online");
            Assert(!(bool)isRecovery.Invoke(null, new object[] { waiting, wifiFallback }),
                "进入 Wi-Fi 回退不算网络已恢复");
            Assert((bool)isRecovery.Invoke(null, new object[] { wifiFallback, online }),
                "Wi-Fi 回退成功后算网络已恢复");
        }

        Type appSettings = campus.GetType("CampusNetGuard.AppSettings", true);
        object defaultSettings = Activator.CreateInstance(appSettings);
        FieldInfo autoEnableWifi = appSettings.GetField("AutoEnableWifi",
            BindingFlags.Public | BindingFlags.Instance);
        Assert(autoEnableWifi != null && (bool)autoEnableWifi.GetValue(defaultSettings),
            "默认允许有线断开后尝试开启 Wi-Fi");
        Type wifiManager = campus.GetType("CampusNetGuard.WifiManager", true);
        Assert(wifiManager.GetMethod("TryEnableSoftwareRadio", BindingFlags.Public | BindingFlags.Static) != null,
            "Wi-Fi 软件无线电开启接口存在");

        string testLogDirectory = Path.Combine(Path.GetTempPath(),
            "CampusNetGuard-log-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            object normalized = Static("AppPaths", "NormalizeLogDirectory", testLogDirectory);
            Assert(normalized is string &&
                string.Equals((string)normalized, Path.GetFullPath(testLogDirectory), StringComparison.OrdinalIgnoreCase),
                "日志目录路径规范化");
            Assert((bool)Static("AppPaths", "TrySetLogDirectory", testLogDirectory), "日志目录可配置");
            Type appPaths = campus.GetType("CampusNetGuard.AppPaths", true);
            string activeDirectory = (string)appPaths.GetProperty("LogDirectory",
                BindingFlags.Public | BindingFlags.Static).GetValue(null, null);
            Assert(string.Equals(activeDirectory, Path.GetFullPath(testLogDirectory),
                StringComparison.OrdinalIgnoreCase), "日志目录切换立即生效");
        }
        finally
        {
            try
            {
                if (Directory.Exists(testLogDirectory))
                    Directory.Delete(testLogDirectory, true);
            }
            catch { }
        }

        if (args != null && Array.IndexOf(args, "--browser") >= 0)
        {
            Type browserSession = campus.GetType("CampusNetGuard.PortalBrowserSession", true);
            object available = browserSession.GetMethod("IsBrowserAvailable",
                BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
            Assert(available is bool && (bool)available, "浏览器回退可用浏览器已发现");

            object[] startArgs = new object[] { null };
            object session = browserSession.GetMethod("Start",
                BindingFlags.Public | BindingFlags.Static).Invoke(null, startArgs);
            Assert(session != null, "浏览器回退会话可启动");
            if (session == null)
                Console.WriteLine("BROWSER_START_ERROR " + Convert.ToString(startArgs[0]));
            if (session != null)
            {
                try
                {
                    object[] loadArgs = new object[]
                    {
                        "https://nac.gdei.edu.cn/portal/2021/login.jsp?nasip=192.168.104.2",
                        null, null, null
                    };
                    bool loaded = (bool)browserSession.GetMethod("LoadPage",
                        BindingFlags.Public | BindingFlags.Instance).Invoke(session, loadArgs);
                    string portalHtml = Convert.ToString(loadArgs[1]);
                    Assert(loaded && !string.IsNullOrEmpty(portalHtml) && portalHtml.Length > 1000,
                        "浏览器回退可加载门户页面");
                    if (!loaded)
                        Console.WriteLine("BROWSER_LOAD_ERROR " + Convert.ToString(loadArgs[3]));
                    Assert(!string.IsNullOrEmpty(portalHtml) &&
                        portalHtml.IndexOf("f_preLoginForInputPanel", StringComparison.OrdinalIgnoreCase) >= 0,
                        "浏览器回退页面保留门户原生登录脚本");
                    IEnumerable browserFields = (IEnumerable)Static("HtmlFormParser", "Parse", portalHtml);
                    int browserFieldCount = 0;
                    bool browserHasUser = false, browserHasPwd = false;
                    foreach (object field in browserFields)
                    {
                        string name = (string)field.GetType().GetField("Name").GetValue(field);
                        browserFieldCount++;
                        browserHasUser |= string.Equals(name, "userName", StringComparison.OrdinalIgnoreCase);
                        browserHasPwd |= string.Equals(name, "userPwd", StringComparison.OrdinalIgnoreCase);
                    }
                    Assert(browserFieldCount > 0 && browserHasUser && browserHasPwd,
                        "浏览器回退门户登录字段可解析");
                }
                finally
                {
                    browserSession.GetMethod("Dispose", BindingFlags.Public | BindingFlags.Instance)
                        .Invoke(session, null);
                }
            }
        }
        Environment.ExitCode = failures == 0 ? 0 : 1;
    }
}
