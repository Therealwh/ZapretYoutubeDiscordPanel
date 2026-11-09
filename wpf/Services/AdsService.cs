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
            var serializer = new DataContractJsonSerializer(typeof(AdConfiguration));
            using (var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)))
                Current = (AdConfiguration)serializer.ReadObject(stream) ?? new AdConfiguration();
            if (Current.Partners == null) Current.Partners = new List<AdCampaign>();
            Current.Partners = Current.Partners.FindAll(x => x != null && x.Active);
            if (Current.Popup != null && !Current.Popup.Active) Current.Popup = null;
            if (Current.Bottom != null && !Current.Bottom.Active) Current.Bottom = null;
            foreach (var item in Current.Partners)
                if (!string.IsNullOrWhiteSpace(item.Image) && !IsHttps(item.Image)) item.Image = null;
            if (Current.Popup != null && !string.IsNullOrWhiteSpace(Current.Popup.Image) && !IsHttps(Current.Popup.Image)) Current.Popup.Image = null;
            if (Current.Bottom != null && !string.IsNullOrWhiteSpace(Current.Bottom.Image) && !IsHttps(Current.Bottom.Image)) Current.Bottom.Image = null;
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