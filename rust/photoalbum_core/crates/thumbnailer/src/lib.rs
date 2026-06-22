use image::{DynamicImage, ImageFormat, ImageReader};
use std::io::Cursor;
use std::path::Path;
use thiserror::Error;

#[derive(Debug, Error)]
pub enum ThumbnailerError {
    #[error("IO error: {0}")]
    Io(#[from] std::io::Error),
    #[error("Image error: {0}")]
    Image(#[from] image::ImageError),
    #[error("File not found: {0}")]
    NotFound(String),
    #[error("Unsupported format")]
    UnsupportedFormat,
}

/// Generate a JPEG thumbnail of `target_size` on the longest side.
/// Returns raw JPEG bytes.
pub fn generate_thumbnail(path: &Path, target_size: u32) -> Result<Vec<u8>, ThumbnailerError> {
    if !path.exists() {
        return Err(ThumbnailerError::NotFound(path.display().to_string()));
    }
    let img = ImageReader::open(path)?.with_guessed_format()?.decode()?;
    let thumb = img.thumbnail(target_size, target_size);
    let mut buf = Cursor::new(Vec::new());
    thumb.write_to(&mut buf, ImageFormat::Jpeg)?;
    Ok(buf.into_inner())
}

/// Generate thumbnail from raw bytes (for in-memory use).
pub fn generate_thumbnail_from_bytes(data: &[u8], target_size: u32) -> Result<Vec<u8>, ThumbnailerError> {
    let img = ImageReader::new(Cursor::new(data))
        .with_guessed_format()?
        .decode()?;
    let thumb = img.thumbnail(target_size, target_size);
    let mut buf = Cursor::new(Vec::new());
    thumb.write_to(&mut buf, ImageFormat::Jpeg)?;
    Ok(buf.into_inner())
}

#[cfg(test)]
mod tests {
    use super::*;
    use image::{ImageBuffer, Rgb};
    use std::io::Write;
    use tempfile::NamedTempFile;

    fn write_test_png(w: u32, h: u32) -> NamedTempFile {
        let img: ImageBuffer<Rgb<u8>, Vec<u8>> = ImageBuffer::new(w, h);
        let mut f = NamedTempFile::with_suffix(".png").unwrap();
        let mut buf = Vec::new();
        DynamicImage::ImageRgb8(img)
            .write_to(&mut Cursor::new(&mut buf), ImageFormat::Png)
            .unwrap();
        f.write_all(&buf).unwrap();
        f
    }

    #[test]
    fn thumbnail_fits_target_size() {
        let f = write_test_png(800, 600);
        let bytes = generate_thumbnail(f.path(), 200).unwrap();
        let img = image::load_from_memory(&bytes).unwrap();
        assert!(img.width() <= 200 && img.height() <= 200);
    }

    #[test]
    fn thumbnail_missing_file_errors() {
        let result = generate_thumbnail(Path::new("/no/such/file.jpg"), 200);
        assert!(matches!(result, Err(ThumbnailerError::NotFound(_))));
    }

    #[test]
    fn thumbnail_returns_jpeg_bytes() {
        let f = write_test_png(100, 100);
        let bytes = generate_thumbnail(f.path(), 50).unwrap();
        // JPEG magic bytes: FF D8 FF
        assert_eq!(&bytes[..3], &[0xFF, 0xD8, 0xFF]);
    }
}
