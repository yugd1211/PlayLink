using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

// QR 주소가 가리키는 HTTP 경로를 내줍니다(설계 9.1, 10.4). 앞단(Host)의 LAN 리스너와 기존 서버의 루프백 리스너가 함께 씁니다.
//   GET|HEAD /c         착륙 페이지(PlayLinkLanding.html, 요청 때마다 디스크에서 읽음). 토큰은 페이지에 없고 주소의 # 뒤에만 있습니다.
//   GET|HEAD /apk       APK 파일(기본: 도구 폴더의 App~/PlayLink.apk, 설정 apkPath 로 바꿀 수 있음)
//   GET|HEAD /app.json  APK 옆의 app.json(버전 정보)
// 응답은 헤더와 본문을 한 번에 씁니다(APK 만 헤더 뒤 64KB 씩).
public static class PlayLinkFiles
{
    private const int APK_CHUNK_BYTES = 64 * 1024;
    // 동시에 APK 를 받는 연결 수 상한(연결마다 스레드 하나와 수 MB 전송이 붙으므로 제한, 리뷰 M4)
    private const int MAX_APK_TRANSFERS = 6;
    // 원격 주소 하나가 동시에 받을 수 있는 수. 1 이면 실제 폰 브라우저에서 내려받기가 실패합니다: 삼성 인터넷과 앱 내 브라우저(WebView)는
    // 첫 응답을 열어 둔 채 확인 창을 띄우고, 확인하면 다운로드 관리자가 같은 주소를 새 연결로 다시 요청합니다. 그 두 번째 요청이 503 을 받던 것.
    private const int MAX_APK_TRANSFERS_PER_ADDRESS = 3;
    // 전송 하나의 전체 시간 상한. 조금씩만 읽어 자리를 오래 붙잡는 연결을 끊습니다(코드 리뷰 m6). 3.5MB 를 이 시간에 못 받는 링크면 어차피 영상도 못 봅니다.
    // 브라우저의 경고 창("유해할 수 있는 파일", "안전하게 다운로드할 수 없음")을 읽고 누르는 시간도 여기에 들어갑니다.
    private const int MAX_APK_TRANSFER_SECONDS = 300;
    private const double APK_EXISTS_RECHECK_SECONDS = 1.0;
    private const string APK_EXTENSION = ".apk";
    // 착륙 페이지 안의 자리 표시. 내줄 때 PlayLinkAppInfo 의 값으로 바꿉니다(패키지와 스킴을 한 파일에서만 정하게, 코드 리뷰 m19).
    private const string PLACEHOLDER_PACKAGE = "%%PLAYLINK_PACKAGE%%";
    private const string PLACEHOLDER_SCHEME = "%%PLAYLINK_SCHEME%%";
    // 받는 쪽이 읽지 않으면 쓰기가 이 시간 뒤 실패합니다(멈춘 연결이 스레드를 계속 붙잡지 않게).
    // 10초였을 때는 브라우저가 경고 창을 띄우고 읽기를 멈춘 사이 전송이 끊겨 APK 가 잘렸습니다. 사람이 창을 읽고 누를 만큼 줍니다.
    private const int SEND_TIMEOUT_MS = 90000;
    private const double APP_INFO_RECHECK_SECONDS = 1.0;

    [Serializable]
    private class AppJson
    {
        public int versionCode;
        public string versionName;
    }

    private static volatile string toolFolder;
    private static volatile string apkOverride = string.Empty;
    private static int apkTransfers;
    // 원격 주소마다 받고 있는 수(읽지 않는 연결 몇 개로 한 기기가 내려받기 자리를 다 채우지 못하게, 코드 리뷰 m6).
    private static readonly System.Collections.Generic.Dictionary<string, int> apkAddresses = new System.Collections.Generic.Dictionary<string, int>();
    private static readonly object existsLock = new object();
    private static DateTime existsCheckedUtc;
    private static string existsPath;
    private static bool existsValue;
    private static readonly object infoLock = new object();
    private static DateTime infoCheckedUtc;
    private static DateTime infoWriteUtc;
    private static int versionCode;
    private static string versionName = string.Empty;

    public static string ToolFolder { get { return toolFolder; } }

    // 메인 스레드에서 한 번(도메인 리로드마다). 도구 폴더는 스크립트 자신의 위치에서 찾아 넘깁니다(폴더 경로를 글자로 적지 않음, 설계 15.3).
    public static void Configure(string toolFolderFullPath, string apkPathOverride)
    {
        toolFolder = toolFolderFullPath;
        apkOverride = apkPathOverride ?? string.Empty;
        lock (infoLock)
        {
            infoCheckedUtc = DateTime.MinValue;
        }
    }

    // 내줄 APK 의 전체 경로(파일이 없어도 경로는 돌려줌). 도구 폴더를 아직 모르면 null
    public static string ApkPath
    {
        get
        {
            // 설정 파일의 apkPath 는 .apk 파일일 때만 받습니다. 이 경로의 파일은 인증 없이 LAN 에 나가므로,
            // 설정이 잘못됐거나 남이 커밋한 설정 파일이 다른 파일을 가리켜도 내주지 않습니다(코드 리뷰 m7).
            string overridePath = apkOverride;
            if (!string.IsNullOrEmpty(overridePath) && overridePath.EndsWith(APK_EXTENSION, StringComparison.OrdinalIgnoreCase))
            {
                return overridePath;
            }

            string folder = toolFolder;
            return folder == null ? null : Path.Combine(folder, PlayLinkAppInfo.APP_FOLDER, PlayLinkAppInfo.APK_FILE);
        }
    }

    public static bool ApkExists
    {
        get
        {
            // 창의 OnGUI 가 초당 수십 번 묻기 때문에 파일 확인은 1초에 한 번만 합니다(코드 리뷰 m15).
            string path = ApkPath;
            if (path == null)
            {
                return false;
            }

            lock (existsLock)
            {
                DateTime now = DateTime.UtcNow;
                if (path != existsPath || (now - existsCheckedUtc).TotalSeconds >= APK_EXISTS_RECHECK_SECONDS)
                {
                    existsPath = path;
                    existsCheckedUtc = now;
                    try
                    {
                        existsValue = File.Exists(path);
                    }
                    catch (Exception)
                    {
                        existsValue = false;
                    }
                }

                return existsValue;
            }
        }
    }

    // app.json 은 APK 와 같은 폴더에 둡니다.
    public static string AppJsonPath
    {
        get
        {
            string apk = ApkPath;
            return apk == null ? null : Path.Combine(Path.GetDirectoryName(apk) ?? string.Empty, PlayLinkAppInfo.APP_JSON_FILE);
        }
    }

    // 도구에 든 APK 의 versionCode(인증 응답의 app). app.json 이 없으면 0
    public static int ApkVersionCode
    {
        get
        {
            RefreshAppInfo();
            lock (infoLock)
            {
                return versionCode;
            }
        }
    }

    public static string ApkVersionName
    {
        get
        {
            RefreshAppInfo();
            lock (infoLock)
            {
                return versionName;
            }
        }
    }

    // 이 경로를 처리했으면 true(응답을 이미 썼음). 아니면 false(호출한 쪽이 404 등을 씀).
    // userAgent 는 콘솔 기록에만 씁니다(어느 브라우저가 페이지를 열고 APK 를 받았는지). 없으면 null.
    public static bool TryServe(string method, string path, TcpClient client, NetworkStream stream, string userAgent = null)
    {
        if (path != "/c" && path != "/apk" && path != "/app.json")
        {
            return false;
        }

        string browser = DescribeBrowser(userAgent);

        bool head = method == "HEAD";
        if (!head && method != "GET")
        {
            WriteError(stream, "405 Method Not Allowed", "method_not_allowed", "GET 만 됩니다.");
            return true;
        }

        if (path == "/c")
        {
            byte[] page = ReadFile(toolFolder == null ? null : Path.Combine(toolFolder, PlayLinkAppInfo.LANDING_FILE));
            if (page == null)
            {
                WriteError(stream, "404 Not Found", "not_found", "착륙 페이지 파일이 없습니다.");
            }
            else
            {
                page = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(page)
                    .Replace(PLACEHOLDER_PACKAGE, PlayLinkAppInfo.PACKAGE).Replace(PLACEHOLDER_SCHEME, PlayLinkAppInfo.SCHEME));
                WriteResponse(stream, "200 OK", "text/html; charset=utf-8", page, "X-Content-Type-Options: nosniff\r\n", head);
                if (!head)
                {
                    // 주소의 # 뒤(토큰)는 서버로 오지 않으므로 여기 적히지 않습니다.
                    Debug.Log($"{PlayLink.LOG_PREFIX} 착륙 페이지를 열었습니다: {RemoteAddress(client)} ({browser})");
                }
            }

            return true;
        }

        if (path == "/app.json")
        {
            // APK 가 없으면 app.json 만 있어도 내주지 않습니다(코드 리뷰 m8: .gitignore 가 *.apk 를 빼는 프로젝트에서 app.json 만 받은 사람의
            // 착륙 페이지가 버전과 [앱 설치]를 보이는데 /apk 는 404 이던 것).
            byte[] json = File.Exists(ApkPath ?? string.Empty) ? ReadFile(AppJsonPath) : null;
            if (json == null)
            {
                WriteError(stream, "404 Not Found", "not_found", "app.json 이 없습니다.");
            }
            else
            {
                WriteResponse(stream, "200 OK", "application/json; charset=utf-8", json, null, head);
            }

            return true;
        }

        ServeApk(client, stream, head, browser);
        return true;
    }

    private static string RemoteAddress(TcpClient client)
    {
        try
        {
            System.Net.IPEndPoint remote = client.Client.RemoteEndPoint as System.Net.IPEndPoint;
            return remote != null ? remote.Address.ToString() : "?";
        }
        catch (Exception)
        {
            return "?";
        }
    }

    // User-Agent 를 콘솔 기록용 짧은 이름으로 줄입니다. 문제가 생겼을 때 "어느 브라우저였나"가 가장 먼저 필요한 정보입니다.
    public static string DescribeBrowser(string userAgent)
    {
        if (string.IsNullOrEmpty(userAgent))
        {
            return "브라우저 모름";
        }

        string name;
        if (userAgent.IndexOf("iPhone", StringComparison.Ordinal) >= 0 || userAgent.IndexOf("iPad", StringComparison.Ordinal) >= 0)
        {
            name = "iOS(이 앱은 안드로이드 전용)";
        }
        else if (userAgent.IndexOf("KAKAOTALK", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            name = "카카오톡 앱 내 브라우저";
        }
        else if (userAgent.IndexOf("NAVER(", StringComparison.Ordinal) >= 0)
        {
            name = "네이버 앱 내 브라우저";
        }
        else if (userAgent.IndexOf("; wv)", StringComparison.Ordinal) >= 0)
        {
            name = "앱 내 브라우저(WebView)";
        }
        else if (userAgent.IndexOf("SamsungBrowser/", StringComparison.Ordinal) >= 0)
        {
            name = "삼성 인터넷 " + VersionAfter(userAgent, "SamsungBrowser/");
        }
        else if (userAgent.IndexOf("Firefox/", StringComparison.Ordinal) >= 0)
        {
            name = "Firefox " + VersionAfter(userAgent, "Firefox/");
        }
        else if (userAgent.IndexOf("Chrome/", StringComparison.Ordinal) >= 0)
        {
            name = "Chrome " + VersionAfter(userAgent, "Chrome/");
        }
        else
        {
            // 다운로드 관리자("Dalvik/…", "AndroidDownloadManager/…") 등: 앞부분만 글자를 걸러 적습니다.
            name = PlayLinkAuth.SanitizeName(userAgent.Replace('/', '_').Replace(' ', '_').Replace('.', '_'), 32);
        }

        return userAgent.IndexOf("Android", StringComparison.Ordinal) >= 0 ? name + ", Android" : name;
    }

    private static string VersionAfter(string text, string marker)
    {
        int start = text.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        int end = start;
        while (end < text.Length && text[end] >= '0' && text[end] <= '9')
        {
            end++;
        }

        return text.Substring(start, end - start);
    }

    private static void ServeApk(TcpClient client, NetworkStream stream, bool head, string browser)
    {
        string path = ApkPath;
        if (path == null || !File.Exists(path))
        {
            WriteError(stream, "404 Not Found", "not_found", "APK 파일이 도구에 없습니다. PC 에서 [USB 로 앱 설치]를 쓰거나 APK 를 넣어 주세요.");
            return;
        }

        string address = RemoteAddress(client);
        bool entered;
        lock (apkAddresses)
        {
            // HEAD 는 본문이 없어 자리를 차지하지 않습니다.
            apkAddresses.TryGetValue(address, out int mine);
            entered = head || (apkTransfers < MAX_APK_TRANSFERS && mine < MAX_APK_TRANSFERS_PER_ADDRESS);
            if (entered && !head)
            {
                apkTransfers++;
                apkAddresses[address] = mine + 1;
            }
        }

        if (!entered)
        {
            WriteError(stream, "503 Service Unavailable", "busy", "다른 내려받기가 진행 중입니다. 잠시 뒤 다시 시도하세요.");
            Debug.LogWarning($"{PlayLink.LOG_PREFIX} APK 요청을 거절했습니다(동시 내려받기 한도): {address} ({browser})");
            return;
        }

        long sent = 0;
        long total = 0;
        long startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        string failure = null;
        try
        {
            long giveUpAt = PlayLinkWs.DeadlineAfter(MAX_APK_TRANSFER_SECONDS * 1000);
            string name = ApkVersionName;
            string fileName = string.IsNullOrEmpty(name) ? "PlayLink.apk" : "PlayLink-" + PlayLinkAuth.SanitizeName(name.Replace('.', '_'), 24).Replace('_', '.') + ".apk";
            client.SendTimeout = SEND_TIMEOUT_MS;
            using (FileStream file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                total = file.Length;
                byte[] headBytes = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/vnd.android.package-archive\r\n"
                    + $"Content-Disposition: attachment; filename=\"{fileName}\"\r\nContent-Length: {file.Length}\r\n"
                    + "Cache-Control: no-store\r\nConnection: close\r\n\r\n");
                stream.Write(headBytes, 0, headBytes.Length);
                if (head)
                {
                    return;
                }

                byte[] buffer = new byte[APK_CHUNK_BYTES];
                int read;
                while ((read = file.Read(buffer, 0, buffer.Length)) > 0)
                {
                    stream.Write(buffer, 0, read);
                    sent += read;
                    if (System.Diagnostics.Stopwatch.GetTimestamp() > giveUpAt)
                    {
                        throw new IOException("apk transfer took too long");
                    }
                }
            }
        }
        catch (Exception e)
        {
            // 브라우저가 중간에 끊는 것은 흔합니다(확인 창을 띄우며 첫 연결을 버리고 다시 요청). 그래서 경고가 아니라 기록으로만 남깁니다.
            failure = e.Message;
            throw;
        }
        finally
        {
            if (!head)
            {
                lock (apkAddresses)
                {
                    apkTransfers--;
                    if (apkAddresses.TryGetValue(address, out int mine) && mine > 1)
                    {
                        apkAddresses[address] = mine - 1;
                    }
                    else
                    {
                        apkAddresses.Remove(address);
                    }
                }

                double seconds = (System.Diagnostics.Stopwatch.GetTimestamp() - startedAt) / (double)System.Diagnostics.Stopwatch.Frequency;
                Debug.Log(failure == null
                    ? $"{PlayLink.LOG_PREFIX} APK 를 보냈습니다: {address} ({browser}) {sent:n0}바이트, {seconds:0.0}초"
                    : $"{PlayLink.LOG_PREFIX} APK 전송이 중간에 끝났습니다: {address} ({browser}) {sent:n0}/{total:n0}바이트, {seconds:0.0}초 ({failure})");
            }
        }
    }

    // 헤더와 본문을 한 번에 씁니다. extraHeaders 는 "이름: 값\r\n" 꼴(없으면 null).
    public static void WriteResponse(NetworkStream stream, string status, string contentType, byte[] body, string extraHeaders, bool headOnly)
    {
        byte[] head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\n"
            + (extraHeaders ?? string.Empty) + "Cache-Control: no-store\r\nConnection: close\r\n\r\n");
        int bodyLength = headOnly ? 0 : body.Length;
        byte[] response = new byte[head.Length + bodyLength];
        Buffer.BlockCopy(head, 0, response, 0, head.Length);
        Buffer.BlockCopy(body, 0, response, head.Length, bodyLength);
        stream.Write(response, 0, response.Length);
        stream.Flush();
    }

    // 업그레이드 전에 거절할 때의 응답(설계 3.3): 본문 JSON 과 같은 코드를 X-PlayLink-Error 헤더에도 넣습니다.
    public static void WriteError(NetworkStream stream, string status, string code, string message)
    {
        byte[] body = Encoding.UTF8.GetBytes("{\"t\":\"err\",\"code\":\"" + code + "\",\"msg\":\"" + message.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"}");
        WriteResponse(stream, status, "application/json; charset=utf-8", body, "X-PlayLink-Error: " + code + "\r\n", false);
    }

    private static byte[] ReadFile(string path)
    {
        try
        {
            return path != null && File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // app.json 이 바뀌었으면 다시 읽습니다(1초에 한 번까지만 파일 시각을 확인).
    private static void RefreshAppInfo()
    {
        string path = AppJsonPath;
        lock (infoLock)
        {
            DateTime now = DateTime.UtcNow;
            if ((now - infoCheckedUtc).TotalSeconds < APP_INFO_RECHECK_SECONDS)
            {
                return;
            }

            infoCheckedUtc = now;
            try
            {
                // APK 가 없으면 버전도 0 입니다(인증 응답의 app 이 없는 APK 의 버전을 말하지 않게, 코드 리뷰 m8).
                if (path == null || !File.Exists(path) || !File.Exists(ApkPath ?? string.Empty))
                {
                    versionCode = 0;
                    versionName = string.Empty;
                    infoWriteUtc = DateTime.MinValue;
                    return;
                }

                DateTime write = File.GetLastWriteTimeUtc(path);
                if (write == infoWriteUtc)
                {
                    return;
                }

                AppJson info = JsonUtility.FromJson<AppJson>(File.ReadAllText(path));
                versionCode = info != null ? info.versionCode : 0;
                versionName = info != null && info.versionName != null ? info.versionName : string.Empty;
                infoWriteUtc = write;
            }
            catch (Exception)
            {
                versionCode = 0;
                versionName = string.Empty;
            }
        }
    }
}
