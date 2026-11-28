
fn main() {
    if std::env::var_os("CARGO_FEATURE_DESKTOP").is_none() {
        return;
    }
    if std::env::var("CARGO_CFG_TARGET_OS").as_deref() == Ok("macos") {
        println!("cargo:rerun-if-changed=src/mac_camera.m");
        println!("cargo:rustc-link-lib=framework=ImageCaptureCore");
        println!("cargo:rustc-link-lib=framework=Foundation");
        cc::Build::new()
            .file("src/mac_camera.m")
            .flag("-fobjc-arc")
            .compile("camera_inspector_mac");
    }
    tauri_build::build();
}
