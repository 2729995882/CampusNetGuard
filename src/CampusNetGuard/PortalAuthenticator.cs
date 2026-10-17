using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;

namespace CampusNetGuard
{
    public enum AuthOutcome
    {
        Success,
        RejectedCredentials,
        TransientFailure,
        FormParseFailure,
        ManualActionRequired
    }

    public sealed class AuthResult
    {
        public AuthOutcome Outcome;
        public string Message = "";
        public string ErrorCode = "";
        public string PortalLink = "";

        public static AuthResult Success()
        {
            AuthResult result = new AuthResult();
            result.Outcome = AuthOutcome.Success;
            result.Message = "门户认证成功";
            return result;
        }

        public static AuthResult Failure(AuthOutcome outcome, string message, string code)
        {
            AuthResult result = new AuthResult();
            result.Outcome = outcome;
            result.Message = message ?? "";
            result.ErrorCode = code ?? "";
            return result;
        }
    }

    public sealed class FormField
    {
        public string Name = "";
        public string Id = "";
        public string Type = "";
        public string Value = "";
        public bool Checked;
    }

    public static class PortalAuthenticator
    {
        private const string ConnectTestUrl = "http://www.msftconnecttest.com/connecttest.txt";
        private const string ExpectedConnectTest = "Microsoft Connect Test";

        public static bool DiscoverCaptivePortal(IPAddress bindAddress, out string portalUrl)
        {
            portalUrl = "";
            CookieContainer cookies = new CookieContainer();
            HttpResult result = HttpHelper.Get(ConnectTestUrl, bindAddress, cookies, false);
            if (!string.IsNullOrEmpty(result.Location))
            {
                portalUrl = MakeAbsolute(ConnectTestUrl, result.Location);
                if (!string.IsNullOrEmpty(portalUrl))
                    return true;
            }

            if (!string.IsNullOrEmpty(result.Content) &&
                result.Content.IndexOf(ExpectedConnectTest, StringComparison.OrdinalIgnoreCase) < 0)
            {
                Match match = Regex.Match(result.Content,
                    @"https?://[^""'<>\s]+nac\.gdei\.edu\.cn[^""'<>\s]*",
                    RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    portalUrl = WebUtility.HtmlDecode(match.Value);
                    return true;
                }
            }
            return false;
        }

        public static AuthResult Authenticate(string[] candidates, IPAddress bindAddress,
            string username, string password)
        {
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
                return AuthResult.Failure(AuthOutcome.RejectedCredentials, "尚未配置校园网账号或密码。", "CONFIG");

            string lastError = "";
            if (candidates != null)
            {
                for (int i = 0; i < candidates.Length; i++)
                {
                    string candidate = candidates[i];
                    if (string.IsNullOrEmpty(candidate))
                        continue;
                    AuthResult result = AuthenticateCandidate(candidate, bindAddress, username, password);
                    if (result.Outcome != AuthOutcome.FormParseFailure)
                        return result;
                    lastError = result.Message;
                }
            }

            if (string.IsNullOrEmpty(lastError))
                lastError = "找不到可解析的校园门户登录页面。";
            return AuthResult.Failure(AuthOutcome.FormParseFailure, lastError, "PORTAL_FORM");
        }

        private static AuthResult AuthenticateCandidate(string entryUrl, IPAddress bindAddress,
            string username, string password)
        {
            CookieContainer cookies = new CookieContainer();
            HttpResult page = HttpHelper.Get(entryUrl, bindAddress, cookies, true);
            if (page == null || string.IsNullOrEmpty(page.Content) ||
                page.Content.IndexOf("<input", StringComparison.OrdinalIgnoreCase) < 0)
                return AuthenticateWithBrowser(entryUrl, username, password,
                    "门户页面为空或未返回登录表单。");

            List<FormField> fields = HtmlFormParser.Parse(page.Content);
            if (fields.Count == 0)
                return AuthenticateWithBrowser(entryUrl, username, password,
                    "门户登录表单中没有可提交字段。");

            if (HasManualChallenge(page.Content, fields))
                return AuthResult.Failure(AuthOutcome.ManualActionRequired,
                    "门户要求验证码或二次认证，自动认证已停止，请手动完成验证。", "PORTAL_MANUAL");

            SetField(fields, IsUserNameField, username);
            SetField(fields, IsPasswordField, Convert.ToBase64String(Encoding.UTF8.GetBytes(password)));
            if (FindField(fields, IsUserNameField) == null)
                fields.Add(new FormField { Name = "userName", Value = username });
            if (FindField(fields, IsPasswordField) == null)
                fields.Add(new FormField { Name = "userPwd", Value = Convert.ToBase64String(Encoding.UTF8.GetBytes(password)) });

            Uri finalUri;
            if (!Uri.TryCreate(page.FinalUrl, UriKind.Absolute, out finalUri) ||
                string.IsNullOrEmpty(finalUri.Scheme) || string.IsNullOrEmpty(finalUri.Host))
                finalUri = new Uri(entryUrl);

            string postUrl = finalUri.GetLeftPart(UriPartial.Authority) +
                "/portal/pws?t=li&ifEmailAuth=false";
            string formBody = BuildFormBody(fields);
            HttpResult response = HttpHelper.PostForm(postUrl, formBody, page.FinalUrl, bindAddress, cookies);
            if (response == null || string.IsNullOrEmpty(response.Content))
                return AuthenticateWithBrowser(entryUrl, username, password,
                    "门户登录请求没有返回内容。");

            return ParseAuthResponse(response.Content);
        }

        private static AuthResult AuthenticateWithBrowser(string entryUrl, string username,
            string password, string directFailure)
        {
            if (!PortalBrowserSession.IsBrowserAvailable())
                return AuthResult.Failure(AuthOutcome.FormParseFailure,
                    "直连门户失败（" + directFailure + "），且未找到 Google Chrome 或 Microsoft Edge。",
                    "PORTAL_BROWSER_MISSING");

            string startError;
            PortalBrowserSession session = PortalBrowserSession.Start(out startError);
            if (session == null)
                return AuthResult.Failure(AuthOutcome.FormParseFailure,
                    "直连门户失败（" + directFailure + "），浏览器回退启动失败：" +
                    (string.IsNullOrEmpty(startError) ? "未知原因。" : startError),
                    "PORTAL_BROWSER_START");

            using (session)
            {
                string content;
                string finalUrl;
                string loadError;
                if (!session.LoadPage(entryUrl, out content, out finalUrl, out loadError))
                    return AuthResult.Failure(AuthOutcome.FormParseFailure,
                        "浏览器回退无法打开门户：" +
                        (string.IsNullOrEmpty(loadError) ? "页面加载失败。" : loadError),
                        "PORTAL_BROWSER_LOAD");

                List<FormField> fields = HtmlFormParser.Parse(content);
                if (fields.Count == 0)
                    return AuthResult.Failure(AuthOutcome.FormParseFailure,
                        "浏览器已打开门户，但没有找到登录表单。", "PORTAL_BROWSER_FORM");

                if (HasManualChallenge(content, fields))
                    return AuthResult.Failure(AuthOutcome.ManualActionRequired,
                        "门户要求验证码或二次认证，自动认证已停止，请手动完成验证。", "PORTAL_MANUAL");

                SetField(fields, IsUserNameField, username);
                SetField(fields, IsPasswordField, Convert.ToBase64String(Encoding.UTF8.GetBytes(password)));
                if (FindField(fields, IsUserNameField) == null)
                    fields.Add(new FormField { Name = "userName", Value = username });
                if (FindField(fields, IsPasswordField) == null)
                    fields.Add(new FormField { Name = "userPwd", Value = Convert.ToBase64String(Encoding.UTF8.GetBytes(password)) });

                string responseContent;
                string submitError;
                if (!session.SubmitLogin(username, password, BuildFormBody(fields),
                    out responseContent, out submitError))
                    return AuthResult.Failure(AuthOutcome.TransientFailure,
                        "浏览器回退认证失败：" +
                        (string.IsNullOrEmpty(submitError) ? "门户请求失败。" : submitError),
                        "PORTAL_BROWSER_SUBMIT");

                return ParseAuthResponse(responseContent);
            }
        }

        private static AuthResult ParseAuthResponse(string responseContent)
        {
            List<FormField> responseFields = HtmlFormParser.Parse(responseContent);
            if (HasManualChallenge(responseContent, responseFields))
                return AuthResult.Failure(AuthOutcome.ManualActionRequired,
                    "门户要求验证码或二次认证，自动认证已停止。", "PORTAL_MANUAL");

            string json;
            if (!TryDecodePayload(responseContent, out json))
                return AuthResult.Failure(AuthOutcome.TransientFailure,
                    "门户返回内容无法解析。", "PORTAL_RESPONSE");

            string portalLink = FindJsonValue(json, "portalLink");
            string errorNumber = FindJsonValue(json, "errorNumber");
            if (string.IsNullOrEmpty(errorNumber))
            {
                AuthResult missing = AuthResult.Failure(AuthOutcome.TransientFailure,
                    "门户响应缺少认证结果。", "PORTAL_RESPONSE");
                missing.PortalLink = portalLink;
                return missing;
            }

            if (errorNumber == "1")
            {
                AuthResult success = AuthResult.Success();
                success.PortalLink = portalLink;
                return success;
            }

            string errorCode = FindErrorCode(json);
            string message = FindErrorDescription(json);
            if (LooksLikeCredentialError(errorCode, message))
            {
                AuthResult rejected = AuthResult.Failure(AuthOutcome.RejectedCredentials,
                    string.IsNullOrEmpty(message) ? "账号或密码错误。" : message, errorCode);
                rejected.PortalLink = portalLink;
                return rejected;
            }
            AuthResult failure = AuthResult.Failure(AuthOutcome.TransientFailure,
                string.IsNullOrEmpty(message) ? "门户认证失败。" : message, errorCode);
            failure.PortalLink = portalLink;
            return failure;
        }
        private static bool HasManualChallenge(string html, List<FormField> fields)
        {
            // The GDEI portal embeds many hidden localization template fields whose
            // values contain words such as "验证码" and "动态密码".  They are not
            // an active challenge.  Only visible, submittable challenge fields stop
            // automatic authentication.
            if (fields == null)
                return false;

            foreach (FormField field in fields)
            {
                if (field == null || string.Equals(field.Type, "hidden", StringComparison.OrdinalIgnoreCase))
                    continue;
                string descriptor = ((field.Name ?? "") + " " + (field.Id ?? "") + " " + (field.Type ?? "")).ToLowerInvariant();
                if (ContainsAny(descriptor, "captcha", "verifycode", "verificationcode", "authcode", "smscode", "otp", "验证码"))
                    return true;
            }
            return false;
        }

        private static bool ContainsAny(string value, params string[] terms)
        {
            foreach (string term in terms)
            {
                if (!string.IsNullOrEmpty(term) &&
                    value.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }
        private static void SetField(List<FormField> fields, Predicate<FormField> predicate, string value)
        {
            foreach (FormField field in fields)
            {
                if (predicate(field))
                    field.Value = value;
            }
        }

        private static FormField FindField(List<FormField> fields, Predicate<FormField> predicate)
        {
            foreach (FormField field in fields)
            {
                if (predicate(field))
                    return field;
            }
            return null;
        }

        private static bool IsUserNameField(FormField field)
        {
            if (field == null) return false;
            return string.Equals(field.Name, "userName", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(field.Id, "id_userName_", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(field.Id, "id_userName", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsPasswordField(FormField field)
        {
            if (field == null) return false;
            return string.Equals(field.Name, "userPwd", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(field.Id, "id_userPwd_", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(field.Id, "id_userPwd_IE", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(field.Name, "userPwd_IE", StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildFormBody(List<FormField> fields)
        {
            StringBuilder builder = new StringBuilder();
            foreach (FormField field in fields)
            {
                if (field == null || string.IsNullOrEmpty(field.Name))
                    continue;
                if (builder.Length > 0)
                    builder.Append('&');
                builder.Append(Uri.EscapeDataString(field.Name));
                builder.Append('=');
                builder.Append(Uri.EscapeDataString(field.Value ?? ""));
            }
            return builder.ToString();
        }

        private static bool TryDecodePayload(string raw, out string json)
        {
            json = "";
            if (string.IsNullOrEmpty(raw))
                return false;

            string candidate = raw.Trim();
            if (candidate.IndexOf("errorNumber", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                json = candidate;
                return true;
            }

            try
            {
                string urlDecoded = HttpUtility.UrlDecode(candidate);
                if (urlDecoded.IndexOf("errorNumber", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    json = urlDecoded;
                    return true;
                }
                string base64 = urlDecoded.Replace(" ", "+").Trim();
                byte[] bytes = Convert.FromBase64String(base64);
                string decoded = Encoding.UTF8.GetString(bytes);
                if (decoded.IndexOf("errorNumber", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    json = decoded;
                    return true;
                }
            }
            catch
            {
            }
            return false;
        }

        private static string FindErrorCode(string json)
        {
            string keyOrValue = FindJsonValue(json, "e_c");
            if (string.IsNullOrEmpty(keyOrValue))
                keyOrValue = FindJsonValue(json, "errorCode");
            if (string.IsNullOrEmpty(keyOrValue))
                return "";
            if (Regex.IsMatch(keyOrValue, "^[0-9]+$"))
                return keyOrValue;
            string indirect = FindJsonValue(json, keyOrValue);
            return string.IsNullOrEmpty(indirect) ? keyOrValue : indirect;
        }

        private static string FindErrorDescription(string json)
        {
            string keyOrValue = FindJsonValue(json, "e_d");
            if (string.IsNullOrEmpty(keyOrValue))
                keyOrValue = FindJsonValue(json, "errorMessage");
            if (string.IsNullOrEmpty(keyOrValue))
                return "";
            if (keyOrValue.IndexOf(" ", StringComparison.Ordinal) >= 0 ||
                keyOrValue.Length > 20 || ContainsChinese(keyOrValue))
                return keyOrValue;
            string indirect = FindJsonValue(json, keyOrValue);
            return string.IsNullOrEmpty(indirect) ? keyOrValue : indirect;
        }

        private static bool LooksLikeCredentialError(string code, string message)
        {
            if (!string.IsNullOrEmpty(code) && code.IndexOf("63032", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            string all = ((code ?? "") + " " + (message ?? "")).ToLowerInvariant();
            return all.IndexOf("password") >= 0 || all.IndexOf("账号") >= 0 ||
                all.IndexOf("密码") >= 0 || all.IndexOf("用户名") >= 0;
        }

        private static bool ContainsChinese(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            foreach (char c in value)
            {
                if (c >= 0x4e00 && c <= 0x9fff)
                    return true;
            }
            return false;
        }

        private static string FindJsonValue(string json, string key)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key))
                return "";
            string escapedKey = Regex.Escape(key);
            Match quoted = Regex.Match(json, "\"" + escapedKey + "\"\\s*:\\s*\"((?:\\\\.|[^\"])*)\"",
                RegexOptions.IgnoreCase);
            if (quoted.Success)
                return UnescapeJson(quoted.Groups[1].Value);
            Match plain = Regex.Match(json, "\"" + escapedKey + "\"\\s*:\\s*([^,}\\s]+)",
                RegexOptions.IgnoreCase);
            return plain.Success ? plain.Groups[1].Value.Trim('"') : "";
        }

        private static string UnescapeJson(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "";
            return value.Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\/", "/");
        }

        private static string MakeAbsolute(string baseUrl, string location)
        {
            try
            {
                Uri uri = new Uri(new Uri(baseUrl), location);
                return uri.ToString();
            }
            catch
            {
                return "";
            }
        }
    }

    public static class HtmlFormParser
    {
        public static List<FormField> Parse(string html)
        {
            List<FormField> fields = new List<FormField>();
            if (string.IsNullOrEmpty(html))
                return fields;

            string form = FindForm(html);
            if (string.IsNullOrEmpty(form))
                form = html;

            Regex inputRegex = new Regex(@"<input\b(?<attrs>[^>]*)>", RegexOptions.IgnoreCase);
            foreach (Match match in inputRegex.Matches(form))
            {
                string attrs = match.Groups["attrs"].Value;
                FormField field = new FormField();
                field.Name = GetAttribute(attrs, "name");
                field.Id = GetAttribute(attrs, "id");
                field.Type = GetAttribute(attrs, "type");
                field.Value = WebUtility.HtmlDecode(GetAttribute(attrs, "value"));
                field.Checked = attrs.IndexOf(" checked", StringComparison.OrdinalIgnoreCase) >= 0;
                if (string.IsNullOrEmpty(field.Name))
                    continue;
                if (field.Type.Equals("submit", StringComparison.OrdinalIgnoreCase) ||
                    field.Type.Equals("button", StringComparison.OrdinalIgnoreCase) ||
                    field.Type.Equals("reset", StringComparison.OrdinalIgnoreCase) ||
                    field.Type.Equals("file", StringComparison.OrdinalIgnoreCase))
                    continue;
                if ((field.Type.Equals("checkbox", StringComparison.OrdinalIgnoreCase) ||
                     field.Type.Equals("radio", StringComparison.OrdinalIgnoreCase)) && !field.Checked)
                    continue;
                fields.Add(field);
            }

            Regex selectRegex = new Regex(@"<select\b(?<attrs>[^>]*)>(?<body>.*?)</select>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            foreach (Match select in selectRegex.Matches(form))
            {
                string attrs = select.Groups["attrs"].Value;
                string name = GetAttribute(attrs, "name");
                if (string.IsNullOrEmpty(name))
                    continue;
                FormField field = new FormField();
                field.Name = name;
                field.Id = GetAttribute(attrs, "id");
                field.Type = "select";
                MatchCollection options = Regex.Matches(select.Groups["body"].Value,
                    @"<option\b(?<attrs>[^>]*)>(?<text>.*?)</option>",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (options.Count > 0)
                {
                    Match selected = null;
                    foreach (Match option in options)
                    {
                        if (option.Groups["attrs"].Value.IndexOf("selected", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            selected = option;
                            break;
                        }
                    }
                    if (selected == null)
                        selected = options[0];
                    field.Value = GetAttribute(selected.Groups["attrs"].Value, "value");
                    if (string.IsNullOrEmpty(field.Value))
                        field.Value = Regex.Replace(selected.Groups["text"].Value, "<.*?>", "").Trim();
                }
                fields.Add(field);
            }
            return fields;
        }

        private static string FindForm(string html)
        {
            Match named = Regex.Match(html, @"<form\b[^>]*id\s*=\s*[""']?form1[""']?[^>]*>(?<body>.*?)</form>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (named.Success)
                return named.Groups["body"].Value;
            Match user = Regex.Match(html, @"<form\b[^>]*>(?<body>[\s\S]*?userName[\s\S]*?)</form>",
                RegexOptions.IgnoreCase);
            return user.Success ? user.Groups["body"].Value : "";
        }

        private static string GetAttribute(string attrs, string name)
        {
            Match match = Regex.Match(attrs,
                @"(?<![A-Za-z0-9_-])" + Regex.Escape(name) +
                @"\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)'|(?<v>[^\s>]+))",
                RegexOptions.IgnoreCase);
            return match.Success ? match.Groups["v"].Value : "";
        }
    }
}
