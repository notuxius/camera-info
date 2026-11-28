
use camera_inspector::camera::Reading;
use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::{fs::{self, OpenOptions}, io::{self, Write}, path::{Path, PathBuf}};
use uuid::Uuid;

#[derive(Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct Decrease {
    from: u32,
    to: u32,
    observed_at: DateTime<Utc>,
}

#[derive(Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
struct SavedReading {
    count: u32,
    last_decrease: Option<Decrease>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Comparison {
    previous_count: Option<u32>,
    last_decrease: Option<Decrease>,
}

pub fn text(path: &Path) -> String {
    fs::read_to_string(path).map(|text| text.trim().to_owned()).unwrap_or_default()
}

pub fn replace(path: &Path, content: &[u8]) -> io::Result<()> {
    let directory = path.parent().ok_or_else(|| io::Error::other("Missing settings directory."))?;
    fs::create_dir_all(directory)?;
    let mut temp = tempfile::NamedTempFile::new_in(directory)?;
    temp.write_all(content)?;
    temp.as_file().sync_all()?;
    temp.persist(path).map_err(|error| error.error)?;
    Ok(())
}

pub fn move_legacy_settings(legacy: &Path, settings: &Path) -> io::Result<()> {
    if !legacy.try_exists()? {
        return Ok(());
    }
    fs::create_dir_all(settings)?;
    let migration = OpenOptions::new().read(true).write(true).create(true).truncate(false)
        .open(settings.join("settings-migration.lock"))?;
    migration.lock()?;
    for name in ["language.txt", "theme.txt", "wifi-client.txt"] {
        let old = legacy.join(name);
        let current = settings.join(name);
        let result = if current.is_file() {
            fs::remove_file(old)
        } else {
            fs::rename(old, current)
        };
        match result {
            Ok(()) => {}
            Err(error) if error.kind() == io::ErrorKind::NotFound => {}
            Err(error) => return Err(error),
        }
    }
    Ok(())
}

fn lock(path: &Path) -> io::Result<fs::File> {
    let file = OpenOptions::new().read(true).write(true).create(true).truncate(false).open(path)?;
    file.try_lock().map_err(io::Error::other)?;
    Ok(file)
}

pub fn client_id(directory: &Path) -> io::Result<Uuid> {
    fs::create_dir_all(directory)?;
    let _lock = lock(&directory.join("wifi-client.lock"))?;
    let path = directory.join("wifi-client.txt");
    if let Ok(id) = Uuid::parse_str(&text(&path)) {
        if !id.is_nil() {
            return Ok(id);
        }
    }
    let id = Uuid::new_v4();
    replace(&path, id.to_string().as_bytes())?;
    Ok(id)
}

pub fn history(directory: &Path, reading: &Reading) -> Option<Comparison> {
    let count = reading.count?;
    let property = count.property?;
    let serial = reading.serial.as_deref()?.trim();
    if serial.is_empty() {
        return None;
    }
    let name = reading.model.trim().to_uppercase();
    let suffix = if count.is_upper_bound { "\nmechanical upper bound" } else { "" };
    let key = format!("{name}\n{serial}\n{property:04X}{suffix}");
    let file = format!("{:X}.json", Sha256::digest(key.as_bytes()));
    compare(&directory.join("Shutter History"), &file, count.value).ok()
}

fn compare(directory: &Path, name: &str, count: u32) -> io::Result<Comparison> {
    fs::create_dir_all(directory)?;
    let path: PathBuf = directory.join(name);
    let lock_path = directory.join(format!("{name}.lock"));
    let _lock = lock(&lock_path)?;
    let previous = match fs::read(&path) {
        Ok(bytes) => Some(serde_json::from_slice::<SavedReading>(&bytes)?),
        Err(error) if error.kind() == io::ErrorKind::NotFound => None,
        Err(error) => return Err(error),
    };
    let previous_count = previous.as_ref().map(|reading| reading.count);
    let last_decrease = if let Some(from) = previous_count.filter(|prior| count < *prior) {
        Some(Decrease { from, to: count, observed_at: Utc::now() })
    } else {
        previous.and_then(|reading| reading.last_decrease)
    };
    let record = SavedReading { count, last_decrease };
    if previous_count != Some(count) {
        replace(&path, &serde_json::to_vec(&record)?)?;
    }
    Ok(Comparison { previous_count, last_decrease: record.last_decrease })
}

#[cfg(test)]
mod tests {
    use super::*;
    use camera_inspector::camera::{CountKind, ShutterCount, Source};

    #[test]
    fn moves_legacy_settings_once_and_preserves_the_current_value() {
        let temporary = tempfile::tempdir().unwrap();
        let legacy = temporary.path().join("Camera Info");
        let settings = temporary.path().join("com.notuxius.camera-inspector");
        let identity = "5b9bce4e-8a76-482a-9ce6-60a27a49749b";
        fs::create_dir_all(legacy.join("Shutter History")).unwrap();
        fs::write(legacy.join("Shutter History").join("saved.json"), "history").unwrap();
        fs::write(legacy.join("window.json"), "old placement").unwrap();
        fs::write(legacy.join("language.txt"), "uk").unwrap();
        fs::write(legacy.join("theme.txt"), "Dark").unwrap();
        fs::write(legacy.join("wifi-client.txt"), identity).unwrap();
        fs::create_dir(&settings).unwrap();
        fs::write(settings.join("theme.txt"), "Light").unwrap();

        move_legacy_settings(&legacy, &settings).unwrap();
        assert_eq!(text(&settings.join("language.txt")), "uk");
        assert_eq!(text(&settings.join("theme.txt")), "Light");
        assert_eq!(client_id(&settings).unwrap(), Uuid::parse_str(identity).unwrap());
        for name in ["language.txt", "theme.txt", "wifi-client.txt"] {
            assert!(!legacy.join(name).exists());
        }
        assert_eq!(text(&legacy.join("Shutter History").join("saved.json")), "history");
        assert_eq!(text(&legacy.join("window.json")), "old placement");
        assert!(!settings.join("window.json").exists());

        fs::remove_dir_all(&settings).unwrap();
        move_legacy_settings(&legacy, &settings).unwrap();
        for name in ["language.txt", "theme.txt", "wifi-client.txt"] {
            assert!(!settings.join(name).exists());
        }
    }

    #[test]
    fn preserves_existing_windows_history_and_distinguishes_upper_bounds() {
        let temporary = tempfile::tempdir().unwrap();
        let directory = temporary.path().join("Shutter History");
        fs::create_dir(&directory).unwrap();
        let file = format!("{:X}.json", Sha256::digest(b"CANON EOS R8\nABC123\nD167\nmechanical upper bound"));
        fs::write(directory.join(file), r#"{"Count":87000,"LastDecrease":null}"#).unwrap();
        let mut reading = Reading::new("Canon EOS R8".into(), "Canon".into(), Source::Wifi);
        reading.serial = Some("ABC123".into());
        reading.count = Some(ShutterCount::live(86_000, CountKind::Mechanical, 0xD167, true));
        let result = history(temporary.path(), &reading).unwrap();
        assert_eq!(result.previous_count, Some(87_000));
        assert_eq!(result.last_decrease.unwrap().to, 86_000);
        reading.count.as_mut().unwrap().is_upper_bound = false;
        assert_eq!(history(temporary.path(), &reading).unwrap().previous_count, None);
    }
}
