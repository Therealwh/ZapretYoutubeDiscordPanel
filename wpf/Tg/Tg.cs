using System;

namespace Ydp
{
    /// <summary>
    /// Live state of the local Telegram MTProto proxy, mirrored into the UI.
    ///
    /// The transport itself (AES-CTR obfuscated2, raw WebSocket client, Cloudflare
    /// fronting) is ported separately; until it lands this holder keeps the
    /// dashboard honest — the cards show "выключен" instead of pretending.
    /// </summary>
    public sealed class ProxyStats
    {
        public bool Running { get; set; }
        public string Host { get; set; } = "127.0.0.1";
        public int Port { get; set; } = 1443;
        public long Connections { get; set; }
        public long BytesUp { get; set; }
        public long BytesDown { get; set; }
        public string Link { get; set; } = string.Empty;
        public string LastError { get; set; }
    }

    public static class Tg
    {
        public static ProxyStats Stats { get; } = new ProxyStats();
    }
}