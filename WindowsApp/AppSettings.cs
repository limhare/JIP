using System.Collections.Generic;

namespace instplayer;

internal record AppSettings
{
	public double Volume { get; init; } = 1.0;

	public double Pitch { get; init; }

	public double Tempo { get; init; }

	public bool ShowPlaylist { get; init; } = true;

	public bool ShowLibrary { get; init; }

	public bool ShowLyrics { get; init; }

	public bool RepeatOne { get; init; }

	public bool RepeatAll { get; init; }

	public bool Shuffle { get; init; }

	public List<string> PlaylistFiles { get; init; } = new List<string>();

	public List<string> LibraryFolders { get; init; } = new List<string>();

	public string RecordingBasePath { get; init; } = "";

	public int LyricsFontSize { get; init; } = 14;

	public string OutputDeviceId { get; init; } = "";

	public int MicDeviceNumber { get; init; }

	public List<string> DownloadedFiles { get; init; } = new List<string>();
}
