use blake3::Hasher;
use serde::{Deserialize, Serialize};
use std::{
    fs,
    io::{BufReader, BufWriter, Read, Write},
    path::{Path, PathBuf},
};
use thiserror::Error;

#[derive(Debug, Error)]
pub enum CopyError {
    #[error("IO error on {path}: {source}")]
    Io { path: String, source: std::io::Error },
    #[error("Hash mismatch for {0}")]
    HashMismatch(String),
    #[error("Manifest error: {0}")]
    Manifest(String),
    #[error("Source not found: {0}")]
    NotFound(String),
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct CopyEntry {
    pub source: PathBuf,
    pub dest: PathBuf,
    pub expected_hash: Option<String>,
    pub completed: bool,
}

#[derive(Debug, Serialize, Deserialize)]
pub struct CopyManifest {
    pub entries: Vec<CopyEntry>,
    pub resume_path: PathBuf,
}

#[derive(Debug)]
pub struct CopyResult {
    pub copied: usize,
    pub failed: usize,
    pub verified: usize,
    pub errors: Vec<String>,
}

/// Copy files per manifest, verifying BLAKE3 hash of each copied file.
/// Progress callback receives (files_done, files_total).
pub fn copy_files_verified<F>(manifest: &mut CopyManifest, mut on_progress: F) -> CopyResult
where
    F: FnMut(usize, usize),
{
    let total = manifest.entries.len();
    let mut result = CopyResult { copied: 0, failed: 0, verified: 0, errors: Vec::new() };

    for (i, entry) in manifest.entries.iter_mut().enumerate() {
        if entry.completed {
            result.copied += 1;
            result.verified += 1;
            on_progress(i + 1, total);
            continue;
        }
        if !entry.source.exists() {
            result.failed += 1;
            result.errors.push(format!("Source missing: {}", entry.source.display()));
            on_progress(i + 1, total);
            continue;
        }
        if let Some(parent) = entry.dest.parent() {
            if let Err(e) = fs::create_dir_all(parent) {
                result.failed += 1;
                result.errors.push(format!("mkdir failed {}: {}", parent.display(), e));
                on_progress(i + 1, total);
                continue;
            }
        }
        match copy_and_hash(&entry.source, &entry.dest) {
            Ok(hash_hex) => {
                if let Some(ref expected) = entry.expected_hash {
                    if &hash_hex != expected {
                        result.failed += 1;
                        result.errors.push(format!("Hash mismatch: {}", entry.dest.display()));
                        on_progress(i + 1, total);
                        continue;
                    }
                }
                entry.completed = true;
                result.copied += 1;
                result.verified += 1;
            }
            Err(e) => {
                result.failed += 1;
                result.errors.push(format!("{}", e));
            }
        }
        on_progress(i + 1, total);
    }
    result
}

fn copy_and_hash(src: &Path, dst: &Path) -> Result<String, CopyError> {
    let src_file = fs::File::open(src).map_err(|e| CopyError::Io { path: src.display().to_string(), source: e })?;
    let dst_file = fs::File::create(dst).map_err(|e| CopyError::Io { path: dst.display().to_string(), source: e })?;
    let mut reader = BufReader::with_capacity(1024 * 1024, src_file);
    let mut writer = BufWriter::new(dst_file);
    let mut hasher = Hasher::new();
    let mut buf = vec![0u8; 1024 * 1024];
    loop {
        let n = reader.read(&mut buf).map_err(|e| CopyError::Io { path: src.display().to_string(), source: e })?;
        if n == 0 { break; }
        writer.write_all(&buf[..n]).map_err(|e| CopyError::Io { path: dst.display().to_string(), source: e })?;
        hasher.update(&buf[..n]);
    }
    writer.flush().map_err(|e| CopyError::Io { path: dst.display().to_string(), source: e })?;
    let hash = hasher.finalize();
    Ok(hash.to_hex().to_string())
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Write;
    use tempfile::{NamedTempFile, TempDir};

    #[test]
    fn copy_and_verify_succeed() {
        let mut src = NamedTempFile::new().unwrap();
        src.write_all(b"test content for copy").unwrap();
        let dst_dir = TempDir::new().unwrap();
        let dst = dst_dir.path().join("copy.bin");

        let src_hash = {
            let h = blake3::hash(b"test content for copy");
            h.to_hex().to_string()
        };

        let mut manifest = CopyManifest {
            entries: vec![CopyEntry {
                source: src.path().to_path_buf(),
                dest: dst.clone(),
                expected_hash: Some(src_hash.clone()),
                completed: false,
            }],
            resume_path: dst_dir.path().join("manifest.json"),
        };

        let mut progress_calls = 0usize;
        let result = copy_files_verified(&mut manifest, |_, _| progress_calls += 1);
        assert_eq!(result.copied, 1);
        assert_eq!(result.failed, 0);
        assert_eq!(result.verified, 1);
        assert!(dst.exists());
    }

    #[test]
    fn missing_source_counted_as_failed() {
        let dst_dir = TempDir::new().unwrap();
        let mut manifest = CopyManifest {
            entries: vec![CopyEntry {
                source: PathBuf::from("/nonexistent/file.jpg"),
                dest: dst_dir.path().join("out.jpg"),
                expected_hash: None,
                completed: false,
            }],
            resume_path: dst_dir.path().join("manifest.json"),
        };
        let result = copy_files_verified(&mut manifest, |_, _| {});
        assert_eq!(result.failed, 1);
        assert_eq!(result.copied, 0);
    }

    #[test]
    fn already_completed_entries_skipped() {
        let dst_dir = TempDir::new().unwrap();
        let mut manifest = CopyManifest {
            entries: vec![CopyEntry {
                source: PathBuf::from("/nonexistent/file.jpg"),
                dest: dst_dir.path().join("out.jpg"),
                expected_hash: None,
                completed: true, // mark done
            }],
            resume_path: dst_dir.path().join("manifest.json"),
        };
        let result = copy_files_verified(&mut manifest, |_, _| {});
        assert_eq!(result.copied, 1);
        assert_eq!(result.failed, 0);
    }
}
