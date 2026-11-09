using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Windows.Media.Imaging;

namespace Ydp
{
    [DataContract]
    public sealed class AdCampaign
    {
        [DataMember(Name = "image")] public string Image;
        [DataMember(Name = "url")] public string Url;
        [DataMember(Name = "active")] public bool Active = true;
        // ISO date (YYYY-MM-DD), inclusive. Empty/null means no expiration.
        [DataMember(Name = "expires", EmitDefaultValue = false)] public string Expires;
        public bool IsAvailable
        {
            get
            {
                if (!Active) return false;
                if (string.IsNullOrWhiteSpace(Expires)) return true;
                DateTime expiry;
                if (!DateTime.TryParseExact(Expires.Trim(), "yyyy-MM-dd",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out expiry)) return false;
                return DateTime.UtcNow.Date <= expiry.Date;
            }
        }
    }

    [DataContract]
    public sealed class AdConfiguration
    {
        [DataMember(Name = "partners")] public List<AdCampaign> Partners = new List<AdCampaign>();
        [DataMember(Name = "popup")] public AdCampaign Popup = new AdCampaign();
        [DataMember(Name = "bottom")] public AdCampaign Bottom = new AdCampaign();
        public string BottomImage { get { return Bottom == null ? null : Bottom.Image; } }
        public string BottomUrl { get { return Bottom == null ? null : Bottom.Url; } }
    }

    /// <summary>Remote ad manifest with local cache and safe HTTPS-only links.</summary>
    public static class AdsService
    {
        private const string ManifestUrl = "https://ads.zapretyoutubediscordpanel.top/config.json";
        private static readonly string CachePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YoutubeDiscordPanel", "ads-cache.json");
        public static AdConfiguration Current { get; private set; } = new AdConfiguration();
        public static bool PopupShownThisRun;
        public static event Action Updated;
        private static int _refreshing;

        public static void LoadCached()
        {
            try { if (File.Exists(CachePath)) Apply(File.ReadAllText(CachePath)); } catch { }
        }

        public static async void RefreshAsync()
        {
            if (System.Threading.Interlocked.Exchange(ref _refreshing, 1) != 0) return;
            try
            {
                string json = await Task.Run(() =>
                {
                    using (var wc = new WebClient())
                    {
                        wc.Headers[HttpRequestHeader.UserAgent] = "YoutubeDiscordPanel/2.2.5";
                        wc.Encoding = System.Text.Encoding.UTF8;
                        return wc.DownloadString(ManifestUrl);
                    }
                });
                Apply(json);
                Directory.CreateDirectory(Path.GetDirectoryName(CachePath));
                File.WriteAllText(CachePath, json, System.Text.Encoding.UTF8);
                Updated?.Invoke();
            }
            catch { /* advertising is optional; cached/default content remains available */ }
            finally { System.Threading.Interlocked.Exchange(ref _refreshing, 0); }
        }

        private static void Apply(string json)
        {
            var serializer = new DataContractJsonSerializer(typeof(AdConfiguration));
            using (var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)))
                Current = (AdConfiguration)serializer.ReadObject(stream) ?? new AdConfiguration();
            if (Current.Partners == null) Current.Partners = new List<AdCampaign>();
            Current.Partners = Current.Partners.FindAll(x => x != null);
            if (Current.Popup != null && !Current.Popup.IsAvailable) Current.Popup = null;
            if (Current.Bottom != null && !Current.Bottom.IsAvailable) Current.Bottom = null;
            foreach (var item in Current.Partners)
            {
                if (!string.IsNullOrWhiteSpace(item.Image) && !IsHttps(item.Image)) item.Image = null;
                item.Url = SafeUrl(item.Url);
            }
            if (Current.Popup != null)
            {
                if (!string.IsNullOrWhiteSpace(Current.Popup.Image) && !IsHttps(Current.Popup.Image)) Current.Popup.Image = null;
                Current.Popup.Url = SafeUrl(Current.Popup.Url);
            }
            if (Current.Bottom != null)
            {
                if (!string.IsNullOrWhiteSpace(Current.Bottom.Image) && !IsHttps(Current.Bottom.Image)) Current.Bottom.Image = null;
                Current.Bottom.Url = SafeUrl(Current.Bottom.Url);
            }
        }

        private static bool IsHttps(string value)
        {
            Uri uri; return Uri.TryCreate(value, UriKind.Absolute, out uri) && uri.Scheme == Uri.UriSchemeHttps;
        }
        private static string SafeUrl(string value) { return IsHttps(value) ? value : "https://ads.zapretyoutubediscordpanel.top/"; }
        public static AdCampaign Partner(int index) { return Current.Partners != null && index >= 0 && index < Current.Partners.Count && Current.Partners[index] != null && Current.Partners[index].IsAvailable ? Current.Partners[index] : null; }
        public static string PartnerUrl(int index) { var ad = Partner(index); return ad == null ? null : ad.Url; }
        public static BitmapImage LoadImage(string url)
        {
            if (!IsHttps(url)) throw new InvalidOperationException("Only HTTPS images are allowed.");
            var image = new BitmapImage(); image.BeginInit(); image.UriSource = new Uri(url); image.CacheOption = BitmapCacheOption.OnLoad; image.EndInit(); image.Freeze(); return image;
        }
    }
}