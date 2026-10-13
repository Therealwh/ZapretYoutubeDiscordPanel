using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Ydp.Tg
{
    public sealed class ProxyStatsView
    {
        public bool running { get; set; }
        public string host { get; set; }
        public int port { get; set; }
        public long connections { get; set; }
        public long bytesUp { get; set; }
        public long bytesDown { get; set; }
        public string lastError { get; set; }
        public string startedAt { get; set; }
        public string proxyUrl { get; set; }
    }

    /// <summary>
    /// Local MTProto -&gt; WebSocket bridge (MTProxy for Telegram Desktop).
    ///
    /// Route order mirrors the reference implementation (Flowseal/tg-ws-proxy):
    ///   1. Telegram's own MTWS relays, connected BY DC IP with the kws host as
    ///      SNI/Host - plus a fronted variant whose SNI is a neutral domain, which
    ///      is what survives a DPI that blocks on "telegram" in the handshake;
    ///   2. Cloudflare-proxied relays: a WebSocket to kws&lt;dc&gt;.&lt;domain&gt; from
    ///      the published domain list (NOT an HTTP POST to /api - the worker
    ///      only speaks WebSocket here);
    ///   3. plain TCP straight to the DC address.
    /// </summary>
    public static class TgProxy
    {
        public const string DefaultHost = "127.0.0.1";
        public const int DefaultPort = 1443;
        public const string WsPath = "/apiws";
        public const string FrontingSni = "sprinthost.ru";
        public const string CfDomainsUrl =
            "https://raw.githubusercontent.com/Flowseal/tg-ws-proxy/main/.github/cfproxy-domains.txt";

        public static readonly Dictionary<int, string> DcDefaultIps = new Dictionary<int, string>
        {
            { 1, "149.154.175.50" },
            { 2, "149.154.167.51" },
            { 3, "149.154.175.100" },
            { 4, "149.154.167.91" },
            { 5, "149.154.171.5" },
            { 203, "91.105.192.100" },
        };

        // The reference redirects DC2/DC4 to one address by default.
        public static readonly Dictionary<int, string> DcRedirects = new Dictionary<int, string>
        {
            { 2, "149.154.167.220" },
            { 4, "149.154.167.220" },
        };

        public static readonly string[] SeedCfDomains =
        {
            "virkgj.com", "vmmzovy.com", "mkuosckvso.com", "zaewayzmplad.com",
            "twdmbzcm.com", "awzwsldi.com", "clngqrflngqin.com", "tjacxbqtj.com",
        };

        private static TcpListener _listener;
        private static CancellationTokenSource _cts;
        private static byte[] _secret;
        private static string _host = DefaultHost;
        private static int _port = DefaultPort;
        private static readonly object Gate = new object();
        private static Action<string, string> _log = (m, l) => { };
        private static List<string> _cfDomains = new List<string>(SeedCfDomains);
        private static string _cachePath;
private static volatile bool _cfEnabled = true;
        private static volatile bool _tryFrontingFirst;

        /// <summary>
        /// Own Cloudflare worker domain (e.g. from the reference project's
        /// CfWorker guide). This is the route that survives a network where the
        /// DC addresses are unreachable AND the shared relay list is dead: the
        /// worker dials Telegram from Cloudflare's edge, while the client only
        /// talks to an ordinary Cloudflare domain.
        /// </summary>
        public static string WorkerDomain
        {
            get => _workerDomain;
            set => _workerDomain = (value ?? string.Empty).Trim().ToLowerInvariant();
        }

        private static string _workerDomain = string.Empty;
        private static volatile bool _cfUsable = true;

        private static int DcAddress(int dc)
        {
            if (DcRedirects.TryGetValue(dc, out string redirect)) return ParseIp(redirect);
            if (DcDefaultIps.TryGetValue(dc, out string ip)) return ParseIp(ip);
            return 0;
        }

        private static int ParseIp(string address)
        {
            if (string.IsNullOrEmpty(address)) return 0;
            System.Net.IPAddress parsed;
            return System.Net.IPAddress.TryParse(address, out parsed)
                ? BitConverter.ToInt32(parsed.GetAddressBytes(), 0)
                : 0;
        }

        private static string DcAddressString(int dc)
        {
            if (DcRedirects.TryGetValue(dc, out string redirect)) return redirect;
            if (DcDefaultIps.TryGetValue(dc, out string ip)) return ip;
            return null;
        }

        public static ProxyStatsView Stats { get; private set; } = new ProxyStatsView { host = DefaultHost, port = DefaultPort };

        public static void SetLogger(Action<string, string> log)
        {
            _log = log ?? ((m, l) => { });
        }

        public static void LoadSecret(byte[] secret)
        {
            if (secret != null && secret.Length == 16) _secret = secret;
        }

        public static byte[] RegenerateSecret()
        {
            _secret = TgCrypto.NewSecret();
            return _secret;
        }

        public static byte[] Secret => _secret;

        public static string ProxyLink()
        {
            string hex = _secret == null ? string.Empty : BitConverter.ToString(_secret).Replace("-", string.Empty).ToLowerInvariant();
            return $"tg://proxy?server={_host}&port={_port}&secret={hex}";
        }

        public static bool Running => _listener != null;

        /// <summary>Re-project the current state into the stats view the UI reads.</summary>
        public static void RefreshStats()
        {
            Stats.running = _listener != null;
            Stats.host = _host;
            Stats.port = _port;
            Stats.proxyUrl = ProxyLink();
        }

        public static async Task<ProxyStatsView> StartAsync(string host, int port, byte[] secret, bool cfEnabled, Action<string, string> log)
        {
            SetLogger(log);
            if (cfEnabled) _cfEnabled = cfEnabled;
            LoadSecret(secret);
            lock (Gate)
            {
                if (_listener != null) return Stats;
                _host = string.IsNullOrWhiteSpace(host) ? DefaultHost : host;
                _port = port > 0 ? port : DefaultPort;
                _secret = _secret ?? TgCrypto.NewSecret();

_cts = new CancellationTokenSource();
                var listener = new TcpListener(IPAddress.Parse(_host == "0.0.0.0" ? "127.0.0.1" : _host), _port);
                try
                {
                    listener.Start();
                }
                catch (System.Net.Sockets.SocketException ex)
                {
                    _cts = null;
                    // The raw Win32 message ("address already in use") is useless
                    // here: Telegram is configured for 127.0.0.1:1443 and keeps
                    // reconnecting to a dead port. Say what actually happened.
                    string reason = ex.SocketErrorCode == System.Net.Sockets.SocketError.AddressAlreadyInUse
                        ? "port_busy"
                        : "listen_failed";
                    _log(reason == "port_busy"
                        ? "Порт " + _port + " уже занят — вероятно, прокси уже запущен в другом экземпляре панели. Закройте старую панель и запустите заново."
                        : "Не удалось занять порт " + _port + ": " + ex.Message, "error");
                    return new ProxyStatsView
                    {
                        running = false,
                        host = _host,
                        port = _port,
                        lastError = reason,
                        proxyUrl = ProxyLink(),
                    };
                }
                _listener = listener;
                Stats = new ProxyStatsView
                {
                    running = true,
                    host = _host,
                    port = _port,
                    startedAt = DateTime.UtcNow.ToString("HH:mm:ss"),
                    proxyUrl = ProxyLink(),
                };
                _log($"Telegram proxy listening on {_host}:{_port}", "info");
var token = _cts.Token;
                Task.Run(() => AcceptLoopAsync(token));
                // Load the last validated pool first so a new connection does not
                // wait for the network check, then refresh and re-validate in the
                // background.
                LoadCfCache(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "YoutubeDiscordPanel", "cf-domains.txt"));
                Task.Run(() => RefreshCfDomainsAsync());
            }
            return Stats;
        }

        public static void Stop()
        {
            lock (Gate)
            {
                try { _cts?.Cancel(); } catch { /* ignore */ }
                try { _listener?.Stop(); } catch { /* ignore */ }
                _listener = null;
                _cts = null;
                Stats.running = false;
                _log("Telegram proxy stopped", "info");
            }
        }

        private static async Task AcceptLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested && _listener != null)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(); }
                catch { break; }
                _ = Task.Run(() => HandleClientAsync(client, token));
            }
        }

private static async Task RefreshCfDomainsAsync()
        {
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                using (var client = new WebClient())
                {
                    string raw = await client.DownloadStringTaskAsync(CfDomainsUrl);
                    var list = raw.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(d => d.Trim().ToLowerInvariant())
                        .Where(d => d.Length > 0
                            && d.Contains(".")
                            && !d.StartsWith("#")
                            && !d.StartsWith("http"))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    if (list.Count == 0)
                    {
                        _log("Список Cloudflare-релеев пуст, остаюсь на кэше", "warn");
                        return;
                    }

                    _log($"Загружено доменов из списка автора: {list.Count}. Проверяю, какие живые…", "info");
                    var live = await ValidateDomainsAsync(list);
                    if (live.Count > 0)
                    {
                        _cfDomains = live;
                        SaveCfCache(live);
                        _log($"Живых Cloudflare-релеев: {live.Count} из {list.Count}", "info");
                    }
                    else
                    {
                        // Every entry was dead. Remember that, so a new connection
                        // does not spend 40 s proving it again — a single DNS pass
                        // already answered the question.
                        _cfUsable = false;
                        _log("Ни один домен из списка не отвечает — нужен свой Cloudflare worker", "warn");
                    }
                    if (live.Count > 0) _cfUsable = true;
                }
            }
            catch (Exception ex)
            {
                _log("Не удалось обновить список CF-доменов (" + ex.Message + ")", "warn");
            }
        }

        /// <summary>
        /// Resolve kws2.&lt;domain&gt; for every entry and keep the ones that answer.
        ///
        /// The author's list is a set of throwaway domains ("сделано, чтобы их не
        /// забанили"), so entries die much faster than the list is refreshed. A
        /// blind loop over it burns 8 s per dead domain on every new connection;
        /// one parallel DNS pass costs a second and keeps only what works.
        /// </summary>
        private static async Task<List<string>> ValidateDomainsAsync(List<string> domains)
        {
            var candidates = domains.Take(12).ToList();
            var checks = candidates.Select(async domain =>
            {
                try
                {
                    var task = System.Net.Dns.GetHostAddressesAsync("kws2." + domain);
                    var done = await Task.WhenAny(task, Task.Delay(5000));
                    if (done != task) return null;
                    var addresses = await task;
                    return addresses != null && addresses.Length > 0 ? domain : null;
                }
                catch
                {
                    return null;
                }
            }).ToList();

            var resolved = await Task.WhenAll(checks);
            return resolved.Where(d => d != null).ToList();
        }

        private static void SaveCfCache(List<string> domains)
        {
            try
            {
                string dir = _cachePath == null
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YoutubeDiscordPanel")
                    : Path.GetDirectoryName(_cachePath);
                Directory.CreateDirectory(dir);
                _cachePath = Path.Combine(dir, "cf-domains.txt");
                File.WriteAllLines(_cachePath, domains);
            }
            catch { /* cache is optional */ }
        }

        /// <summary>Load previously validated domains so the first connection is fast.</summary>
        public static void LoadCfCache(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
                var list = File.ReadAllLines(path)
                    .Select(l => l.Trim().ToLowerInvariant())
                    .Where(l => l.Length > 0 && l.Contains("."))
                    .ToList();
                if (list.Count > 0)
                {
                    _cfDomains = list;
                    _log($"Загружены проверенные CF-релеи из кэша: {list.Count}", "info");
                }
            }
            catch { /* ignore */ }
        }

        public static int CfDomainCount => _cfDomains.Count;

        /// <summary>
        /// The user's own Cloudflare-proxied relay domain (the scheme from the
        /// reference's CfProxy guide: DNS records kws1..kws6.&lt;domain&gt; pointing
        /// at the DC addresses, orange-clouded through Cloudflare). It is tried
        /// FIRST, before the author's shared list, because it is the only variant
        /// that does not die every few weeks.
        /// </summary>
        public static string CustomCfDomain
        {
            get => _customCfDomain;
            set => _customCfDomain = (value ?? string.Empty).Trim().ToLowerInvariant();
        }

        private static string _customCfDomain = string.Empty;

        private static IEnumerable<string> CfPool()
        {
            if (!string.IsNullOrEmpty(_customCfDomain)) yield return _customCfDomain;
            foreach (var domain in _cfDomains) yield return domain;
        }

        private static IEnumerable<string> WsDomains(int dc, bool isMedia)
        {
            int target = dc == 203 ? 2 : dc;
            if (!isMedia)
            {
                yield return $"kws{target}.web.telegram.org";
            }
            else
            {
                yield return $"kws{target}-1.web.telegram.org";
                yield return $"kws{target}.web.telegram.org";
            }
        }

        private static async Task HandleClientAsync(TcpClient client, CancellationToken token)
        {
            var stats = Stats;
            stats.connections++;
            try
            {
                client.NoDelay = true;
                var stream = client.GetStream();

                var handshake = await ReadExactlyAsync(stream, TgCrypto.HandshakeLen, token);
                if (handshake == null) return;

                var parsed = TgCrypto.ParseHandshake(handshake, _secret);
                if (parsed == null)
                {
                    _log("handshake parse failed (wrong secret or not MTProto)", "warn");
                    return;
                }

                string tag = "DC" + parsed.DcId + (parsed.IsMedia ? "m" : string.Empty);
                _log(tag + ": client handshake ok", "info");

                // Hold the client stream while the upstream dial is in flight.
                byte[] relayInit = TgCrypto.GenerateRelayInit(parsed.ProtoTag, (short)(parsed.IsMedia ? -parsed.DcId : parsed.DcId));
                var ctx = TgCrypto.BuildCryptoContext(parsed.DecPrekeyIv, _secret, relayInit);
                var splitter = new MsgSplitter(parsed.ProtoTag);

                var transport = await DialAsync(parsed, tag, token);
                if (transport == null)
                {
                    stats.lastError = "no upstream route for " + tag;
                    ctx.Dispose();
                    return;
                }

                _log(tag + ": upstream ready via " + transport.Kind, "info");
                await transport.SendAsync(relayInit);

                using (transport)
                {
                    var upTask = PumpClientAsync(stream, transport, ctx, splitter, stats, tag, token);
                    var downTask = PumpUpstreamAsync(transport, stream, ctx, stats, tag, token);
                    await Task.WhenAny(upTask, downTask);
                    transport.Close();
                }
                ctx.Dispose();
                _log(tag + ": closed", "info");
            }
            catch (Exception ex)
            {
                stats.lastError = ex.Message;
                _log("client error: " + ex.Message, "error");
            }
            finally
            {
                try { client.Close(); } catch { /* already gone */ }
            }
        }

        private abstract class Transport : IDisposable
        {
            public abstract string Kind { get; }
            public abstract Task SendAsync(byte[] data);
            public abstract Task<byte[]> ReceiveAsync(CancellationToken token);
            public abstract void Close();
            public virtual void Dispose() { Close(); }
        }

        private sealed class WsTransport : Transport
        {
            private readonly RawWebSocket _ws;
            public WsTransport(RawWebSocket ws) { _ws = ws; }
            public override string Kind => "ws";
            public override Task SendAsync(byte[] data) => _ws.SendAsync(data);
            public override Task<byte[]> ReceiveAsync(CancellationToken token) => _ws.ReceiveAsync(token);
            public override void Close() => _ws.Close();
            public override void Dispose() => _ws.Dispose();
        }

        private sealed class TcpTransport : Transport
        {
            private readonly NetworkStream _stream;
            public TcpTransport(NetworkStream stream) { _stream = stream; }
            public override string Kind => "tcp";
            public override Task SendAsync(byte[] data) => _stream.WriteAsync(data, 0, data.Length);
            public override async Task<byte[]> ReceiveAsync(CancellationToken token)
            {
                var buffer = new byte[16384];
                int read = await _stream.ReadAsync(buffer, 0, buffer.Length, token);
                if (read <= 0) throw new IOException("upstream closed");
                var result = new byte[read];
                Buffer.BlockCopy(buffer, 0, result, 0, read);
                return result;
            }
            public override void Close() { try { _stream.Close(); } catch { /* ignore */ } }
        }

        /// <summary>
        /// Actively probe every transport the bridge can use, so support (and
        /// the --proxy-test mode) can say WHICH route this network allows instead
        /// of "no upstream available". Opens the WebSocket / TCP connection the
        /// same way a real client would.
        /// </summary>
        public static async Task<List<string>> ProbeRoutesAsync(int dc, Action<string, string> log)
        {
            log = log ?? ((m, l) => { });
            var results = new List<string>();
            var hostHeader = WsDomains(dc, false).First();
            string targetIp = DcRedirects.TryGetValue(dc, out string redirect)
                ? redirect
                : (DcDefaultIps.TryGetValue(dc, out string ip) ? ip : null);

            if (targetIp == null)
            {
                results.Add("direct: нет известного IP для DC" + dc);
            }
            else
            {
                try
                {
                    var ws = await RawWebSocket.ConnectAsync(targetIp, hostHeader, hostHeader, WsPath, true, 8000, log);
                    results.Add("direct ws (SNI=" + hostHeader + "): OK");
                    ws.Dispose();
                }
                catch (Exception ex)
                {
                    results.Add("direct ws (SNI=" + hostHeader + "): ошибка — " + ex.Message);
                }

                try
                {
                    var ws = await RawWebSocket.ConnectAsync(targetIp, FrontingSni, hostHeader, WsPath, false, 8000, log);
                    results.Add("fronted ws (SNI=" + FrontingSni + "): OK");
                    ws.Dispose();
                }
                catch (Exception ex)
                {
                    results.Add("fronted ws (SNI=" + FrontingSni + "): ошибка — " + ex.Message);
                }

                try
                {
                    var tcp = new TcpClient();
                    var connect = tcp.ConnectAsync(targetIp, 443);
                    if (await Task.WhenAny(connect, Task.Delay(8000)) == connect)
                    {
                        results.Add("tcp " + targetIp + ":443: OK");
                    }
                    else
                    {
                        results.Add("tcp " + targetIp + ":443: таймаут");
                    }
                    tcp.Close();
                }
                catch (Exception ex)
                {
                    results.Add("tcp " + targetIp + ":443: ошибка — " + ex.Message);
                }
            }

            var pool = CfPool().ToList();
            if (pool.Count > 0)
            {
                // Try a few entries: the shared list is a rotating set of
                // throwaway domains and some of them are already dead.
                foreach (var baseDomain in CfPool().Take(5))
                {
                    string host = $"kws{dc}.{baseDomain}";
                    try
                    {
                        var ws = await RawWebSocket.ConnectAsync(host, host, host, WsPath, true, 8000, log);
                        results.Add("cloudflare ws " + host + ": OK");
                        ws.Dispose();
                        break;
                    }
                    catch (Exception ex)
                    {
                        results.Add("cloudflare ws " + host + ": ошибка — " + ex.Message);
                    }
                }
            }

            if (!string.IsNullOrEmpty(WorkerDomain))
            {
                string target = WorkerTarget(dc);
                if (target != null)
                {
                    string path = "/apiws?dst=" + target + "&dc=" + dc;
                    try
                    {
                        var ws = await RawWebSocket.ConnectAsync(WorkerDomain, WorkerDomain, WorkerDomain, path, true, 8000, log);
                        results.Add("cloudflare worker " + WorkerDomain + " -> " + target + ": OK");
                        ws.Dispose();
                    }
                    catch (Exception ex)
                    {
                        results.Add("cloudflare worker " + WorkerDomain + " -> " + target + ": ошибка — " + ex.Message);
                    }
                }
            }
            return results;
        }

/// <summary>
        /// Check every relay record of the custom domain. We probe kws1..kws6 as
        /// they are actually named in DNS (the user created those records), not
        /// by DC number — the reference maps DC 203 onto kws2, so probing by DC
        /// would ask for a host that does not exist.
        /// </summary>
        public static async Task<List<string>> ProbeCfRelaysAsync(IEnumerable<int> dcs, Action<string, string> log)
        {
            var results = new List<string>();
            var hosts = new List<string>();
            if (!string.IsNullOrEmpty(CustomCfDomain))
            {
                for (int i = 1; i <= 6; i++) hosts.Add("kws" + i + "." + CustomCfDomain);
            }
            else
            {
                foreach (var dc in dcs) hosts.Add("kws" + dc + "." + _cfDomains.FirstOrDefault());
            }
            foreach (var host in hosts)
            {
                try
                {
                    var ws = await RawWebSocket.ConnectAsync(host, host, host, WsPath, true, 8000, log);
                    results.Add(host + ": OK");
                    ws.Dispose();
                }
                catch (Exception ex)
                {
                    results.Add(host + ": ошибка — " + ex.Message);
                }
            }
            return results;
        }

        // The worker route must use the REAL DC address from DC_DEFAULT_IPS, not the
// dc_redirects entry: the redirect address belongs to the direct-WS pool and is
// a different cluster. Connecting there completes TLS and the WebSocket upgrade
// but Telegram never answers the auth, so the client sits on "Connecting" forever.
private static string WorkerTarget(int dc)
        {
            if (DcDefaultIps.TryGetValue(dc, out string ip)) return ip;
            return null;
        }

        private static async Task<Transport> DialAsync(ParsedHandshake parsed, string tag, CancellationToken token)
        {
            // 0) Our own Cloudflare worker, when configured. It asks the worker to
            //    open the DC connection from Cloudflare's edge, which is the only
            //    route that works when the DC address is unreachable from here.
            if (!string.IsNullOrEmpty(WorkerDomain))
            {
                string target = WorkerTarget(parsed.DcId);
                if (target != null)
                {
                    string path = "/apiws?dst=" + target + "&dc=" + parsed.DcId;
                    try
                    {
                        var ws = await RawWebSocket.ConnectAsync(
                            WorkerDomain, WorkerDomain, WorkerDomain, path, true, 8000, _log);
                        _log(tag + $": using Cloudflare worker {WorkerDomain} -> {target}", "info");
                        return new WsTransport(ws);
                    }
                    catch (Exception ex)
                    {
                        _log(tag + $": Cloudflare worker {WorkerDomain} failed ({ex.Message})", "warn");
                    }
                }
            }

            // 1) Telegram's MTWS relays. Connect to the DC ADDRESS so the DPI sees
            //    an IP dial; the SNI/Host carry the relay name. The fronted variant
            //    swaps the SNI for a neutral domain - that is what gets past
            //    DPI rules keyed on "telegram" in the TLS handshake.
            var hostnames = WsDomains(parsed.DcId, parsed.IsMedia).ToList();
            string targetIp = DcRedirects.TryGetValue(parsed.DcId, out string redirect)
                ? redirect
                : (DcDefaultIps.TryGetValue(parsed.DcId, out string ip) ? ip : null);

            foreach (var hostHeader in hostnames)
            {
                foreach (var fronted in (_tryFrontingFirst ? new[] { true, false } : new[] { false, true }))
                {
                    string connectHost = targetIp ?? hostHeader;
                    string sni = fronted ? FrontingSni : hostHeader;
                    try
                    {
                        var ws = await RawWebSocket.ConnectAsync(
                            connectHost, sni, hostHeader, WsPath, verifyCertificate: !fronted, timeoutMs: 8000,
                            log: _log);
                        _tryFrontingFirst = fronted;
                        _log(tag + $": ws {hostHeader} via {connectHost} (fronting={fronted})", "info");
                        return new WsTransport(ws);
                    }
                    catch (Exception ex)
                    {
                        _log(tag + $": ws {hostHeader} via {connectHost} fronting={fronted} failed ({ex.Message})", "warn");
                    }
                }
            }

            // 2) Cloudflare-proxied relays: a WebSocket to kws<dc>.<domain>.
            if (_cfEnabled && (_cfUsable || !string.IsNullOrEmpty(_customCfDomain)))
            {
                foreach (var baseDomain in CfPool().Take(5))
                {
                    string host = $"kws{parsed.DcId}.{baseDomain}";
                    try
                    {
                        var ws = await RawWebSocket.ConnectAsync(host, host, host, WsPath, true, 8000, _log);
                        _log(tag + $": using Cloudflare relay {host}", "info");
                        return new WsTransport(ws);
                    }
                    catch (Exception ex)
                    {
                        _log(tag + $": CF {host} failed ({ex.Message})", "warn");
                    }
                }
            }

            // 3) Straight TCP to the DC address.
            if (targetIp != null)
            {
                try
                {
                    var tcp = new TcpClient();
                    await tcp.ConnectAsync(targetIp, 443);
                    tcp.NoDelay = true;
                    _log(tag + $": falling back to direct TCP {targetIp}:443", "info");
                    return new TcpTransport(tcp.GetStream());
                }
                catch (Exception ex)
                {
                    _log(tag + $": direct TCP {targetIp} failed ({ex.Message})", "warn");
                }
            }
            return null;
        }

        private static async Task PumpClientAsync(NetworkStream client, Transport transport, CryptoContext ctx,
            MsgSplitter splitter, ProxyStatsView stats, string tag, CancellationToken token)
        {
            var buffer = new byte[16384];
            try
            {
                while (!token.IsCancellationRequested)
                {
                    int read = await client.ReadAsync(buffer, 0, buffer.Length, token);
                    if (read <= 0) break;
                    var chunk = new byte[read];
                    Buffer.BlockCopy(buffer, 0, chunk, 0, read);

                    var plain = ctx.ClientDecrypt.Update(chunk, 0, read);
                    var reencrypted = ctx.TgEncrypt.Update(plain, 0, plain.Length);

                    if (transport is WsTransport)
                    {
                        foreach (var part in splitter.Push(reencrypted, plain))
                        {
                            await transport.SendAsync(part);
                        }
                    }
                    else
                    {
                        await transport.SendAsync(reencrypted);
                    }
                    stats.bytesUp += read;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _log(tag + ": client->upstream ended (" + ex.Message + ")", "warn");
            }
        }

        private static async Task PumpUpstreamAsync(Transport transport, NetworkStream client, CryptoContext ctx,
            ProxyStatsView stats, string tag, CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var data = await transport.ReceiveAsync(token);
                    if (data == null || data.Length == 0) break;
                    var plain = ctx.TgDecrypt.Update(data, 0, data.Length);
                    var encrypted = ctx.ClientEncrypt.Update(plain, 0, plain.Length);
                    await client.WriteAsync(encrypted, 0, encrypted.Length, token);
                    stats.bytesDown += data.Length;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _log(tag + ": upstream->client ended (" + ex.Message + ")", "warn");
            }
        }

        private static async Task<byte[]> ReadExactlyAsync(NetworkStream stream, int count, CancellationToken token)
        {
            var buffer = new byte[count];
            int total = 0;
            while (total < count)
            {
                int read = await stream.ReadAsync(buffer, total, count - total, token);
                if (read <= 0) return null;
                total += read;
            }
            return buffer;
        }
    }

    /// <summary>
    /// Split the TCP stream into whole MTProto packets so each becomes its own
    /// WebSocket frame (the reference does the same for the MTWS relays).
    /// </summary>
    public sealed class MsgSplitter
    {
        private readonly bool _intermediate;
        private readonly bool _enabled = true;
        private byte[] _cipher = new byte[0];
        private byte[] _plain = new byte[0];

        public MsgSplitter(byte[] protoTag)
        {
            _intermediate = TgCrypto.IsIntermediate(protoTag) || TgCrypto.IsSecure(protoTag);
            _enabled = !TgCrypto.IsAbridged(protoTag);
        }

public List<byte[]> Push(byte[] cipherChunk, byte[] plainChunk)
        {
            var result = new List<byte[]>();
            if (cipherChunk == null || cipherChunk.Length == 0) return result;
            if (!_enabled)
            {
                result.Add(cipherChunk);
                return result;
            }

            _cipher = Concat(_cipher, cipherChunk);
            _plain = Concat(_plain, plainChunk ?? new byte[0]);

            int pos = 0;
            while (pos < _cipher.Length)
            {
                int packetLen = NextPacketLen(pos, _cipher.Length - pos);
                if (packetLen <= 0) break;
                var part = new byte[packetLen];
                Buffer.BlockCopy(_cipher, pos, part, 0, packetLen);
                result.Add(part);
                pos += packetLen;
            }
            _cipher = Slice(_cipher, pos, _cipher.Length - pos);
            _plain = Slice(_plain, pos, _plain.Length - pos);
            return result;
        }

        private int NextPacketLen(int offset, int avail)
        {
            if (avail <= 0) return 0;
            if (!_intermediate)
            {
                // Abridged: length is encoded in the first byte (multiples of 4).
                byte first = _plain[offset];
                int payload = (first & 0x7f) * 4;
                if (first == 0x7f || first == 0xff)
                {
                    if (avail < 4) return 0;
                    payload = (_plain[offset + 1] | (_plain[offset + 2] << 8) | (_plain[offset + 3] << 16)) * 4;
                    return avail < 4 + payload ? 0 : 4 + payload;
                }
                if (payload <= 0) return 0;
                return avail < 1 + payload ? 0 : 1 + payload;
            }
            // Intermediate / padded intermediate: 4-byte little-endian length.
            if (avail < 4) return 0;
            int len = _plain[offset] | (_plain[offset + 1] << 8) | (_plain[offset + 2] << 16) | (_plain[offset + 3] << 24);
            len &= 0x7fffffff;
            if (len <= 0) return 0;
            int total = 4 + len;
            return avail < total ? 0 : total;
        }

        private static byte[] Concat(byte[] a, byte[] b)
        {
            var result = new byte[a.Length + b.Length];
            Buffer.BlockCopy(a, 0, result, 0, a.Length);
            Buffer.BlockCopy(b, 0, result, a.Length, b.Length);
            return result;
        }

        private static byte[] Slice(byte[] source, int offset, int count)
        {
            var result = new byte[count];
            Buffer.BlockCopy(source, offset, result, 0, count);
            return result;
        }
    }
}