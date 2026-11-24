using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace OpenM50Webcam.Server;

/// <summary>
/// Tiny HTTP server:
///   /             full-window viewer page (use as OBS "Browser" source)
///   /stream.mjpg  multipart MJPEG stream (use as OBS "Media Source" input / VLC / ffmpeg)
///   /snapshot.jpg latest frame
///   /status       JSON status
/// </summary>
/// <param name="RemotePort">Client-side TCP port, used to find the owning process.</param>
internal sealed record StreamClient(long Id, int RemotePort, bool IsVirtualCamera, DateTime Since);

internal sealed class MjpegServer
{
    private const string Boundary = "openm50frame";

    private readonly FrameHub _hub;
    private readonly Func<string> _cameraState;
    private readonly TcpListener _listener;
    private readonly object _clientsLock = new();
    private readonly Dictionary<long, StreamClient> _streamClients = new();
    private long _nextClientId;

    public MjpegServer(FrameHub hub, IPAddress address, int port, Func<string> cameraState)
    {
        _hub = hub;
        _cameraState = cameraState;
        _listener = new TcpListener(address, port);
    }

    /// <summary>Clients currently receiving the MJPEG stream (OBS, browsers, the virtual camera).</summary>
    public IReadOnlyList<StreamClient> StreamClients
    {
        get { lock (_clientsLock) return _streamClients.Values.ToArray(); }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _listener.Start();
        using var reg = ct.Register(() => _listener.Stop());
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (Exception) when (ct.IsCancellationRequested) { break; }
            _ = Task.Run(() => HandleAsync(client, ct), ct);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            client.NoDelay = true;
            using (client)
            await using (var stream = client.GetStream())
            {
                string? path = await ReadRequestPathAsync(stream, ct).ConfigureAwait(false);
                if (path is null) return;
                string query = "";
                int q = path.IndexOf('?');
                if (q >= 0)
                {
                    query = path[(q + 1)..];
                    path = path[..q];
                }

                switch (path)
                {
                    case "/":
                    case "/index.html":
                        await WriteResponseAsync(stream, "200 OK", "text/html; charset=utf-8", Encoding.UTF8.GetBytes(ViewerHtml), ct);
                        break;
                    case "/stream.mjpg":
                    case "/stream":
                        var info = new StreamClient(
                            Interlocked.Increment(ref _nextClientId),
                            (client.Client.RemoteEndPoint as IPEndPoint)?.Port ?? 0,
                            query.Contains("client=vcam", StringComparison.OrdinalIgnoreCase),
                            DateTime.Now);
                        lock (_clientsLock) _streamClients[info.Id] = info;
                        try { await StreamAsync(stream, ct).ConfigureAwait(false); }
                        finally { lock (_clientsLock) _streamClients.Remove(info.Id); }
                        break;
                    case "/snapshot.jpg":
                        var f = _hub.Latest;
                        if (f is null)
                            await WriteResponseAsync(stream, "503 Service Unavailable", "text/plain", "no frame yet"u8.ToArray(), ct);
                        else
                            await WriteResponseAsync(stream, "200 OK", "image/jpeg", f.Jpeg, ct);
                        break;
                    case "/status":
                        await WriteResponseAsync(stream, "200 OK", "application/json", Encoding.UTF8.GetBytes(StatusJson()), ct);
                        break;
                    default:
                        await WriteResponseAsync(stream, "404 Not Found", "text/plain", "not found"u8.ToArray(), ct);
                        break;
                }
            }
        }
        catch (Exception) { /* client went away */ }
    }

    private async Task StreamAsync(NetworkStream stream, CancellationToken ct)
    {
        string header =
            "HTTP/1.1 200 OK\r\n" +
            $"Content-Type: multipart/x-mixed-replace; boundary={Boundary}\r\n" +
            "Cache-Control: no-cache, no-store, must-revalidate\r\n" +
            "Pragma: no-cache\r\n" +
            "Access-Control-Allow-Origin: *\r\n" +
            "Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct).ConfigureAwait(false);

        long seq = 0;
        while (!ct.IsCancellationRequested)
        {
            Frame frame;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                frame = await _hub.NextAsync(seq, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // No new frame (camera disconnected/reconnecting). Send nothing: clients detect the
                // stall themselves and show a "no signal" message instead of a frozen frame.
                continue;
            }
            seq = frame.Sequence;

            string partHeader =
                $"--{Boundary}\r\n" +
                "Content-Type: image/jpeg\r\n" +
                $"Content-Length: {frame.Jpeg.Length}\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(partHeader), ct).ConfigureAwait(false);
            await stream.WriteAsync(frame.Jpeg, ct).ConfigureAwait(false);
            await stream.WriteAsync("\r\n"u8.ToArray(), ct).ConfigureAwait(false);
        }
    }

    // "state" is one of: streaming, no-camera, connecting, error (machine readable, used by the
    // viewer page and the virtual camera to show a matching message).
    private string StatusJson()
    {
        var f = _hub.Latest;
        long ageMs = _hub.MillisecondsSinceLastFrame;
        string camera = _cameraState();
        string state = ageMs < 1500 ? "streaming"
            : camera.StartsWith("waiting", StringComparison.Ordinal) ? "no-camera"
            : camera.StartsWith("error", StringComparison.Ordinal) ? "error"
            : "connecting";
        return string.Create(CultureInfo.InvariantCulture,
            $"{{\"state\":\"{state}\",\"camera\":\"{camera.Replace("\"", "'")}\",\"fps\":{_hub.Fps:0.0},\"width\":{f?.Width ?? 0},\"height\":{f?.Height ?? 0},\"frames\":{f?.Sequence ?? 0},\"age_ms\":{ageMs},\"clients\":{StreamClients.Count}}}");
    }

    private static async Task<string?> ReadRequestPathAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[4096];
        int total = 0;
        while (total < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (n == 0) return null;
            total += n;
            if (buffer.AsSpan(0, total).IndexOf("\r\n\r\n"u8) >= 0) break;
        }
        string request = Encoding.ASCII.GetString(buffer, 0, total);
        string firstLine = request.Split("\r\n", 2)[0];
        string[] parts = firstLine.Split(' ');
        return parts.Length >= 2 ? parts[1] : null;
    }

    private static async Task WriteResponseAsync(NetworkStream stream, string status, string contentType, byte[] body, CancellationToken ct)
    {
        string header =
            $"HTTP/1.1 {status}\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Cache-Control: no-cache\r\n" +
            "Access-Control-Allow-Origin: *\r\n" +
            "Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct).ConfigureAwait(false);
        await stream.WriteAsync(body, ct).ConfigureAwait(false);
    }

    // Viewer page: fills the whole browser source, keeps aspect ratio, black bars if needed.
    // Add "?status" to the URL to show an fps overlay (not for OBS).
    private const string ViewerHtml = """
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <title>OpenM50 Webcam</title>
        <style>
          html, body { margin: 0; height: 100%; background: #000; overflow: hidden; }
          img { width: 100vw; height: 100vh; object-fit: contain; display: block; transition: filter .4s; }
          body.off img { filter: brightness(.25) grayscale(.8); }
          #s { position: fixed; left: 8px; top: 8px; font: 13px system-ui, sans-serif; color: #fff;
               background: rgba(0,0,0,.55); padding: 4px 8px; border-radius: 4px; display: none; }
          #msg { position: fixed; inset: 0; display: none; flex-direction: column; align-items: center;
                 justify-content: center; text-align: center; color: #fff; font-family: "Segoe UI", system-ui, sans-serif;
                 padding: 0 10vw; }
          body.off #msg { display: flex; }
          #msg svg { width: 13vh; height: 13vh; margin-bottom: 4vh; }
          #msg h1 { font-size: 6.5vh; font-weight: 600; margin: 0 0 2vh; }
          #msg p { font-size: 3.1vh; color: #c8c8c8; margin: 0; white-space: pre-line; line-height: 1.4; }
          #msg small { position: fixed; bottom: 3vh; font-size: 2.3vh; color: #777; }
        </style>
        </head>
        <body>
        <img id="v" src="/stream.mjpg" alt="">
        <div id="msg">
          <svg viewBox="0 0 64 64" fill="none" stroke="#ebebeb" stroke-width="2.5" stroke-linecap="round">
            <rect x="8" y="20" width="48" height="30" rx="6"/><path d="M24 20l3-6h10l3 6"/><circle cx="32" cy="35" r="9"/>
            <path id="slash" d="M6 6l52 52"/>
          </svg>
          <h1 id="t"></h1><p id="d"></p><small>OpenM50 Webcam</small>
        </div>
        <div id="s"></div>
        <script>
          const texts = {
            offline:     ['OpenM50 Webcam is not running', 'Start the OpenM50 Webcam app to see the camera image.'],
            'no-camera': ['No camera connected', 'Connect the Canon EOS M50 via USB and switch it on.\nWi-Fi and Bluetooth on the camera must be off.'],
            error:       ['Camera is not responding', 'Reconnecting automatically.\nIf this persists, switch the camera off and on again.'],
            connecting:  ['Connecting to the camera…', 'One moment please.'],
          };
          const v = document.getElementById('v');
          const showStatus = location.search.includes('status');
          if (showStatus) document.getElementById('s').style.display = 'block';
          // Reconnect if the stream drops (e.g. program restarted).
          v.onerror = () => setTimeout(() => { v.src = '/stream.mjpg?t=' + Date.now(); }, 1000);

          function show(state) {
            const off = state !== 'streaming';
            document.body.classList.toggle('off', off);
            if (!off) return;
            const [t, d] = texts[state] || texts.connecting;
            document.getElementById('t').textContent = t;
            document.getElementById('d').textContent = d;
            document.getElementById('slash').style.display = state === 'connecting' ? 'none' : '';
          }
          let wasOffline = false;
          setInterval(async () => {
            try {
              const j = await (await fetch('/status', { cache: 'no-store' })).json();
              if (wasOffline) { v.src = '/stream.mjpg?t=' + Date.now(); wasOffline = false; }
              show(j.state);
              if (showStatus) document.getElementById('s').textContent = `${j.camera} · ${j.width}×${j.height} · ${j.fps} fps`;
            } catch {
              wasOffline = true;
              show('offline');
              if (showStatus) document.getElementById('s').textContent = 'server offline';
            }
          }, 1000);
        </script>
        </body>
        </html>
        """;
}
