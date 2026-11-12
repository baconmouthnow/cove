using System.Collections.Concurrent;
using Cove.Core.Common;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;
using Microsoft.EntityFrameworkCore;

namespace Cove.Api.Services;

/// <summary>
/// Maintains byte identity for scanned files and reconciles moves, duplicates, and in-place changes.
/// </summary>
internal sealed class ScanFileIdentityService(IFingerprintService fingerprintService)
{
    /// <summary>
    /// Matches a newly observed path to an existing row by byte identity. A missing candidate is re-pointed as
    /// a move when every missing candidate the path could take belongs to one entity; an on-disk candidate
    /// identifies a duplicate's parent entity.
    /// </summary>
    public async Task<(TFile? Match, bool IsMove)> MatchExistingAsync<TFile>(
        DbSet<TFile> trackedSet,
        string path,
        int folderId,
        string basename,
        FileStat stat,
        MoveDetectionIndex moveIndex,
        CancellationToken ct)
        where TFile : BaseFileEntity
    {
        // Byte-identical files must have the same size. Most newly discovered files therefore cannot
        // be moves or duplicates at all; avoid touching their bytes or issuing a fingerprint query.
        if (!moveIndex.ContainsSize(stat.Size))
            return (null, false);

        var oshash = await moveIndex.GetOrComputeOshashAsync(path, ct);
        if (string.IsNullOrEmpty(oshash) || !moveIndex.Contains(stat.Size, oshash))
            return (null, false);

        var candidates = await trackedSet
            .Where(file => file.ZipFileId == null && file.Size == stat.Size
                && file.Fingerprints.Any(fingerprint => fingerprint.Type == "oshash" && fingerprint.Value == oshash))
            .ToListAsync(ct);

        if (candidates.Count == 0)
            return (null, false);

        var missing = candidates
            .Where(candidate => !string.IsNullOrEmpty(candidate.Path) && !File.Exists(candidate.Path))
            .OrderBy(candidate => candidate.Id)
            .ToList();

        // The claiming path may retry after a failed batch save. Re-apply the move so the fallback
        // preserves the existing row and entity identity.
        var claim = missing.FirstOrDefault(candidate => IsClaimedBy(candidate, path, moveIndex));
        if (claim == null)
        {
            // Moving every copy of an entity leaves each copy's row missing, and each new path takes one of them,
            // a row with its own name first. Another path may claim a row at any moment, so try them all. Content
            // alone cannot tell two entities apart, so a path takes no row while the unclaimed rows belong to more
            // than one entity.
            var unclaimed = missing.Where(candidate => !moveIndex.ClaimedFilePaths.ContainsKey(candidate.Id)).ToList();
            if (unclaimed.Select(OwnerId).Distinct().Count() == 1)
            {
                claim = unclaimed
                    .OrderBy(candidate => string.Equals(candidate.Basename, basename, FilesystemPaths.PathComparison) ? 0 : 1)
                    .FirstOrDefault(candidate => moveIndex.ClaimedFilePaths.TryAdd(candidate.Id, path));
            }
        }

        if (claim != null)
        {
            claim.ParentFolderId = folderId;
            claim.ParentFolder = null;
            claim.Basename = basename;
            claim.Size = stat.Size;
            claim.ModTime = stat.ModTime;
            return (claim, true);
        }

        // Other paths took every missing row of one entity: this is one more copy of its content.
        if (missing.Count > 0 && missing.Select(OwnerId).Distinct().Count() == 1
            && missing.All(candidate => moveIndex.ClaimedFilePaths.ContainsKey(candidate.Id)))
        {
            return (missing[0], false);
        }

        var present = candidates
            .Where(candidate => !string.IsNullOrEmpty(candidate.Path) && File.Exists(candidate.Path))
            .OrderBy(candidate => candidate.Id)
            .FirstOrDefault();

        return present != null ? (present, false) : (null, false);
    }

    private static bool IsClaimedBy(BaseFileEntity candidate, string path, MoveDetectionIndex moveIndex)
        => moveIndex.ClaimedFilePaths.TryGetValue(candidate.Id, out var claimedPath)
            && string.Equals(claimedPath, path, FilesystemPaths.PathComparison);

    private static int? OwnerId(BaseFileEntity file) => file switch
    {
        VideoFile video => video.VideoId,
        ImageFile image => image.ImageId,
        AudioFile audio => audio.AudioId,
        TextFile text => text.TextDocumentId,
        _ => throw new NotSupportedException($"{file.GetType().Name} has no move detection."),
    };

    public async Task RefreshChangedFingerprintsAsync(
        BaseFileEntity file,
        string path,
        bool md5Enabled,
        MoveDetectionIndex? moveIndex,
        CancellationToken ct)
    {
        var oshash = await ComputeOshashAsync(path, moveIndex, ct);
        if (!string.IsNullOrWhiteSpace(oshash))
            UpsertFingerprint(file, "oshash", oshash);
        else
            BlankFingerprint(file, "oshash");

        if (md5Enabled)
        {
            var md5 = await fingerprintService.ComputeMd5Async(path, ct);
            if (!string.IsNullOrWhiteSpace(md5))
                UpsertFingerprint(file, "md5", md5);
            else
                BlankFingerprint(file, "md5");
        }
        else
        {
            BlankFingerprint(file, "md5");
        }

        // Perceptual hashes use media-specific pipelines in the asset-generation phase. The old
        // value describes different bytes even when this scan did not request a replacement.
        BlankFingerprint(file, "phash");
    }

    public static void UpsertFingerprint(BaseFileEntity file, string type, string value)
    {
        var existing = file.Fingerprints.FirstOrDefault(fingerprint =>
            string.Equals(fingerprint.Type, type, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            existing.Value = value;
            return;
        }

        file.Fingerprints.Add(new FileFingerprint
        {
            Type = type,
            Value = value,
        });
    }

    public static void BlankFingerprint(BaseFileEntity file, string type)
    {
        var existing = file.Fingerprints.FirstOrDefault(fingerprint =>
            string.Equals(fingerprint.Type, type, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
            existing.Value = string.Empty;
    }

    /// <summary>
    /// Computes the OpenSubtitles hash from the file size plus its first and last 64 KiB.
    /// </summary>
    public static async Task<string?> ComputeOshashAsync(string path, CancellationToken ct)
    {
        const int chunkSize = 65_536;
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                chunkSize,
                useAsync: true);
            var fileSize = stream.Length;
            if (fileSize < chunkSize)
                return null;

            ulong hash = (ulong)fileSize;
            var buffer = new byte[chunkSize];

            await stream.ReadExactlyAsync(buffer, ct);
            for (var index = 0; index < chunkSize; index += 8)
                hash += BitConverter.ToUInt64(buffer, index);

            stream.Seek(-chunkSize, SeekOrigin.End);
            await stream.ReadExactlyAsync(buffer, ct);
            for (var index = 0; index < chunkSize; index += 8)
                hash += BitConverter.ToUInt64(buffer, index);

            return hash.ToString("x16");
        }
        catch
        {
            return null;
        }
    }

    internal static Task<string?> ComputeOshashAsync(
        string path,
        MoveDetectionIndex? moveIndex,
        CancellationToken ct) => moveIndex != null
            ? moveIndex.GetOrComputeOshashAsync(path, ct)
            : ComputeOshashAsync(path, ct);
}

/// <summary>
/// Coordinates scan-wide identity reads and concurrent move claims. Identity reads stay bounded even
/// when move matching is disabled; <see cref="Enabled"/> only controls matching against stored rows.
/// </summary>
internal sealed class MoveDetectionIndex
{
    private const int MaxConcurrentIdentityReads = 4;
    private const string MissingHash = "";
    private readonly HashSet<MoveDetectionFingerprint> knownFingerprints = [];
    private readonly HashSet<long> knownSizes = [];
    private readonly ConcurrentDictionary<string, string> computedOshashes = new(FilesystemPaths.PathComparer);
    private readonly SemaphoreSlim identityReadGate = new(MaxConcurrentIdentityReads, MaxConcurrentIdentityReads);

    public required bool Enabled { get; init; }
    public int KnownFingerprintCount => knownFingerprints.Count;
    public ConcurrentDictionary<int, string> ClaimedFilePaths { get; } = new();

    public static async Task<MoveDetectionIndex> LoadAsync(
        CoveContext db,
        bool enabled,
        CancellationToken ct)
    {
        if (!enabled)
            return new MoveDetectionIndex { Enabled = false };

        var storedFingerprints = await db.Set<BaseFileEntity>()
            .AsNoTracking()
            .Where(file => file.ZipFileId == null)
            .SelectMany(
                file => file.Fingerprints.Where(fingerprint =>
                    fingerprint.Type == "oshash" && fingerprint.Value != string.Empty),
                (file, fingerprint) => new { file.Size, fingerprint.Value })
            .Distinct()
            .ToListAsync(ct);

        var index = new MoveDetectionIndex { Enabled = storedFingerprints.Count > 0 };
        foreach (var fingerprint in storedFingerprints)
        {
            index.knownFingerprints.Add(new MoveDetectionFingerprint(fingerprint.Size, fingerprint.Value));
            index.knownSizes.Add(fingerprint.Size);
        }

        return index;
    }

    public bool ContainsSize(long size) => Enabled && knownSizes.Contains(size);

    public bool Contains(long size, string oshash) =>
        Enabled && knownFingerprints.Contains(new MoveDetectionFingerprint(size, oshash));

    public async Task<string?> GetOrComputeOshashAsync(string path, CancellationToken ct)
    {
        if (computedOshashes.TryGetValue(path, out var cached))
            return cached == MissingHash ? null : cached;

        await identityReadGate.WaitAsync(ct);
        try
        {
            if (computedOshashes.TryGetValue(path, out cached))
                return cached == MissingHash ? null : cached;

            var computed = await ScanFileIdentityService.ComputeOshashAsync(path, ct);
            computedOshashes[path] = computed ?? MissingHash;
            return computed;
        }
        finally
        {
            identityReadGate.Release();
        }
    }
}

internal readonly record struct MoveDetectionFingerprint(long Size, string OshaHash);
