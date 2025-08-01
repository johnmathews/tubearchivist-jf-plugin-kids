using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.TubeArchivistMetadataKids.Configuration;
using Jellyfin.Plugin.TubeArchivistMetadataKids.Tasks;
using Jellyfin.Plugin.TubeArchivistMetadataKids.TubeArchivist;
using Jellyfin.Plugin.TubeArchivistMetadataKids.Utilities;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TubeArchivistMetadataKids
{
    /// <summary>
    /// The main plugin.
    /// </summary>
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        private readonly IUserManager _userManager;
        private readonly IUserDataManager _userDataManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="Plugin"/> class.
        /// </summary>
        /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
        /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
        /// <param name="logger">Instance of the <see cref="ILogger"/> interface.</param>
        /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
        /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
        /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
        /// <param name="userDataManager">Instance of the <see cref="IUserDataManager"/> interface.</param>
        public Plugin(
            IApplicationPaths applicationPaths,
            IXmlSerializer xmlSerializer,
            ILogger<Plugin> logger,
            ISessionManager sessionManager,
            ILibraryManager libraryManager,
            IUserManager userManager,
            IUserDataManager userDataManager)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
            Logger = logger;
            HttpClientHandler handler = new HttpClientHandler();
            handler.AllowAutoRedirect = false;
            handler.CheckCertificateRevocationList = true;
            HttpClient = new HttpClient(handler);
            UpdateAuthorizationHeader(Configuration.TubeArchivistApiKey);

            SessionManager = sessionManager;
            sessionManager.PlaybackProgress += OnPlaybackProgress;
            LibraryManager = libraryManager;
            _userManager = userManager;
            _userDataManager = userDataManager;
            userDataManager.UserDataSaved += OnWatchedStatusChange;

            logger.LogInformation("{Message}", "Collection display name: " + Instance?.Configuration.CollectionTitle);
            logger.LogInformation("{Message}", "TubeArchivist API URL: " + Instance?.Configuration.TubeArchivistUrl);
            logger.LogInformation("{Message}", "Pinging TubeArchivist API...");
        }

        /// <inheritdoc />
        public override string Name => Constants.PluginName;

        /// <inheritdoc />
        public override Guid Id => Guid.Parse(Constants.PluginGuid);

        /// <summary>
        /// Gets the current plugin instance.
        /// </summary>
        public static Plugin? Instance { get; private set; }

        /// <summary>
        /// Gets the logger instance used globally by the plugin.
        /// </summary>
        public ILogger<Plugin> Logger { get; }

        /// <summary>
        /// Gets the HTTP client used globally by the plugin.
        /// </summary>
        public HttpClient HttpClient { get; }

        /// <summary>
        /// Gets the SessionManager used globally by the plugin.
        /// </summary>
        public ISessionManager SessionManager { get; }

        /// <summary>
        /// Gets the LibraryManager used globally by the plugin.
        /// </summary>
        public ILibraryManager LibraryManager { get; }

        /// <inheritdoc />
        public IEnumerable<PluginPageInfo> GetPages() => new[]
        {
            new PluginPageInfo
            {
                Name = this.Name,
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace)
            }
        };

        /// <summary>
        /// Updates the HTTP client's Authorization header with the current API key.
        /// </summary>
        /// <param name="apiKey">TubeArchivist API key.</param>
        public void UpdateAuthorizationHeader(string apiKey)
        {
            if (!string.IsNullOrEmpty(apiKey))
            {
                HttpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Token", apiKey);
                Logger.LogInformation("{Message}", "Updated Authorization header with API key");
            }
            else
            {
                Logger.LogWarning("{Message}", "No TubeArchivist API key configured");
            }
        }

        /// <summary>
        /// Logs the TubeArchivist API connection status.
        /// </summary>
        public void LogTAApiConnectionStatus()
        {
            Logger.LogInformation("{Message}", "Ping...");

            var api = TubeArchivistApi.GetInstance();
            Task.Run(() => api.Ping()).ContinueWith(
                task =>
                {
                    if (task.Result != null)
                    {
                        Logger.LogInformation("{Message}", "TubeArchivist API said: " + task.Result.Response + "!");
                    }
                    else
                    {
                        Logger.LogCritical("{Message}", "TubeArchivist API was unreachable!");
                    }
                },
                TaskScheduler.Default);
        }

        private async void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs eventArgs)
        {
            if (eventArgs == null || eventArgs.Item.Id == Guid.Empty)
            {
                Logger.LogDebug("Skipping progress synchronization: PlaybackProgress event triggered with null or empty Guid.");
                return;
            }

            if (Instance!.Configuration.JFTASync && eventArgs.Users.Any(u => Instance!.Configuration.JFUsernameFrom.Equals(u.Username, StringComparison.Ordinal)))
            {
                BaseItem? season = LibraryManager.GetItemById(eventArgs.Item.ParentId);
                BaseItem? channel = LibraryManager.GetItemById(season!.ParentId);
                BaseItem? collection = LibraryManager.GetItemById(channel!.ParentId);
                if (collection?.Name.ToLower(CultureInfo.CurrentCulture) == Instance?.Configuration.CollectionTitle.ToLower(CultureInfo.CurrentCulture) && eventArgs.PlaybackPositionTicks != null)
                {
                    long progress = (long)eventArgs.PlaybackPositionTicks / TimeSpan.TicksPerSecond;
                    var videoId = Utils.GetVideoNameFromPath(eventArgs.Item.Path);
                    var statusCode = await TubeArchivistApi.GetInstance().SetProgress(videoId, progress).ConfigureAwait(true);
                    if (statusCode != System.Net.HttpStatusCode.OK)
                    {
                        Logger.LogCritical("{Message}", $"POST /video/{videoId}/progress returned {statusCode} for video {eventArgs.Item.Name} with progress {progress} seconds");
                    }
                }
            }
        }

        private async void OnWatchedStatusChange(object? sender, UserDataSaveEventArgs eventArgs)
        {
            if (eventArgs == null || eventArgs.Item.Id == Guid.Empty)
            {
                Logger.LogDebug("Skipping watched status synchronization: WatchedStatusChange event triggered with null or empty Guid.");
                return;
            }

            var user = _userManager.GetUserById(eventArgs.UserId);
            if (Configuration.JFTASync && user != null && Configuration.GetJFUsernamesToArray().Contains(user!.Username))
            {
                var isPlayed = eventArgs.Item.IsPlayed(user);
                Logger.LogDebug("User {UserId} changed watched status to {Status} for the item {ItemName}", eventArgs.UserId, isPlayed, eventArgs.Item.Name);
                string itemYTId;
                try
                {
                    if (eventArgs.Item is Series)
                    {
                        itemYTId = Utils.GetChannelNameFromPath(eventArgs.Item.Path);
                    }
                    else if (eventArgs.Item is Episode)
                    {
                        itemYTId = Utils.GetVideoNameFromPath(eventArgs.Item.Path);
                    }
                    else
                    {
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error while processing item path: {ItemPath}", eventArgs.Item.Path ?? "null");
                    return;
                }

                try
                {
                    var statusCode = await TubeArchivistApi.GetInstance().SetWatchedStatus(itemYTId, isPlayed).ConfigureAwait(true);
                    if (statusCode != System.Net.HttpStatusCode.OK)
                    {
                        Logger.LogCritical("POST /watched returned {StatusCode} for item {ItemName} ({VideoYTId}) with watched status {IsPlayed}", statusCode, eventArgs.Item.Name, itemYTId, isPlayed);
                    }

                    if (eventArgs.Item is Episode)
                    {
                        var progress = _userDataManager.GetUserData(user, eventArgs.Item).PlaybackPositionTicks / TimeSpan.TicksPerSecond;
                        var videoId = Utils.GetVideoNameFromPath(eventArgs.Item.Path);
                        statusCode = await TubeArchivistApi.GetInstance().SetProgress(videoId, progress).ConfigureAwait(true);
                        if (statusCode != System.Net.HttpStatusCode.OK)
                        {
                            Logger.LogCritical("{Message}", $"POST /video/{videoId}/progress returned {statusCode} for video {eventArgs.Item.Name} with progress {progress} seconds");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogCritical("An exception occurred while calling POST /watched for item {ItemName} ({VideoYTId}) with watched status {IsPlayed}: {ExceptionMessage}", eventArgs.Item.Name, itemYTId, isPlayed, ex.Message);
                }
            }
        }
    }
}
