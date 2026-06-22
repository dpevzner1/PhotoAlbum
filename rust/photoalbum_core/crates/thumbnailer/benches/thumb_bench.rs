use criterion::{criterion_group, criterion_main, Criterion};
use image::{DynamicImage, ImageBuffer, ImageFormat, Rgb};
use std::io::{Cursor, Write};
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

fn bench_thumb_200px(c: &mut Criterion) {
    let f = write_test_png(2000, 1500);
    c.bench_function("thumbnail_200px", |b| {
        b.iter(|| thumbnailer::generate_thumbnail(f.path(), 200).unwrap());
    });
}

criterion_group!(benches, bench_thumb_200px);
criterion_main!(benches);
