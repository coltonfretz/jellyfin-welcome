using MediaBrowser.Model.Users;

namespace Jellyfin.Plugin.Welcome.Services;

/// <summary>
/// What an invited account can do when no template user is set: watch everything incl.
/// Live TV, download, SyncPlay, cast to shared devices; no deleting, no managing, no
/// remote-controlling other people. Hidden from the login screen's user list. (Matches
/// the hand-made accounts on the server this was written for. To give new accounts
/// something else, set a template user in the plugin settings.)
/// </summary>
public static class FriendPolicy
{
    public static void Apply(UserPolicy p)
    {
        p.IsAdministrator = false;
        p.IsHidden = true;
        p.IsDisabled = false;
        p.EnableAllFolders = true;
        p.EnableAllChannels = true;
        p.EnableAllDevices = true;
        p.EnableMediaPlayback = true;
        p.EnableAudioPlaybackTranscoding = true;
        p.EnableVideoPlaybackTranscoding = true;
        p.EnablePlaybackRemuxing = true;
        p.EnableRemoteAccess = true;
        p.EnableLiveTvAccess = true;
        p.EnableLiveTvManagement = false;
        p.EnableContentDeletion = false;
        p.EnableContentDownloading = true;
        p.EnableCollectionManagement = false;
        p.EnableSubtitleManagement = false;
        p.EnableLyricManagement = false;
        p.EnableRemoteControlOfOtherUsers = false;
        p.EnableSharedDeviceControl = true;
        p.EnablePublicSharing = true;
        // The enum's namespace moved between Jellyfin versions; resolve it from the property.
        var syncPlay = p.GetType().GetProperty(nameof(UserPolicy.SyncPlayAccess))!;
        syncPlay.SetValue(p, Enum.Parse(syncPlay.PropertyType, "CreateAndJoinGroups"));
    }
}
