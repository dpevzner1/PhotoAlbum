use criterion::{criterion_group, criterion_main, Criterion};
use std::fs;
use tempfile::TempDir;

fn create_test_dir(n: usize) -> TempDir {
    let dir = tempfile::tempdir().unwrap();
    for i in 0..n {
        fs::write(dir.path().join(format!("photo_{}.jpg", i)), b"fake").unwrap();
    }
    dir
}

fn bench_scan_1000(c: &mut Criterion) {
    let dir = create_test_dir(1000);
    c.bench_function("scan_1000_files", |b| {
        b.iter(|| scanner::scan_directory(dir.path()).unwrap());
    });
}

criterion_group!(benches, bench_scan_1000);
criterion_main!(benches);
