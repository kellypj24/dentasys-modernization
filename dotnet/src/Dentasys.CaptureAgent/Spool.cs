using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Dentasys.CaptureAgent;

/// <summary>What the agent knows about a capture before any server has heard of it.</summary>
public sealed record CaptureManifest(
    Guid CaptureId, string PracticeId, int PatientId, int? ApptId, string ProviderCode,
    bool ConsentRecorded, int? ChunkCount);

/// <summary>
/// Audio waiting to leave the workstation, encrypted at rest.
///
/// Layout: one directory per capture, holding manifest.bin and chunk-NNNNNN.bin.
/// Every file is AES-GCM with the capture id and chunk number as associated
/// data, so a chunk cannot be moved to another capture or position without
/// failing authentication. The manifest is encrypted too: the patient id is PHI.
///
/// Writes go to a temp file and are renamed into place, so a power cut leaves
/// either the old file or the new one, never half of one. A chunk is deleted the
/// moment the server acknowledges it: audio stays on the workstation no longer
/// than the network forces it to.
///
/// The key comes from the caller. On the operatory workstations it is protected
/// with DPAPI (machine scope); in the lab it is a key file, see <see cref="LoadOrCreateKey"/>.
/// </summary>
public sealed class Spool
{
    private readonly string _root;
    private readonly byte[] _key;

    public Spool(string root, byte[] key)
    {
        if (key.Length != 32) throw new ArgumentException("spool key must be 256 bits", nameof(key));
        _root = root;
        _key = key;
        Directory.CreateDirectory(root);
    }

    public void Start(CaptureManifest manifest)
    {
        if (!manifest.ConsentRecorded)
            throw new InvalidOperationException("no consent recorded for this visit; the agent does not record");
        Directory.CreateDirectory(Dir(manifest.CaptureId));
        WriteManifest(manifest);
    }

    public void AppendChunk(Guid captureId, int chunkNo, byte[] audio) =>
        WriteAtomic(ChunkPath(captureId, chunkNo), Seal(audio, Aad(captureId, chunkNo)));

    /// <summary>Recording is over; the capture has exactly <paramref name="chunkCount"/> chunks.</summary>
    public void Finish(Guid captureId, int chunkCount) =>
        WriteManifest(ReadManifest(captureId) with { ChunkCount = chunkCount });

    public IReadOnlyList<Guid> Captures() =>
        Directory.GetDirectories(_root).Select(d => Guid.TryParse(Path.GetFileName(d), out var g) ? g : Guid.Empty)
                 .Where(g => g != Guid.Empty).OrderBy(g => Directory.GetCreationTimeUtc(Dir(g))).ToList();

    public CaptureManifest ReadManifest(Guid captureId) =>
        JsonSerializer.Deserialize<CaptureManifest>(Open(File.ReadAllBytes(ManifestPath(captureId)), Aad(captureId, -1)))!;

    public IReadOnlyList<int> PendingChunks(Guid captureId) =>
        Directory.GetFiles(Dir(captureId), "chunk-*.bin")
                 .Select(f => int.Parse(Path.GetFileNameWithoutExtension(f)[6..])).Order().ToList();

    /// <summary>Throws <see cref="AuthenticationTagMismatchException"/> if the file was altered or moved.</summary>
    public byte[] ReadChunk(Guid captureId, int chunkNo) =>
        Open(File.ReadAllBytes(ChunkPath(captureId, chunkNo)), Aad(captureId, chunkNo));

    public void Acknowledge(Guid captureId, int chunkNo) => File.Delete(ChunkPath(captureId, chunkNo));

    public void Remove(Guid captureId) => Directory.Delete(Dir(captureId), recursive: true);

    /// <summary>Lab key source: 32 random bytes in a file only this user can read.</summary>
    public static byte[] LoadOrCreateKey(string path)
    {
        if (File.Exists(path)) return File.ReadAllBytes(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var key = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(path, key);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return key;
    }

    private void WriteManifest(CaptureManifest m) =>
        WriteAtomic(ManifestPath(m.CaptureId), Seal(JsonSerializer.SerializeToUtf8Bytes(m), Aad(m.CaptureId, -1)));

    // nonce (12) | tag (16) | ciphertext
    private byte[] Seal(byte[] plain, byte[] aad)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[plain.Length];
        using var aes = new AesGcm(_key, tag.Length);
        aes.Encrypt(nonce, plain, cipher, tag, aad);
        return [.. nonce, .. tag, .. cipher];
    }

    private byte[] Open(byte[] sealedBytes, byte[] aad)
    {
        var plain = new byte[sealedBytes.Length - 28];
        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(sealedBytes.AsSpan(0, 12), sealedBytes.AsSpan(28), sealedBytes.AsSpan(12, 16), plain, aad);
        return plain;
    }

    private static byte[] Aad(Guid captureId, int chunkNo) => Encoding.UTF8.GetBytes($"{captureId:N}:{chunkNo}");

    private static void WriteAtomic(string path, byte[] bytes)
    {
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
    }

    private string Dir(Guid id) => Path.Combine(_root, id.ToString("N"));
    private string ManifestPath(Guid id) => Path.Combine(Dir(id), "manifest.bin");
    private string ChunkPath(Guid id, int n) => Path.Combine(Dir(id), $"chunk-{n:D6}.bin");
}
