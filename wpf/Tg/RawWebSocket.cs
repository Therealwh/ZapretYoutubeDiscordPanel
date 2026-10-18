using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Ydp.Tg
{
    /// <summary>
    /// Minimal RFC 6455 client over TLS.
    ///
    /// Built by hand (no WebSocket library) for one reason: the anti-DPI tricks
    /// the reference implementation relies on need the TCP peer, the TLS SNI and
    /// the HTTP Host to be set INDEPENDENTLY.
    ///   - direct route: connect to the DC IP, SNI/Host = kwsN.web.telegram.org;
    ///   - fronted route: connect to the DC IP, SNI = a neutral domain, Host =
    ///     kwsN.web.telegram.org, and the certificate is not verified because the
    ///     SNI deliberately does not match it.
    /// A high-level WebSocket API cannot express that; raw SslStream can.
    /// </summary>
    public sealed class RawWebSocket : IDisposable
    {
        private const string WsGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
        private const int MaxMessageLen = 16 * 1024 * 1024;

        private readonly Stream _stream;
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        private bool _closed;

        private RawWebSocket(Stream stream)
        {
            _stream = stream;
        }

        /// <summary>
        /// Open a WebSocket.
        /// </summary>
        /// <param name="connectHost">DNS name or IP used for the TCP/TLS connection.</param>
        /// <param name="sni">TLS server name; may differ from host for fronting.</param>
        /// <param name="hostHeader">HTTP Host header.</param>
        /// <param name="verifyCertificate">false only for the fronted route.</param>
        public static async Task<RawWebSocket> ConnectAsync(
            string connectHost,
            string sni,
            string hostHeader,
            string path = "/apiws",
            bool verifyCertificate = true,
            int timeoutMs = 10000,
            Action<string, string> log = null)
        {
            var tcp = new TcpClient();
            try
            {
                var connect = tcp.ConnectAsync(connectHost, 443);
                if (await Task.WhenAny(connect, Task.Delay(timeoutMs)) != connect)
                {
                    // No log here on purpose: every caller already logs the
                    // failure with its own context (DC id, host, route), so
                    // logging here too wrote every Cloudflare relay failure
                    // twice — once bare, once prefixed — and drowned the log.
                    throw new IOException("tcp_timeout");
                }
                await connect;

                var ssl = new SslStream(tcp.GetStream(), false, (sender, cert, chain, errors) =>
                {
                    if (verifyCertificate) return errors == SslPolicyErrors.None;
                    // Fronted route: the SNI is intentionally unrelated to the
                    // certificate, so certificate validation is skipped here.
                    log?.Invoke("fronted: certificate not verified", "info");
                    return true;
                });
                // net48 has no SslClientAuthenticationOptions overload; the validation callback
                // passed to the SslStream constructor decides what we accept, and the
                // target host here is the SNI (which is deliberately NOT the Host
                // header on the fronted route).
                ssl.AuthenticateAsClient(sni);

                var ws = new RawWebSocket(ssl);
                await ws.HandshakeAsync(hostHeader, path, timeoutMs);
                return ws;
            }
            catch
            {
                tcp.Close();
                throw;
            }
        }

        private async Task HandshakeAsync(string hostHeader, string path, int timeoutMs)
        {
            var key = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(key);
            string wsKey = Convert.ToBase64String(key);

            var request = new StringBuilder();
            request.Append("GET ").Append(path).Append(" HTTP/1.1\r\n");
            request.Append("Host: ").Append(hostHeader).Append("\r\n");
            request.Append("Upgrade: websocket\r\n");
            request.Append("Connection: Upgrade\r\n");
            request.Append("Sec-WebSocket-Key: ").Append(wsKey).Append("\r\n");
            request.Append("Sec-WebSocket-Version: 13\r\n");
            request.Append("Sec-WebSocket-Protocol: binary\r\n");
            request.Append("\r\n");

            var bytes = Encoding.ASCII.GetBytes(request.ToString());
            var write = _stream.WriteAsync(bytes, 0, bytes.Length, CancellationToken.None);
            if (await Task.WhenAny(write, Task.Delay(timeoutMs)) != write) throw new IOException("ws_write_timeout");
            await write;

            // Read the response headers.
            var head = new MemoryStream();
            var one = new byte[1];
            while (true)
            {
                int read = await ReadExactAsync(one, 1, timeoutMs);
                if (read <= 0) throw new IOException("ws_closed");
                head.WriteByte(one[0]);
                if (head.Length >= 4)
                {
                    byte[] tail = head.ToArray();
                    int n = tail.Length;
                    if (tail[n - 4] == '\r' && tail[n - 3] == '\n' && tail[n - 2] == '\r' && tail[n - 1] == '\n') break;
                }
                if (head.Length > 32768) throw new IOException("ws_headers_too_long");
            }

            string headerText = Encoding.ASCII.GetString(head.ToArray());
            string[] lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
            string[] statusParts = lines[0].Split(' ');
            int status = statusParts.Length > 1 && int.TryParse(statusParts[1], out int code) ? code : 0;
            if (status != 101) throw new IOException("ws_handshake_" + status);

            string accept = null;
            foreach (var line in lines)
            {
                if (line.StartsWith("Sec-WebSocket-Accept:", StringComparison.OrdinalIgnoreCase))
                {
                    accept = line.Substring("Sec-WebSocket-Accept:".Length).Trim();
                }
            }
            using (var sha = SHA1.Create())
            {
                string expected = Convert.ToBase64String(sha.ComputeHash(Encoding.ASCII.GetBytes(wsKey + WsGuid)));
                if (accept != expected) throw new IOException("ws_accept_mismatch");
            }
        }

        private async Task<int> ReadExactAsync(byte[] buffer, int count, int timeoutMs)
        {
            int total = 0;
            using (var cts = new CancellationTokenSource(timeoutMs))
            {
                while (total < count)
                {
                    int read = await _stream.ReadAsync(buffer, total, count - total, cts.Token);
                    if (read <= 0) return total;
                    total += read;
                }
            }
            return total;
        }

        private readonly byte[] _frameHeader = new byte[14];
        private readonly byte[] _readBuffer = new byte[16384];

        /// <summary>Read one complete binary message, answering pings.</summary>
        public async Task<byte[]> ReceiveAsync(CancellationToken token)
        {
            using var frag = new MemoryStream();
            int fragOpcode = -1;
            while (true)
            {
                if (await ReadExactAsync(_frameHeader, 2, 60000) <= 1) throw new IOException("ws_closed");
                bool fin = (_frameHeader[0] & 0x80) != 0;
                int opcode = _frameHeader[0] & 0x0f;
                bool masked = (_frameHeader[1] & 0x80) != 0;
                long length = _frameHeader[1] & 0x7f;

                if (length == 126)
                {
                    if (await ReadExactAsync(_frameHeader, 2, 60000) <= 1) throw new IOException("ws_closed");
                    length = (_frameHeader[0] << 8) | _frameHeader[1];
                }
                else if (length == 127)
                {
                    if (await ReadExactAsync(_frameHeader, 8, 60000) <= 7) throw new IOException("ws_closed");
                    length = 0;
                    for (int i = 0; i < 8; i++) length = (length << 8) | _frameHeader[i];
                }
                if (length > MaxMessageLen) throw new IOException("ws_frame_too_large");

                byte[] mask = null;
                if (masked)
                {
                    if (await ReadExactAsync(_frameHeader, 4, 60000) <= 3) throw new IOException("ws_closed");
                    mask = new byte[4];
                    Buffer.BlockCopy(_frameHeader, 0, mask, 0, 4);
                }

                var payload = new byte[length];
                if (length > 0 && await ReadExactAsync(payload, (int)length, 120000) <= 0) throw new IOException("ws_closed");
                if (mask != null)
                {
                    for (int i = 0; i < payload.Length; i++) payload[i] ^= mask[i % 4];
                }

                switch (opcode)
                {
                    case 0x8: // close
                        throw new IOException("ws_close_from_upstream");
                    case 0x9: // ping
                        await SendFrameAsync(0xA, payload);
                        continue;
                    case 0xA: // pong
                        continue;
                    case 0x0:
                        frag.Write(payload, 0, payload.Length);
                        break;
                    default:
                        fragOpcode = opcode;
                        frag.Write(payload, 0, payload.Length);
                        break;
                }
                if (fin)
                {
                    return frag.ToArray();
                }
            }
        }

        public Task SendAsync(byte[] data)
        {
            return SendFrameAsync(0x2, data);
        }

        private async Task SendFrameAsync(int opcode, byte[] data)
        {
            await _sendLock.WaitAsync();
            try
            {
                using var ms = new MemoryStream();
                ms.WriteByte((byte)(0x80 | opcode));
                var mask = new byte[4];
                using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(mask);

                int length = data.Length;
                if (length < 126)
                {
                    ms.WriteByte((byte)(0x80 | length));
                }
                else if (length < 65536)
                {
                    ms.WriteByte(0x80 | 126);
                    ms.WriteByte((byte)(length >> 8));
                    ms.WriteByte((byte)(length & 0xff));
                }
                else
                {
                    ms.WriteByte(0x80 | 127);
                    for (int i = 7; i >= 0; i--) ms.WriteByte((byte)(length >> (i * 8)));
                }
                ms.Write(mask, 0, 4);
                // Client frames MUST be masked (RFC 6455); the reference does the
                // same or Telegram closes the session.
                for (int i = 0; i < length; i++) ms.WriteByte((byte)(data[i] ^ mask[i % 4]));

                var buffer = ms.ToArray();
                await _stream.WriteAsync(buffer, 0, buffer.Length, CancellationToken.None);
                await _stream.FlushAsync();
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public void Close()
        {
            if (_closed) return;
            _closed = true;
            try { _stream.Close(); } catch { /* already gone */ }
        }

        public void Dispose()
        {
            Close();
            _sendLock.Dispose();
        }
    }
}