use blake3::Hasher;
use std::{
    fs,
    io::{BufReader, Read},
    path::Path,
};
use thiserror::Error;

#[derive(Debug, Error)]
pub enum VerifyError {
    #[error("IO error on {path}: {source}")]
    Io { path: String, source: std::io::Error },
    #[error("File not found: {0}")]
    NotFound(String),
    #[error("Hash mismatch: expected {expected}, got {actual}")]
    Mismatch { expected: String, actual: String },
}

/// Compute BLAKE3 hash of `path` and compare against `expected_hex`.
/// Returns Ok(()) if they match, VerifyError::Mismatch if they differ.
pub fn verify_file(path: &Path, expected_hex: &str) -> Result<(), VerifyError> {
    if !path.exists() {
        return Err(VerifyError::NotFound(path.display().to_string()));
    }
    let actual = hash_file(path)?;
    if actual != expected_hex {
        return Err(VerifyError::Mismatch { expected: expected_hex.to_string(), actual });
    }
    Ok(())
}

/// Compute BLAKE3 hash of `path`, returning hex string.
pub fn hash_file(path: &Path) -> Result<String, VerifyError> {
    let file = fs::File::open(path).map_err(|e| {
        if e.kind() == std::io::ErrorKind::NotFound {
            VerifyError::NotFound(path.display().to_string())
        } else {
            VerifyError::Io { path: path.display().to_string(), source: e }
        }
    })?;
    let mut reader = BufReader::with_capacity(1024 * 1024, file);
    let mut hasher = Hasher::new();
    let mut buf = vec![0u8; 1024 * 1024];
    loop {
        let n = reader.read(&mut buf).map_err(|e| VerifyError::Io { path: path.display().to_string(), source: e })?;
        if n == 0 { break; }
        hasher.update(&buf[..n]);
    }
    Ok(hasher.finalize().to_hex().to_string())
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Write;
    use tempfile::NamedTempFile;

    fn tmp_with(data: &[u8]) -> NamedTempFile {
        let mut f = NamedTempFile::new().unwrap();
        f.write_all(data).unwrap();
        f
    }

    #[test]
    fn verify_correct_hash_passes() {
        let f = tmp_with(b"verify me");
        let hash = blake3::hash(b"verify me").to_hex().to_string();
        assert!(verify_file(f.path(), &hash).is_ok());
    }

    #[test]
    fn verify_wrong_hash_fails() {
        let f = tmp_with(b"verify me");
        let result = verify_file(f.path(), "0000000000000000000000000000000000000000000000000000000000000000");
        assert!(matches!(result, Err(VerifyError::Mismatch { .. })));
    }

    #[test]
    fn verify_missing_file_errors() {
        let result = verify_file(Path::new("/nonexistent/file.bin"), "aabbcc");
        assert!(matches!(result, Err(VerifyError::NotFound(_))));
    }

    #[test]
    fn hash_file_returns_correct_hex() {
        let f = tmp_with(b"hello verifier");
        let expected = blake3::hash(b"hello verifier").to_hex().to_string();
        assert_eq!(hash_file(f.path()).unwrap(), expected);
    }
}
