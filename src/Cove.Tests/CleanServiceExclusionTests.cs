using Cove.Api.Services;
using Cove.Core.Entities;
using Cove.Core.Events;
using Cove.Core.Interfaces;
using Cove.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cove.Tests;

public sealed class CleanServiceExclusionTests
{
    [Fact]
    public async Task CleanRemovesExactlyWhatAScanWouldSkip()
    {
        await using var library = await Library.CreateAsync();
        library.Config.ExcludePatterns = ["Extras/**", "sample"];
        library.Config.ExcludeImagePatterns = ["Scans/**"];
        library.Config.ExcludeGalleryPatterns = ["old-*.zip"];
        library.Write("Library/.coveignore", "Private/\n!Private/keep.mp4\n*.txt\n");
        library.Write("Library/Mixed/.stashignore", "*.mp4\n");
        string[] kept =
        [
            "Library/Videos/clip.mp4", "Library/Videos/photo.jpg", "Library/Scans/clip.mp4", "Library/Sets/new-set.zip",
            "Library/Notes/story.md", "Library/Mixed/photo.jpg", "Music/cover.jpg",
        ];
        string[] excluded =
        [
            "Library/Extras/clip.mp4", "Library/Extras/Deep/photo.jpg", "Library/Videos/sample-clip.mp4", "Library/Scans/page.jpg",
            "Library/Sets/old-set.zip", "Library/Private/keep.mp4", "Library/Notes/readme.txt", "Library/Mixed/clip.mp4",
            "Library/Mixed/Sub/clip.mp4", "Music/song.mp3",
        ];
        foreach (var path in kept.Concat(excluded))
            await library.AddMediaAsync(path);

        var discovered = await library.DiscoverAsync();
        await library.CleanAsync(removeExcluded: true);

        var expected = kept.Select(library.StoredPath).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(expected, discovered.Order(StringComparer.Ordinal));
        Assert.Equal(expected, (await library.FilePathsAsync()).Order(StringComparer.Ordinal));
        Assert.Equal(kept.Length, await library.CountMediaAsync());
        Assert.All(kept.Concat(excluded), path => Assert.True(File.Exists(library.FullPath(path)), path));
    }

    [Fact]
    public async Task CleanRemovesTheExcludedFileButKeepsMediaThatHasAnotherFile()
    {
        await using var library = await Library.CreateAsync();
        library.Config.ExcludePatterns = ["Extras/**"];
        var excludedVideo = await library.AddVideoAsync("Library/Extras/clip.mp4");
        var keptVideo = await library.AddVideoAsync("Library/Extras/copy.mp4", "Library/Videos/copy.mp4");

        await library.CleanAsync(removeExcluded: true);

        await using var db = library.Open();
        var video = Assert.Single(await db.Videos.Include(item => item.Files).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(keptVideo, video.Id);
        Assert.Equal(library.StoredPath("Library/Videos/copy.mp4"), Assert.Single(video.Files).Path);
        Assert.Equal([excludedVideo], library.Thumbnails.DeletedVideoIds);
        Assert.True(File.Exists(library.FullPath("Library/Extras/clip.mp4")));
        Assert.True(File.Exists(library.FullPath("Library/Extras/copy.mp4")));
    }

    [Fact]
    public async Task CleanRemovesGalleriesTheExclusionsLeaveWithoutImages()
    {
        await using var library = await Library.CreateAsync();
        library.Config.ExcludePatterns = ["Extras/**"];
        await library.AddArchiveGalleryAsync("Library/Extras/set.zip", "page.jpg");
        await library.AddFolderGalleryAsync("Library/Extras/Pictures", "a.jpg", "b.jpg");
        var keptGallery = await library.AddFolderGalleryAsync("Library/Pictures", "a.jpg");

        await library.CleanAsync(removeExcluded: true);

        await using var db = library.Open();
        Assert.Equal([keptGallery], await db.Galleries.Select(gallery => gallery.Id).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal([library.StoredPath("Library/Pictures/a.jpg")], await db.ImageFiles.Select(file => file.Path).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await db.GalleryFiles.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DryRunCountsAndListsExcludedFilesApartFromMissingOnesAndRemovesNothing()
    {
        await using var library = await Library.CreateAsync();
        library.Config.ExcludePatterns = ["Extras/**"];
        await library.AddMediaAsync("Library/Extras/clip.mp4");
        await library.AddMediaAsync("Library/Videos/gone.mp4");
        await library.AddMediaAsync("Library/Videos/clip.mp4");
        File.Delete(library.FullPath("Library/Videos/gone.mp4"));

        await library.CleanAsync(dryRun: true, removeExcluded: true);

        Assert.Equal(3, await library.CountMediaAsync());
        Assert.Equal(3, (await library.FilePathsAsync()).Count);
        Assert.Contains("1 missing", library.Summary);
        Assert.Contains("1 excluded", library.Summary);
        Assert.Single(library.Log.Messages, message => message.Contains(library.StoredPath("Library/Extras/clip.mp4")));
        Assert.DoesNotContain(library.Log.Messages, message => message.Contains(library.StoredPath("Library/Videos/gone.mp4")));
        Assert.DoesNotContain(library.Log.Messages, message => message.Contains(library.StoredPath("Library/Videos/clip.mp4")));
    }

    [Fact]
    public async Task CleanScopedToAFolderRemovesOnlyTheExcludedFilesInsideIt()
    {
        await using var library = await Library.CreateAsync();
        library.Config.ExcludePatterns = ["**/Extras/**"];
        await library.AddMediaAsync("Library/Selected/Extras/clip.mp4");
        var outside = await library.AddMediaAsync("Library/Other/Extras/clip.mp4");

        await library.CleanAsync(paths: [library.FullPath("Library/Selected")], removeExcluded: true);

        await using var db = library.Open();
        Assert.Equal([outside], await db.Videos.Select(video => video.Id).ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CleanWithoutTheOptionKeepsMediaTheExclusionsCover()
    {
        await using var library = await Library.CreateAsync();
        library.Config.ExcludePatterns = ["Extras/**"];
        await library.AddMediaAsync("Library/Extras/clip.mp4");

        await library.CleanAsync(removeExcluded: false);

        Assert.Equal([library.StoredPath("Library/Extras/clip.mp4")], await library.FilePathsAsync());
    }

    private sealed class Library : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;

        private Library(string root, DbContextOptions<CoveContext> options)
        {
            Root = root;
            Options = options;
            _provider = BuildServices(options, Thumbnails);
            Config = new CoveConfiguration
            {
                CovePaths =
                [
                    new CovePath { Path = FullPath("Library") },
                    new CovePath { Path = FullPath("Music"), ExcludeAudio = true },
                ],
            };
        }

        public string Root { get; }
        public DbContextOptions<CoveContext> Options { get; }
        public CoveConfiguration Config { get; }
        public DeletionSafetyTests.RecordingThumbnailService Thumbnails { get; } = new();
        public RecordingLogger Log { get; } = new();
        public string Summary { get; set; } = string.Empty;

        public static async Task<Library> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), $"cove-clean-exclusions-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var options = new DbContextOptionsBuilder<CoveContext>()
                .UseSqlite($"Data Source={Path.Combine(root, "library.db")};Pooling=False").Options;
            var library = new Library(root, options);
            await using var db = library.Open();
            await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            return library;
        }

        private static ServiceProvider BuildServices(DbContextOptions<CoveContext> options, IThumbnailService thumbnails)
        {
            var services = new ServiceCollection();
            services.AddScoped(_ => new CoveContext(options));
            services.AddSingleton(thumbnails);
            services.AddScoped<CustomFieldService>();
            services.AddScoped<IBlobService>(provider => new DeletionSafetyTests.ReferenceAwareBlobService(provider.GetRequiredService<CoveContext>()));
            services.AddScoped<ImageDeletionService>(provider => new ImageDeletionService(provider.GetRequiredService<CoveContext>(),
                provider.GetRequiredService<CustomFieldService>(), provider.GetRequiredService<IThumbnailService>(),
                blobService: provider.GetRequiredService<IBlobService>()));
            services.AddSingleton<IEventBus>(new EventBus());
            services.AddScoped(provider => new BulkEntityDeletionService(provider.GetRequiredService<CoveContext>(),
                provider.GetRequiredService<CustomFieldService>(), provider.GetRequiredService<ImageDeletionService>(),
                provider.GetRequiredService<IThumbnailService>(), provider.GetRequiredService<IBlobService>(),
                provider.GetRequiredService<IEventBus>()));
            return services.BuildServiceProvider();
        }

        public CoveContext Open() => new(Options);

        public string FullPath(string relativePath) => Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));

        public string StoredPath(string relativePath) => ScanPath.NormalizeStoredFilePath(FullPath(relativePath));

        public string Write(string relativePath, string content = "x")
        {
            var path = FullPath(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        public async Task<int> AddMediaAsync(string relativePath)
        {
            await using var db = Open();
            var file = await NewFileAsync(db, relativePath);
            BaseEntity entity = file switch
            {
                VideoFile video => new Video { Title = relativePath, Files = [video] },
                ImageFile image => new Image { Title = relativePath, Files = [image] },
                GalleryFile archive => new Gallery { Title = relativePath, Files = [archive] },
                AudioFile audio => new Audio { Title = relativePath, Files = [audio] },
                TextFile text => new TextDocument { Title = relativePath, Files = [text] },
                _ => throw new ArgumentOutOfRangeException(nameof(relativePath)),
            };
            db.Add(entity);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            return entity.Id;
        }

        public async Task<int> AddVideoAsync(params string[] relativePaths)
        {
            await using var db = Open();
            var video = new Video { Title = relativePaths[0] };
            foreach (var relativePath in relativePaths)
                video.Files.Add((VideoFile)await NewFileAsync(db, relativePath));
            db.Add(video);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            return video.Id;
        }

        public async Task AddArchiveGalleryAsync(string relativeArchivePath, string memberName)
        {
            await using var db = Open();
            var archive = (GalleryFile)await NewFileAsync(db, relativeArchivePath);
            db.Add(new Gallery { Title = relativeArchivePath, Files = [archive] });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            var content = new Folder { Path = archive.Path, ZipFileId = archive.Id };
            db.Add(new Image
            {
                Title = memberName,
                Files = [new ImageFile { ParentFolder = content, Basename = memberName, ZipFileId = archive.Id }],
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        public async Task<int> AddFolderGalleryAsync(string relativeFolder, params string[] imageNames)
        {
            await using var db = Open();
            var images = new List<Image>();
            foreach (var name in imageNames)
                images.Add(new Image { Title = name, Files = [(ImageFile)await NewFileAsync(db, $"{relativeFolder}/{name}")] });
            db.AddRange(images);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            var gallery = new Gallery { Title = relativeFolder, FolderId = images[0].Files.First().ParentFolderId };
            db.Add(gallery);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            db.AddRange(images.Select(image => new ImageGallery { ImageId = image.Id, GalleryId = gallery.Id }));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            return gallery.Id;
        }

        public async Task<List<string>> DiscoverAsync()
        {
            var discovery = new ScanDiscoveryService(_provider.GetRequiredService<IServiceScopeFactory>(), Config, NullLogger.Instance);
            using var result = await discovery.DiscoverAsync(new ScanOperationOptions(), new Progress(this), TestContext.Current.CancellationToken);
            return [.. result.Files.Select(file => file.StoredPath)];
        }

        public async Task CleanAsync(bool dryRun = false, IReadOnlyList<string>? paths = null, bool removeExcluded = false)
        {
            var jobs = new ImmediateJobs(this);
            var service = new CleanService(jobs, _provider.GetRequiredService<IServiceScopeFactory>(), Config, Log);
            if (removeExcluded)
                service.StartClean(dryRun, paths, removeExcluded: true);
            else
                ((ICleanService)service).StartClean(dryRun, paths);
            await jobs.Completion;
        }

        public async Task<List<string>> FilePathsAsync()
        {
            await using var db = Open();
            return await db.Set<BaseFileEntity>().Select(file => file.Path).ToListAsync(TestContext.Current.CancellationToken);
        }

        public async Task<int> CountMediaAsync()
        {
            await using var db = Open();
            var ct = TestContext.Current.CancellationToken;
            return await db.Videos.CountAsync(ct) + await db.Images.CountAsync(ct) + await db.Galleries.CountAsync(ct)
                + await db.Audios.CountAsync(ct) + await db.TextDocuments.CountAsync(ct);
        }

        private async Task<BaseFileEntity> NewFileAsync(CoveContext db, string relativePath)
        {
            var path = Write(relativePath);
            var folderPath = ScanPath.NormalizeStoredFolderPath(Path.GetDirectoryName(path)!);
            var folder = db.Folders.Local.FirstOrDefault(item => item.Path == folderPath)
                ?? await db.Folders.FirstOrDefaultAsync(item => item.Path == folderPath, TestContext.Current.CancellationToken)
                ?? db.Folders.Add(new Folder { Path = folderPath }).Entity;
            var basename = Path.GetFileName(path);
            return Path.GetExtension(path) switch
            {
                ".mp4" => new VideoFile { ParentFolder = folder, Basename = basename },
                ".jpg" => new ImageFile { ParentFolder = folder, Basename = basename },
                ".zip" => new GalleryFile { ParentFolder = folder, Basename = basename },
                ".mp3" => new AudioFile { ParentFolder = folder, Basename = basename },
                ".txt" or ".md" => new TextFile { ParentFolder = folder, Basename = basename },
                var extension => throw new ArgumentOutOfRangeException(nameof(relativePath), extension, null),
            };
        }

        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync();
            Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class ImmediateJobs(Library library) : IJobService
    {
        public Task Completion { get; private set; } = Task.CompletedTask;
        public string Enqueue(string type, string description, Func<IJobProgress, CancellationToken, Task> work, bool exclusive = true)
        {
            Completion = work(new Progress(library), TestContext.Current.CancellationToken);
            return "clean-test";
        }
        public bool Cancel(string id) => false;
        public bool ReorderQueued(string id, string? before) => false;
        public JobInfo? GetJob(string id) => null;
        public IReadOnlyList<JobInfo> GetAllJobs() => [];
        public IReadOnlyList<JobInfo> GetJobHistory() => [];
    }

    private sealed class Progress(Library library) : IJobProgress
    {
        public void Report(double progress, string? subTask = null) => library.Summary = subTask ?? library.Summary;
    }

    private sealed class RecordingLogger : ILogger<CleanService>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }
}
