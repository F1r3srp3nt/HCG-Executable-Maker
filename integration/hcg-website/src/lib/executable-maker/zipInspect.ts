// Structural inspection of an uploaded ZIP without extracting anything: reads the central directory and refuses
// archives that could harm whoever opens them (path traversal, absolute paths, symbolic links, too many entries,
// too much expanded data, extreme compression ratios, encrypted or ZIP64 entries). Nothing is executed or unpacked.

export type ZipLimits = { maxEntries: number; maxExpandedBytes: number; maxRatio: number };
export type ZipReport = { ok: boolean; reason?: string; entries: number; expandedBytes: number; nestedArchives: string[] };

export const DEFAULT_ZIP_LIMITS: ZipLimits = { maxEntries: 20000, maxExpandedBytes: 1024 * 1024 * 1024, maxRatio: 200 };

const fail = (reason: string, entries = 0, expandedBytes = 0): ZipReport => ({ ok: false, reason, entries, expandedBytes, nestedArchives: [] });

export function isZip(bytes: Uint8Array): boolean {
  return bytes.length >= 4 && bytes[0] === 0x50 && bytes[1] === 0x4b && (bytes[2] === 0x03 || bytes[2] === 0x05) ;
}

export function inspectZip(bytes: Uint8Array, limits: ZipLimits = DEFAULT_ZIP_LIMITS): ZipReport {
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  // End of central directory: search the last 64 KiB + 22 bytes.
  let eocd = -1;
  for (let i = bytes.length - 22; i >= Math.max(0, bytes.length - 65557); i--) {
    if (view.getUint32(i, true) === 0x06054b50) { eocd = i; break; }
  }
  if (eocd < 0) return fail('Not a readable ZIP archive (no central directory).');
  const total = view.getUint16(eocd + 10, true);
  const cdSize = view.getUint32(eocd + 12, true);
  const cdOffset = view.getUint32(eocd + 16, true);
  if (total === 0xffff || cdOffset === 0xffffffff || cdSize === 0xffffffff) return fail('ZIP64 archives are not accepted.');
  if (total > limits.maxEntries) return fail(`The archive has ${total} entries; at most ${limits.maxEntries} are accepted.`, total);
  if (cdOffset + cdSize > eocd) return fail('The ZIP central directory is corrupt.');

  const decoder = new TextDecoder('utf-8', { fatal: false });
  const seen = new Set<string>();
  const nested: string[] = [];
  let p = cdOffset;
  let expanded = 0;
  for (let n = 0; n < total; n++) {
    if (p + 46 > bytes.length || view.getUint32(p, true) !== 0x02014b50) return fail('The ZIP central directory is corrupt.', n, expanded);
    const flags = view.getUint16(p + 8, true);
    const compressed = view.getUint32(p + 20, true);
    const size = view.getUint32(p + 24, true);
    const nameLen = view.getUint16(p + 28, true);
    const extraLen = view.getUint16(p + 30, true);
    const commentLen = view.getUint16(p + 32, true);
    const external = view.getUint32(p + 38, true);
    const name = decoder.decode(bytes.subarray(p + 46, p + 46 + nameLen));
    p += 46 + nameLen + extraLen + commentLen;

    if (flags & 0x1) return fail(`Encrypted entries are not accepted (${name}).`, n, expanded);
    if (compressed === 0xffffffff || size === 0xffffffff) return fail('ZIP64 entries are not accepted.', n, expanded);
    if (name.length === 0 || name.includes('\0')) return fail('An entry has an empty or invalid name.', n, expanded);
    if (name.startsWith('/') || name.startsWith('\\') || /^[A-Za-z]:/.test(name)) return fail(`Absolute path in archive: ${name}`, n, expanded);
    if (name.includes('\\')) return fail(`Backslash in entry name (not portable, possible traversal): ${name}`, n, expanded);
    if (name.split('/').some((seg) => seg === '..')) return fail(`Path traversal in archive: ${name}`, n, expanded);
    const unixType = (external >>> 16) & 0xf000;
    if (unixType === 0xa000) return fail(`Symbolic link in archive: ${name}`, n, expanded);
    const key = name.toLowerCase();
    if (seen.has(key)) return fail(`Duplicate entry: ${name}`, n, expanded);
    seen.add(key);
    expanded += size;
    if (expanded > limits.maxExpandedBytes) return fail(`The archive expands to more than ${limits.maxExpandedBytes} bytes.`, n + 1, expanded);
    if (compressed > 0 && size / compressed > limits.maxRatio && size > 10 * 1024 * 1024)
      return fail(`Suspicious compression ratio for ${name} (possible archive bomb).`, n + 1, expanded);
    if (/\.(zip|jar|apk|aab|ipa|7z|rar|tar|tgz|gz|xz|bz2)$/i.test(name)) nested.push(name);
  }
  return { ok: true, entries: total, expandedBytes: expanded, nestedArchives: nested };
}
