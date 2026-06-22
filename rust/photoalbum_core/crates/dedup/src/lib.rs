use std::{collections::HashMap, path::{Path, PathBuf}};
use thiserror::Error;

#[derive(Debug, Error)]
pub enum DedupError {
    #[error("Hash computation failed for {path}: {reason}")]
    HashFailed { path: String, reason: String },
}

/// Compute a 64-bit difference hash (dHash) of an image file.
///
/// Algorithm: resize to 9×8 grayscale, compare each pixel to its right
/// neighbour in each row (8 comparisons × 8 rows = 64 bits).
/// Two images with Hamming distance ≤ 10 are visually near-identical.
pub fn phash_file(path: &Path) -> Result<u64, DedupError> {
    let img = image::ImageReader::open(path)
        .map_err(|e| DedupError::HashFailed {
            path: path.display().to_string(),
            reason: e.to_string(),
        })?
        .decode()
        .map_err(|e| DedupError::HashFailed {
            path: path.display().to_string(),
            reason: e.to_string(),
        })?;

    let small = image::imageops::resize(
        &img.to_luma8(),
        9, 8,
        image::imageops::FilterType::Lanczos3,
    );

    let mut hash: u64 = 0;
    for y in 0u32..8 {
        for x in 0u32..8 {
            let left  = small.get_pixel(x,     y)[0];
            let right = small.get_pixel(x + 1, y)[0];
            if left > right {
                hash |= 1u64 << (y * 8 + x);
            }
        }
    }
    Ok(hash)
}

/// Hamming distance between two 64-bit perceptual hashes.
#[inline]
pub fn hamming_distance(a: u64, b: u64) -> u32 {
    (a ^ b).count_ones()
}

#[derive(Debug, Clone)]
pub struct DedupEntry {
    pub path: PathBuf,
    pub hash: String,
    pub size_bytes: u64,
}

#[derive(Debug)]
pub struct DedupResult {
    /// Groups of files sharing the same BLAKE3 hash (all are duplicates of each other).
    pub duplicate_groups: Vec<Vec<DedupEntry>>,
    /// Files that are unique (no duplicate found).
    pub unique_count: usize,
}

/// Find duplicate files from pre-computed (path, hash, size) entries.
/// Groups entries by hash; groups with > 1 member are duplicates.
pub fn find_duplicates(entries: Vec<DedupEntry>) -> DedupResult {
    let mut by_hash: HashMap<String, Vec<DedupEntry>> = HashMap::new();
    for entry in entries {
        by_hash.entry(entry.hash.clone()).or_default().push(entry);
    }

    let mut duplicate_groups = Vec::new();
    let mut unique_count = 0usize;

    for (_, group) in by_hash {
        if group.len() > 1 {
            duplicate_groups.push(group);
        } else {
            unique_count += 1;
        }
    }

    DedupResult { duplicate_groups, unique_count }
}

/// Filter out `to_remove` paths, keeping only the canonical path per duplicate group.
/// Caller decides which path to keep (e.g., the oldest, or preferred drive).
pub fn select_keepers(groups: &[Vec<DedupEntry>], prefer: impl Fn(&[DedupEntry]) -> usize) -> Vec<PathBuf> {
    groups.iter().map(|g| g[prefer(g)].path.clone()).collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn entry(path: &str, hash: &str, size: u64) -> DedupEntry {
        DedupEntry { path: PathBuf::from(path), hash: hash.to_string(), size_bytes: size }
    }

    #[test]
    fn identical_hashes_grouped() {
        let entries = vec![
            entry("/a.jpg", "aaaa", 100),
            entry("/b.jpg", "aaaa", 100),
            entry("/c.jpg", "bbbb", 200),
        ];
        let result = find_duplicates(entries);
        assert_eq!(result.duplicate_groups.len(), 1);
        assert_eq!(result.duplicate_groups[0].len(), 2);
        assert_eq!(result.unique_count, 1);
    }

    #[test]
    fn no_duplicates_gives_empty_groups() {
        let entries = vec![
            entry("/a.jpg", "aaaa", 100),
            entry("/b.jpg", "bbbb", 200),
        ];
        let result = find_duplicates(entries);
        assert!(result.duplicate_groups.is_empty());
        assert_eq!(result.unique_count, 2);
    }

    #[test]
    fn empty_input_is_fine() {
        let result = find_duplicates(vec![]);
        assert!(result.duplicate_groups.is_empty());
        assert_eq!(result.unique_count, 0);
    }

    #[test]
    fn select_keepers_picks_correct_index() {
        let groups = vec![vec![
            entry("/a.jpg", "hash1", 100),
            entry("/b.jpg", "hash1", 100),
        ]];
        let keepers = select_keepers(&groups, |_| 0);
        assert_eq!(keepers[0], PathBuf::from("/a.jpg"));
    }
}
