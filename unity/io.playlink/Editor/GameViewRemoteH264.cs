using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

// ffmpeg(h264_nvenc) 외부 프로세스로 Game 뷰 프레임을 H.264로 인코딩합니다(무선 adb처럼 대역폭이 좁은 경로용, MJPEG의 약 1/5~1/20).
// 입력: stdin으로 RGBA 원본 프레임(GPU에서 이미 뒤집고 줄인 것). 출력: stdout FLV. FLV 태그는 길이가 앞에 있어서 프레임 하나가 끝나는 즉시
// 잘라 낼 수 있고(Annex-B처럼 다음 시작 코드를 기다리지 않음), 첫 태그는 폰 WebCodecs 설정에 그대로 쓰는 avcC(AVCDecoderConfigurationRecord)입니다.
// 설정: B프레임 없음, 초저지연(-tune ull, -zerolatency), CBR. 실측(RTX 4060, 720x1280): 프레임 입력 → 출력 p50 3.0ms, p95 3.5ms, 시작 약 0.3초.
// 입력 1장 = 출력 1장(재정렬 없음)이라 프레임 정보(입력 번호, 캡처 시각)는 넣은 순서대로 꺼내 짝을 맞춥니다.
public sealed class GameViewRemoteH264 : IDisposable
{
    // 키프레임 간격(프레임). 새 클라이언트나 디코딩 오류 때는 인코더를 다시 시작해 바로 키프레임을 만듭니다.
    private const int GOP_FRAMES = 600;
    private const int FLV_HEADER_BYTES = 13;
    private const int FLV_TAG_HEADER_BYTES = 11;
    private const int FLV_VIDEO_TAG = 9;
    // VIDEODATA 앞부분: frameType/codec 1 + AVCPacketType 1 + compositionTime 3
    private const int AVC_PACKET_PREFIX = 5;

    // 인코딩을 기다리는 프레임 정보(넣은 순서)
    public struct FrameInfo
    {
        public int width;
        public int height;
        public int inputSeq;
        public long captureTicks;
        public long feedTicks;
    }

    // 출력 1개: config = avcC(설정), 아니면 AVCC(길이 4바이트 + NAL) 프레임 1장
    public Action<byte[], bool, bool, FrameInfo> onOutput;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public bool HasExited { get { return process == null || process.HasExited; } }
    public string LastError { get { return lastError; } }

    private readonly object infoLock = new object();
    private readonly Queue<FrameInfo> pendingInfo = new Queue<FrameInfo>();
    private readonly StringBuilder stderrTail = new StringBuilder();
    private Process process;
    private Stream stdin;
    private Thread reader;
    private volatile string lastError;
    private volatile bool disposed;

    // 설정 경로 → PATH → winget 링크 순서로 ffmpeg.exe를 찾습니다. 없으면 null
    public static string FindFfmpeg(string configured)
    {
        if (!string.IsNullOrEmpty(configured) && File.Exists(configured))
        {
            return configured;
        }

        List<string> candidates = new List<string>();
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            if (!string.IsNullOrWhiteSpace(dir))
            {
                candidates.Add(Path.Combine(dir.Trim(), "ffmpeg.exe"));
            }
        }

        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links", "ffmpeg.exe"));
        foreach (string candidate in candidates)
        {
            try
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
            }
        }

        return null;
    }

    // 인코더 프로세스를 시작합니다(인코더 스레드). 실패하면 false + LastError
    public bool Start(string ffmpegPath, int width, int height, int bitrateKbps)
    {
        Width = width;
        Height = height;
        // bufsize를 프레임 2장 분량으로 작게 잡아 비트레이트가 순간적으로 튀지 않게 합니다(전송 큐가 쌓이지 않게).
        int bufsizeKbits = Math.Max(100, bitrateKbps / 30);
        string args = "-hide_banner -loglevel error -f rawvideo -pix_fmt rgba "
            + $"-s {width}x{height} -framerate 60 -i pipe:0 "
            + "-c:v h264_nvenc -preset p1 -tune ull -zerolatency 1 -delay 0 -bf 0 "
            + $"-g {GOP_FRAMES} -rc cbr -b:v {bitrateKbps}k -maxrate {bitrateKbps}k -bufsize {bufsizeKbits}k -profile:v high "
            + "-flush_packets 1 -flvflags no_duration_filesize -f flv pipe:1";
        try
        {
            ProcessStartInfo info = new ProcessStartInfo(ffmpegPath, args)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            process = Process.Start(info);
            stdin = process.StandardInput.BaseStream;
            process.ErrorDataReceived += OnStderr;
            process.BeginErrorReadLine();
            reader = new Thread(ReadLoop) { IsBackground = true, Name = "GVR H264 Reader" };
            reader.Start();
            return true;
        }
        catch (Exception e)
        {
            lastError = $"ffmpeg 시작 실패: {e.Message}";
            return false;
        }
    }

    // 프레임 1장을 넣습니다(인코더 스레드). 파이프에 다 써질 때까지 기다립니다. 실패하면 false
    public bool Encode(byte[] rgba, FrameInfo info)
    {
        if (HasExited || disposed)
        {
            lastError = lastError ?? $"ffmpeg가 종료됨: {StderrTail()}";
            return false;
        }

        info.feedTicks = Stopwatch.GetTimestamp();
        lock (infoLock)
        {
            pendingInfo.Enqueue(info);
        }

        try
        {
            stdin.Write(rgba, 0, Width * Height * 4);
            stdin.Flush();
            return true;
        }
        catch (Exception e)
        {
            lastError = $"ffmpeg 입력 실패: {e.Message} {StderrTail()}";
            return false;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        try
        {
            stdin?.Close();
        }
        catch (Exception)
        {
        }

        try
        {
            if (process != null && !process.WaitForExit(300))
            {
                process.Kill();
            }
        }
        catch (Exception)
        {
        }

        process?.Dispose();
        process = null;
    }

    private void OnStderr(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Data))
        {
            return;
        }

        lock (stderrTail)
        {
            stderrTail.AppendLine(e.Data);
            if (stderrTail.Length > 2000)
            {
                stderrTail.Remove(0, stderrTail.Length - 2000);
            }
        }
    }

    private string StderrTail()
    {
        lock (stderrTail)
        {
            return stderrTail.ToString().Trim();
        }
    }

    // stdout FLV를 태그 단위로 읽어 영상 태그만 내보냅니다(리더 스레드).
    private void ReadLoop()
    {
        Stream output = process.StandardOutput.BaseStream;
        byte[] tagHeader = new byte[FLV_TAG_HEADER_BYTES];
        byte[] skip = new byte[4];
        try
        {
            if (!ReadExactly(output, new byte[FLV_HEADER_BYTES], FLV_HEADER_BYTES))
            {
                return;
            }

            while (!disposed)
            {
                if (!ReadExactly(output, tagHeader, FLV_TAG_HEADER_BYTES))
                {
                    break;
                }

                int size = (tagHeader[1] << 16) | (tagHeader[2] << 8) | tagHeader[3];
                byte[] data = new byte[size];
                if (!ReadExactly(output, data, size) || !ReadExactly(output, skip, 4))
                {
                    break;
                }

                if (tagHeader[0] != FLV_VIDEO_TAG || size <= AVC_PACKET_PREFIX)
                {
                    continue;
                }

                bool key = (data[0] >> 4) == 1;
                bool config = data[1] == 0;
                byte[] payload = new byte[size - AVC_PACKET_PREFIX];
                Buffer.BlockCopy(data, AVC_PACKET_PREFIX, payload, 0, payload.Length);
                FrameInfo info = default;
                if (!config)
                {
                    lock (infoLock)
                    {
                        if (pendingInfo.Count > 0)
                        {
                            info = pendingInfo.Dequeue();
                        }
                    }
                }

                onOutput?.Invoke(payload, key, config, info);
            }
        }
        catch (Exception e)
        {
            if (!disposed)
            {
                lastError = $"ffmpeg 출력 읽기 실패: {e.Message}";
            }
        }

        if (!disposed && lastError == null)
        {
            lastError = $"ffmpeg가 종료됨: {StderrTail()}";
        }
    }

    private static bool ReadExactly(Stream stream, byte[] buffer, int count)
    {
        int offset = 0;
        while (offset < count)
        {
            int read = stream.Read(buffer, offset, count - offset);
            if (read <= 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }
}
