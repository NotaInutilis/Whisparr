using System.Collections.Specialized;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Processes;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MediaFiles.MediaInfo;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Tags;

namespace NzbDrone.Core.MediaFiles
{
    public interface IImportScript
    {
        public ScriptImportDecision TryImport(string sourcePath, string destinationFilePath, LocalMovie localMovie, MovieFile movieFile, TransferMode mode);
    }

    public class ImportScriptService : IImportScript
    {
        private readonly IConfigFileProvider _configFileProvider;
        private readonly IVideoFileInfoReader _videoFileInfoReader;
        private readonly IProcessProvider _processProvider;
        private readonly IConfigService _configService;
        private readonly ITagRepository _tagRepository;
        private readonly Logger _logger;

        public ImportScriptService(IProcessProvider processProvider,
                                   IVideoFileInfoReader videoFileInfoReader,
                                   IConfigService configService,
                                   IConfigFileProvider configFileProvider,
                                   ITagRepository tagRepository,
                                   Logger logger)
        {
            _processProvider = processProvider;
            _videoFileInfoReader = videoFileInfoReader;
            _configService = configService;
            _configFileProvider = configFileProvider;
            _tagRepository = tagRepository;
            _logger = logger;
        }

        public ScriptImportDecision TryImport(string sourcePath, string destinationFilePath, LocalMovie localMovie, MovieFile movieFile, TransferMode mode)
        {
            var movie = localMovie.Movie;
            var oldFiles = localMovie.OldFiles;
            var downloadClientInfo = localMovie.DownloadItem?.DownloadClientInfo;
            var downloadId = localMovie.DownloadItem?.DownloadId;

            if (!_configService.UseScriptImport)
            {
                return ScriptImportDecision.DeferMove;
            }

            var environmentVariables = new StringDictionary();

            environmentVariables.Add("Whisparr_SourcePath", sourcePath);
            environmentVariables.Add("Whisparr_DestinationPath", destinationFilePath);

            environmentVariables.Add("Whisparr_InstanceName", _configFileProvider.InstanceName);
            environmentVariables.Add("Whisparr_ApplicationUrl", _configService.ApplicationUrl);
            environmentVariables.Add("Whisparr_TransferMode", mode.ToString());

            environmentVariables.Add("Whisparr_Movie_Id", movie.Id.ToString());
            environmentVariables.Add("Whisparr_Movie_Title", movie.MovieMetadata.Value.Title);
            environmentVariables.Add("Whisparr_Movie_Year", movie.MovieMetadata.Value.Year.ToString());
            environmentVariables.Add("Whisparr_Movie_Path", movie.Path);
            environmentVariables.Add("Whisparr_Movie_TmdbId", movie.MovieMetadata.Value.TmdbId.ToString());
            environmentVariables.Add("Whisparr_Movie_ImdbId", movie.MovieMetadata.Value.ImdbId ?? string.Empty);
            environmentVariables.Add("Whisparr_Movie_OriginalLanguage", IsoLanguages.Get(movie.MovieMetadata.Value.OriginalLanguage).ThreeLetterCode);
            environmentVariables.Add("Whisparr_Movie_Genres", string.Join("|", movie.MovieMetadata.Value.Genres));
            environmentVariables.Add("Whisparr_Movie_Tags", string.Join("|", movie.Tags.Select(t => _tagRepository.Get(t).Label)));

            environmentVariables.Add("Whisparr_Movie_Release_Date", movie.MovieMetadata.Value.ReleaseDate.ToString() ?? string.Empty);
            environmentVariables.Add("Whisparr_Movie_Overview", movie.MovieMetadata.Value.Overview);
            environmentVariables.Add("Whisparr_MovieFile_Id", movieFile.Id.ToString());
            environmentVariables.Add("Whisparr_MovieFile_RelativePath", movieFile.RelativePath);
            environmentVariables.Add("Whisparr_MovieFile_Path", Path.Combine(movie.Path, movieFile.RelativePath));
            environmentVariables.Add("Whisparr_MovieFile_Quality", movieFile.Quality.Quality.Name);
            environmentVariables.Add("Whisparr_MovieFile_QualityVersion", movieFile.Quality.Revision.Version.ToString());
            environmentVariables.Add("Whisparr_MovieFile_ReleaseGroup", movieFile.ReleaseGroup ?? string.Empty);
            environmentVariables.Add("Whisparr_MovieFile_SceneName", movieFile.SceneName ?? string.Empty);

            environmentVariables.Add("Whisparr_Download_Client", downloadClientInfo?.Name ?? string.Empty);
            environmentVariables.Add("Whisparr_Download_Client_Type", downloadClientInfo?.Type ?? string.Empty);
            environmentVariables.Add("Whisparr_Download_Id", downloadId ?? string.Empty);
            environmentVariables.Add("Whisparr_MovieFile_MediaInfo_AudioChannels", MediaInfoFormatter.FormatAudioChannels(localMovie.MediaInfo).ToString());
            environmentVariables.Add("Whisparr_MovieFile_MediaInfo_AudioCodec", MediaInfoFormatter.FormatAudioCodec(movieFile.MediaInfo, null));
            environmentVariables.Add("Whisparr_MovieFile_MediaInfo_AudioLanguages", movieFile.MediaInfo.AudioLanguages.Distinct().ConcatToString(" / "));
            environmentVariables.Add("Whisparr_MovieFile_MediaInfo_Languages", movieFile.MediaInfo.AudioLanguages.ConcatToString(" / "));
            environmentVariables.Add("Whisparr_MovieFile_MediaInfo_Height", movieFile.MediaInfo.Height.ToString());
            environmentVariables.Add("Whisparr_MovieFile_MediaInfo_Width", movieFile.MediaInfo.Width.ToString());
            environmentVariables.Add("Whisparr_MovieFile_MediaInfo_Subtitles", movieFile.MediaInfo.Subtitles.ConcatToString(" / "));
            environmentVariables.Add("Whisparr_MovieFile_MediaInfo_VideoCodec", MediaInfoFormatter.FormatVideoCodec(movieFile.MediaInfo, null));
            environmentVariables.Add("Whisparr_MovieFile_MediaInfo_VideoDynamicRangeType", MediaInfoFormatter.FormatVideoDynamicRangeType(movieFile.MediaInfo));

            environmentVariables.Add("Whisparr_MovieFile_CustomFormat", string.Join("|", localMovie.CustomFormats));
            environmentVariables.Add("Whisparr_MovieFile_CustomFormatScore", localMovie.CustomFormatScore.ToString());

            if (oldFiles.Any())
            {
                environmentVariables.Add("Whisparr_DeletedRelativePaths", string.Join("|", oldFiles.Select(e => e.RelativePath)));
                environmentVariables.Add("Whisparr_DeletedPaths", string.Join("|", oldFiles.Select(e => Path.Combine(movie.Path, e.RelativePath))));
                environmentVariables.Add("Whisparr_DeletedDateAdded", string.Join("|", oldFiles.Select(e => e.DateAdded)));
            }

            _logger.Debug("Executing external script: {0}", _configService.ScriptImportPath);

            var processOutput = _processProvider.StartAndCapture(_configService.ScriptImportPath, $"\"{sourcePath}\" \"{destinationFilePath}\"", environmentVariables);

            _logger.Debug("Executed external script: {0} - Status: {1}", _configService.ScriptImportPath, processOutput.ExitCode);
            _logger.Debug("Script Output: \r\n{0}", string.Join("\r\n", processOutput.Lines));

            switch (processOutput.ExitCode)
            {
                case 0: // Copy complete
                    return ScriptImportDecision.MoveComplete;
                case 2: // Copy complete, file potentially changed, should try renaming again
                    movieFile.MediaInfo = _videoFileInfoReader.GetMediaInfo(destinationFilePath);
                    movieFile.Path = null;
                    return ScriptImportDecision.RenameRequested;
                case 3: // Let Whisparr handle it
                    return ScriptImportDecision.DeferMove;
                default: // Error, fail to import
                    throw new ScriptImportException("Moving with script failed! Exit code {0}", processOutput.ExitCode);
            }
        }
    }
}
