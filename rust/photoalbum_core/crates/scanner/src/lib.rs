use std::path::{Path, PathBuf};
use walkdir::WalkDir;
use thiserror::Error;

#[derive(Debug, Error)]
pub enum ScannerError {
    #[error("IO error: {0}")]
    Io(#[from] std::io::Error),
    #[error("Walk error: {0}")]
    Walk(#[from] walkdir::Error),
    #[error("Root path not found: {0}")]
    NotFound(String),
}

#[derive(Debug, Clone)]
pub struct ScannedFile {
    pub path: PathBuf,
    pub size_bytes: u64,
    pub extension: String,
}

const SUPPORTED_EXTENSIONS: &[&str] = &[
    "jpg", "jpeg", "png", "heic", "heif", "mov", "mp4", "m4v",
];

/// Recursively scan `root` and return all supported media files.
pub fn scan_directory(root: &Path) -> Result<Vec<ScannedFile>, ScannerError> {
    if !root.exists() {
        return Err(ScannerError::NotFound(root.display().to_string()));
    }
    let mut results = Vec::new();
    for entry in WalkDir::new(root).follow_links(false).into_iter() {
        let entry = entry?;
        if !entry.file_type().is_file() {
            continue;
        }
        let path = entry.path();
        let ext = path
            .extension()
            .and_then(|e| e.to_str())
            .map(|e| e.to_lowercase())
            .unwrap_or_default();
        if !SUPPORTED_EXTENSIONS.contains(&ext.as_str()) {
            continue;
        }
        let size_bytes = entry.metadata()?.len();
        results.push(ScannedFile { path: path.to_path_buf(), size_bytes, extension: ext });
    }
    Ok(results)
}

/// Returns true if the file extension is a supported media type.
pub fn is_supported(path: &Path) -> bool {
    path.extension()
        .and_then(|e| e.to_str())
        .map(|e| SUPPORTED_EXTENSIONS.contains(&e.to_lowercase().as_str()))
        .unwrap_or(false)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;
    use tempfile::TempDir;

    fn setup_dir() -> TempDir {
        let dir = tempfile::tempdir().unwrap();
        fs::write(dir.path().join("photo.jpg"), b"fake jpeg").unwrap();
        fs::write(dir.path().join("photo.png"), b"fake png").unwrap();
        fs::write(dir.path().join("video.mp4"), b"fake mp4").unwrap();
        fs::write(dir.path().join("ignore.txt"), b"text").unwrap();
        fs::create_dir(dir.path().join("sub")).unwrap();
        fs::write(dir.path().join("sub").join("nested.heic"), b"fake heic").unwrap();
        dir
    }

    #[test]
    fn finds_supported_files() {
        let dir = setup_dir();
        let files = scan_directory(dir.path()).unwrap();
        assert_eq!(files.len(), 4);
    }

    #[test]
    fn excludes_unsupported_extensions() {
        let dir = setup_dir();
        let files = scan_directory(dir.path()).unwrap();
        assert!(!files.iter().any(|f| f.extension == "txt"));
    }

    #[test]
    fn finds_nested_files() {
        let dir = setup_dir();
        let files = scan_directory(dir.path()).unwrap();
        assert!(files.iter().any(|f| f.path.to_string_lossy().contains("nested.heic")));
    }

    #[test]
    fn missing_root_errors() {
        let result = scan_directory(Path::new("/nonexistent/path"));
        assert!(matches!(result, Err(ScannerError::NotFound(_))));
    }

    #[test]
    fn is_supported_checks_extension() {
        assert!(is_supported(Path::new("photo.jpg")));
        assert!(is_supported(Path::new("photo.HEIC")));
        assert!(!is_supported(Path::new("doc.pdf")));
        assert!(!is_supported(Path::new("noextension")));
    }
}
