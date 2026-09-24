<div align="center">

# 🛡️ ZapretYoutubeDiscordPanel

**Красивая панель управления для [zapret-discord-youtube](https://github.com/Flowseal/zapret-discord-youtube)**
**A beautiful control panel for [zapret-discord-youtube](https://github.com/Flowseal/zapret-discord-youtube)**

![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-blue)
![Release](https://img.shields.io/github/v/release/Therealwh/ZapretYoutubeDiscordPanel)
![License](https://img.shields.io/badge/license-MIT-green)
![Electron](https://img.shields.io/badge/Electron-44-47848F)

*RU / EN · Тёмная и светлая темы · Автообновление панели*

<br>

[![🌐 Официальный сайт](https://img.shields.io/badge/🌐_Официальный_сайт-zapretyoutubediscordpanel.top-7c6cff?style=for-the-badge&logo=googlechrome&logoColor=white)](https://zapretyoutubediscordpanel.top/)

> ✨ **Загляните на [zapretyoutubediscordpanel.top](https://zapretyoutubediscordpanel.top/)** —
> там скриншоты всех экранов панели, пошаговые инструкции, ответы на частые вопросы
> и скачивание последней версии в один клик.

</div>

---

Заблокирован Discord или замедлен YouTube? [**zapret**](https://github.com/Flowseal/zapret-discord-youtube) от Flowseal обходит DPI-блокировки, но управляется неудобными bat-скриптами. Эта панель заменяет их на понятный интерфейс: установка в один клик, автоподбор рабочей стратегии, обновления и диагностика — всё в нескольких кликах.

---

## ✨ Возможности

### 🚀 Главный экран
- **Большой переключатель обхода** — включение/выключение одним кликом
- **Живые статусы**: служба zapret, служба WinDivert, процесс `winws.exe`, активная стратегия
- **Баннеры-подсказки**: если обход уже запущен другой копией или найден конфликтующий обход (GoodbyeDPI и др.) — панель предложит отключить
- **Перезапуск** обхода одной кнопкой

### 🧪 Автоподбор стратегии
- Панель по очереди запускает все стратегии (`general*.bat`) и проверяет **реальную работу** Discord и YouTube
- Проверка YouTube — **замер скорости видео**: панель получает настоящий CDN-адрес через API и меряет пропускную способность (замедление провайдером распознаётся отдельно)
- Живой лог проверок прямо в карточках, цветные вердикты: 🟢 работает всё · 🟠 частично/задушен · 🔴 не работает
- Лучшая стратегия помечается **«Рекомендуемая»**
- Кнопка «Остановить» прерывает подбор в любой момент

### 🔽 Фильтры
- **Game Filter**: off / TCP / UDP / TCP+UDP + свои диапазоны портов
- **IPSet Filter**: none / loaded / any (с автоматическим бэкапом списка)

### 📋 Списки
- Редактирование пользовательских списков: домены обхода, исключения доменов, исключения IP
- Просмотр полного `ipset-all.txt`

### 🔄 Обновления
- **Панель обновляется сама**: проверка новых версий на GitHub, скачивание с прогрессом и установка в один клик (для установленной версии)
- **Обновление zapret**: проверка версии, скачивание релиза с проверкой безопасности и заменой папки (старая сохраняется как `.backup`)
- Обновление `ipset-all.txt` и файла `hosts` (авто через UAC или вручную)

### 🛡️ Безопасность релизов
- **SHA-256 whitelist** проверенных сборок zapret
- Проверка **цифровой подписи драйвера WinDivert** — подделанные сборки блокируются
- Опциональная проверка через **VirusTotal** (вставьте свой API-ключ в настройках)

### 🩺 Диагностика
Все проверки из штатного `service.bat`: BFE, системный прокси, TCP timestamps, Adguard, Killer, Intel Connectivity, Check Point, SmartByte, кириллица в пути, OneDrive, WinDivert64.sys, VPN, Secure DNS (DoH), записи YouTube в hosts, конфликтующие службы — с кнопками «Исправить» и очисткой кэша Discord.

### ⚙️ Прочее
- 🖥️ **Сворачивание в трей** (иконка возле часов), закрытие окна — в трей
- 🔁 **Автозапуск с Windows** (свёрнутой в трей)
- 🌗 **Тёмная и светлая темы**
- 🌍 **Русский и английский** интерфейс (автоопределение + переключатель)
- 🔐 Запуск без админ-прав: панель предложит перезапуститься с UAC только когда это действительно нужно

---

## 📦 Установка

1. Скачайте `YoutubeDiscordPanel Setup X.X.X.exe` со страницы [Releases](https://github.com/Therealwh/ZapretYoutubeDiscordPanel/releases/latest)
2. Установите и запустите — панель сама найдёт установленный zapret или предложит **скачать его автоматически** (рекомендуемый путь: `C:\zapret`)

> **Portable**: скачайте `YoutubeDiscordPanel X.X.X.exe` — без установки. Обновления portable — вручную через страницу релизов.

### ⚠️ Важно
- Антивирус может ругаться на **WinDivert** — это инструмент перехвата трафика, не вирус. Добавьте папку в исключения ([подробнее в README zapret](https://github.com/Flowseal/zapret-discord-youtube#readme))
- Ставьте zapret в путь **без кириллицы** (лучше `C:\zapret`)
- Во время автоподбора стратегий интернет кратковременно «мигает» — это нормально

---

## 🛠️ Разработка

```bash
git clone https://github.com/Therealwh/ZapretYoutubeDiscordPanel.git
cd ZapretYoutubeDiscordPanel
npm install
npm start          # запуск из исходников
npm test           # тесты
npm run dist       # сборка: NSIS + portable в dist/
```

Требуется Node.js 18+ (разработка на 24) и Windows 10/11.

### 🏷️ Как выпустить новую версию

```bash
git commit -am "feat: ..."
npm version patch   # или minor / major
git push --follow-tags
```

GitHub Actions соберёт приложение, прогонит тесты и опубликует релиз — установленные панели обнаружат обновление автоматически.

---

## 💞 Благодарность

- **[Flowseal/zapret-discord-youtube](https://github.com/Flowseal/zapret-discord-youtube)** — сама утилита обхода и её логика; эта панель лишь удобная оболочка над ней
- **[bolvan/zapret](https://github.com/bol-van/zapret)** — ядро обхода DPI и `winws.exe`

## 📄 Лицензия

MIT — см. [LICENSE](LICENSE)
