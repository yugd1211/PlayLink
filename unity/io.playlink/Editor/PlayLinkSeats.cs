using System;
using System.Collections.Generic;

// 자리 배정(설계 3.5): 영상을 받는 클라이언트는 한 무리뿐이고, 나머지는 연결을 유지한 채 대기합니다.
//   무리 = 같은 클라이언트의 소켓들. 앱은 did 로 묶고("app:<did>"), 인증 헤더 없는 연결(Chrome 페이지)은 전부 "page" 한 무리.
//   영상을 받는 무리 = 선호 무리가 붙어 있으면 그 무리, 아니면 가장 먼저 붙은 앱 무리, 앱이 없으면 page.
//   선호가 바뀌는 때는 둘뿐: take=1 인 앱 연결이 인증을 마침 / 영상 소켓으로 {"t":"take"} 가 옴.
//   자동 재접속(take=0)과 페이지의 재접속은 선호를 바꾸지 않습니다. 선호는 서버 Stop(Play 종료, 리로드)으로 지워지지 않고
//   앞단의 [Stop] 과 [새 QR 발급]으로만 지워집니다(리뷰 M3). 리로드를 넘기는 저장은 앞단(Host)이 SessionState 로 합니다.
// 대기 무리의 연결은 영상 프레임과 상태 텍스트를 받지 않고, 입력 소켓의 포인터 메시지는 버려지며, 눌려 있던 손가락은 취소됩니다.
// 입력 연결 수(PlayLinkInput.OnInputConnected/Disconnected)는 여기 한 곳에서만 셉니다(리뷰 m4).
public static class PlayLinkSeats
{
    public const string PAGE_GROUP = "page";
    private const string APP_GROUP_PREFIX = "app:";
    private const string STANDBY_MESSAGE = "다른 기기가 보고 있습니다.";
    private const string STATUS_FRESH = "{\"t\":\"s\",\"stale\":false}";
    // 같은 앱(did)이 같은 종류의 소켓을 새로 열어 옛 소켓을 닫을 때의 닫기 코드. 앱은 이 코드로 닫힌 소켓 때문에 다시 붙지 않습니다(08_interop_notes).
    public const ushort CLOSE_REPLACED = 4409;

    // 창 표시용: 무리 하나
    public struct Viewer
    {
        public string group;
        public string deviceName;
        public string remote;
        public bool usb;
        public int appVersion;
        // "active" | "standby" | "lobby"
        public string state;
        public bool video;
        public bool input;
    }

    private sealed class Member
    {
        public PlayLinkServer server;
        public PlayLinkServer.Connection connection;
        public PlayLinkSession session;
        public string group;
        public long order;
        // hello 를 보낸 뒤에만 알림을 보냅니다(hello 가 항상 첫 메시지, 리뷰 M8 b).
        public bool announced;
        public readonly object sendLock = new object();
        public long lastNoticeSeq;
    }

    private struct Notice
    {
        public Member member;
        public long seq;
        public string first;
        public string second;
    }

    private static readonly object gate = new object();
    // 서버의 연결 목록을 복사해 보지 않고 자기 표를 둡니다(들어오는 중인 연결과 동시에 들어오는 소켓을 놓치지 않게, 리뷰 m5).
    private static readonly List<Member> members = new List<Member>();
    private static string preferred;
    private static long orderCounter;
    private static long noticeCounter;

    // 자리나 접속 기기 목록이 바뀌었을 때(아무 스레드에서 불림). 앞단이 메인 스레드로 넘겨 창을 다시 그리게 합니다.
    public static Action onChanged;

    // 선호 무리 키("page" 또는 "app:<did>", 없으면 null)
    public static string Preferred
    {
        get
        {
            lock (gate)
            {
                return preferred;
            }
        }
        set
        {
            lock (gate)
            {
                preferred = string.IsNullOrEmpty(value) ? null : value;
            }
        }
    }

    public static string GroupOf(PlayLinkSession session)
    {
        return session != null ? APP_GROUP_PREFIX + session.deviceId : PAGE_GROUP;
    }

    // 연결이 서버의 목록에 들어가기 직전(서버가 자기 잠금 밖에서 부름). 이 연결의 standby 를 정하고, 밀려난 다른 연결들에 알립니다.
    // 들어온 연결 자신에게는 hello 를 쓴 뒤 Announce 가 알립니다.
    public static void OnJoin(PlayLinkServer server, PlayLinkServer.Connection connection)
    {
        List<Notice> notices = new List<Notice>();
        List<Member> replaced = null;
        lock (gate)
        {
            PlayLinkSession session = connection.tag as PlayLinkSession;
            Member member = new Member
            {
                server = server,
                connection = connection,
                session = session,
                group = GroupOf(session),
                order = ++orderCounter,
            };
            if (session != null && session.take)
            {
                preferred = member.group;
            }

            // 같은 앱이 같은 종류의 소켓을 다시 열었으면 옛 소켓을 내보냅니다(새 것이 이김, 코드 리뷰 M2).
            // Wi-Fi 가 잠깐 끊긴 뒤 앱은 곧바로 다시 붙는데 서버에는 옛 소켓이 반쯤 열린 채 남아, 영상 소켓이 둘로 보여 H.264 가 꺼졌습니다.
            // 옛 연결을 대기로 표시해 두면 닫히기 전에도 프레임 대상과 "영상 클라이언트 수"에서 빠집니다.
            if (session != null)
            {
                for (int i = members.Count - 1; i >= 0; i--)
                {
                    Member old = members[i];
                    if (old.group != member.group || old.connection.kind != connection.kind)
                    {
                        continue;
                    }

                    members.RemoveAt(i);
                    if (old.connection.kind == PlayLinkServer.ChannelKind.Input && !old.connection.standby)
                    {
                        PlayLinkInput.OnInputDisconnected(old.connection.id);
                    }

                    old.connection.standby = true;
                    (replaced ?? (replaced = new List<Member>())).Add(old);
                }
            }

            members.Add(member);
            Recompute(notices, member);
            if (connection.kind == PlayLinkServer.ChannelKind.Input && !connection.standby)
            {
                PlayLinkInput.OnInputConnected(connection.id);
            }
        }

        if (replaced != null)
        {
            // 잠금 밖에서 닫습니다. CloseWith 는 막힌 쓰기를 기다리지 않습니다(상대가 사라진 소켓일 수 있음).
            foreach (Member old in replaced)
            {
                old.server.CloseWith(old.connection, CLOSE_REPLACED);
            }
        }

        Send(notices);
        onChanged?.Invoke();
    }

    // hello 를 쓴 직후(영상) 또는 목록에 들어간 직후(입력). 들어온 연결에 자기 자리 상태를 알립니다.
    public static void Announce(PlayLinkServer server, PlayLinkServer.Connection connection)
    {
        List<Notice> notices = new List<Notice>();
        lock (gate)
        {
            Member member = Find(connection);
            if (member == null)
            {
                return;
            }

            member.announced = true;
            // 앱에는 항상 알리고, 페이지에는 대기일 때만 알립니다(보는 중인 페이지는 지금과 똑같이 서버의 마지막 상태만 받음).
            if (connection.kind == PlayLinkServer.ChannelKind.Video && (member.session != null || connection.standby))
            {
                AddNotice(notices, member, false);
            }
        }

        Send(notices);
    }

    // 연결이 닫혔을 때. 여러 번 불려도 됩니다(표에 없으면 아무 일도 안 함).
    public static void OnLeave(PlayLinkServer server, PlayLinkServer.Connection connection)
    {
        List<Notice> notices = new List<Notice>();
        lock (gate)
        {
            Member member = Find(connection);
            if (member == null)
            {
                return;
            }

            members.Remove(member);
            if (connection.kind == PlayLinkServer.ChannelKind.Input && !connection.standby)
            {
                PlayLinkInput.OnInputDisconnected(connection.id);
            }

            Recompute(notices, null);
            // 서버가 닫히는 중이면(Stop) 남은 연결도 곧 닫히므로 알리지 않습니다.
            if (!server.IsRunning)
            {
                notices.Clear();
            }
        }

        Send(notices);
        onChanged?.Invoke();
    }

    // 영상 소켓으로 {"t":"take"} 가 옴([여기서 보기]). 앱이든 페이지든 그 무리가 선호가 됩니다.
    public static void OnTake(PlayLinkServer server, PlayLinkServer.Connection connection)
    {
        List<Notice> notices = new List<Notice>();
        lock (gate)
        {
            Member member = Find(connection);
            if (member == null)
            {
                return;
            }

            preferred = member.group;
            Recompute(notices, null);
            // 이미 보는 중이던 앱이 눌렀어도 답을 줍니다(앱이 seat active 를 기다림).
            if (notices.Count == 0 && member.session != null && member.announced)
            {
                AddNotice(notices, member, false);
            }
        }

        Send(notices);
        onChanged?.Invoke();
    }

    // 서버 Stop. 연결 표만 비우고 선호는 남깁니다(Play 를 다시 시작해도 보던 쪽이 계속 봄).
    public static void Reset()
    {
        lock (gate)
        {
            members.Clear();
        }

        onChanged?.Invoke();
    }

    // 앞단의 [Stop] 과 [새 QR 발급]
    public static void ClearPreferred()
    {
        Preferred = null;
    }

    public static List<Viewer> Snapshot()
    {
        List<Viewer> result = new List<Viewer>();
        lock (gate)
        {
            foreach (Member member in members)
            {
                int index = result.FindIndex(viewer => viewer.group == member.group);
                Viewer viewer = index >= 0 ? result[index] : new Viewer
                {
                    group = member.group,
                    deviceName = member.session != null ? member.session.deviceName : "Chrome 페이지",
                    remote = member.connection.remote,
                    usb = member.session == null || member.session.viaLoopback,
                    appVersion = member.session != null ? member.session.appVersion : 0,
                    state = member.connection.standby ? "standby" : "active",
                };
                if (member.connection.kind == PlayLinkServer.ChannelKind.Video)
                {
                    viewer.video = true;
                }
                else
                {
                    viewer.input = true;
                }

                if (index >= 0)
                {
                    result[index] = viewer;
                }
                else
                {
                    result.Add(viewer);
                }
            }
        }

        return result;
    }

    // gate 안에서 부릅니다. 영상을 받는 무리를 다시 정하고, 바뀐 연결의 standby 를 고친 뒤 알림을 모읍니다.
    // joining: 지금 들어오는 연결(아직 hello 전이라 알리지 않고, 입력 연결 수도 OnJoin 이 한 번만 셈)
    private static void Recompute(List<Notice> notices, Member joining)
    {
        string active = ActiveGroup();
        foreach (Member member in members)
        {
            PlayLinkServer.Connection connection = member.connection;
            bool standby = member.group != active;
            if (connection.standby == standby)
            {
                continue;
            }

            connection.standby = standby;
            if (member == joining)
            {
                continue;
            }

            if (connection.kind == PlayLinkServer.ChannelKind.Input)
            {
                // 대기로 가면 눌려 있던 손가락을 취소하고 연결 수에서 뺍니다. 돌아오면 다시 셉니다(0 → 1 이면 입력 번호가 0 으로 돌아감).
                if (standby)
                {
                    PlayLinkInput.OnInputDisconnected(connection.id);
                }
                else
                {
                    PlayLinkInput.OnInputConnected(connection.id);
                }

                continue;
            }

            if (standby)
            {
                // 아직 못 보낸 프레임은 버립니다(대기 중에는 영상이 가지 않음).
                lock (connection.writeLock)
                {
                    connection.ordered.Clear();
                    connection.pending = null;
                }
            }
            else
            {
                // 대기하는 동안 프레임이 빠졌으므로 설정 + 키프레임부터 다시 받게 합니다(화면이 멈춰 있어도 한 장 보냄).
                connection.keyframeRequested = true;
            }

            if (member.announced)
            {
                AddNotice(notices, member, true);
            }
        }
    }

    private static string ActiveGroup()
    {
        Member oldestApp = null;
        foreach (Member member in members)
        {
            if (preferred != null && member.group == preferred)
            {
                return preferred;
            }

            if (member.session != null && (oldestApp == null || member.order < oldestApp.order))
            {
                oldestApp = member;
            }
        }

        return oldestApp != null ? oldestApp.group : PAGE_GROUP;
    }

    // gate 안에서 부릅니다. 이 영상 연결에 보낼 자리 알림을 만듭니다. changed: 자리가 방금 바뀐 것인가(아니면 접속 직후의 첫 알림).
    private static void AddNotice(List<Notice> notices, Member member, bool changed)
    {
        PlayLinkServer.Connection connection = member.connection;
        if (connection.kind != PlayLinkServer.ChannelKind.Video)
        {
            return;
        }

        Notice notice = new Notice { member = member, seq = ++noticeCounter };
        string lastStatus = member.server.LastStatus;
        if (member.session != null)
        {
            if (!member.session.Has(PlayLinkAppInfo.CAP_SEAT))
            {
                return;
            }

            if (connection.standby)
            {
                notice.first = "{\"t\":\"seat\",\"s\":\"standby\",\"who\":\"" + (ActiveGroup() == PAGE_GROUP ? "page" : "app") + "\",\"msg\":\"" + STANDBY_MESSAGE + "\"}";
            }
            else
            {
                notice.first = "{\"t\":\"seat\",\"s\":\"active\"}";
                // 대기에서 돌아온 앱은 그사이 상태 텍스트(일시정지 등)를 받지 못했으므로 마지막 것을 이어 보냅니다.
                notice.second = changed ? lastStatus : null;
            }
        }
        else if (connection.standby)
        {
            // 페이지는 stale:true 의 msg 를 그대로 화면에 보입니다(페이지를 고치지 않아도 대기 문구가 보임).
            notice.first = "{\"t\":\"s\",\"stale\":true,\"code\":\"standby\",\"msg\":\"" + STANDBY_MESSAGE + "\"}";
        }
        else
        {
            // 서버의 마지막 상태가 "멈춤"이면 그것이 대기 문구를 대신하고, 아니면 풀림을 보냅니다.
            notice.first = lastStatus ?? STATUS_FRESH;
        }

        notices.Add(notice);
    }

    // gate 밖에서 보냅니다(소켓 쓰기가 자리 배정 전체를 붙잡지 않게). 늦게 도착한 옛 알림이 새 알림을 덮지 않게 번호로 거릅니다.
    private static void Send(List<Notice> notices)
    {
        foreach (Notice notice in notices)
        {
            Member member = notice.member;
            lock (member.sendLock)
            {
                if (notice.seq < member.lastNoticeSeq || member.connection.closed)
                {
                    continue;
                }

                member.lastNoticeSeq = notice.seq;
                member.server.SendText(member.connection, notice.first);
                if (notice.second != null)
                {
                    member.server.SendText(member.connection, notice.second);
                }
            }
        }
    }

    private static Member Find(PlayLinkServer.Connection connection)
    {
        foreach (Member member in members)
        {
            if (member.connection == connection)
            {
                return member;
            }
        }

        return null;
    }
}
