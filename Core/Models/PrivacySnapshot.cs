namespace HyprNetShell.Core.Models;

public sealed record PrivacySnapshot(
    IReadOnlyList<string> ScreenRecordingApplications,
    IReadOnlyList<string> MicrophoneApplications,
    IReadOnlyList<string> CameraApplications)
{
    public static PrivacySnapshot Empty { get; } = new([], [], []);

    public bool IsScreenRecording => ScreenRecordingApplications.Count > 0;
    public bool IsMicrophoneInUse => MicrophoneApplications.Count > 0;
    public bool IsCameraInUse => CameraApplications.Count > 0;
    public bool IsActive => IsScreenRecording || IsMicrophoneInUse || IsCameraInUse;
}
