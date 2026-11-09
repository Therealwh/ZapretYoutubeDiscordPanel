using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Media.Imaging;

namespace Ydp
{
    public sealed class AdCampaign
    {
        public string Image;
        public string Url;
        public bool Active = true;
    }

    public sealed class AdConfiguration
    {
        public List<AdCampaign> Partners = new List<AdCampaign>();
        public AdCampaign Popup = new AdCampaign();
        public AdCampaign Bottom = new AdCampaign();
        public string BottomImage { get { return Bottom == null ? null : Bottom.Image; } }
        public string BottomUrl { get { return Bottom == null ? null : Bottom.Url; } }
    }

    /// <summary>Remote ad manifest with local cache and safe HTTPS-only links.</summary>
    public static class AdsService
    {
        private const string ManifestUrl = "https://zapretyoutubediscordpanel.top/ads/config.json";
        private static readonly string CachePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YoutubeDiscordPanel", "ads-cache.json");
        public static AdConfiguration Current { get; private set; } = new AdConfiguration();
        public static bool PopupShownThisRun;
        public static event Action Updated;

        public static void LoadCached()
        {
            try { if (File.Exists(CachePath)) Apply(File.ReadAllText(CachePath)); } catch { }
        }

        public static async void RefreshAsync()
        {
            try
            {
                string json = await Task.Run(() =>
                {
                    using (var wc = new WebClient())
                    {
                        wc.Headers[HttpRequestHeader.UserAgent] = "YoutubeDiscordPanel/2.2.3";
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
        }

        private static void Apply(string json)
        {
            var raw = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
            var next = new AdConfiguration();
            object value;
            if (raw.TryGetValue("partners", out value) && value is object[])
                foreach (var item in (object[])value) { var ad = Parse(item); if (ad != null && ad.Active) next.Partners.Add(ad); }
            if (raw.TryGetValue("popup", out value)) next.Popup = Parse(value);
            if (raw.TryGetValue("bottom", out value)) next.Bottom = Parse(value);
            Current = next;
        }

        private static AdCampaign Parse(object value)
        {
            var map = value as Dictionary<string, object>;
            if (map == null) return null;
            object image, url, active;
            map.TryGetValue("image", out image); map.TryGetValue("url", out url); map.TryGetValue("active", out active);
            var ad = new AdCampaign { Image = image as string, Url = SafeUrl(url as string), Active = active == null || Convert.ToBoolean(active) };
            if (!ad.Active) return null;
            if (!string.IsNullOrWhiteSpace(ad.Image) && !IsHttps(ad.Image)) ad.Image = null;
            return ad;
        }

        private static bool IsHttps(string value)
        {
            Uri uri; return Uri.TryCreate(value, UriKind.Absolute, out uri) && uri.Scheme == Uri.UriSchemeHttps;
        }
        private static string SafeUrl(string value) { return IsHttps(value) ? value : "https://zapretyoutubediscordpanel.top/"; }
        public static AdCampaign Partner(int index) { return Current.Partners != null && index >= 0 && index < Current.Partners.Count ? Current.Partners[index] : null; }
        public static string PartnerUrl(int index) { var ad = Partner(index); return ad == null ? null : ad.Url; }
        public static BitmapImage LoadImage(string url)
        {
            if (!IsHttps(url)) throw new InvalidOperationException("Only HTTPS images are allowed.");
            var image = new BitmapImage(); image.BeginInit(); image.UriSource = new Uri(url); image.CacheOption = BitmapCacheOption.OnLoad; image.EndInit(); image.Freeze(); return image;
        }
    }
}