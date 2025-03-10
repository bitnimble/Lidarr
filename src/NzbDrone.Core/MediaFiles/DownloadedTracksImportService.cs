using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.MediaFiles.TrackImport;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.MediaFiles
{
    public interface IDownloadedTracksImportService
    {
        List<ImportResult> ProcessRootFolder(IDirectoryInfo directoryInfo);
        List<ImportResult> ProcessPath(string path, ImportMode importMode = ImportMode.Auto, Artist artist = null, DownloadClientItem downloadClientItem = null);
        bool ShouldDeleteFolder(IDirectoryInfo directoryInfo, Artist artist);
    }

    public class DownloadedTracksImportService : IDownloadedTracksImportService
    {
        private readonly IDiskProvider _diskProvider;
        private readonly IDiskScanService _diskScanService;
        private readonly IArtistService _artistService;
        private readonly IParsingService _parsingService;
        private readonly IMakeImportDecision _importDecisionMaker;
        private readonly IImportApprovedTracks _importApprovedTracks;
        private readonly IEventAggregator _eventAggregator;
        private readonly IRuntimeInfo _runtimeInfo;
        private readonly Logger _logger;

        public DownloadedTracksImportService(IDiskProvider diskProvider,
                                             IDiskScanService diskScanService,
                                             IArtistService artistService,
                                             IParsingService parsingService,
                                             IMakeImportDecision importDecisionMaker,
                                             IImportApprovedTracks importApprovedTracks,
                                             IEventAggregator eventAggregator,
                                             IRuntimeInfo runtimeInfo,
                                             Logger logger)
        {
            _diskProvider = diskProvider;
            _diskScanService = diskScanService;
            _artistService = artistService;
            _parsingService = parsingService;
            _importDecisionMaker = importDecisionMaker;
            _importApprovedTracks = importApprovedTracks;
            _eventAggregator = eventAggregator;
            _runtimeInfo = runtimeInfo;
            _logger = logger;
        }

        public List<ImportResult> ProcessRootFolder(IDirectoryInfo directoryInfo)
        {
            var results = new List<ImportResult>();

            foreach (var subFolder in _diskProvider.GetDirectoryInfos(directoryInfo.FullName))
            {
                var folderResults = ProcessFolder(subFolder, ImportMode.Auto, null);
                results.AddRange(folderResults);
            }

            foreach (var audioFile in _diskScanService.GetAudioFiles(directoryInfo.FullName, false))
            {
                var fileResults = ProcessFile(audioFile, ImportMode.Auto, null);
                results.AddRange(fileResults);
            }

            return results;
        }

        public List<ImportResult> ProcessPath(string path, ImportMode importMode = ImportMode.Auto, Artist artist = null, DownloadClientItem downloadClientItem = null)
        {
            _logger.Debug("Processing path: {0}", path);

            if (_diskProvider.FolderExists(path))
            {
                var directoryInfo = _diskProvider.GetDirectoryInfo(path);

                if (artist == null)
                {
                    return ProcessFolder(directoryInfo, importMode, downloadClientItem);
                }

                return ProcessFolder(directoryInfo, importMode, artist, downloadClientItem);
            }

            if (_diskProvider.FileExists(path))
            {
                var fileInfo = _diskProvider.GetFileInfo(path);

                if (artist == null)
                {
                    return ProcessFile(fileInfo, importMode, downloadClientItem);
                }

                return ProcessFile(fileInfo, importMode, artist, downloadClientItem);
            }

            LogInaccessiblePathError(path);
            _eventAggregator.PublishEvent(new TrackImportFailedEvent(null, null, true, downloadClientItem));

            return new List<ImportResult>();
        }

        public bool ShouldDeleteFolder(IDirectoryInfo directoryInfo, Artist artist)
        {
            try
            {
                var audioFiles = _diskScanService.GetAudioFiles(directoryInfo.FullName);
                var rarFiles = _diskProvider.GetFiles(directoryInfo.FullName, true).Where(f => Path.GetExtension(f).Equals(".rar", StringComparison.OrdinalIgnoreCase));

                foreach (var audioFile in audioFiles)
                {
                    var albumParseResult = Parser.Parser.ParseMusicTitle(audioFile.Name);

                    if (albumParseResult == null)
                    {
                        _logger.Warn("Unable to parse file on import: [{0}]", audioFile);
                        return false;
                    }

                    _logger.Warn("Audio file detected: [{0}]", audioFile);
                    return false;
                }

                if (rarFiles.Any(f => _diskProvider.GetFileSize(f) > 10.Megabytes()))
                {
                    _logger.Warn("RAR file detected, will require manual cleanup");
                    return false;
                }

                return true;
            }
            catch (DirectoryNotFoundException e)
            {
                _logger.Debug(e, "Folder {0} has already been removed", directoryInfo.FullName);
                return false;
            }
        }

        private List<ImportResult> ProcessFolder(IDirectoryInfo directoryInfo, ImportMode importMode, DownloadClientItem downloadClientItem)
        {
            var cleanedUpName = GetCleanedUpFolderName(directoryInfo.Name);
            var artist = _parsingService.GetArtist(cleanedUpName);

            if (artist == null)
            {
                _logger.Debug("Unknown Artist {0}", cleanedUpName);

                return new List<ImportResult>
                       {
                           UnknownArtistResult("Unknown Artist")
                       };
            }

            return ProcessFolder(directoryInfo, importMode, artist, downloadClientItem);
        }

        private List<ImportResult> ProcessFolder(IDirectoryInfo directoryInfo, ImportMode importMode, Artist artist, DownloadClientItem downloadClientItem)
        {
            if (_artistService.ArtistPathExists(directoryInfo.FullName))
            {
                _logger.Warn("Unable to process folder that is mapped to an existing artist");
                return new List<ImportResult>();
            }

            var cleanedUpName = GetCleanedUpFolderName(directoryInfo.Name);
            var folderInfo = Parser.Parser.ParseAlbumTitle(directoryInfo.Name);

            var audioFiles = _diskScanService.FilterFiles(directoryInfo.FullName, _diskScanService.GetAudioFiles(directoryInfo.FullName));

            if (downloadClientItem == null)
            {
                foreach (var audioFile in audioFiles)
                {
                    if (_diskProvider.IsFileLocked(audioFile.FullName))
                    {
                        return new List<ImportResult>
                               {
                                   FileIsLockedResult(audioFile.FullName)
                               };
                    }
                }
            }

            var idOverrides = new IdentificationOverrides
            {
                Artist = artist
            };
            var idInfo = new ImportDecisionMakerInfo
            {
                DownloadClientItem = downloadClientItem,
                ParsedAlbumInfo = folderInfo
            };
            var idConfig = new ImportDecisionMakerConfig
            {
                Filter = FilterFilesType.None,
                NewDownload = true,
                SingleRelease = false,
                IncludeExisting = false,
                AddNewArtists = false
            };

            var decisions = _importDecisionMaker.GetImportDecisions(audioFiles, idOverrides, idInfo, idConfig);
            var importResults = _importApprovedTracks.Import(decisions, true, downloadClientItem, importMode);

            if (importMode == ImportMode.Auto)
            {
                importMode = (downloadClientItem == null || downloadClientItem.CanMoveFiles) ? ImportMode.Move : ImportMode.Copy;
            }

            if (importMode == ImportMode.Move &&
                importResults.Any(i => i.Result == ImportResultType.Imported) &&
                ShouldDeleteFolder(directoryInfo, artist))
            {
                _logger.Debug("Deleting folder after importing valid files");

                try
                {
                    _diskProvider.DeleteFolder(directoryInfo.FullName, true);
                }
                catch (IOException e)
                {
                    _logger.Debug(e, "Unable to delete folder after importing: {0}", e.Message);
                }
            }

            return importResults;
        }

        private List<ImportResult> ProcessFile(IFileInfo fileInfo, ImportMode importMode, DownloadClientItem downloadClientItem)
        {
            var artist = _parsingService.GetArtist(Path.GetFileNameWithoutExtension(fileInfo.Name));

            if (artist == null)
            {
                _logger.Debug("Unknown Artist for file: {0}", fileInfo.Name);

                return new List<ImportResult>
                       {
                           UnknownArtistResult(string.Format("Unknown Artist for file: {0}", fileInfo.Name), fileInfo.FullName)
                       };
            }

            return ProcessFile(fileInfo, importMode, artist, downloadClientItem);
        }

        private List<ImportResult> ProcessFile(IFileInfo fileInfo, ImportMode importMode, Artist artist, DownloadClientItem downloadClientItem)
        {
            if (Path.GetFileNameWithoutExtension(fileInfo.Name).StartsWith("._"))
            {
                _logger.Debug("[{0}] starts with '._', skipping", fileInfo.FullName);

                return new List<ImportResult>
                       {
                           new ImportResult(new ImportDecision<LocalTrack>(new LocalTrack { Path = fileInfo.FullName }, new Rejection("Invalid music file, filename starts with '._'")), "Invalid music file, filename starts with '._'")
                       };
            }

            var extension = Path.GetExtension(fileInfo.Name);

            if (extension.IsNullOrWhiteSpace() || !MediaFileExtensions.Extensions.Contains(extension))
            {
                _logger.Debug("[{0}] has an unsupported extension: '{1}'", fileInfo.FullName, extension);

                return new List<ImportResult>
                       {
                           new ImportResult(new ImportDecision<LocalTrack>(new LocalTrack { Path = fileInfo.FullName },
                               new Rejection($"Invalid audio file, unsupported extension: '{extension}'")),
                               $"Invalid audio file, unsupported extension: '{extension}'")
                       };
            }

            if (downloadClientItem == null)
            {
                if (_diskProvider.IsFileLocked(fileInfo.FullName))
                {
                    return new List<ImportResult>
                           {
                               FileIsLockedResult(fileInfo.FullName)
                           };
                }
            }

            var idOverrides = new IdentificationOverrides
            {
                Artist = artist
            };
            var idInfo = new ImportDecisionMakerInfo
            {
                DownloadClientItem = downloadClientItem
            };
            var idConfig = new ImportDecisionMakerConfig
            {
                Filter = FilterFilesType.None,
                NewDownload = true,
                SingleRelease = false,
                IncludeExisting = false,
                AddNewArtists = false
            };

            var decisions = _importDecisionMaker.GetImportDecisions(new List<IFileInfo>() { fileInfo }, idOverrides, idInfo, idConfig);

            return _importApprovedTracks.Import(decisions, true, downloadClientItem, importMode);
        }

        private string GetCleanedUpFolderName(string folder)
        {
            folder = folder.Replace("_UNPACK_", "")
                           .Replace("_FAILED_", "");

            return folder;
        }

        private ImportResult FileIsLockedResult(string audioFile)
        {
            _logger.Debug("[{0}] is currently locked by another process, skipping", audioFile);
            return new ImportResult(new ImportDecision<LocalTrack>(new LocalTrack { Path = audioFile }, new Rejection("Locked file, try again later")), "Locked file, try again later");
        }

        private ImportResult UnknownArtistResult(string message, string audioFile = null)
        {
            var localTrack = audioFile == null ? null : new LocalTrack { Path = audioFile };

            return new ImportResult(new ImportDecision<LocalTrack>(localTrack, new Rejection("Unknown Artist")), message);
        }

        private void LogInaccessiblePathError(string path)
        {
            if (_runtimeInfo.IsWindowsService)
            {
                var mounts = _diskProvider.GetMounts();
                var mount = mounts.FirstOrDefault(m => m.RootDirectory == Path.GetPathRoot(path));

                if (mount == null)
                {
                    _logger.Error("Import failed, path does not exist or is not accessible by Lidarr: {0}. Unable to find a volume mounted for the path. If you're using a mapped network drive see the FAQ for more info", path);
                    return;
                }

                if (mount.DriveType == DriveType.Network)
                {
                    _logger.Error("Import failed, path does not exist or is not accessible by Lidarr: {0}. It's recommended to avoid mapped network drives when running as a Windows service. See the FAQ for more info", path);
                    return;
                }
            }

            if (OsInfo.IsWindows)
            {
                if (path.StartsWith(@"\\"))
                {
                    _logger.Error("Import failed, path does not exist or is not accessible by Lidarr: {0}. Ensure the user running Lidarr has access to the network share", path);
                    return;
                }
            }

            _logger.Error("Import failed, path does not exist or is not accessible by Lidarr: {0}. Ensure the path exists and the user running Lidarr has the correct permissions to access this file/folder", path);
        }
    }
}
