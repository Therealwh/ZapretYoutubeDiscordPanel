// Tauri v2 spike — same window, same WebView2, same UI files as Electron.
// Only the shell is Tauri so the footprint can be measured before committing
// to the port.
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

use tauri::menu::{Menu, MenuItem};
use tauri::tray::TrayIconBuilder;
use tauri::Manager;

fn main() {
    tauri::Builder::default()
        .setup(|app| {
            // Footprint probe: hide the window after N seconds so we can measure
            // the tray-only state, which is how a panel is actually used.
            if let Ok(secs) = std::env::var("SPIKE_HIDE_AFTER") {
                if let Ok(secs) = secs.parse::<u64>() {
                    let handle = app.handle().clone();
                    std::thread::spawn(move || {
                        std::thread::sleep(std::time::Duration::from_secs(secs));
                        if let Some(w) = handle.get_webview_window("main") {
                            let _ = w.hide();
                        }
                    });
                }
            }
            // Tray: proves the tray-icon feature works and keeps the panel
            // reachable from the notification area like the Electron build.
            let open = MenuItem::with_id(app, "open", "Открыть панель", true, None::<&str>)?;
            let quit = MenuItem::with_id(app, "quit", "Выход", true, None::<&str>)?;
            let menu = Menu::with_items(app, &[&open, &quit])?;
            let mut tray = TrayIconBuilder::with_id("main")
                .tooltip("YoutubeDiscordPanel")
                .menu(&menu)
                .on_menu_event(|app, event| match event.id.as_ref() {
                    "open" => {
                        if let Some(w) = app.get_webview_window("main") {
                            let _ = w.show();
                            let _ = w.set_focus();
                        }
                    }
                    "quit" => app.exit(0),
                    _ => {}
                });
            if let Some(icon) = app.default_window_icon().cloned() {
                tray = tray.icon(icon);
            }
            tray.build(app)?;
            Ok(())
        })
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}
