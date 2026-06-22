//! C-compatible FFI layer for photoalbum_core.
//! Rules:
//!   - Every entry point is wrapped in catch_unwind; panics never cross the boundary.
//!   - All return types are i32 error codes (see ERR_* constants).
//!   - No Rust types cross the boundary.
//!   - Strings cross as null-terminated UTF-8 via *const c_char / *mut c_char.
//!   - Caller-allocated output buffers use (ptr, len) pairs.

use copy_engine::{CopyEntry, CopyManifest};
use std::{
    ffi::{CStr, CString},
    panic,
    path::PathBuf,
    os::raw::c_char,
};

// ─── Error codes ──────────────────────────────────────────────────────────────
pub const ERR_OK: i32 = 0;
pub const ERR_INVALID_ARG: i32 = 1;
pub const ERR_NOT_FOUND: i32 = 2;
pub const ERR_IO: i32 = 3;
pub const ERR_HASH_MISMATCH: i32 = 4;
pub const ERR_BUFFER_TOO_SMALL: i32 = 5;
pub const ERR_INTERNAL: i32 = 99;

// ─── Helpers ──────────────────────────────────────────────────────────────────

fn cstr_to_path(ptr: *const c_char) -> Option<PathBuf> {
    if ptr.is_null() {
        return None;
    }
    unsafe { CStr::from_ptr(ptr) }.to_str().ok().map(PathBuf::from)
}

fn write_cstring_to_buf(s: &str, buf: *mut c_char, buf_len: usize) -> i32 {
    if buf.is_null() || buf_len == 0 {
        return ERR_INVALID_ARG;
    }
    let cstr = match CString::new(s) {
        Ok(c) => c,
        Err(_) => return ERR_INTERNAL,
    };
    let bytes = cstr.as_bytes_with_nul();
    if bytes.len() > buf_len {
        return ERR_BUFFER_TOO_SMALL;
    }
    unsafe {
        std::ptr::copy_nonoverlapping(bytes.as_ptr() as *const c_char, buf, bytes.len());
    }
    ERR_OK
}

// ─── Hash ──────────────────────────────────────────────────────────────────────

/// Compute BLAKE3 hash of `path`, writing hex into `out_buf` (must be ≥ 65 bytes).
/// Returns ERR_OK, ERR_NOT_FOUND, ERR_IO, ERR_BUFFER_TOO_SMALL.
#[no_mangle]
pub extern "C" fn pa_hash_file(path: *const c_char, out_buf: *mut c_char, out_len: usize) -> i32 {
    panic::catch_unwind(|| {
        let p = match cstr_to_path(path) {
            Some(p) => p,
            None => return ERR_INVALID_ARG,
        };
        match hasher::hash_file(&p) {
            Ok(bytes) => {
                let hex = hasher::hash_to_hex(&bytes);
                write_cstring_to_buf(&hex, out_buf, out_len)
            }
            Err(hasher::HasherError::NotFound(_)) => ERR_NOT_FOUND,
            Err(_) => ERR_IO,
        }
    })
    .unwrap_or(ERR_INTERNAL)
}

// ─── Verify ───────────────────────────────────────────────────────────────────

/// Verify BLAKE3 hash of `path` matches `expected_hex`.
/// Returns ERR_OK, ERR_NOT_FOUND, ERR_HASH_MISMATCH, ERR_IO.
#[no_mangle]
pub extern "C" fn pa_verify_file(path: *const c_char, expected_hex: *const c_char) -> i32 {
    panic::catch_unwind(|| {
        let p = match cstr_to_path(path) {
            Some(p) => p,
            None => return ERR_INVALID_ARG,
        };
        let expected = if expected_hex.is_null() {
            return ERR_INVALID_ARG;
        } else {
            match unsafe { CStr::from_ptr(expected_hex) }.to_str() {
                Ok(s) => s.to_owned(),
                Err(_) => return ERR_INVALID_ARG,
            }
        };
        match verifier::verify_file(&p, &expected) {
            Ok(()) => ERR_OK,
            Err(verifier::VerifyError::NotFound(_)) => ERR_NOT_FOUND,
            Err(verifier::VerifyError::Mismatch { .. }) => ERR_HASH_MISMATCH,
            Err(_) => ERR_IO,
        }
    })
    .unwrap_or(ERR_INTERNAL)
}

// ─── Scanner ──────────────────────────────────────────────────────────────────

/// Scan `root_path` for supported media files.
/// `out_json` receives a JSON array of {path, size_bytes, extension} objects.
/// Returns ERR_OK, ERR_NOT_FOUND, ERR_BUFFER_TOO_SMALL.
#[no_mangle]
pub extern "C" fn pa_scan_directory(
    root_path: *const c_char,
    out_json: *mut c_char,
    out_len: usize,
) -> i32 {
    panic::catch_unwind(|| {
        let p = match cstr_to_path(root_path) {
            Some(p) => p,
            None => return ERR_INVALID_ARG,
        };
        match scanner::scan_directory(&p) {
            Err(scanner::ScannerError::NotFound(_)) => ERR_NOT_FOUND,
            Err(_) => ERR_IO,
            Ok(files) => {
                let mut json = String::from("[");
                for (i, f) in files.iter().enumerate() {
                    if i > 0 {
                        json.push(',');
                    }
                    let path_str = f.path.display().to_string().replace('\\', "/");
                    json.push_str(&format!(
                        r#"{{"path":"{}","size_bytes":{},"extension":"{}"}}"#,
                        path_str, f.size_bytes, f.extension
                    ));
                }
                json.push(']');
                write_cstring_to_buf(&json, out_json, out_len)
            }
        }
    })
    .unwrap_or(ERR_INTERNAL)
}

// ─── Thumbnail ────────────────────────────────────────────────────────────────

/// Generate a JPEG thumbnail of `path` at `target_size` pixels.
/// JPEG bytes written to `out_buf`; `out_written` receives byte count.
/// Returns ERR_OK, ERR_NOT_FOUND, ERR_IO, ERR_BUFFER_TOO_SMALL.
#[no_mangle]
pub extern "C" fn pa_generate_thumbnail(
    path: *const c_char,
    target_size: u32,
    out_buf: *mut u8,
    out_buf_len: usize,
    out_written: *mut usize,
) -> i32 {
    panic::catch_unwind(|| {
        if out_buf.is_null() || out_written.is_null() {
            return ERR_INVALID_ARG;
        }
        let p = match cstr_to_path(path) {
            Some(p) => p,
            None => return ERR_INVALID_ARG,
        };
        match thumbnailer::generate_thumbnail(&p, target_size) {
            Err(thumbnailer::ThumbnailerError::NotFound(_)) => ERR_NOT_FOUND,
            Err(_) => ERR_IO,
            Ok(bytes) => {
                if bytes.len() > out_buf_len {
                    return ERR_BUFFER_TOO_SMALL;
                }
                unsafe {
                    std::ptr::copy_nonoverlapping(bytes.as_ptr(), out_buf, bytes.len());
                    *out_written = bytes.len();
                }
                ERR_OK
            }
        }
    })
    .unwrap_or(ERR_INTERNAL)
}

// ─── Copy ─────────────────────────────────────────────────────────────────────

/// Copy a single file from `src_path` to `dst_path`, verifying with BLAKE3.
/// Returns ERR_OK, ERR_NOT_FOUND, ERR_IO.
#[no_mangle]
pub extern "C" fn pa_copy_file_verified(src_path: *const c_char, dst_path: *const c_char) -> i32 {
    panic::catch_unwind(|| {
        let src = match cstr_to_path(src_path) {
            Some(p) => p,
            None => return ERR_INVALID_ARG,
        };
        let dst = match cstr_to_path(dst_path) {
            Some(p) => p,
            None => return ERR_INVALID_ARG,
        };
        let mut manifest = CopyManifest {
            entries: vec![CopyEntry {
                source: src,
                dest: dst,
                expected_hash: None,
                completed: false,
            }],
            resume_path: PathBuf::from(""),
        };
        let result = copy_engine::copy_files_verified(&mut manifest, |_, _| {});
        if result.failed > 0 { ERR_IO } else { ERR_OK }
    })
    .unwrap_or(ERR_INTERNAL)
}

// ─── Perceptual hashing ───────────────────────────────────────────────────────

/// Compute a 64-bit dHash for the image at `path`.
///
/// On success writes the hash into `*out_hash` and returns ERR_OK.
/// Returns ERR_INVALID_ARG if either pointer is null or the path is invalid UTF-8.
/// Returns ERR_IO if the file cannot be opened or decoded.
#[no_mangle]
pub extern "C" fn pa_phash_file(path: *const c_char, out_hash: *mut u64) -> i32 {
    panic::catch_unwind(|| {
        if out_hash.is_null() {
            return ERR_INVALID_ARG;
        }
        let p = match cstr_to_path(path) {
            Some(p) => p,
            None => return ERR_INVALID_ARG,
        };
        match dedup::phash_file(&p) {
            Ok(hash) => {
                unsafe { *out_hash = hash; }
                ERR_OK
            }
            Err(_) => ERR_IO,
        }
    })
    .unwrap_or(ERR_INTERNAL)
}

// ─── Health check ─────────────────────────────────────────────────────────────

/// Returns ERR_OK. Used by C# StartupIntegrityService to confirm DLL is loaded.
#[no_mangle]
pub extern "C" fn pa_health_check() -> i32 {
    panic::catch_unwind(|| ERR_OK).unwrap_or(ERR_INTERNAL)
}
