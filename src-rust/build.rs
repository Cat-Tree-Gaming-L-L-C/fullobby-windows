fn main() {
    if std::env::var("CARGO_CFG_TARGET_OS").unwrap_or_default() == "windows" {
        let mut res = winres::WindowsResource::new();
        res.set_icon("icons/icon.ico");
        res.set("ProductName", "Esprit Seeder");
        res.set("FileDescription", "Esprit Seeder");
        res.compile().expect("Failed to compile Windows resources");
    }
}
