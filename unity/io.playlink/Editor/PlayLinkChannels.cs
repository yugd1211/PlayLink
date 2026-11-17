using System.Collections.Generic;
using System.Net.Sockets;

// 영상(/video)과 입력(/input) 밖의 흐름을 붙일 자리(설계 15.1 R5). 시제품에는 등록된 채널이 없습니다.
// 새 흐름은 경로 /x/<이름> 의 WebSocket 을 따로 엽니다. 인증의 채널 문자열은 <이름>, 기능 이름은 ch.<이름> 입니다.
// 등록된 이름이 없으면 앞단이 404 를 주고 앱은 그 기능이 없는 것으로 봅니다.
public static class PlayLinkChannels
{
    public const string PATH_PREFIX = "/x/";

    // 인증을 마친 소켓을 넘겨받습니다(앞단의 연결 스레드에서 불림). 돌아오면 앞단이 소켓을 닫습니다.
    public delegate void Handler(TcpClient client, NetworkStream stream, PlayLinkSession session);

    private static readonly Dictionary<string, Handler> handlers = new Dictionary<string, Handler>();

    public static void Register(string name, Handler handler)
    {
        lock (handlers)
        {
            handlers[name] = handler;
        }
    }

    public static Handler Find(string name)
    {
        lock (handlers)
        {
            return handlers.TryGetValue(name, out Handler handler) ? handler : null;
        }
    }
}
