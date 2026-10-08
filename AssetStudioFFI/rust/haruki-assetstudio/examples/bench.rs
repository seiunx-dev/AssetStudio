//! Read-path benchmark and byte-exactness harness.
//!
//! Opens each input bundle, lists every object, reads all objects through the
//! by-index direct retry path, and prints per-bundle timing plus an FNV-1a hash
//! over every item's metadata and payload bytes. Running the same inputs against
//! two library builds must produce identical hashes.
//!
//! Usage:
//!   bench <native-library> [--unity-version V] [--rounds N] [--kind K] [--threads T] <input>...
//!
//! With --threads T the inputs are split across T OS threads, each using its own
//! contexts on the shared library handle; total wall time shows how decode
//! throughput scales without the old process-wide image guard lock.

use std::time::Instant;

use haruki_assetstudio::{AssetStudioLibrary, ObjectReadByIndexRequest};

const FNV_OFFSET: u64 = 0xcbf29ce484222325;
const FNV_PRIME: u64 = 0x100000001b3;

fn fnv1a(hash: &mut u64, bytes: &[u8]) {
    for &byte in bytes {
        *hash ^= u64::from(byte);
        *hash = hash.wrapping_mul(FNV_PRIME);
    }
}

fn main() -> Result<(), Box<dyn std::error::Error>> {
    let mut library_path = None;
    let mut unity_version = None;
    let mut rounds = 1usize;
    let mut kind = "auto".to_string();
    let mut threads = 1usize;
    let mut inputs = Vec::new();

    let mut args = std::env::args().skip(1);
    while let Some(arg) = args.next() {
        match arg.as_str() {
            "--unity-version" => unity_version = Some(args.next().expect("--unity-version value")),
            "--rounds" => rounds = args.next().expect("--rounds value").parse()?,
            "--kind" => kind = args.next().expect("--kind value"),
            "--threads" => threads = args.next().expect("--threads value").parse()?,
            _ if library_path.is_none() => library_path = Some(arg),
            _ => inputs.push(arg),
        }
    }
    let library_path = library_path.expect("native library path");
    assert!(!inputs.is_empty(), "at least one input bundle required");
    assert!(threads >= 1, "--threads must be at least 1");

    if threads > 1 {
        return run_threaded(
            &library_path,
            unity_version.as_deref(),
            rounds,
            &kind,
            threads,
            inputs,
        );
    }

    let library = AssetStudioLibrary::load(&library_path)?;

    let mut grand_hash = FNV_OFFSET;
    let mut grand_bytes = 0u64;
    let mut grand_objects = 0u64;
    let started = Instant::now();

    for round in 0..rounds {
        for input in &inputs {
            let bundle_started = Instant::now();
            let context = library.open(input, unity_version.as_deref(), &[], false)?;

            let mut object_indexes = Vec::new();
            let mut offset = 0;
            loop {
                let page = context.list_objects(offset, 4096, &[])?;
                if page.is_empty() {
                    break;
                }
                offset += page.len() as i32;
                object_indexes.extend(page.iter().map(|asset| asset.index));
                if page.len() < 4096 {
                    break;
                }
            }

            let mut bundle_hash = FNV_OFFSET;
            let mut bundle_bytes = 0u64;
            for chunk in object_indexes.chunks(64) {
                let requests: Vec<ObjectReadByIndexRequest> = chunk
                    .iter()
                    .map(|&object_index| ObjectReadByIndexRequest {
                        object_index,
                        kind: &kind,
                        image_format: "raw_rgba",
                    })
                    .collect();
                let read = context.read_by_index_retry(&requests)?;
                for item in &read.items {
                    fnv1a(&mut bundle_hash, &item.path_id.to_le_bytes());
                    fnv1a(&mut bundle_hash, &item.status.to_le_bytes());
                    fnv1a(&mut bundle_hash, item.payload_kind.as_bytes());
                    fnv1a(&mut bundle_hash, item.suggested_extension.as_bytes());
                    if let Some(payload) = read.payload_for(item) {
                        fnv1a(&mut bundle_hash, &(payload.len() as u64).to_le_bytes());
                        fnv1a(&mut bundle_hash, payload);
                        bundle_bytes += payload.len() as u64;
                    }
                }
                grand_objects += read.items.len() as u64;
            }

            fnv1a(&mut grand_hash, &bundle_hash.to_le_bytes());
            grand_bytes += bundle_bytes;
            if round == 0 {
                println!(
                    "{input}: objects={} payload_bytes={} hash={bundle_hash:016x} elapsed_ms={}",
                    object_indexes.len(),
                    bundle_bytes,
                    bundle_started.elapsed().as_millis()
                );
            }
        }
    }

    println!(
        "TOTAL rounds={rounds} objects={grand_objects} payload_bytes={grand_bytes} hash={grand_hash:016x} elapsed_ms={}",
        started.elapsed().as_millis()
    );
    Ok(())
}

fn run_threaded(
    library_path: &str,
    unity_version: Option<&str>,
    rounds: usize,
    kind: &str,
    threads: usize,
    inputs: Vec<String>,
) -> Result<(), Box<dyn std::error::Error>> {
    let library = std::sync::Arc::new(AssetStudioLibrary::load(library_path)?);
    let started = Instant::now();
    let mut handles = Vec::new();
    for worker in 0..threads {
        let library = std::sync::Arc::clone(&library);
        let unity_version = unity_version.map(|value| value.to_string());
        let kind = kind.to_string();
        let subset: Vec<String> = inputs
            .iter()
            .enumerate()
            .filter(|(index, _)| index % threads == worker)
            .map(|(_, input)| input.clone())
            .collect();
        handles.push(std::thread::spawn(
            move || -> Result<(u64, u64, u64), String> {
                let mut hash = FNV_OFFSET;
                let mut bytes = 0u64;
                let mut objects = 0u64;
                for _ in 0..rounds {
                    for input in &subset {
                        let context = library
                            .open(input, unity_version.as_deref(), &[], false)
                            .map_err(|error| format!("{input}: open: {error}"))?;
                        let mut object_indexes = Vec::new();
                        let mut offset = 0;
                        loop {
                            let page = context
                                .list_objects(offset, 4096, &[])
                                .map_err(|error| format!("{input}: list: {error}"))?;
                            if page.is_empty() {
                                break;
                            }
                            offset += page.len() as i32;
                            object_indexes.extend(page.iter().map(|asset| asset.index));
                            if page.len() < 4096 {
                                break;
                            }
                        }
                        for chunk in object_indexes.chunks(64) {
                            let requests: Vec<ObjectReadByIndexRequest> = chunk
                                .iter()
                                .map(|&object_index| ObjectReadByIndexRequest {
                                    object_index,
                                    kind: &kind,
                                    image_format: "raw_rgba",
                                })
                                .collect();
                            let read = context
                                .read_by_index_retry(&requests)
                                .map_err(|error| format!("{input}: read: {error}"))?;
                            for item in &read.items {
                                fnv1a(&mut hash, &item.path_id.to_le_bytes());
                                if let Some(payload) = read.payload_for(item) {
                                    fnv1a(&mut hash, payload);
                                    bytes += payload.len() as u64;
                                }
                            }
                            objects += read.items.len() as u64;
                        }
                    }
                }
                Ok((hash, bytes, objects))
            },
        ));
    }

    let mut grand_bytes = 0u64;
    let mut grand_objects = 0u64;
    let mut combined = FNV_OFFSET;
    for handle in handles {
        let (hash, bytes, objects) = handle
            .join()
            .map_err(|_| "bench thread panicked")?
            .map_err(|error| -> Box<dyn std::error::Error> { error.into() })?;
        // Combine order-independently so thread scheduling does not affect the value.
        combined ^= hash;
        grand_bytes += bytes;
        grand_objects += objects;
    }
    println!(
        "TOTAL threads={threads} rounds={rounds} objects={grand_objects} payload_bytes={grand_bytes} hash={combined:016x} elapsed_ms={}",
        started.elapsed().as_millis()
    );
    Ok(())
}
