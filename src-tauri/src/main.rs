
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod data;
#[cfg(target_os = "macos")]
#[path = "mac_usb.rs"]
mod usb;
#[cfg(target_os = "windows")]
#[path = "win_usb.rs"]
mod usb;

use camera_inspector::{camera::{Device, Reading}, photo, wifi};
use serde::{Deserialize, Serialize};
use std::{
    env, fs, io,
    net::{Ipv4Addr, SocketAddr},
    path::{Path, PathBuf},
    sync::{Arc, Mutex, atomic::{AtomicBool, Ordering}},
};
use tauri::{AppHandle, Manager, PhysicalPosition, PhysicalSize, State, Theme, WebviewWindow, WindowEvent};
use tauri_plugin_dialog::DialogExt;

#[derive(Clone)]
struct Inspector {
    dir: PathBuf,
    devices: Arc<Mutex<Vec<Device>>>,
    operation: Arc<Mutex<()>>,
    cancelled: Arc<AtomicBool>,
    smoke: bool,
}

#[derive(Serialize)]
struct Report {
    reading: Reading,
    comparison: Option<data::Comparison>,
}

impl Inspector {
    fn report(&self, reading: Reading) -> Report {
        let comparison = data::history(&self.dir, &reading);
        Report { reading, comparison }
    }
}

#[derive(Serialize)]
struct Settings {
    language: String,
    theme: String,
    version: &'static str,
    platform: &'static str,
}

fn error(error: impl std::fmt::Display) -> String {
    error.to_string()
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct CameraFailure {
    disconnected: bool,
    cancelled: bool,
}

impl From<io::Error> for CameraFailure {
    fn from(error: io::Error) -> Self {
        eprintln!("{error}");
        Self {
            disconnected: error.kind() == io::ErrorKind::NotConnected,
            cancelled: error.kind() == io::ErrorKind::Interrupted,
        }
    }
}

async fn background<T: Send + 'static>(
    state: State<'_, Inspector>, work: impl FnOnce(&Inspector) -> io::Result<T> + Send + 'static,
) -> Result<T, CameraFailure> {
    let state = state.inner().clone();
    tauri::async_runtime::spawn_blocking(move || {
        let _active = state.operation.try_lock().map_err(|_| io::Error::other("A camera operation is already running."))?;
        work(&state)
    }).await.map_err(|error| CameraFailure::from(io::Error::other(error.to_string())))?
        .map_err(Into::into)
}

#[tauri::command]
async fn scan_usb(state: State<'_, Inspector>) -> Result<Vec<Device>, CameraFailure> {
    background(state, |state| {
        let devices = usb::find()?;
        *state.devices.lock().map_err(|_| io::Error::other("Camera list is unavailable."))? = devices.clone();
        Ok(devices)
    }).await
}

#[tauri::command]
async fn read_usb(state: State<'_, Inspector>, id: String) -> Result<Report, CameraFailure> {
    background(state, move |state| {
        let device = state.devices.lock().map_err(|_| io::Error::other("Camera list is unavailable."))?
            .iter().find(|device| device.id == id).cloned()
            .ok_or_else(|| io::Error::new(io::ErrorKind::NotConnected, "Camera is unavailable."))?;
        let result = usb::read(device);
        if result.as_ref().is_err_and(|error| error.kind() == io::ErrorKind::NotConnected) {
            if let Ok(mut devices) = state.devices.lock() {
                devices.retain(|device| device.id != id);
            }
        }
        Ok(state.report(result?))
    }).await
}

#[tauri::command]
async fn scan_wifi(state: State<'_, Inspector>) -> Result<Vec<Ipv4Addr>, CameraFailure> {
    background(state, |state| {
        state.cancelled.store(false, Ordering::Relaxed);
        wifi::discover(&state.cancelled)
    }).await
}

#[tauri::command]
async fn read_wifi(state: State<'_, Inspector>, address: String) -> Result<Report, CameraFailure> {
    background(state, move |state| {
        state.cancelled.store(false, Ordering::Relaxed);
        let address = wifi::parse_address(&address).ok_or_else(|| io::Error::other("Invalid camera address."))?;
        let id = data::client_id(&state.dir)?;
        let reading = wifi::read(SocketAddr::from((address, wifi::PORT)), id, &state.cancelled)?;
        Ok(state.report(reading))
    }).await
}

#[tauri::command]
fn cancel_wifi(state: State<'_, Inspector>) {
    state.cancelled.store(true, Ordering::Relaxed);
}

#[tauri::command]
async fn open_photo(app: AppHandle, state: State<'_, Inspector>) -> Result<Option<Report>, CameraFailure> {
    background(state, move |_| {
        let file = app.dialog().file()
            .add_filter("Original camera photo", &["nef", "arw", "raf", "pef", "dng", "jpg", "jpeg"])
            .blocking_pick_file();
        file.map(|file| {
            let path = file.into_path().map_err(io::Error::other)?;
            Ok(Report { reading: photo::read(&path)?, comparison: None })
        }).transpose()
    }).await
}

fn messages(language: &str) -> Option<&'static str> {
    Some(match language {
        "en" => include_str!("../../Locales/en.json"),
        "ar" => include_str!("../../Locales/ar.json"),
        "arz" => include_str!("../../Locales/arz.json"),
        "bn" => include_str!("../../Locales/bn.json"),
        "zh" => include_str!("../../Locales/zh.json"),
        "fr" => include_str!("../../Locales/fr.json"),
        "de" => include_str!("../../Locales/de.json"),
        "hi" => include_str!("../../Locales/hi.json"),
        "id" => include_str!("../../Locales/id.json"),
        "ja" => include_str!("../../Locales/ja.json"),
        "pcm" => include_str!("../../Locales/pcm.json"),
        "pt" => include_str!("../../Locales/pt.json"),
        "ru" => include_str!("../../Locales/ru.json"),
        "es" => include_str!("../../Locales/es.json"),
        "uk" => include_str!("../../Locales/uk.json"),
        "ur" => include_str!("../../Locales/ur.json"),
        _ => return None,
    })
}

#[tauri::command]
fn catalog(language: &str) -> Result<&'static str, &'static str> {
    messages(language).ok_or("Unknown language.")
}

#[tauri::command]
fn settings(state: State<'_, Inspector>) -> Settings {
    let language = data::text(&state.dir.join("language.txt"));
    let theme = data::text(&state.dir.join("theme.txt"));
    Settings {
        language: if messages(&language).is_some() { language } else { "system".into() },
        theme: if matches!(theme.as_str(), "Dark" | "Light") { theme } else { "System".into() },
        version: env!("CARGO_PKG_VERSION"),
        platform: if cfg!(target_os = "macos") { "macOS" } else { "Windows" },
    }
}

#[tauri::command]
fn save_language(state: State<'_, Inspector>, language: &str) -> Result<(), String> {
    if language != "system" && messages(language).is_none() {
        return Err("Unknown language.".into());
    }
    data::replace(&state.dir.join("language.txt"), language.as_bytes()).map_err(error)
}

#[tauri::command]
fn save_theme(state: State<'_, Inspector>, window: WebviewWindow, theme: &str) -> Result<(), String> {
    apply_theme(&window, theme)?;
    data::replace(&state.dir.join("theme.txt"), theme.as_bytes()).map_err(error)
}

fn apply_theme(window: &WebviewWindow, theme: &str) -> Result<(), String> {
    let theme = match theme {
        "Light" => Some(Theme::Light),
        "Dark" => Some(Theme::Dark),
        "System" | "" => None,
        _ => return Err("Unknown theme.".into()),
    };
    window.set_theme(theme).map_err(error)
}

#[tauri::command]
fn open_link(url: &str) -> Result<(), String> {
    let parsed = tauri::Url::parse(url).map_err(error)?;
    if parsed.scheme() != "https" || parsed.host().is_none() {
        return Err("Expected an HTTPS link.".into());
    }
    tauri_plugin_opener::open_url(parsed.as_str(), None::<&str>).map_err(error)
}

#[tauri::command]
fn ui_ready(app: AppHandle, window: WebviewWindow, state: State<'_, Inspector>) -> Result<(), String> {
    if state.smoke {
        app.exit(0);
        return Ok(());
    }
    window.show().map_err(error)
}

#[derive(Copy, Clone, Deserialize, Serialize)]
#[serde(rename_all = "PascalCase")]
struct Placement {
    left: i32,
    top: i32,
    right: i32,
    bottom: i32,
    maximized: bool,
}

fn read_placement(window: &WebviewWindow) -> Option<Placement> {
    let position = window.outer_position().ok()?;
    let size = window.inner_size().ok()?;
    Some(Placement {
        left: position.x, top: position.y,
        right: position.x.checked_add(i32::try_from(size.width).ok()?)?,
        bottom: position.y.checked_add(i32::try_from(size.height).ok()?)?,
        maximized: false,
    })
}

fn restore_window(window: &WebviewWindow, path: &Path) {
    let saved = fs::read(path).ok().and_then(|value| serde_json::from_slice::<Placement>(&value).ok());
    if let Some(saved) = saved {
        let size = saved.right.checked_sub(saved.left).zip(saved.bottom.checked_sub(saved.top))
            .filter(|(width, height)| (360..=16_384).contains(width) && (280..=16_384).contains(height));
        if let Some((width, height)) = size {
            let _ = window.set_size(PhysicalSize::new(width as u32, height as u32));
            let point = (f64::from(saved.left) + 48.0, f64::from(saved.top) + 24.0);
            if window.monitor_from_point(point.0, point.1).ok().flatten().is_some() {
                let _ = window.set_position(PhysicalPosition::new(saved.left, saved.top));
            } else {
                let _ = window.center();
            }
        }
    }

    let window = window.clone();
    let path = path.to_owned();
    let placement = Mutex::new(read_placement(&window));
    let observer = window.clone();
    window.on_window_event(move |event| {
        let Ok(mut value) = placement.lock() else { return };
        let maximized = observer.is_maximized().unwrap_or(false);
        match event {
            WindowEvent::Moved(_) | WindowEvent::Resized(_) if !maximized && !observer.is_minimized().unwrap_or(false) => {
                *value = read_placement(&observer);
            }
            WindowEvent::CloseRequested { .. } => {
                if let Some(saved) = value.as_mut() {
                    saved.maximized = maximized;
                    if let Ok(content) = serde_json::to_vec(&saved) {
                        let _ = data::replace(&path, &content);
                    }
                }
            }
            _ => {}
        }
    });
    if saved.is_some_and(|saved| saved.maximized) {
        let _ = window.maximize();
    }
}

fn main() {
    tauri::Builder::default()
        .plugin(tauri_plugin_dialog::init())
        .setup(|app| {
            let smoke = env::args().any(|arg| arg == "--smoke-test");
            let dir = if cfg!(target_os = "windows") {
                app.path().local_data_dir()?.join("Camera Info")
            } else {
                app.path().data_dir()?.join("Camera Inspector")
            };
            let window = app.get_webview_window("main").ok_or_else(|| io::Error::other("Missing dashboard window."))?;
            if !smoke {
                restore_window(&window, &dir.join("window.json"));
            }
            let _ = apply_theme(&window, &data::text(&dir.join("theme.txt")));
            app.manage(Inspector { dir, devices: Arc::default(), operation: Arc::default(), cancelled: Arc::default(), smoke });
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![
            scan_usb, read_usb, scan_wifi, read_wifi, cancel_wifi, open_photo, catalog, settings,
            save_language, save_theme, open_link, ui_ready,
        ])
        .run(tauri::generate_context!())
        .expect("Camera Inspector could not start.");
}
