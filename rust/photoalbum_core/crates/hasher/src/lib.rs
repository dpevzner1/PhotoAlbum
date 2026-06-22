use std::io::{BufReader, Read};
use std::path::Path;
use thiserror::Error;

#[derive(Debug, Error)]
pub enum HasherError {
    #[error("IO error: {0}")]
    Io(#[from] std::io::Error),
    #[error("Path not found: {0}")]
    NotFound(String),
}

/// BLAKE3 hash of a file. Returns 32-byte digest.
pub fn hash_file(path: &Path) -> Result<[u8; 32], HasherError> {
    if !path.exists() {
        return Err(HasherError::NotFound(path.display().to_string()));
    }
    let file = std::fs::File::open(path)?;
    let mut reader = BufReader::with_capacity(1024 * 1024, file);
    let mut hasher = blake3::Hasher::new();
    let mut buf = vec![0u8; 1024 * 1024];
    loop {
        let n = reader.read(&mut buf)?;
        if n == 0 { break; }
        hasher.update(&buf[..n]);
    }
    Ok(*hasher.finalize().as_bytes())
}

/// Hex string of a BLAKE3 hash.
pub fn hash_to_hex(hash: &[u8; 32]) -> String {
    hash.iter().map(|b| format!("{:02x}", b)).collect()
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Write;
    use tempfile::NamedTempFile;

    #[test]
    fn hash_known_content() {
        let mut f = NamedTempFile::new().unwrap();
        f.write_all(b"hello world").unwrap();
        let h = hash_file(f.path()).unwrap();
        // BLAKE3("hello world") known reference
        let expected = blake3::hash(b"hello world");
        assert_eq!(h, *expected.as_bytes());
    }

    #[test]
    fn hash_missing_file_errors() {
        let result = hash_file(Path::new("/nonexistent/file.jpg"));
        assert!(matches!(result, Err(HasherError::NotFound(_))));
    }

    #[test]
    fn two_identical_files_same_hash() {
        let content = b"same content";
        let mut f1 = NamedTempFile::new().unwrap();
        let mut f2 = NamedTempFile::new().unwrap();
        f1.write_all(content).unwrap();
        f2.write_all(content).unwrap();
        assert_eq!(hash_file(f1.path()).unwrap(), hash_file(f2.path()).unwrap());
    }

    #[test]
    fn different_content_different_hash() {
        let mut f1 = NamedTempFile::new().unwrap();
        let mut f2 = NamedTempFile::new().unwrap();
        f1.write_all(b"content a").unwrap();
        f2.write_all(b"content b").unwrap();
        assert_ne!(hash_file(f1.path()).unwrap(), hash_file(f2.path()).unwrap());
    }
}
