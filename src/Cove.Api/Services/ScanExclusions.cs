using Cove.Core.Common;
using Cove.Core.Interfaces;

namespace Cove.Api.Services;

/// <summary>
/// Tells whether a full scan would skip a file because of the scan exclusions: the exclude patterns,
/// a library path's media-type exclusions, or a <c>.coveignore</c> or <c>.stashignore</c> rule. It runs
/// the checks <see cref="ScanDiscoveryService"/> runs while it walks a library path, so Clean removes
/// exactly what a scan would no longer add. The patterns and library paths are read when it is created.
/// </summary>
internal sealed class ScanExclusions
{
    private readonly ScanExtensionCatalog _extensions;
    private readonly ConfiguredScanPatternMatcher _patterns;
    private readonly ScanTarget[] _libraryPaths;
    private readonly Dictionary<string, List<IgnoreRule>> _ignoreRules = new(FilesystemPaths.PathComparer);

    public ScanExclusions(CoveConfiguration config)
    {
        _extensions = ScanExtensionCatalog.From(config);
        _patterns = new ConfiguredScanPatternMatcher(config);
        _libraryPaths = [.. config.CovePaths.Select(path => new ScanTarget(
            ScanPath.Normalize(path.Path),
            path.ExcludeVideo,
            path.ExcludeImage,
            path.ExcludeAudio,
            path.ExcludeText,
            false,
            ScanPath.Normalize(path.Path)))];
    }

    /// <summary>
    /// True when every library path that contains <paramref name="path"/> would skip it. A file outside
    /// every library path is not excluded.
    /// </summary>
    public bool IsExcluded(string path)
    {
        var fullPath = ScanPath.Normalize(path);
        var excluded = false;
        foreach (var libraryPath in _libraryPaths)
        {
            if (!ScanPath.IsWithin(fullPath, libraryPath.Path))
                continue;
            // A full scan walks each library path, nested ones included, and adds a file any walk finds.
            if (!IsExcludedUnder(libraryPath, fullPath))
                return false;
            excluded = true;
        }

        return excluded;
    }

    /// <summary>
    /// True when <paramref name="folder"/> holds image files and the exclusions cover every one of them. A scan makes
    /// a folder gallery only for a folder whose images it adds, so it would not make one for this folder.
    /// </summary>
    public bool CoversEveryImageIn(string folder)
    {
        var covered = false;
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*", new EnumerationOptions { AttributesToSkip = 0 }))
            {
                if (!_extensions.Image.Contains(Path.GetExtension(file)))
                    continue;
                if (!IsExcluded(file))
                    return false;
                covered = true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return covered;
    }

    private bool IsExcludedUnder(ScanTarget libraryPath, string fullPath)
    {
        var relativePath = Path.GetRelativePath(libraryPath.PatternRoot, fullPath);
        var extension = Path.GetExtension(fullPath);
        return _patterns.IsGloballyExcluded(fullPath, relativePath)
            || ScanDiscoveryService.IsMediaTypeExcludedByScanTarget(
                extension,
                libraryPath.ExcludeVideo,
                libraryPath.ExcludeImage,
                libraryPath.ExcludeAudio,
                libraryPath.ExcludeText,
                _extensions.Video,
                _extensions.Image,
                _extensions.Gallery,
                _extensions.Audio,
                _extensions.Text)
            || _patterns.IsMediaTypeExcluded(fullPath, relativePath, extension, _extensions.Image, _extensions.Gallery)
            || IsExcludedByIgnoreFiles(libraryPath.Path, fullPath);
    }

    private bool IsExcludedByIgnoreFiles(string root, string fullPath)
    {
        var ruleSets = new List<ActiveIgnoreRuleSet>();
        var directory = root;
        AddRules(directory);
        var relativeDirectory = Path.GetRelativePath(root, Path.GetDirectoryName(fullPath) ?? root);
        if (relativeDirectory != ".")
        {
            foreach (var name in relativeDirectory.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            {
                directory = Path.Combine(directory, name);
                // The walk skips a folder that the rules above it ignore, without reading the rules inside it.
                if (ScanDiscoveryService.IsExcludedByActiveIgnoreRules(directory, ruleSets, isDirectory: true))
                    return true;
                AddRules(directory);
            }
        }

        return ScanDiscoveryService.IsExcludedByActiveIgnoreRules(fullPath, ruleSets);

        void AddRules(string folder)
        {
            var rules = ScanDiscoveryService.GetIgnoreRules(folder, _ignoreRules);
            if (rules.Count > 0)
                ruleSets.Add(new ActiveIgnoreRuleSet(folder, rules));
        }
    }
}
