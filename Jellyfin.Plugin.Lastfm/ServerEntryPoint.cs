namespace Jellyfin.Plugin.Lastfm
{
    using Api;
    using MediaBrowser.Controller.Entities.Audio;
    using MediaBrowser.Controller.Library;
    using MediaBrowser.Controller.Session;
    using MediaBrowser.Model.Entities;
    using Jellyfin.Data.Enums;
    using Models;
    using System.Linq;
    using System.Net.Http;
    using System.Threading.Tasks;
    using System;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Hosting;
    using System.Threading;
    using System.Collections.Concurrent;

    /// <summary>
    /// Class ServerEntryPoint
    /// </summary>
    public class ServerEntryPoint : IHostedService, IDisposable
    {

        // if the length of the song is >= 30 seconds, allow scrobble.
        private const long minimumSongLengthToScrobbleInTicks = 30 * TimeSpan.TicksPerSecond;
        // if a song reaches >= 4 minutes  in playtime, allow scrobble.
        private const long minimumPlayTimeToScrobbleInTicks = 4 * TimeSpan.TicksPerMinute;
        // if a song reaches >= 50% played, allow scrobble.
        private const double minimumPlayPercentage = 50.00;

        private readonly ISessionManager _sessionManager;
        private readonly IUserDataManager _userDataManager;
        private readonly ILibraryManager _libraryManager;

        private LastfmApiClient _apiClient;
        private readonly ILogger<ServerEntryPoint> _logger;

        private readonly ConcurrentDictionary<string, TrackedPlayback> _playbackTracker = new();

        private class TrackedPlayback
        {
            public Guid ItemId { get; set; }
            public Guid UserId { get; set; }
            public string SessionId { get; set; }
            public DateTime StartedAt { get; set; }
            public DateTime LastCheckpoint { get; set; }
            public long PlayedDurationTicks { get; set; }
            public bool IsPaused { get; set; }

            public void UpdateDuration()
            {
                var now = DateTime.UtcNow;
                if (!IsPaused)
                {
                    PlayedDurationTicks += (now - LastCheckpoint).Ticks;
                }
                LastCheckpoint = now;
            }
        }

        /// <summary>
        /// Gets the instance.
        /// </summary>
        /// <value>The instance.</value>
        public static ServerEntryPoint Instance { get; private set; }

        public ServerEntryPoint(
            ISessionManager sessionManager,
            IHttpClientFactory httpClientFactory,
            ILoggerFactory loggerFactory,
            IUserDataManager userDataManager,
            ILibraryManager libraryManager)
        {
            _logger = loggerFactory.CreateLogger<ServerEntryPoint>();

            _sessionManager = sessionManager;
            _userDataManager = userDataManager;
            _libraryManager = libraryManager;
            _apiClient = new LastfmApiClient(httpClientFactory, _logger);
            Instance = this;
        }

        private bool IsInExcludedLibrary(Audio item, LastfmUser user)
        {
            if (user.Options.ExcludedLibraries == null || user.Options.ExcludedLibraries.Length == 0)
                return false;

            var libraryFolder = _libraryManager.GetVirtualFolders()
                .FirstOrDefault(f => item.GetAncestorIds().Any(id => id.ToString() == f.ItemId));

            if (libraryFolder == null)
                return false;

            return user.Options.ExcludedLibraries.Contains(libraryFolder.ItemId);
        }

        /// <summary>
        /// Let last fm know when a user favourites or unfavourites a track
        /// </summary>
        async void UserDataSaved(object sender, UserDataSaveEventArgs e)
        {
            // We only care about audio
            if (e.Item is not Audio)
                return;

            var lastfmUser = Utils.UserHelpers.GetUser(e.UserId);
            if (lastfmUser == null)
            {
                _logger.LogDebug("Could not find user");
                return;
            }

            if (string.IsNullOrWhiteSpace(lastfmUser.SessionKey))
            {
                _logger.LogInformation("No session key present, aborting");
                return;
            }

            var item = e.Item as Audio;

            // Dont do if syncing
            if (Plugin.Syncing)
                return;

            if (e.SaveReason.Equals(UserDataSaveReason.UpdateUserRating))
            {
                if (!lastfmUser.Options.SyncFavourites)
                {
                    _logger.LogDebug("{0} does not want to sync liked songs", lastfmUser.Username);
                    return;
                }
                await _apiClient.LoveTrack(item, lastfmUser, e.UserData.IsFavorite).ConfigureAwait(false);
            }

            if (e.SaveReason.Equals(UserDataSaveReason.PlaybackFinished))
            {
                if (!lastfmUser.Options.Scrobble)
                {
                    _logger.LogDebug("{0} does not want to scrobble", lastfmUser.Username);
                    return;
                }
                if (!lastfmUser.Options.AlternativeMode)
                {
                    _logger.LogDebug("{0} does not use AlternativeMode", lastfmUser.Username);
                    return;
                }

                if (item.MediaType != Jellyfin.Data.Enums.MediaType.Audio)
                {
                    _logger.LogDebug("{0} is not a music track (MediaType={1}), skipping", item.Name, item.MediaType);
                    return;
                }

                if (IsInExcludedLibrary(item, lastfmUser))
                {
                    _logger.LogDebug("{0} is in an excluded library, skipping", item.Name);
                    return;
                }

                if (string.IsNullOrWhiteSpace(item.Artists.FirstOrDefault()) || string.IsNullOrWhiteSpace(item.Name))
                {
                    _logger.LogInformation("track {0} is missing  artist ({1}) or track name ({2}) metadata. Not submitting", item.Path, item.Artists.FirstOrDefault(), item.Name);
                    return;
                }

                // Check for tracked duration if available
                var playback = _playbackTracker.Values.FirstOrDefault(p => p.ItemId == item.Id && p.UserId == e.UserId);
                if (playback != null)
                {
                    playback.UpdateDuration();
                    var playPercent = ((double)playback.PlayedDurationTicks / item.RunTimeTicks) * 100;
                    if (playPercent < minimumPlayPercentage && playback.PlayedDurationTicks < minimumPlayTimeToScrobbleInTicks)
                    {
                        _logger.LogDebug("{0} - played {1}%, Last.Fm requires minplayed={2}% . played {3} ticks of minimumPlayTimeToScrobbleInTicks ({4}), won't scrobble", item.Name, playPercent, minimumPlayPercentage, playback.PlayedDurationTicks, minimumPlayTimeToScrobbleInTicks);
                        return;
                    }
                }

                // If we don't have a tracked session (e.g. missed start event), estimate the start time
                // to avoid "Scrobbling now" issue (scrobbles must have a timestamp in the past).
                var startedAt = playback?.StartedAt ?? DateTime.UtcNow.AddTicks(-(item.RunTimeTicks ?? 0));
                await _apiClient.Scrobble(item, lastfmUser, startedAt).ConfigureAwait(false);
            }
        }


        private void PlaybackProgress(object sender, PlaybackProgressEventArgs e)
        {
            if (e.Item is not Audio || e.Session == null)
                return;

            if (_playbackTracker.TryGetValue(e.Session.Id, out var playback))
            {
                playback.UpdateDuration();
                playback.IsPaused = e.IsPaused;
            }
        }

        /// <summary>
        /// Let last.fm know when a track has finished.
        /// Playback stopped is run when a track is finished.
        /// </summary>
        private async void PlaybackStopped(object sender, PlaybackStopEventArgs e)
        {
            // We only care about audio
            if (e.Item is not Audio)
                return;

            var item = e.Item as Audio;

            if (item.MediaType != Jellyfin.Data.Enums.MediaType.Audio)
            {
                _logger.LogDebug("{0} is not a music track (MediaType={1}), skipping", item.Name, item.MediaType);
                return;
            }

            var user = e.Users.FirstOrDefault();
            if (user == null)
            {
                return;
            }

            var lastfmUser = Utils.UserHelpers.GetUser(user);
            if (lastfmUser == null)
            {
                _logger.LogDebug("Could not find last.fm user");
                return;
            }

            if (IsInExcludedLibrary(item, lastfmUser))
            {
                _logger.LogDebug("{0} is in an excluded library, skipping", item.Name);
                return;
            }

            TrackedPlayback playback = null;
            if (e.Session != null)
            {
                _playbackTracker.TryRemove(e.Session.Id, out playback);
            }

            if (playback != null)
            {
                playback.UpdateDuration();
            }

            if (e.PlaybackPositionTicks == null && playback == null)
            {
                _logger.LogDebug("Playback ticks for {0} is null and no tracked playback found", item.Name);
                return;
            }

            // Use tracked duration if available, otherwise fallback to playback position
            long playedTicks = playback?.PlayedDurationTicks ?? e.PlaybackPositionTicks ?? 0;

            // Required checkpoints before scrobbling noted at https://www.last.fm/api/scrobbling#when-is-a-scrobble-a-scrobble .
            // A track should only be scrobbled when the following conditions have been met:
            //   * The track must be longer than 30 seconds.
            //   * And the track has been played for at least half its duration, or for 4 minutes (whichever occurs earlier.)
            // is the track length greater than 30 seconds.
            if (item.RunTimeTicks < minimumSongLengthToScrobbleInTicks)
            {
                _logger.LogDebug("{0} - played {1} ticks which is less minimumSongLengthToScrobbleInTicks ({2}), won't scrobble.", item.Name, item.RunTimeTicks, minimumSongLengthToScrobbleInTicks);
                return;
            }

            // the track must have played the minimum percentage (minimumPlayPercentage = 50%) or played for atleast 4 minutes (minimumPlayTimeToScrobbleInTicks).
            var playPercent = ((double)playedTicks / item.RunTimeTicks) * 100;
            if (playPercent < minimumPlayPercentage & playedTicks < minimumPlayTimeToScrobbleInTicks)
            {
                _logger.LogDebug("{0} - played {1}%, Last.Fm requires minplayed={2}% . played {3} ticks of minimumPlayTimeToScrobbleInTicks ({4}), won't scrobble", item.Name, playPercent, minimumPlayPercentage, playedTicks, minimumPlayTimeToScrobbleInTicks);
                return;
            }

            // User doesn't want to scrobble
            if (!lastfmUser.Options.Scrobble)
            {
                _logger.LogDebug("{0} ({1}) does not want to scrobble", user.Username, lastfmUser.Username);
                return;
            }
            if (lastfmUser.Options.AlternativeMode)
            {
                _logger.LogDebug("{0} uses AlternativeMode", lastfmUser.Username);
                return;
            }

            if (string.IsNullOrWhiteSpace(lastfmUser.SessionKey))
            {
                _logger.LogInformation("No session key present, aborting");
                return;
            }

            if (string.IsNullOrWhiteSpace(item.Artists.FirstOrDefault()) || string.IsNullOrWhiteSpace(item.Name))
            {
                _logger.LogInformation("track {0} is missing  artist ({1}) or track name ({2}) metadata. Not submitting", item.Path, item.Artists.FirstOrDefault(), item.Name);
                return;
            }

            // If we don't have a tracked session, estimate the start time
            var startedAt = playback?.StartedAt ?? DateTime.UtcNow.AddTicks(-(item.RunTimeTicks ?? 0));
            await _apiClient.Scrobble(item, lastfmUser, startedAt).ConfigureAwait(false);
        }

        /// <summary>
        /// Let Last.fm know when a user has started listening to a track
        /// </summary>
        private async void PlaybackStart(object sender, PlaybackProgressEventArgs e)
        {
            // We only care about audio
            if (e.Item is not Audio)
                return;

            var user = e.Users.FirstOrDefault();
            if (user == null)
            {
                return;
            }

            var item = e.Item as Audio;
            if (item.MediaType != Jellyfin.Data.Enums.MediaType.Audio)
                return;

            var lastfmUser = Utils.UserHelpers.GetUser(user);

            if (lastfmUser != null && IsInExcludedLibrary(item, lastfmUser))
            {
                _logger.LogDebug("{0} is in an excluded library, skipping NowPlaying", item.Name);
                return;
            }

            if (e.Session != null)
            {
                var playback = new TrackedPlayback
                {
                    ItemId = item.Id,
                    UserId = user.Id,
                    SessionId = e.Session.Id,
                    StartedAt = DateTime.UtcNow,
                    LastCheckpoint = DateTime.UtcNow,
                    PlayedDurationTicks = 0,
                    IsPaused = e.IsPaused
                };
                _playbackTracker.AddOrUpdate(e.Session.Id, playback, (id, old) => playback);
            }

            if (lastfmUser == null)
            {
                _logger.LogDebug("Could not find last.fm user");
                return;
            }

            // User doesn't want to scrobble
            if (!lastfmUser.Options.Scrobble)
            {
                _logger.LogDebug("{0} ({1}) does not want to scrobble", user.Username, lastfmUser.Username);
                return;
            }

            if (string.IsNullOrWhiteSpace(lastfmUser.SessionKey))
            {
                _logger.LogInformation("No session key present, aborting");
                return;
            }

            if (string.IsNullOrWhiteSpace(item.Artists.FirstOrDefault()) || string.IsNullOrWhiteSpace(item.Name))
            {
                _logger.LogInformation("track {0} is missing artist ({1}) or track name ({2}) metadata. Not submitting", item.Path, item.Artists.FirstOrDefault(), item.Name);
                return;
            }
            await _apiClient.NowPlaying(item, lastfmUser).ConfigureAwait(false);
        }

        /// <summary>
        /// Runs this instance.
        /// </summary>
        public Task StartAsync(CancellationToken cancellationToken)
        {
            //Bind events
            _sessionManager.PlaybackStart += PlaybackStart;
            _sessionManager.PlaybackStopped += PlaybackStopped;
            _sessionManager.PlaybackProgress += PlaybackProgress;
            _userDataManager.UserDataSaved += UserDataSaved;
            return Task.CompletedTask;
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public Task StopAsync(CancellationToken cancellationToken)
        {
            // Unbind events
            _sessionManager.PlaybackStart -= PlaybackStart;
            _sessionManager.PlaybackStopped -= PlaybackStopped;
            _sessionManager.PlaybackProgress -= PlaybackProgress;
            _userDataManager.UserDataSaved -= UserDataSaved;

            // Clean up
            _apiClient = null;
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
        }
    }
}
