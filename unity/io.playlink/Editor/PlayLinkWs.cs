using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;

// 작은 WebSocket 도우미. 입장 심사(Gate)와 Play 밖 대기실(Host)이 씁니다. 기존 서버의 연결 객체가 만들어지기 전 단계용입니다.
// 쓰기는 메시지 1개 = 소켓 쓰기 1번입니다(나눠 쓰면 adb 터널에서 뒤쪽이 약 40ms 늦게 감, 개발 문서 12.7).
public static class PlayLinkWs
{
    public const int OPCODE_TEXT = 0x1;
    public const int OPCODE_CLOSE = 0x8;
    public const int OPCODE_PING = 0x9;
    public const int OPCODE_PONG = 0xA;
    // 메인 스레드가 닫기 프레임을 쓰려고 쓰기 잠금을 기다리는 최대 시간(ms)
    private const int CLOSE_LOCK_WAIT_MS = 50;

    // 지금부터 ms 뒤의 기한(Stopwatch 틱). 0은 "기한 없음"으로 씁니다.
    public static long DeadlineAfter(int ms)
    {
        return Stopwatch.GetTimestamp() + ms * Stopwatch.Frequency / 1000;
    }

    // 다음 읽기의 제한 시간을 기한까지 남은 시간으로 맞춥니다. ReceiveTimeout 은 읽기 한 번마다의 제한이라,
    // 읽을 때마다 다시 맞춰야 "1바이트씩 천천히 보내는 연결"이 기한을 넘기지 못합니다(리뷰 M4).
    public static void ApplyDeadline(TcpClient client, long deadlineTicks)
    {
        if (deadlineTicks == 0)
        {
            client.ReceiveTimeout = 0;
            return;
        }

        long remainingMs = (deadlineTicks - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency;
        if (remainingMs <= 0)
        {
            throw new IOException("deadline passed");
        }

        client.ReceiveTimeout = (int)Math.Max(1, remainingMs);
    }

    public static void ReadExactly(TcpClient client, NetworkStream stream, byte[] buffer, int offset, int count, long deadlineTicks)
    {
        while (count > 0)
        {
            ApplyDeadline(client, deadlineTicks);
            int read = stream.Read(buffer, offset, count);
            if (read <= 0)
            {
                throw new IOException("connection closed");
            }

            offset += read;
            count -= read;
        }
    }

    // HTTP 요청 헤더를 "\r\n\r\n" 까지 읽습니다. 끊겼거나 너무 길면 null, 기한을 넘기면 IOException.
    public static string ReadHttpHeader(TcpClient client, NetworkStream stream, int maxBytes, long deadlineTicks)
    {
        byte[] buffer = new byte[maxBytes];
        int length = 0;
        while (length < buffer.Length)
        {
            ApplyDeadline(client, deadlineTicks);
            int read = stream.Read(buffer, length, buffer.Length - length);
            if (read <= 0)
            {
                return null;
            }

            int from = Math.Max(3, length);
            length += read;
            for (int i = from; i < length; i++)
            {
                if (buffer[i - 3] == '\r' && buffer[i - 2] == '\n' && buffer[i - 1] == '\r' && buffer[i] == '\n')
                {
                    return Encoding.ASCII.GetString(buffer, 0, i - 3);
                }
            }
        }

        return null;
    }

    // 클라이언트 프레임 1개를 읽습니다(조각 메시지는 받지 않음: FIN 이 없으면 오류). 반환값은 opcode.
    public static int ReadFrame(TcpClient client, NetworkStream stream, int maxBytes, long deadlineTicks, out byte[] payload)
    {
        byte[] header = new byte[8];
        ReadExactly(client, stream, header, 0, 2, deadlineTicks);
        bool fin = (header[0] & 0x80) != 0;
        int opcode = header[0] & 0x0F;
        bool masked = (header[1] & 0x80) != 0;
        long length = header[1] & 0x7F;
        if (length == 126)
        {
            ReadExactly(client, stream, header, 0, 2, deadlineTicks);
            length = (header[0] << 8) | header[1];
        }
        else if (length == 127)
        {
            ReadExactly(client, stream, header, 0, 8, deadlineTicks);
            length = 0;
            for (int i = 0; i < 8; i++)
            {
                length = (length << 8) | header[i];
            }
        }

        if (!fin || length < 0 || length > maxBytes)
        {
            throw new IOException("unsupported frame");
        }

        byte[] mask = new byte[4];
        if (masked)
        {
            ReadExactly(client, stream, mask, 0, 4, deadlineTicks);
        }

        payload = new byte[length];
        ReadExactly(client, stream, payload, 0, (int)length, deadlineTicks);
        if (masked)
        {
            for (int i = 0; i < payload.Length; i++)
            {
                payload[i] ^= mask[i & 3];
            }
        }

        return opcode;
    }

    // 서버 → 클라이언트 프레임(마스크 없음, FIN)을 배열 하나로 만듭니다.
    public static byte[] BuildFrame(int opcode, byte[] payload)
    {
        int count = payload.Length;
        int head = count < 126 ? 2 : count <= ushort.MaxValue ? 4 : 10;
        byte[] frame = new byte[head + count];
        frame[0] = (byte)(0x80 | opcode);
        if (head == 2)
        {
            frame[1] = (byte)count;
        }
        else if (head == 4)
        {
            frame[1] = 126;
            frame[2] = (byte)(count >> 8);
            frame[3] = (byte)count;
        }
        else
        {
            frame[1] = 127;
            long length = count;
            for (int i = 0; i < 8; i++)
            {
                frame[9 - i] = (byte)(length >> (8 * i));
            }
        }

        Buffer.BlockCopy(payload, 0, frame, head, count);
        return frame;
    }

    public static byte[] BuildCloseFrame(ushort code)
    {
        return BuildFrame(OPCODE_CLOSE, new[] { (byte)(code >> 8), (byte)code });
    }

    // 같은 소켓에 두 스레드가 쓸 수 있는 곳(대기실: 읽기 스레드의 답장과 메인 스레드의 닫기)에서 메시지 단위로 잠급니다.
    public static void WriteFrame(NetworkStream stream, int opcode, byte[] payload)
    {
        byte[] frame = BuildFrame(opcode, payload);
        lock (stream)
        {
            stream.Write(frame, 0, frame.Length);
        }
    }

    public static void WriteText(NetworkStream stream, string text)
    {
        WriteFrame(stream, OPCODE_TEXT, Encoding.UTF8.GetBytes(text));
    }

    // 닫기 프레임을 보냅니다. 이미 끊긴 소켓이어도 예외를 내지 않습니다.
    public static void TryWriteClose(NetworkStream stream, ushort code)
    {
        try
        {
            byte[] frame = BuildCloseFrame(code);
            lock (stream)
            {
                stream.Write(frame, 0, frame.Length);
            }
        }
        catch (Exception)
        {
        }
    }

    // 거절할 때 쓰는 닫기: 닫기 프레임을 보내고, 보내는 쪽만 닫은 뒤, 상대가 이미 보낸 것을 잠깐(최대 200ms, 8KB) 읽어 버립니다.
    // 읽지 않은 데이터가 남은 채 소켓을 닫으면 Windows 가 RST 를 보내, 닫기 코드가 상대에게 닿기 전에 연결이 끊깁니다
    // (너무 긴 인증 메시지를 거절할 때 닫기 코드 4401 이 사라지던 것: 06_unity_integration 4장).
    public static void WriteCloseAndDrain(TcpClient client, NetworkStream stream, ushort code)
    {
        TryWriteClose(stream, code);
        try
        {
            client.Client.Shutdown(SocketShutdown.Send);
            client.ReceiveTimeout = 200;
            byte[] sink = new byte[1024];
            int total = 0;
            while (total < 8 * 1024)
            {
                int read = stream.Read(sink, 0, sink.Length);
                if (read <= 0)
                {
                    break;
                }

                total += read;
            }
        }
        catch (Exception)
        {
        }
    }

    // 메인 스레드용: 닫기 프레임을 "기다리지 않고" 보냅니다. 다른 스레드가 쓰기 잠금을 쥐고 있거나(사라진 상대에게 쓰다 막힘)
    // 소켓 버퍼가 차 있으면 보내지 않습니다. 부르는 쪽이 곧 소켓을 닫습니다(코드 리뷰 M1).
    public static void TryWriteCloseNoWait(TcpClient client, NetworkStream stream, ushort code)
    {
        bool taken = false;
        try
        {
            Monitor.TryEnter(stream, CLOSE_LOCK_WAIT_MS, ref taken);
            if (!taken || !client.Client.Poll(0, System.Net.Sockets.SelectMode.SelectWrite))
            {
                return;
            }

            byte[] frame = BuildCloseFrame(code);
            stream.Write(frame, 0, frame.Length);
        }
        catch (Exception)
        {
        }
        finally
        {
            if (taken)
            {
                Monitor.Exit(stream);
            }
        }
    }
}
