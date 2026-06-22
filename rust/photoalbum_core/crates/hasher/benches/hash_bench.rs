use criterion::{criterion_group, criterion_main, Criterion, Throughput};
use std::io::Write;
use tempfile::NamedTempFile;

fn bench_hash_1mb(c: &mut Criterion) {
    let data = vec![0u8; 1024 * 1024];
    let mut f = NamedTempFile::new().unwrap();
    f.write_all(&data).unwrap();
    let path = f.path().to_owned();

    let mut group = c.benchmark_group("hasher");
    group.throughput(Throughput::Bytes(data.len() as u64));
    group.bench_function("blake3_1mb", |b| {
        b.iter(|| hasher::hash_file(&path).unwrap());
    });
    group.finish();
}

criterion_group!(benches, bench_hash_1mb);
criterion_main!(benches);
