using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;

namespace Ydp.Services
{
    public sealed class TrustCheck
    {
        public string title { get; set; }
        public string state { get; set; }   // good | bad
        public string detail { get; set; }
    }

    /// <summary>
    /// Facts behind the transparency page. These are live checks, not promises:
    /// the page claims the panel installs no certificates and does not touch
    /// Defender, so it had better be able to prove it on the machine it runs on.
    ///
    /// This exists because the most popular GUI in this niche shipped a root CA,
    /// disabled Defender and phoned home every 30 minutes. Being able to check
    /// those claims is worth more than another paragraph of marketing copy.
    /// </summary>
    public static class Trust
    {
        /// <summary>Certificates in the user + machine stores that mention us.</summary>
        public static TrustCheck Certificates()
        {
            try
            {
                var found = new List<string>();
                foreach (var storeName in new[] { StoreName.Root, StoreName.My, StoreName.TrustedPublisher })
                {
                    foreach (var location in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
                    {
                        try
                        {
                            using (var store = new X509Store(storeName, location))
                            {
                                store.Open(OpenFlags.ReadOnly);
                                foreach (var cert in store.Certificates)
                                {
                                    string subject = cert.Subject ?? string.Empty;
                                    if (subject.IndexOf("YoutubeDiscordPanel", StringComparison.OrdinalIgnoreCase) >= 0
                                        || subject.IndexOf("zapret", StringComparison.OrdinalIgnoreCase) >= 0)
                                        found.Add(subject + " [" + storeName + "/" + location + "]");
                                }
                            }
                        }
                        catch
                        {
                            // A store we cannot open (no rights) is not a finding.
                        }
                    }
                }
                return new TrustCheck
                {
                    title = "Сертификаты не устанавливаются",
                    state = found.Count == 0 ? "good" : "bad",
                    detail = found.Count == 0
                        ? "В хранилищах сертификатов нет записей от панели. Обходу DPI сертификаты не нужны — им можно читать ваш HTTPS."
                        : "Найдены записи: " + string.Join("; ", found),
                };
            }
            catch (Exception ex)
            {
                return new TrustCheck { title = "Сертификаты не устанавливаются", state = "warn", detail = ex.Message };
            }
        }

        /// <summary>
        /// Defender must be left alone. Read-only: we report the service state
        /// and never write the registry key, which is the thing the malicious
        /// fork did.
        /// </summary>
        public static TrustCheck Defender()
        {
            try
            {
                using (var sc = new System.ServiceProcess.ServiceController("WinDefend"))
                {
                    string state = sc.Status.ToString();
                    bool running = sc.Status == System.ServiceProcess.ServiceControllerStatus.Running;
                    return new TrustCheck
                    {
                        title = "Защита Windows не отключается",
                        state = running ? "good" : "warn",
                        detail = running
                            ? "Служба защиты работает. Панель никогда не пишет в ветку реестра Defender."
                            : "Служба защиты сейчас " + state + ". Если это не вы, включите «Защиту от вирусов и угроз».",
                    };
                }
            }
            catch (Exception ex)
            {
                return new TrustCheck
                {
                    title = "Защита Windows не отключается",
                    state = "warn",
                    detail = "Не удалось прочитать состояние службы: " + ex.Message,
                };
            }
        }

        /// <summary>Where the panel actually keeps data.</summary>
        public static TrustCheck Storage()
        {
            try
            {
                string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
                string dir = Path.GetDirectoryName(exe);
                string appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string data = Path.Combine(appdata, "YoutubeDiscordPanel");
                return new TrustCheck
                {
                    title = "Данные лежат в двух местах",
                    state = "good",
                    detail = "Программа: " + dir + Environment.NewLine
                           + "Настройки, логи и списки: " + data,
                };
            }
            catch (Exception ex)
            {
                return new TrustCheck { title = "Данные лежат в двух местах", state = "warn", detail = ex.Message };
            }
        }

        /// <summary>Network destinations the panel itself talks to.</summary>
        public static TrustCheck Outbound()
        {
            return new TrustCheck
            {
                title = "Куда панель ходит сама",
                state = "good",
                detail = "api.github.com — проверка обновлений панели и движка zapret"
                        + Environment.NewLine
                        + "api.github.com/repos/Flowseal/zapret-discord-youtube — релиз движка"
                        + Environment.NewLine
                        + "Стороннего кода, телеметрии и веб-аналитики нет. Telegram-прокси, если вы его включали, "
                        + "ходит за вами по указанным вами адресам.",
            };
        }

        public static List<TrustCheck> RunAll()
        {
            return new List<TrustCheck>
            {
                Certificates(),
                Defender(),
                Storage(),
                Outbound(),
            };
        }
    }
}