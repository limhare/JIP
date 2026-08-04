using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Resources;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.Lame;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SoundTouch.Net.NAudioSupport;
using TagLib;

namespace instplayer;

public partial class MainWindow : Window
{
	private class SharedReadFileAbstraction : TagLib.File.IFileAbstraction
	{
		private readonly string path;

		public string Name => path;

		public Stream ReadStream => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

		public Stream WriteStream => new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);

		public SharedReadFileAbstraction(string path)
		{
			this.path = path;
		}

		public void CloseStream(Stream stream)
		{
			stream.Dispose();
		}
	}

	private class AudioMonitorBoostProvider : IWaveProvider
	{
		private IWaveProvider _source;

		private readonly object _sourceLock = new object();

		private readonly WaveFormat _waveFormat;

		public float Volume { get; set; }

		public WaveFormat WaveFormat => _waveFormat;

		public event Action<float>? LevelUpdated;

		public AudioMonitorBoostProvider(IWaveProvider source, float volume = 1f)
		{
			_source = source;
			_waveFormat = source.WaveFormat;
			Volume = volume;
		}

		public void SetSource(IWaveProvider source)
		{
			lock (_sourceLock)
			{
				_source = source;
			}
		}

		public int Read(byte[] buffer, int offset, int count)
		{
			IWaveProvider source;
			lock (_sourceLock)
			{
				source = _source;
			}
			int num = source.Read(buffer, offset, count);
			if (num == 0)
			{
				return 0;
			}
			bool num2 = WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat;
			float volume = Volume;
			if (num2)
			{
				int num3 = num / 4;
				double num4 = 0.0;
				for (int i = 0; i < num3; i++)
				{
					int num5 = offset + i * 4;
					float num6 = BitConverter.ToSingle(buffer, num5);
					if (volume != 1f)
					{
						num6 = Math.Clamp(num6 * volume, -1f, 1f);
						byte[] bytes = BitConverter.GetBytes(num6);
						buffer[num5] = bytes[0];
						buffer[num5 + 1] = bytes[1];
						buffer[num5 + 2] = bytes[2];
						buffer[num5 + 3] = bytes[3];
					}
					num4 += (double)(num6 * num6);
				}
				this.LevelUpdated?.Invoke((float)Math.Sqrt(num4 / (double)num3));
			}
			else
			{
				int num7 = num / 2;
				double num8 = 0.0;
				for (int j = 0; j < num7; j++)
				{
					float num9 = (float)BitConverter.ToInt16(buffer, offset + j * 2) / 32768f;
					num8 += (double)(num9 * num9);
				}
				this.LevelUpdated?.Invoke((float)Math.Sqrt(num8 / (double)num7));
			}
			return num;
		}
	}

	private IWavePlayer? outputDevice;

	private SoundTouchWaveStream? soundTouchStream;

	private AudioFileReader? audioFileReader;

	private readonly DispatcherTimer progressTimer;

	private bool isSeeking;

	private readonly List<string> playlist = new List<string>();

	private int currentIndex = -1;

	private Point? playlistDragStartPos;

	private int playlistDragSourceIdx = -1;

	private bool repeatOne;

	private bool repeatAll;

	private bool shuffleMode;

	private readonly Random rng = new Random();

	private readonly Stack<int> playHistory = new Stack<int>();

	private double abPointA = -1.0;

	private double abPointB = -1.0;

	private readonly List<string> libraryFolders = new List<string>();

	private readonly List<string> libraryFiles = new List<string>();

	private readonly List<string> filteredLibraryFiles = new List<string>();

	private Point? libraryDragStart;

	private bool libPreserveSelection;

	private string[] libDragFiles = Array.Empty<string>();

	private readonly List<FileSystemWatcher> libraryWatchers = new List<FileSystemWatcher>();

	private DispatcherTimer? libraryRefreshTimer;

	private static readonly string DefaultJipPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), "JIP");

	private bool showPlaylist = true;

	private bool showLibrary;

	private bool showLyrics;

	private bool isKorean = true;

	private WaveInEvent? waveIn;

	private WaveFileWriter? waveWriter;

	private WasapiLoopbackCapture? loopbackCapture;

	private WaveFileWriter? loopbackWriter;

	private bool isRecording;

	private bool isVolumeDragging;

	private bool loopbackMode;

	private string pendingSavePath = "";

	private string recordingBasePath = "";

	private string tempMrPath = "";

	private string tempMicPath = "";

	private int mixStopCount;

	private long mrFirstTick;

	private long micFirstTick;

	private bool isYtDownloading;

	private bool lyricsEditMode;

	private int lyricsFontSize = 14;

	private string currentLyricsPath = "";

	private static readonly string SettingsPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "InstPlayer", "settings.json");

	private AudioMonitorBoostProvider? monitorBoost;

	private readonly DispatcherTimer visualTimer;

	private volatile float _instRms;

	private volatile float _micRms;

	private float _instPeak;

	private float _micPeak;

	private long _instLastTick;

	private long _micLastTick;

	private WaveInEvent? micMonitor;

	private string _outputDeviceId = "";

	private int _micDeviceNumber;

	private readonly List<string> downloadedFiles = new List<string>();

	private string? _lastClipboardUrl;

	private bool _isOfferingDownload;

	private string S(string kor, string eng)
	{
		if (!isKorean)
		{
			return eng;
		}
		return kor;
	}

	public MainWindow()
	{
		InitializeComponent();
		progressTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromMilliseconds(200.0)
		};
		progressTimer.Tick += ProgressTimer_Tick;
		visualTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromMilliseconds(16.0)
		};
		visualTimer.Tick += VisualTimer_Tick;
		visualTimer.Start();
	}

	private void Window_Loaded(object sender, RoutedEventArgs e)
	{
		try
		{
			StreamResourceInfo resourceStream = Application.GetResourceStream(new Uri("jip.ico", UriKind.Relative));
			if (resourceStream != null)
			{
				base.Icon = BitmapFrame.Create(resourceStream.Stream);
			}
		}
		catch
		{
		}
		LoadSettings();
		LyricsSplitter.AddHandler(Thumb.DragDeltaEvent, new DragDeltaEventHandler(LyricsSplitter_DragDelta));
	}

	private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
	{
		if (showLyrics)
		{
			double pixels = Math.Max(100.0, e.NewSize.Width - 640.0 - 5.0 - (double)(showPlaylist ? 320 : 0) - (double)(showLibrary ? 320 : 0));
			LyricsColumn.Width = new GridLength(pixels);
		}
	}

	private void LyricsSplitter_DragDelta(object sender, DragDeltaEventArgs e)
	{
		double val = (LyricsColumn.Width.IsAbsolute ? LyricsColumn.Width.Value : LyricsColumn.ActualWidth);
		val = Math.Max(100.0, val);
		LyricsColumn.Width = new GridLength(val);
		PlaylistColumn.Width = new GridLength(showPlaylist ? 320 : 0);
		LibraryColumn.Width = new GridLength(showLibrary ? 320 : 0);
		base.Width = 640.0 + val + 5.0 + (double)(showPlaylist ? 320 : 0) + (double)(showLibrary ? 320 : 0);
	}

	private void LoadSettings()
	{
		try
		{
			if (!System.IO.File.Exists(SettingsPath))
			{
				Directory.CreateDirectory(DefaultJipPath);
				AddLibraryFolder(DefaultJipPath);
				UpdateRegisteredFoldersPanel();
				SaveSettings();
				return;
			}
			AppSettings appSettings = JsonSerializer.Deserialize<AppSettings>(System.IO.File.ReadAllText(SettingsPath));
			if (appSettings == null)
			{
				return;
			}
			VolumeSlider.Value = appSettings.Volume;
			PitchSlider.Value = appSettings.Pitch;
			TempoSlider.Value = appSettings.Tempo;
			showPlaylist = appSettings.ShowPlaylist;
			showLibrary = appSettings.ShowLibrary;
			showLyrics = appSettings.ShowLyrics;
			repeatOne = appSettings.RepeatOne;
			repeatAll = appSettings.RepeatAll;
			shuffleMode = appSettings.Shuffle;
			UpdatePanelVisibility();
			UpdateRepeatShuffleButtons();
			recordingBasePath = appSettings.RecordingBasePath;
			_outputDeviceId = appSettings.OutputDeviceId;
			_micDeviceNumber = appSettings.MicDeviceNumber;
			lyricsFontSize = ((appSettings.LyricsFontSize > 0) ? appSettings.LyricsFontSize : 14);
			if (LyricsBox != null)
			{
				LyricsBox.FontSize = lyricsFontSize;
			}
			foreach (string item in appSettings.LibraryFolders.Where(Directory.Exists))
			{
				AddLibraryFolder(item);
			}
			UpdateRegisteredFoldersPanel();
			foreach (string item2 in appSettings.PlaylistFiles.Where(System.IO.File.Exists))
			{
				AddToPlaylistInternal(item2);
			}
			downloadedFiles.Clear();
			downloadedFiles.AddRange(appSettings.DownloadedFiles.Where(System.IO.File.Exists));
		}
		catch
		{
		}
	}

	private void SaveSettings()
	{
		try
		{
			Directory.CreateDirectory(System.IO.Path.GetDirectoryName(SettingsPath));
			AppSettings value = new AppSettings
			{
				Volume = VolumeSlider.Value,
				Pitch = PitchSlider.Value,
				Tempo = TempoSlider.Value,
				ShowPlaylist = showPlaylist,
				ShowLibrary = showLibrary,
				ShowLyrics = showLyrics,
				RepeatOne = repeatOne,
				RepeatAll = repeatAll,
				Shuffle = shuffleMode,
				PlaylistFiles = new List<string>(playlist),
				LibraryFolders = new List<string>(libraryFolders),
				RecordingBasePath = recordingBasePath,
				LyricsFontSize = lyricsFontSize,
				OutputDeviceId = _outputDeviceId,
				MicDeviceNumber = _micDeviceNumber,
				DownloadedFiles = new List<string>(downloadedFiles)
			};
			System.IO.File.WriteAllText(SettingsPath, JsonSerializer.Serialize(value, new JsonSerializerOptions
			{
				WriteIndented = true
			}));
		}
		catch
		{
		}
	}

	private void UpdatePanelVisibility()
	{
		PlaylistColumn.Width = (showPlaylist ? new GridLength(320.0) : new GridLength(0.0));
		LibraryColumn.Width = (showLibrary ? new GridLength(320.0) : new GridLength(0.0));
		LyricsColumn.Width = (showLyrics ? new GridLength(320.0) : new GridLength(0.0));
		LyricsSplitterColumn.Width = (showLyrics ? new GridLength(5.0) : new GridLength(0.0));
		LyricsSplitter.Visibility = ((!showLyrics) ? Visibility.Collapsed : Visibility.Visible);
		TogglePlaylistBtn.Style = (showPlaylist ? ((Style)base.Resources["ActiveButton"]) : ((Style)base.Resources["DarkButton"]));
		ToggleLibraryBtn.Style = (showLibrary ? ((Style)base.Resources["ActiveButton"]) : ((Style)base.Resources["DarkButton"]));
		ToggleLyricsBtn.Style = (showLyrics ? ((Style)base.Resources["ActiveButton"]) : ((Style)base.Resources["DarkButton"]));
		base.Width = 710 + (showPlaylist ? 320 : 0) + (showLibrary ? 320 : 0) + (showLyrics ? 325 : 0);
	}

	private void TogglePlaylistBtn_Click(object sender, RoutedEventArgs e)
	{
		showPlaylist = !showPlaylist;
		UpdatePanelVisibility();
	}

	private void ToggleLibraryBtn_Click(object sender, RoutedEventArgs e)
	{
		showLibrary = !showLibrary;
		UpdatePanelVisibility();
	}

	private void ToggleLyricsBtn_Click(object sender, RoutedEventArgs e)
	{
		showLyrics = !showLyrics;
		UpdatePanelVisibility();
	}

	private void LangKorBtn_Click(object sender, RoutedEventArgs e)
	{
		isKorean = true;
		ApplyLanguage();
	}

	private void LangEngBtn_Click(object sender, RoutedEventArgs e)
	{
		isKorean = false;
		ApplyLanguage();
	}

	private void ApplyLanguage()
	{
		LangKorBtn.Style = (isKorean ? ((Style)base.Resources["ActiveButton"]) : ((Style)base.Resources["DarkButton"]));
		LangEngBtn.Style = (isKorean ? ((Style)base.Resources["DarkButton"]) : ((Style)base.Resources["ActiveButton"]));
		VolumeLabelText.Text = S("볼륨", "Vol");
		PitchLabelText.Text = S("음정", "Pitch");
		TempoLabelText.Text = S("속도", "Tempo");
		PitchResetBtn.Content = S("원키", "Reset");
		TempoResetBtn.Content = S("원속도", "Reset");
		AbSetABtn.Content = S("A 지점", "Set A");
		AbSetBBtn.Content = S("B 지점", "Set B");
		AbClearBtn.Content = S("해제", "Clear");
		ExportBtn.Content = S("내보내기", "Export");
		if (!isRecording)
		{
			RecordBtn.Content = S("● 녹음", "● Rec");
			LoopbackBtn.Content = (loopbackMode ? S("반주+마이크", "Inst+Mic") : S("마이크", "Mic"));
		}
		RecordPathBtn.ToolTip = S("녹음 저장 경로 설정", "Set recording save path");
		SyncToggleLabel.Text = S("플레이 연동", "Play Sync");
		SyncPlayRecordToggle.ToolTip = S("녹음 시작 시 자동 재생, 녹음 종료 시 자동 정지", "Auto play on record start, auto stop on record end");
		ToggleLyricsBtn.Content = S("가사", "Lyrics");
		TogglePlaylistBtn.Content = S("플레이리스트", "Playlist");
		ToggleLibraryBtn.Content = S("보관함", "Library");
		LyricsHeaderText.Text = S("가사", "Lyrics");
		PlaylistHeaderText.Text = S("플레이리스트", "Playlist");
		LibraryHeaderText.Text = S("보관함", "Library");
		RepeatOneBtn.Content = "1\ud83d\udd02";
		RepeatOneBtn.ToolTip = S("1곡 반복", "Repeat One");
		ShuffleBtn.Content = "\ud83d\udd00";
		ShuffleBtn.ToolTip = S("셔플", "Shuffle");
		RepeatAllBtn.Content = "\ud83d\udd01";
		RepeatAllBtn.ToolTip = S("전체 반복", "Repeat All");
		SavePlaylistBtn.Content = S("저장", "Save");
		LoadPlaylistBtn.Content = S("불러오기", "Load");
		OpenFileBtn.Content = S("파일 열기", "Open File");
		AddFolderBtn.Content = S("폴더 추가", "Add Folder");
		LibraryRefreshBtn.ToolTip = S("파일 목록 갱신", "Refresh file list");
		LibraryFoldersHeaderText.Text = S("등록된 폴더", "Registered Folders");
		RecordPathBtn.ToolTip = S("최근 녹음 폴더 열기", "Open last recording folder");
		EditLyricsBtn.Content = S("편집", "Edit");
		SaveLyricsBtn.Content = S("저장", "Save");
		if (!isYtDownloading)
		{
			YtDownloadBtn.Content = S("↓ 다운로드", "↓ Download");
		}
		YtLabelText.Text = S("유튜브 추출 :", "YT Extract :");
		if (NowPlayingText.Text == "재생 중인 파일 없음" || NowPlayingText.Text == "No file playing")
		{
			NowPlayingText.Text = S("재생 중인 파일 없음", "No file playing");
		}
		UpdateAbInfo();
	}

	private void HelpBtn_Click(object sender, RoutedEventArgs e)
	{
		Window obj = new Window
		{
			Title = S("InstPlayer 사용법", "InstPlayer Help"),
			Width = 520.0,
			Height = 620.0,
			Background = new SolidColorBrush(Color.FromRgb(42, 42, 42)),
			WindowStartupLocation = WindowStartupLocation.CenterOwner,
			Owner = this,
			ResizeMode = ResizeMode.CanResize
		};
		string text = (isKorean ? "\r\n[ 기본 기능 ]\r\n\r\n▶ 파일 재생\r\n  • 파일 열기 버튼 또는 파일을 플레이리스트에 드래그 앤 드롭\r\n  • 플레이리스트에서 항목 더블클릭으로 재생\r\n  • ⏮ 이전곡 / ▶ 재생·일시정지 / ⏹ 정지 / ⏭ 다음곡\r\n\r\n▶ 재생 바\r\n  • 클릭 또는 드래그로 재생 위치 이동\r\n\r\n▶ 볼륨\r\n  • 클릭 또는 드래그로 음량 조절\r\n  • 마우스 휠로도 조절 가능\r\n\r\n▶ 음정 (Pitch)\r\n  • -12 ~ +12 반음 조절 / -·+ 버튼 또는 슬라이더 조작\r\n  • 원키: 원래 음정으로 초기화\r\n\r\n▶ 속도 (Tempo)\r\n  • -50% ~ +100% 조절 (음정 변환 없이 속도만 변경)\r\n  • 원속도: 원래 속도로 초기화\r\n\r\n──────────────────────────────────\r\n\r\n[ 단축키 ]\r\n\r\n  Space     재생 / 일시정지\r\n  ←  →     이전 곡 / 다음 곡\r\n  W / S     음정 올림 / 내림\r\n  D / A     속도 올림 / 내림\r\n  Q         A 지점 설정\r\n  E         B 지점 설정\r\n  R         구간 반복 해제\r\n  Z         원키 (음정 초기화)\r\n  C         원속도 (속도 초기화)\r\n  X         원키 + 원속도 동시 초기화\r\n\r\n──────────────────────────────────\r\n\r\n[ 부가 기능 ]\r\n\r\n▶ A-B 구간 반복\r\n  • A 지점 버튼: 반복 시작 지점 설정\r\n  • B 지점 버튼: 반복 끝 지점 설정\r\n  • 구간이 지정되면 해당 구간을 자동 반복\r\n  • 해제 버튼: A-B 포인트 초기화\r\n\r\n▶ 내보내기\r\n  • 현재 음정·속도가 적용된 상태로 WAV 또는 MP3로 저장\r\n\r\n▶ 녹음\r\n  • ● 녹음 버튼으로 녹음 시작/정지\r\n  • \ud83d\udcc1 버튼으로 저장 기본 경로 지정\r\n    (미지정 시 내 음악 폴더 → 날짜 하위폴더에 자동 저장)\r\n  • 반주+마이크(ON): 시스템 출력(반주) + 마이크를 혼합 녹음\r\n  • 마이크(OFF): 마이크 입력만 녹음\r\n  • 동기화(ON): 녹음 시작 시 자동 재생, 녹음 종료 시 자동 정지\r\n\r\n▶ 플레이리스트\r\n  • 파일 드래그 앤 드롭으로 추가\r\n  • 항목 드래그로 순서 변경\r\n  • Delete 키로 항목 삭제\r\n  • 저장/불러오기: 플레이리스트 파일(.m3u) 관리\r\n  • 1곡 / 셔플 / 전체 반복 모드 지원\r\n\r\n▶ 보관함\r\n  • 폴더 추가 후 음악 파일 관리\r\n  • 검색창으로 파일 필터링\r\n  • 항목 더블클릭 또는 플레이리스트로 드래그 앤 드롭\r\n\r\n▶ 가사\r\n  • 곡과 같은 이름의 .lrc 또는 .txt 파일 자동 로드\r\n  • A-/A/A+ 버튼으로 글자 크기 조절\r\n  • 편집 모드에서 가사 직접 수정 후 저장\r\n  • 가사 패널 오른쪽 경계를 드래그해 너비 조절 가능 (최대 640px)\r\n  • 설정에서 기본 글자 크기 변경 가능\r\n\r\n──────────────────────────────────\r\n\r\n[ YouTube 음원 추출 ]\r\n\r\n▶ YouTube 다운로더\r\n  • 플레이어 하단 URL 입력창에 YouTube 링크 붙여넣기\r\n  • 오디오 포맷 선택: m4a / mp3 / opus / flac\r\n  • ↓ 다운로드 버튼 클릭\r\n  • 보관함 첫 번째 폴더에 자동 저장\r\n  • 완료 시 보관함 갱신 + 플레이리스트 추가 + 자동 재생\r\n  • 사전 준비: yt-dlp.exe를 앱 폴더 또는 PATH에 배치\r\n    (https://github.com/yt-dlp/yt-dlp/releases)\r\n" : "\r\n[ Basic Features ]\r\n\r\n▶ File Playback\r\n  • Click 'Open File' or drag & drop files into the playlist\r\n  • Double-click a playlist item to play\r\n  • ⏮ Prev / ▶ Play·Pause / ⏹ Stop / ⏭ Next\r\n\r\n▶ Progress Bar\r\n  • Click or drag to seek to a position\r\n\r\n▶ Volume\r\n  • Click or drag to adjust volume\r\n  • Mouse wheel also adjusts volume\r\n\r\n▶ Pitch\r\n  • Adjust -12 ~ +12 semitones via slider or -·+ buttons\r\n  • Reset: restore original pitch\r\n\r\n▶ Tempo\r\n  • Adjust -50% ~ +100% (speed only, no pitch change)\r\n  • Reset: restore original speed\r\n\r\n──────────────────────────────────\r\n\r\n[ Keyboard Shortcuts ]\r\n\r\n  Space     Play / Pause\r\n  ←  →     Previous / Next track\r\n  W / S     Pitch up / down\r\n  D / A     Tempo up / down\r\n  Q         Set A point\r\n  E         Set B point\r\n  R         Clear A-B loop\r\n  Z         Reset pitch\r\n  C         Reset tempo\r\n  X         Reset pitch + tempo\r\n\r\n──────────────────────────────────\r\n\r\n[ Additional Features ]\r\n\r\n▶ A-B Loop\r\n  • Set A button: mark loop start point\r\n  • Set B button: mark loop end point\r\n  • Playback automatically loops between A and B\r\n  • Clear button: reset A-B points\r\n\r\n▶ Export\r\n  • Save with current pitch/tempo applied as WAV or MP3\r\n\r\n▶ Recording\r\n  • Click ● Rec to start/stop recording\r\n  • \ud83d\udcc1 button to set default save folder\r\n    (defaults to My Music → date subfolder)\r\n  • Inst+Mic (ON): mix system output (backing track) + microphone\r\n  • Mic (OFF): record microphone input only\r\n  • Sync (ON): auto play on record start, auto stop on record end\r\n\r\n▶ Playlist\r\n  • Drag & drop files to add\r\n  • Drag items to reorder\r\n  • Delete key to remove items\r\n  • Save/Load playlist files (.m3u)\r\n  • ×1 / Shuffle / All repeat modes\r\n\r\n▶ Library\r\n  • Add folders to manage audio files\r\n  • Filter files using the search box\r\n  • Double-click or drag to playlist\r\n\r\n▶ Lyrics\r\n  • Auto-loads .lrc or .txt file with the same name as the track\r\n  • A-/A/A+ buttons to adjust font size\r\n  • Edit lyrics in edit mode and save\r\n  • Drag the right edge of the lyrics panel to resize (max 640px)\r\n  • Default font size configurable in Settings\r\n\r\n──────────────────────────────────\r\n\r\n[ YouTube Audio Extractor ]\r\n\r\n▶ YouTube Downloader\r\n  • Paste a YouTube link into the URL input at the bottom\r\n  • Select audio format: m4a / mp3 / opus / flac\r\n  • Click ↓ Download\r\n  • File is saved to the first registered library folder\r\n  • On completion: library refresh + playlist add + auto play\r\n  • Prerequisite: place yt-dlp.exe in the app folder or PATH\r\n    (https://github.com/yt-dlp/yt-dlp/releases)\r\n");
		ScrollViewer scrollViewer = new ScrollViewer
		{
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto
		};
		StackPanel stackPanel = new StackPanel();
		TextBlock element = new TextBlock
		{
			Text = text.Trim(),
			Foreground = new SolidColorBrush(Color.FromRgb(204, 204, 204)),
			FontSize = 12.0,
			FontFamily = new FontFamily("Consolas, Malgun Gothic"),
			Padding = new Thickness(20.0, 20.0, 20.0, 8.0),
			TextWrapping = TextWrapping.Wrap,
			LineHeight = 20.0
		};
		stackPanel.Children.Add(element);
		stackPanel.Children.Add(new Border
		{
			Height = 1.0,
			Background = new SolidColorBrush(Color.FromRgb(51, 51, 51)),
			Margin = new Thickness(20.0, 0.0, 20.0, 12.0)
		});
		StackPanel stackPanel2 = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Margin = new Thickness(20.0, 0.0, 20.0, 6.0)
		};
		stackPanel2.Children.Add(new TextBlock
		{
			Text = S("문의 : ", "Contact : "),
			Foreground = new SolidColorBrush(Color.FromRgb(119, 119, 119)),
			FontSize = 11.0,
			VerticalAlignment = VerticalAlignment.Center
		});
		TextBlock helpEmail = new TextBlock
		{
			Text = "mizhashi@naver.com",
			Foreground = new SolidColorBrush(Color.FromRgb(78, 201, 176)),
			FontSize = 11.0,
			VerticalAlignment = VerticalAlignment.Center,
			Cursor = Cursors.Hand,
			TextDecorations = TextDecorations.Underline,
			ToolTip = S("클릭하면 클립보드에 복사됩니다", "Click to copy to clipboard")
		};
		helpEmail.MouseLeftButtonDown += delegate
		{
			Clipboard.SetText("mizhashi@naver.com");
			Brush orig = helpEmail.Foreground;
			helpEmail.Foreground = new SolidColorBrush(Colors.White);
			DispatcherTimer t = new DispatcherTimer
			{
				Interval = TimeSpan.FromMilliseconds(600.0)
			};
			t.Tick += delegate
			{
				helpEmail.Foreground = orig;
				t.Stop();
			};
			t.Start();
		};
		stackPanel2.Children.Add(helpEmail);
		stackPanel2.Children.Add(new TextBlock
		{
			Text = S(" (클릭 → 복사)", " (click → copy)"),
			Foreground = new SolidColorBrush(Color.FromRgb(85, 85, 85)),
			FontSize = 10.0,
			VerticalAlignment = VerticalAlignment.Center
		});
		stackPanel.Children.Add(stackPanel2);
		StackPanel stackPanel3 = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Margin = new Thickness(20.0, 0.0, 20.0, 16.0)
		};
		stackPanel3.Children.Add(new TextBlock
		{
			Text = S("카카오톡 ID : ", "KakaoTalk ID : "),
			Foreground = new SolidColorBrush(Color.FromRgb(119, 119, 119)),
			FontSize = 11.0,
			VerticalAlignment = VerticalAlignment.Center
		});
		TextBlock helpKakao = new TextBlock
		{
			Text = "mizhashi",
			Foreground = new SolidColorBrush(Color.FromRgb(78, 201, 176)),
			FontSize = 11.0,
			VerticalAlignment = VerticalAlignment.Center,
			Cursor = Cursors.Hand,
			TextDecorations = TextDecorations.Underline,
			ToolTip = S("클릭하면 클립보드에 복사됩니다", "Click to copy to clipboard")
		};
		helpKakao.MouseLeftButtonDown += delegate
		{
			Clipboard.SetText("mizhashi");
			Brush orig = helpKakao.Foreground;
			helpKakao.Foreground = new SolidColorBrush(Colors.White);
			DispatcherTimer t = new DispatcherTimer
			{
				Interval = TimeSpan.FromMilliseconds(600.0)
			};
			t.Tick += delegate
			{
				helpKakao.Foreground = orig;
				t.Stop();
			};
			t.Start();
		};
		stackPanel3.Children.Add(helpKakao);
		stackPanel3.Children.Add(new TextBlock
		{
			Text = S(" (클릭 → 복사)", " (click → copy)"),
			Foreground = new SolidColorBrush(Color.FromRgb(85, 85, 85)),
			FontSize = 10.0,
			VerticalAlignment = VerticalAlignment.Center
		});
		stackPanel.Children.Add(stackPanel3);
		scrollViewer.Content = stackPanel;
		obj.Content = scrollViewer;
		obj.ShowDialog();
	}

	private void UpdateRepeatShuffleButtons()
	{
		RepeatOneBtn.Style = (repeatOne ? ((Style)base.Resources["ActiveButton"]) : ((Style)base.Resources["DarkButton"]));
		ShuffleBtn.Style = (shuffleMode ? ((Style)base.Resources["ActiveButton"]) : ((Style)base.Resources["DarkButton"]));
		RepeatAllBtn.Style = (repeatAll ? ((Style)base.Resources["ActiveButton"]) : ((Style)base.Resources["DarkButton"]));
	}

	private void RepeatOneBtn_Click(object sender, RoutedEventArgs e)
	{
		repeatOne = !repeatOne;
		if (repeatOne)
		{
			repeatAll = false;
			shuffleMode = false;
		}
		UpdateRepeatShuffleButtons();
	}

	private void ShuffleBtn_Click(object sender, RoutedEventArgs e)
	{
		shuffleMode = !shuffleMode;
		if (shuffleMode)
		{
			repeatOne = false;
		}
		UpdateRepeatShuffleButtons();
	}

	private void RepeatAllBtn_Click(object sender, RoutedEventArgs e)
	{
		repeatAll = !repeatAll;
		if (repeatAll)
		{
			repeatOne = false;
		}
		UpdateRepeatShuffleButtons();
	}

	private void SavePlaylistBtn_Click(object sender, RoutedEventArgs e)
	{
		if (playlist.Count != 0)
		{
			SaveFileDialog saveFileDialog = new SaveFileDialog
			{
				Filter = "M3U 플레이리스트|*.m3u",
				FileName = "playlist"
			};
			if (saveFileDialog.ShowDialog() == true)
			{
				System.IO.File.WriteAllLines(saveFileDialog.FileName, playlist, Encoding.UTF8);
			}
		}
	}

	private void LoadPlaylistBtn_Click(object sender, RoutedEventArgs e)
	{
		OpenFileDialog openFileDialog = new OpenFileDialog
		{
			Filter = "M3U 플레이리스트|*.m3u|모든 파일|*.*"
		};
		if (openFileDialog.ShowDialog() != true)
		{
			return;
		}
		string[] source = new string[6] { ".mp3", ".wav", ".flac", ".ogg", ".m4a", ".aac" };
		string[] array = System.IO.File.ReadAllLines(openFileDialog.FileName, Encoding.UTF8);
		for (int i = 0; i < array.Length; i++)
		{
			string text = array[i].Trim();
			if (!text.StartsWith("#") && System.IO.File.Exists(text) && source.Contains(System.IO.Path.GetExtension(text).ToLower()))
			{
				AddToPlaylist(text);
			}
		}
		if (currentIndex < 0 && playlist.Count > 0)
		{
			PlayTrack(0);
		}
	}

	private void AbSetABtn_Click(object sender, RoutedEventArgs e)
	{
		if (audioFileReader != null)
		{
			abPointA = audioFileReader.CurrentTime.TotalSeconds;
			if (abPointB >= 0.0 && abPointB <= abPointA)
			{
				abPointB = -1.0;
			}
			UpdateAbInfo();
		}
	}

	private void AbSetBBtn_Click(object sender, RoutedEventArgs e)
	{
		if (audioFileReader != null)
		{
			double totalSeconds = audioFileReader.CurrentTime.TotalSeconds;
			if (abPointA >= 0.0 && totalSeconds <= abPointA)
			{
				MessageBox.Show("B 지점은 A 지점보다 뒤여야 합니다.");
				return;
			}
			abPointB = totalSeconds;
			UpdateAbInfo();
		}
	}

	private void AbClearBtn_Click(object sender, RoutedEventArgs e)
	{
		abPointA = -1.0;
		abPointB = -1.0;
		UpdateAbInfo();
	}

	private void UpdateAbInfo()
	{
		if (abPointA < 0.0)
		{
			AbInfoText.Text = S("A-B 꺼짐", "A-B Off");
		}
		else if (abPointB < 0.0)
		{
			AbInfoText.Text = "A: " + FormatTime(TimeSpan.FromSeconds(abPointA)) + "  |  B: -";
		}
		else
		{
			AbInfoText.Text = "A: " + FormatTime(TimeSpan.FromSeconds(abPointA)) + " ~ " + FormatTime(TimeSpan.FromSeconds(abPointB));
		}
	}

	private async void ExportBtn_Click(object sender, RoutedEventArgs e)
	{
		if (currentIndex < 0 || currentIndex >= playlist.Count)
		{
			MessageBox.Show(S("재생 중인 파일이 없습니다.", "No file is currently playing."));
			return;
		}
		SaveFileDialog saveFileDialog = new SaveFileDialog
		{
			Filter = "MP3 파일|*.mp3|WAV 파일|*.wav",
			FileName = System.IO.Path.GetFileNameWithoutExtension(playlist[currentIndex])
		};
		if (saveFileDialog.ShowDialog() != true)
		{
			return;
		}
		string outputPath = saveFileDialog.FileName;
		bool isMp3 = outputPath.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase);
		string sourcePath = playlist[currentIndex];
		float pitch = (float)PitchSlider.Value;
		float tempo = (float)TempoSlider.Value;
		ExportBtn.IsEnabled = false;
		ExportBtn.Content = "저장 중...";
		try
		{
			await Task.Run(delegate
			{
				using AudioFileReader sourceStream = new AudioFileReader(sourcePath);
				using SoundTouchWaveStream soundTouchWaveStream = new SoundTouchWaveStream(sourceStream);
				soundTouchWaveStream.PitchSemiTones = pitch;
				soundTouchWaveStream.TempoChange = tempo;
				if (isMp3)
				{
					string text = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString() + ".wav");
					try
					{
						WaveFileWriter.CreateWaveFile(text, soundTouchWaveStream);
						using AudioFileReader audioFileReader = new AudioFileReader(text);
						using LameMP3FileWriter destination = new LameMP3FileWriter(outputPath, audioFileReader.WaveFormat, 192);
						audioFileReader.CopyTo(destination);
						return;
					}
					finally
					{
						if (System.IO.File.Exists(text))
						{
							System.IO.File.Delete(text);
						}
					}
				}
				WaveFileWriter.CreateWaveFile(outputPath, soundTouchWaveStream);
			});
			MessageBox.Show("내보내기 완료!");
		}
		catch (Exception ex)
		{
			MessageBox.Show("내보내기 실패: " + ex.Message);
		}
		finally
		{
			ExportBtn.IsEnabled = true;
			ExportBtn.Content = "내보내기";
		}
	}

	private void Window_Activated(object sender, EventArgs e)
	{
		CheckClipboardForYoutubeUrl();
	}

	private void CheckClipboardForYoutubeUrl()
	{
		if (_isOfferingDownload || isYtDownloading)
		{
			return;
		}
		try
		{
			if (!Clipboard.ContainsText())
			{
				return;
			}
			string text = (Clipboard.GetText() ?? "").Trim();
			if (string.IsNullOrEmpty(text))
			{
				return;
			}
			string url = ExtractYoutubeUrl(text);
			if (url == null || url == _lastClipboardUrl)
			{
				return;
			}
			_lastClipboardUrl = url;
			_isOfferingDownload = true;
			try
			{
				if (MessageBox.Show(S("유튜브 주소가 감지되었습니다.\n다운로드할까요?", "A YouTube URL was detected.\nDownload it?"), S("유튜브 다운로드", "YouTube Download"), MessageBoxButton.YesNo) == MessageBoxResult.Yes)
				{
					YtUrlBox.Text = url;
					YtDownloadBtn_Click(YtDownloadBtn, new RoutedEventArgs());
				}
			}
			finally
			{
				_isOfferingDownload = false;
			}
		}
		catch
		{
		}
	}

	private static string? ExtractYoutubeUrl(string text)
	{
		foreach (string part in text.Split(new char[3] { ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
		{
			if (part.Contains("youtube.com/watch") || part.Contains("youtu.be/"))
			{
				return part;
			}
		}
		return null;
	}

	private void DownloadCleanBtn_Click(object sender, RoutedEventArgs e)
	{
		downloadedFiles.RemoveAll(f => !System.IO.File.Exists(f));
		List<string> candidates = downloadedFiles.Where(f => !playlist.Contains(f)).ToList();
		if (candidates.Count == 0)
		{
			MessageBox.Show(S("정리할 다운로드 파일이 없습니다.\n(재생목록에 없는 다운로드 파일만 정리됩니다.)", "No downloaded files to clean up.\n(Only downloads not in the playlist are removed.)"), S("다운로드 정리", "Clean Downloads"));
			return;
		}
		long totalBytes = 0L;
		foreach (string f in candidates)
		{
			try
			{
				totalBytes += new FileInfo(f).Length;
			}
			catch
			{
			}
		}
		string sizeText = ((totalBytes >= 1048576) ? $"{(double)totalBytes / 1048576.0:F1} MB" : $"{(double)totalBytes / 1024.0:F0} KB");
		string names = string.Join("\n", candidates.Select(System.IO.Path.GetFileName).Take(10));
		if (candidates.Count > 10)
		{
			names += S($"\n... 외 {candidates.Count - 10}개", $"\n... and {candidates.Count - 10} more");
		}
		if (MessageBox.Show(S($"재생목록에 없는 다운로드 파일 {candidates.Count}개 ({sizeText})를 삭제할까요?\n\n{names}", $"Delete {candidates.Count} downloaded file(s) ({sizeText}) not in the playlist?\n\n{names}"), S("다운로드 정리", "Clean Downloads"), MessageBoxButton.YesNo) != MessageBoxResult.Yes)
		{
			return;
		}
		int deleted = 0;
		foreach (string f in candidates)
		{
			try
			{
				System.IO.File.Delete(f);
				downloadedFiles.Remove(f);
				deleted++;
			}
			catch
			{
			}
		}
		LibraryRefreshBtn_Click(this, new RoutedEventArgs());
		SaveSettings();
		MessageBox.Show(S($"{deleted}개 파일을 삭제했습니다.", $"Deleted {deleted} file(s)."), S("다운로드 정리", "Clean Downloads"));
	}

	private async void ExportHqBtn_Click(object sender, RoutedEventArgs e)
	{
		if (currentIndex < 0 || currentIndex >= playlist.Count)
		{
			MessageBox.Show(S("재생 중인 파일이 없습니다.", "No file is currently playing."));
			return;
		}
		string rbExe = FindRubberband();
		if (rbExe == null)
		{
			MessageBox.Show(S("rubberband-r3.exe를 찾을 수 없습니다. 프로그램 폴더에 rubberband-r3.exe와 sndfile.dll이 필요합니다.", "Cannot find rubberband-r3.exe. It must be in the program folder along with sndfile.dll."));
			return;
		}
		SaveFileDialog saveFileDialog = new SaveFileDialog
		{
			Filter = "MP3 파일|*.mp3|WAV 파일|*.wav",
			FileName = BuildHqFileName()
		};
		if (saveFileDialog.ShowDialog() != true)
		{
			return;
		}
		string outputPath = saveFileDialog.FileName;
		bool isMp3 = outputPath.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase);
		string sourcePath = playlist[currentIndex];
		float pitch = (float)PitchSlider.Value;
		float tempo = (float)TempoSlider.Value;
		ExportHqBtn.IsEnabled = false;
		ExportBtn.IsEnabled = false;
		Progress<string> progress = new Progress<string>(delegate(string txt)
		{
			ExportHqBtn.Content = txt;
		});
		try
		{
			await Task.Run(() => RunHqExport(rbExe, sourcePath, outputPath, isMp3, pitch, tempo, progress));
			MessageBox.Show(S("HQ 내보내기 완료!", "HQ export complete!"));
		}
		catch (Exception ex)
		{
			MessageBox.Show(S("HQ 내보내기 실패: ", "HQ export failed: ") + ex.Message);
		}
		finally
		{
			ExportHqBtn.IsEnabled = true;
			ExportBtn.IsEnabled = true;
			ExportHqBtn.Content = "HQ";
		}
	}

	private string BuildHqFileName()
	{
		string baseName = System.IO.Path.GetFileNameWithoutExtension(playlist[currentIndex]);
		int pitch = (int)Math.Round(PitchSlider.Value);
		int tempo = (int)Math.Round(TempoSlider.Value);
		string pitchStr = ((pitch == 0) ? "" : $"_P{pitch:+0;-0}");
		string tempoStr = ((tempo == 0) ? "" : $"_T{100 + tempo}");
		return baseName + "_HQ" + pitchStr + tempoStr;
	}

	private static string? FindRubberband()
	{
		string text = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "rubberband-r3.exe");
		if (System.IO.File.Exists(text))
		{
			return text;
		}
		return null;
	}

	private void RunHqExport(string rbExe, string sourcePath, string outputPath, bool isMp3, float pitch, float tempo, IProgress<string> progress)
	{
		string tempIn = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString() + ".wav");
		string tempOut = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString() + ".wav");
		try
		{
			progress?.Report(S("디코딩...", "Decoding..."));
			using (AudioFileReader reader = new AudioFileReader(sourcePath))
			{
				WaveFileWriter.CreateWaveFile(tempIn, reader);
			}
			progress?.Report(S("HQ 변환...", "Processing..."));
			double tempoRatio = 1.0 + (double)tempo / 100.0;
			string pitchArg = pitch.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
			string tempoArg = tempoRatio.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
			ProcessStartInfo psi = new ProcessStartInfo
			{
				FileName = rbExe,
				Arguments = $"--formant --pitch {pitchArg} --tempo {tempoArg} \"{tempIn}\" \"{tempOut}\"",
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardError = true,
				RedirectStandardOutput = true
			};
			StringBuilder rbErr = new StringBuilder();
			using (Process proc = Process.Start(psi))
			{
				proc.ErrorDataReceived += delegate(object s2, DataReceivedEventArgs de)
				{
					if (de.Data != null && rbErr.Length < 4000)
					{
						rbErr.AppendLine(de.Data);
					}
				};
				proc.BeginErrorReadLine();
				proc.StandardOutput.ReadToEnd();
				proc.WaitForExit();
				if (proc.ExitCode != 0)
				{
					throw new Exception("rubberband error (" + proc.ExitCode + "): " + rbErr.ToString());
				}
			}
			if (isMp3)
			{
				progress?.Report(S("MP3 인코딩...", "Encoding..."));
				using AudioFileReader outReader = new AudioFileReader(tempOut);
				using LameMP3FileWriter writer = new LameMP3FileWriter(outputPath, outReader.WaveFormat, 320);
				outReader.CopyTo(writer);
			}
			else
			{
				System.IO.File.Copy(tempOut, outputPath, overwrite: true);
			}
		}
		finally
		{
			try
			{
				if (System.IO.File.Exists(tempIn))
				{
					System.IO.File.Delete(tempIn);
				}
				if (System.IO.File.Exists(tempOut))
				{
					System.IO.File.Delete(tempOut);
				}
			}
			catch
			{
			}
		}
	}

	private bool isDemucsRunning;

	private static string? FindDemucs()
	{
		string[] candidates = new string[2]
		{
			System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "demucs-env", "Scripts", "demucs.exe"),
			"T:\\demucs-env\\Scripts\\demucs.exe"
		};
		foreach (string c in candidates)
		{
			if (System.IO.File.Exists(c))
			{
				return c;
			}
		}
		return null;
	}

	private async void DemucsBtn_Click(object sender, RoutedEventArgs e)
	{
		if (isDemucsRunning)
		{
			return;
		}
		if (currentIndex < 0 || currentIndex >= playlist.Count)
		{
			MessageBox.Show(S("재생 중인 파일이 없습니다.", "No file is currently playing."));
			return;
		}
		string demucsExe = FindDemucs();
		if (demucsExe == null)
		{
			MessageBox.Show(S("Demucs를 찾을 수 없습니다.\nT:\\demucs-env 에 Python demucs가 설치되어 있어야 합니다.", "Cannot find Demucs.\nPython demucs must be installed at T:\\demucs-env."));
			return;
		}
		string sourcePath = playlist[currentIndex];
		string outputPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(sourcePath) ?? "", System.IO.Path.GetFileNameWithoutExtension(sourcePath) + "_Inst.mp3");
		if (MessageBox.Show(S($"AI로 보컬을 제거한 반주 파일을 만들까요?\n\n저장 위치: {outputPath}\n(GPU 사용 시 수십 초, CPU는 몇 분 걸릴 수 있습니다.)", $"Create an instrumental by removing vocals with AI?\n\nOutput: {outputPath}\n(Takes ~1 min on GPU, several minutes on CPU.)"), S("AI 반주 추출", "AI Instrumental"), MessageBoxButton.YesNo) != MessageBoxResult.Yes)
		{
			return;
		}
		isDemucsRunning = true;
		DemucsBtn.IsEnabled = false;
		Progress<string> progress = new Progress<string>(delegate(string txt)
		{
			DemucsBtn.Content = txt;
		});
		try
		{
			await Task.Run(() => RunDemucs(demucsExe, sourcePath, outputPath, progress));
			int idx = playlist.IndexOf(sourcePath);
			if (idx >= 0)
			{
				playlist[idx] = outputPath;
				PlaylistBox.Items[idx] = System.IO.Path.GetFileName(outputPath);
				UpdatePlaylistHighlight();
				if (currentIndex == idx)
				{
					PlayTrack(idx);
				}
			}
			else
			{
				AddToPlaylist(outputPath);
			}
			MessageBox.Show(S("AI 반주 추출 완료!\n재생목록의 원본이 반주 파일로 대체되었습니다.", "AI instrumental complete!\nThe original in the playlist was replaced with the instrumental."));
		}
		catch (Exception ex)
		{
			MessageBox.Show(S("AI 반주 추출 실패: ", "AI extraction failed: ") + ex.Message);
		}
		finally
		{
			isDemucsRunning = false;
			DemucsBtn.IsEnabled = true;
			DemucsBtn.Content = "AI";
		}
	}

	private void RunDemucs(string demucsExe, string sourcePath, string outputPath, IProgress<string> progress)
	{
		string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "jip_demucs_" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(tempDir);
		string tempWav = System.IO.Path.Combine(tempDir, "input.wav");
		try
		{
			progress?.Report(S("디코딩...", "Decoding..."));
			using (AudioFileReader reader = new AudioFileReader(sourcePath))
			{
				WaveFileWriter.CreateWaveFile(tempWav, reader);
			}
			progress?.Report(S("AI 분석...", "AI..."));
			ProcessStartInfo psi = new ProcessStartInfo
			{
				FileName = demucsExe,
				Arguments = $"--two-stems=vocals -n htdemucs -o \"{tempDir}\" \"{tempWav}\"",
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardError = true,
				RedirectStandardOutput = true
			};
			string venvRoot = System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(demucsExe)) ?? "";
			psi.EnvironmentVariables["TORCH_HOME"] = System.IO.Path.Combine(venvRoot, "torch-cache");
			StringBuilder errLog = new StringBuilder();
			using (Process proc = Process.Start(psi))
			{
				proc.OutputDataReceived += delegate
				{
				};
				proc.BeginOutputReadLine();
				System.Text.RegularExpressions.Regex rx = new System.Text.RegularExpressions.Regex("(\\d+)%");
				char[] buf = new char[256];
				int n;
				while ((n = proc.StandardError.Read(buf, 0, buf.Length)) > 0)
				{
					string chunk = new string(buf, 0, n);
					errLog.Append(chunk);
					if (errLog.Length > 8000)
					{
						errLog.Remove(0, errLog.Length - 4000);
					}
					System.Text.RegularExpressions.MatchCollection m = rx.Matches(chunk);
					if (m.Count > 0)
					{
						progress?.Report(S("AI 분석 ", "AI ") + m[m.Count - 1].Groups[1].Value + "%");
					}
				}
				proc.WaitForExit();
				if (proc.ExitCode != 0)
				{
					string log = errLog.ToString();
					throw new Exception("demucs: " + ((log.Length > 400) ? log.Substring(log.Length - 400) : log));
				}
			}
			string stemWav = System.IO.Path.Combine(tempDir, "htdemucs", "input", "no_vocals.wav");
			if (!System.IO.File.Exists(stemWav))
			{
				throw new Exception(S("결과 파일(no_vocals.wav)을 찾을 수 없습니다.", "Result file (no_vocals.wav) not found."));
			}
			progress?.Report(S("MP3 인코딩...", "Encoding..."));
			using AudioFileReader outReader = new AudioFileReader(stemWav);
			using LameMP3FileWriter writer = new LameMP3FileWriter(outputPath, outReader.WaveFormat, 320);
			outReader.CopyTo(writer);
		}
		finally
		{
			try
			{
				Directory.Delete(tempDir, recursive: true);
			}
			catch
			{
			}
		}
	}

	private void StartMicMonitor()
	{
		if (micMonitor != null)
		{
			return;
		}
		try
		{
			micMonitor = new WaveInEvent
			{
				DeviceNumber = _micDeviceNumber,
				WaveFormat = new WaveFormat(44100, 2),
				BufferMilliseconds = 50
			};
			micMonitor.DataAvailable += delegate(object? s, WaveInEventArgs e)
			{
				_micRms = CalcRms16(e.Buffer, e.BytesRecorded);
				_micLastTick = Stopwatch.GetTimestamp();
			};
			micMonitor.StartRecording();
		}
		catch
		{
			micMonitor = null;
		}
	}

	private void StopMicMonitor()
	{
		if (micMonitor != null)
		{
			try
			{
				micMonitor.StopRecording();
			}
			catch
			{
			}
			micMonitor.Dispose();
			micMonitor = null;
		}
	}

	private void LoopbackBtn_Click(object sender, RoutedEventArgs e)
	{
		if (!isRecording)
		{
			loopbackMode = !loopbackMode;
			LoopbackBtn.Content = (loopbackMode ? S("반주+마이크", "Inst+Mic") : S("마이크", "Mic"));
			LoopbackBtn.Style = (loopbackMode ? ((Style)base.Resources["ActiveButton"]) : ((Style)base.Resources["DarkButton"]));
		}
	}

	private void RecordBtn_Click(object sender, RoutedEventArgs e)
	{
		if (!isRecording)
		{
			StartRecording();
		}
		else
		{
			StopRecording();
		}
	}

	private void RecordPathBtn_Click(object sender, RoutedEventArgs e)
	{
		string text = "";
		if (!string.IsNullOrEmpty(pendingSavePath))
		{
			text = System.IO.Path.GetDirectoryName(pendingSavePath) ?? "";
		}
		if (string.IsNullOrEmpty(text) || !Directory.Exists(text))
		{
			text = (string.IsNullOrEmpty(recordingBasePath) ? Environment.GetFolderPath(Environment.SpecialFolder.MyMusic) : recordingBasePath);
		}
		if (Directory.Exists(text))
		{
			Process.Start("explorer.exe", text);
		}
	}

	private void SettingsBtn_Click(object sender, RoutedEventArgs e)
	{
		Window win = new Window
		{
			Title = S("설정", "Settings"),
			Width = 460.0,
			Height = 560.0,
			Background = new SolidColorBrush(Color.FromRgb(42, 42, 42)),
			WindowStartupLocation = WindowStartupLocation.CenterOwner,
			Owner = this,
			ResizeMode = ResizeMode.NoResize
		};
		win.Resources.MergedDictionaries.Add(base.Resources);
		ScrollViewer scrollViewer = new ScrollViewer
		{
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto
		};
		StackPanel stackPanel = (StackPanel)(scrollViewer.Content = new StackPanel
		{
			Margin = new Thickness(18.0)
		});
		TextBlock element = new TextBlock
		{
			Text = S("단축키", "Keyboard Shortcuts"),
			Foreground = new SolidColorBrush(Color.FromRgb(0, 122, 204)),
			FontSize = 12.0,
			FontWeight = FontWeights.SemiBold,
			Margin = new Thickness(0.0, 0.0, 0.0, 8.0)
		};
		stackPanel.Children.Add(element);
		Border border = new Border
		{
			Background = new SolidColorBrush(Color.FromRgb(34, 34, 34)),
			BorderBrush = new SolidColorBrush(Color.FromRgb(68, 68, 68)),
			BorderThickness = new Thickness(1.0),
			CornerRadius = new CornerRadius(4.0),
			Padding = new Thickness(10.0, 8.0, 10.0, 8.0),
			Margin = new Thickness(0.0, 0.0, 0.0, 16.0)
		};
		Grid grid = new Grid();
		grid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(1.0, GridUnitType.Star)
		});
		grid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(80.0)
		});
		border.Child = grid;
		(string, string)[] array = S(new(string, string)[13]
		{
			("재생 / 일시정지", "Space"),
			("이전 곡", "←"),
			("다음 곡", "→"),
			("음정 올림", "W"),
			("음정 내림", "S"),
			("속도 올림", "D"),
			("속도 내림", "A"),
			("A 지점 설정", "Q"),
			("B 지점 설정", "E"),
			("구간 반복 해제", "R"),
			("원키", "Z"),
			("원속도", "C"),
			("원키 + 원속도", "X")
		}, new(string, string)[13]
		{
			("Play / Pause", "Space"),
			("Previous track", "←"),
			("Next track", "→"),
			("Pitch up", "W"),
			("Pitch down", "S"),
			("Tempo up", "D"),
			("Tempo down", "A"),
			("Set A point", "Q"),
			("Set B point", "E"),
			("Clear A-B repeat", "R"),
			("Reset pitch", "Z"),
			("Reset tempo", "C"),
			("Reset pitch+tempo", "X")
		});
		for (int i = 0; i < array.Length; i++)
		{
			grid.RowDefinitions.Add(new RowDefinition
			{
				Height = GridLength.Auto
			});
			Color color = ((i % 2 == 0) ? Color.FromRgb(34, 34, 34) : Color.FromRgb(40, 40, 40));
			Border element2 = new Border
			{
				Background = new SolidColorBrush(color),
				Padding = new Thickness(4.0, 4.0, 4.0, 4.0)
			};
			Grid.SetRow(element2, i);
			Grid.SetColumnSpan(element2, 2);
			grid.Children.Add(element2);
			TextBlock element3 = new TextBlock
			{
				Text = array[i].Item1,
				Foreground = new SolidColorBrush(Color.FromRgb(204, 204, 204)),
				FontSize = 11.0,
				VerticalAlignment = VerticalAlignment.Center,
				Margin = new Thickness(4.0, 0.0, 0.0, 0.0)
			};
			Grid.SetRow(element3, i);
			Grid.SetColumn(element3, 0);
			grid.Children.Add(element3);
			Border border2 = new Border
			{
				Background = new SolidColorBrush(Color.FromRgb(58, 58, 58)),
				BorderBrush = new SolidColorBrush(Color.FromRgb(85, 85, 85)),
				BorderThickness = new Thickness(1.0),
				CornerRadius = new CornerRadius(3.0),
				Padding = new Thickness(6.0, 2.0, 6.0, 2.0),
				HorizontalAlignment = HorizontalAlignment.Center,
				VerticalAlignment = VerticalAlignment.Center,
				Margin = new Thickness(0.0, 2.0, 0.0, 2.0)
			};
			TextBlock child = new TextBlock
			{
				Text = array[i].Item2,
				Foreground = new SolidColorBrush(Color.FromRgb(225, 225, 225)),
				FontSize = 11.0,
				FontFamily = new FontFamily("Consolas"),
				HorizontalAlignment = HorizontalAlignment.Center
			};
			border2.Child = child;
			Grid.SetRow(border2, i);
			Grid.SetColumn(border2, 1);
			grid.Children.Add(border2);
		}
		stackPanel.Children.Add(border);
		TextBlock element4 = new TextBlock
		{
			Text = S("가사 글자 크기", "Lyrics Font Size"),
			Foreground = new SolidColorBrush(Color.FromRgb(0, 122, 204)),
			FontSize = 12.0,
			FontWeight = FontWeights.SemiBold,
			Margin = new Thickness(0.0, 0.0, 0.0, 8.0)
		};
		stackPanel.Children.Add(element4);
		Grid grid2 = new Grid
		{
			Margin = new Thickness(0.0, 0.0, 0.0, 16.0)
		};
		grid2.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(1.0, GridUnitType.Star)
		});
		grid2.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(50.0)
		});
		Slider fontSlider = new Slider
		{
			Minimum = 8.0,
			Maximum = 48.0,
			Value = lyricsFontSize,
			TickFrequency = 2.0,
			IsSnapToTickEnabled = true,
			VerticalAlignment = VerticalAlignment.Center
		};
		TextBlock fontValueText = new TextBlock
		{
			Text = lyricsFontSize.ToString(),
			Foreground = new SolidColorBrush(Color.FromRgb(204, 204, 204)),
			FontSize = 12.0,
			VerticalAlignment = VerticalAlignment.Center,
			HorizontalAlignment = HorizontalAlignment.Center
		};
		fontSlider.ValueChanged += delegate
		{
			int num3 = (int)fontSlider.Value;
			fontValueText.Text = num3.ToString();
			lyricsFontSize = num3;
			if (LyricsBox != null)
			{
				LyricsBox.FontSize = num3;
			}
			SaveSettings();
		};
		Grid.SetColumn(fontSlider, 0);
		Grid.SetColumn(fontValueText, 1);
		grid2.Children.Add(fontSlider);
		grid2.Children.Add(fontValueText);
		stackPanel.Children.Add(grid2);
		TextBlock element5 = new TextBlock
		{
			Text = S("저장경로", "Save Path"),
			Foreground = new SolidColorBrush(Color.FromRgb(0, 122, 204)),
			FontSize = 12.0,
			FontWeight = FontWeights.SemiBold,
			Margin = new Thickness(0.0, 0.0, 0.0, 8.0)
		};
		stackPanel.Children.Add(element5);
		Grid grid3 = new Grid
		{
			Margin = new Thickness(0.0, 0.0, 0.0, 16.0)
		};
		grid3.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(1.0, GridUnitType.Star)
		});
		grid3.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = GridLength.Auto
		});
		System.Windows.Controls.TextBox pathBox = new System.Windows.Controls.TextBox
		{
			Text = recordingBasePath,
			Background = new SolidColorBrush(Color.FromRgb(58, 58, 58)),
			Foreground = new SolidColorBrush(Color.FromRgb(204, 204, 204)),
			BorderBrush = new SolidColorBrush(Color.FromRgb(85, 85, 85)),
			BorderThickness = new Thickness(1.0),
			Padding = new Thickness(6.0, 4.0, 6.0, 4.0),
			FontSize = 11.0,
			CaretBrush = Brushes.White,
			VerticalAlignment = VerticalAlignment.Center
		};
		Button button = new Button
		{
			Content = "...",
			Width = 36.0,
			Height = 28.0,
			Margin = new Thickness(6.0, 0.0, 0.0, 0.0),
			Style = (Style)base.Resources["DarkButton"],
			FontSize = 11.0
		};
		button.Click += delegate
		{
			OpenFolderDialog openFolderDialog = new OpenFolderDialog
			{
				Title = S("녹음 저장 기본 경로 선택", "Select Recording Save Folder")
			};
			if (!string.IsNullOrEmpty(pathBox.Text) && Directory.Exists(pathBox.Text))
			{
				openFolderDialog.InitialDirectory = pathBox.Text;
			}
			if (openFolderDialog.ShowDialog() == true)
			{
				pathBox.Text = openFolderDialog.FolderName;
			}
		};
		Grid.SetColumn(pathBox, 0);
		Grid.SetColumn(button, 1);
		grid3.Children.Add(pathBox);
		grid3.Children.Add(button);
		stackPanel.Children.Add(grid3);
		stackPanel.Children.Add(new TextBlock
		{
			Text = S("출력 장치 (반주 재생)", "Output Device (Playback)"),
			Foreground = new SolidColorBrush(Color.FromRgb(0, 122, 204)),
			FontSize = 12.0,
			FontWeight = FontWeights.SemiBold,
			Margin = new Thickness(0.0, 0.0, 0.0, 8.0)
		});
		List<MMDevice> renderDevices = new List<MMDevice>();
		try
		{
			renderDevices = new MMDeviceEnumerator().EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
		}
		catch
		{
		}
		ComboBox outputCombo = new ComboBox
		{
			Margin = new Thickness(0.0, 0.0, 0.0, 16.0),
			Height = 28.0,
			FontSize = 11.0
		};
		outputCombo.Items.Add(S("시스템 기본 장치", "System Default"));
		foreach (MMDevice item in renderDevices)
		{
			outputCombo.Items.Add(item.FriendlyName);
		}
		int selectedIndex = 0;
		if (!string.IsNullOrEmpty(_outputDeviceId))
		{
			int num = renderDevices.FindIndex((MMDevice d) => d.ID == _outputDeviceId);
			if (num >= 0)
			{
				selectedIndex = num + 1;
			}
		}
		outputCombo.SelectedIndex = selectedIndex;
		stackPanel.Children.Add(outputCombo);
		stackPanel.Children.Add(new TextBlock
		{
			Text = S("입력 장치 (마이크)", "Input Device (Microphone)"),
			Foreground = new SolidColorBrush(Color.FromRgb(0, 122, 204)),
			FontSize = 12.0,
			FontWeight = FontWeights.SemiBold,
			Margin = new Thickness(0.0, 0.0, 0.0, 8.0)
		});
		List<(int Number, string Name)> micDevices = new List<(int, string)>();
		try
		{
			for (int num2 = 0; num2 < WaveInEvent.DeviceCount; num2++)
			{
				micDevices.Add((num2, WaveInEvent.GetCapabilities(num2).ProductName));
			}
		}
		catch
		{
		}
		ComboBox micCombo = new ComboBox
		{
			Margin = new Thickness(0.0, 0.0, 0.0, 16.0),
			Height = 28.0,
			FontSize = 11.0
		};
		micCombo.Items.Add(S("시스템 기본 장치", "System Default"));
		foreach (var item2 in micDevices)
		{
			micCombo.Items.Add(item2.Name);
		}
		int selectedIndex2 = ((_micDeviceNumber < micDevices.Count) ? (_micDeviceNumber + 1) : 0);
		micCombo.SelectedIndex = selectedIndex2;
		stackPanel.Children.Add(micCombo);
		TextBlock element6 = new TextBlock
		{
			Text = S("요청 사항 있으면 자유롭게 연락주세요, 시간나면 고쳐드릴께요 ~", "Feel free to reach out with any requests — I'll fix it when I get a chance ~"),
			Foreground = new SolidColorBrush(Color.FromRgb(119, 119, 119)),
			FontSize = 11.0,
			TextWrapping = TextWrapping.Wrap,
			Margin = new Thickness(0.0, 0.0, 0.0, 6.0)
		};
		stackPanel.Children.Add(element6);
		StackPanel stackPanel2 = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Margin = new Thickness(0.0, 0.0, 0.0, 14.0)
		};
		stackPanel2.Children.Add(new TextBlock
		{
			Text = S("문의 : ", "Contact : "),
			Foreground = new SolidColorBrush(Color.FromRgb(119, 119, 119)),
			FontSize = 11.0,
			VerticalAlignment = VerticalAlignment.Center
		});
		TextBlock emailText = new TextBlock
		{
			Text = "mizhashi@naver.com",
			Foreground = new SolidColorBrush(Color.FromRgb(78, 201, 176)),
			FontSize = 11.0,
			VerticalAlignment = VerticalAlignment.Center,
			Cursor = Cursors.Hand,
			TextDecorations = TextDecorations.Underline,
			ToolTip = S("클릭하면 클립보드에 복사됩니다", "Click to copy to clipboard")
		};
		emailText.MouseLeftButtonDown += delegate
		{
			Clipboard.SetText("mizhashi@naver.com");
			Brush orig = emailText.Foreground;
			emailText.Foreground = new SolidColorBrush(Color.FromRgb(byte.MaxValue, byte.MaxValue, byte.MaxValue));
			DispatcherTimer t = new DispatcherTimer
			{
				Interval = TimeSpan.FromMilliseconds(600.0)
			};
			t.Tick += delegate
			{
				emailText.Foreground = orig;
				t.Stop();
			};
			t.Start();
		};
		stackPanel2.Children.Add(emailText);
		TextBlock element7 = new TextBlock
		{
			Text = S(" (클릭 → 복사)", " (click → copy)"),
			Foreground = new SolidColorBrush(Color.FromRgb(85, 85, 85)),
			FontSize = 10.0,
			VerticalAlignment = VerticalAlignment.Center
		};
		stackPanel2.Children.Add(element7);
		stackPanel.Children.Add(stackPanel2);
		StackPanel stackPanel3 = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Margin = new Thickness(0.0, 0.0, 0.0, 14.0)
		};
		stackPanel3.Children.Add(new TextBlock
		{
			Text = S("카카오톡 ID : ", "KakaoTalk ID : "),
			Foreground = new SolidColorBrush(Color.FromRgb(119, 119, 119)),
			FontSize = 11.0,
			VerticalAlignment = VerticalAlignment.Center
		});
		TextBlock kakaoText = new TextBlock
		{
			Text = "mizhashi",
			Foreground = new SolidColorBrush(Color.FromRgb(78, 201, 176)),
			FontSize = 11.0,
			VerticalAlignment = VerticalAlignment.Center,
			Cursor = Cursors.Hand,
			TextDecorations = TextDecorations.Underline,
			ToolTip = S("클릭하면 클립보드에 복사됩니다", "Click to copy to clipboard")
		};
		kakaoText.MouseLeftButtonDown += delegate
		{
			Clipboard.SetText("mizhashi");
			Brush orig = kakaoText.Foreground;
			kakaoText.Foreground = new SolidColorBrush(Color.FromRgb(byte.MaxValue, byte.MaxValue, byte.MaxValue));
			DispatcherTimer t = new DispatcherTimer
			{
				Interval = TimeSpan.FromMilliseconds(600.0)
			};
			t.Tick += delegate
			{
				kakaoText.Foreground = orig;
				t.Stop();
			};
			t.Start();
		};
		stackPanel3.Children.Add(kakaoText);
		stackPanel3.Children.Add(new TextBlock
		{
			Text = S(" (클릭 → 복사)", " (click → copy)"),
			Foreground = new SolidColorBrush(Color.FromRgb(85, 85, 85)),
			FontSize = 10.0,
			VerticalAlignment = VerticalAlignment.Center
		});
		stackPanel.Children.Add(stackPanel3);
		Button button2 = new Button
		{
			Content = S("확인", "OK"),
			Style = (Style)base.Resources["ActiveButton"],
			Height = 28.0,
			Width = 80.0,
			HorizontalAlignment = HorizontalAlignment.Right,
			FontSize = 11.0
		};
		button2.Click += delegate
		{
			recordingBasePath = pathBox.Text.Trim();
			_outputDeviceId = ((outputCombo.SelectedIndex <= 0) ? "" : (renderDevices.ElementAtOrDefault(outputCombo.SelectedIndex - 1)?.ID ?? ""));
			_micDeviceNumber = ((micCombo.SelectedIndex > 0) ? micDevices.ElementAtOrDefault(micCombo.SelectedIndex - 1).Number : 0);
			SaveSettings();
			win.Close();
		};
		stackPanel.Children.Add(button2);
		win.Content = scrollViewer;
		win.ShowDialog();
	}

	private (string, string)[] S((string, string)[] kor, (string, string)[] eng)
	{
		if (!isKorean)
		{
			return eng;
		}
		return kor;
	}

	private void StartRecording()
	{
		string path = ((libraryFolders.Count > 0 && Directory.Exists(libraryFolders[0])) ? libraryFolders[0] : ((!string.IsNullOrEmpty(recordingBasePath)) ? recordingBasePath : Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)));
		string path2 = DateTime.Now.ToString("yyMMdd");
		string text = System.IO.Path.Combine(path, path2);
		Directory.CreateDirectory(text);
		string text2 = ((currentIndex >= 0 && currentIndex < playlist.Count) ? System.IO.Path.GetFileNameWithoutExtension(playlist[currentIndex]) : "untitled");
		char[] invalidFileNameChars = System.IO.Path.GetInvalidFileNameChars();
		foreach (char oldChar in invalidFileNameChars)
		{
			text2 = text2.Replace(oldChar, '_');
		}
		string text3 = DateTime.Now.ToString("yyMMdd_HHmmss");
		pendingSavePath = System.IO.Path.Combine(text, text3 + "_REC_" + text2 + ".wav");
		try
		{
			if (loopbackMode)
			{
				StartMixedRecording();
			}
			else
			{
				StartLoopbackRecording();
			}
			isRecording = true;
			RecordBtn.Content = S("■ 중지", "■ Stop");
			RecordBtn.Foreground = Brushes.White;
			LoopbackBtn.IsEnabled = false;
			SyncPlayRecordToggle.IsEnabled = false;
			if (SyncPlayRecordToggle.IsChecked == true)
			{
				if (outputDevice != null && outputDevice.PlaybackState != PlaybackState.Playing)
				{
					outputDevice.Play();
					PlayPauseButton.Content = "⏸";
					progressTimer.Start();
				}
				else if (outputDevice == null && playlist.Count > 0)
				{
					PlayTrack((currentIndex >= 0) ? currentIndex : 0);
				}
			}
		}
		catch (Exception ex)
		{
			MessageBox.Show(S("녹음 시작 실패: " + ex.Message, "Recording failed: " + ex.Message));
		}
	}

	private void StartLoopbackRecording()
	{
		StopMicMonitor();
		waveIn = new WaveInEvent
		{
			DeviceNumber = _micDeviceNumber,
			WaveFormat = new WaveFormat(44100, 2),
			BufferMilliseconds = 30
		};
		waveWriter = new WaveFileWriter(pendingSavePath, waveIn.WaveFormat);
		waveIn.DataAvailable += delegate(object? s, WaveInEventArgs e)
		{
			waveWriter?.Write(e.Buffer, 0, e.BytesRecorded);
			_micRms = CalcRms16(e.Buffer, e.BytesRecorded);
			_micLastTick = Stopwatch.GetTimestamp();
		};
		waveIn.RecordingStopped += delegate
		{
			waveWriter?.Dispose();
			waveWriter = null;
			waveIn?.Dispose();
			waveIn = null;
			base.Dispatcher.Invoke(OnRecordingFinished);
			MessageBox.Show(S("녹음 저장 완료:\n" + pendingSavePath, "Recording saved:\n" + pendingSavePath), S("녹음 완료", "Done"));
			base.Dispatcher.Invoke(delegate
			{
				LibraryRefreshBtn_Click(this, new RoutedEventArgs());
			});
		};
		waveIn.StartRecording();
	}

	private void StartMixedRecording()
	{
		StopMicMonitor();
		string tempPath = System.IO.Path.GetTempPath();
		tempMrPath = System.IO.Path.Combine(tempPath, $"instplayer_mr_{Guid.NewGuid()}.wav");
		tempMicPath = System.IO.Path.Combine(tempPath, $"instplayer_mic_{Guid.NewGuid()}.wav");
		mixStopCount = 0;
		mrFirstTick = 0L;
		micFirstTick = 0L;
		MMDevice mMDevice = null;
		if (!string.IsNullOrEmpty(_outputDeviceId))
		{
			try
			{
				mMDevice = new MMDeviceEnumerator().GetDevice(_outputDeviceId);
			}
			catch
			{
				mMDevice = null;
			}
		}
		loopbackCapture = ((mMDevice != null) ? new WasapiLoopbackCapture(mMDevice) : new WasapiLoopbackCapture());
		loopbackWriter = new WaveFileWriter(tempMrPath, loopbackCapture.WaveFormat);
		loopbackCapture.DataAvailable += delegate(object? s, WaveInEventArgs e)
		{
			if (mrFirstTick == 0L)
			{
				mrFirstTick = Stopwatch.GetTimestamp();
			}
			loopbackWriter?.Write(e.Buffer, 0, e.BytesRecorded);
		};
		loopbackCapture.RecordingStopped += delegate
		{
			loopbackWriter?.Dispose();
			loopbackWriter = null;
			loopbackCapture?.Dispose();
			loopbackCapture = null;
			OnMixStopReady();
		};
		waveIn = new WaveInEvent
		{
			DeviceNumber = _micDeviceNumber,
			WaveFormat = new WaveFormat(44100, 2),
			BufferMilliseconds = 30
		};
		waveWriter = new WaveFileWriter(tempMicPath, waveIn.WaveFormat);
		waveIn.DataAvailable += delegate(object? s, WaveInEventArgs e)
		{
			if (micFirstTick == 0L)
			{
				micFirstTick = Stopwatch.GetTimestamp();
			}
			waveWriter?.Write(e.Buffer, 0, e.BytesRecorded);
			_micRms = CalcRms16(e.Buffer, e.BytesRecorded);
			_micLastTick = Stopwatch.GetTimestamp();
		};
		waveIn.RecordingStopped += delegate
		{
			waveWriter?.Dispose();
			waveWriter = null;
			waveIn?.Dispose();
			waveIn = null;
			OnMixStopReady();
		};
		loopbackCapture.StartRecording();
		waveIn.StartRecording();
	}

	private void OnMixStopReady()
	{
		if (Interlocked.Increment(ref mixStopCount) < 2)
		{
			return;
		}
		base.Dispatcher.Invoke(delegate
		{
			OnRecordingFinished();
			RecordBtn.Content = S("혼합 중...", "Mixing...");
		});
		Task.Run(async delegate
		{
			try
			{
				await MixAndSaveAsync(tempMrPath, tempMicPath, pendingSavePath, mrFirstTick, micFirstTick);
				MessageBox.Show(S("녹음 저장 완료:\n" + pendingSavePath, "Recording saved:\n" + pendingSavePath), S("녹음 완료", "Done"));
			}
			catch (Exception ex)
			{
				MessageBox.Show(S("혼합 저장 실패: " + ex.Message, "Mix save failed: " + ex.Message));
			}
			finally
			{
				if (System.IO.File.Exists(tempMrPath))
				{
					System.IO.File.Delete(tempMrPath);
				}
				if (System.IO.File.Exists(tempMicPath))
				{
					System.IO.File.Delete(tempMicPath);
				}
				base.Dispatcher.Invoke(delegate
				{
					RecordBtn.Content = S("● 녹음", "● Rec");
					LibraryRefreshBtn_Click(this, new RoutedEventArgs());
				});
			}
		});
	}

	private static async Task MixAndSaveAsync(string mrPath, string micPath, string savePath, long mrFirstTick = 0L, long micFirstTick = 0L)
	{
		await Task.Run(delegate
		{
			using AudioFileReader audioFileReader = new AudioFileReader(mrPath);
			using AudioFileReader audioFileReader2 = new AudioFileReader(micPath);
			ISampleProvider sampleProvider = audioFileReader;
			ISampleProvider sampleProvider2 = audioFileReader2;
			if (mrFirstTick > 0 && micFirstTick > 0)
			{
				double num = (double)(micFirstTick - mrFirstTick) / (double)Stopwatch.Frequency;
				if (num > 0.005)
				{
					audioFileReader.CurrentTime = TimeSpan.FromSeconds(num);
				}
				else if (num < -0.005)
				{
					audioFileReader2.CurrentTime = TimeSpan.FromSeconds(0.0 - num);
				}
			}
			if (audioFileReader2.WaveFormat.SampleRate != audioFileReader.WaveFormat.SampleRate)
			{
				sampleProvider2 = new WdlResamplingSampleProvider(sampleProvider2, audioFileReader.WaveFormat.SampleRate);
			}
			if (sampleProvider2.WaveFormat.Channels == 1 && sampleProvider.WaveFormat.Channels == 2)
			{
				sampleProvider2 = new MonoToStereoSampleProvider(sampleProvider2);
			}
			else if (sampleProvider.WaveFormat.Channels == 1 && sampleProvider2.WaveFormat.Channels == 2)
			{
				sampleProvider = new StereoToMonoSampleProvider(sampleProvider);
			}
			MixingSampleProvider source = new MixingSampleProvider(new ISampleProvider[2] { sampleProvider, sampleProvider2 });
			WaveFileWriter.CreateWaveFile(savePath, new SampleToWaveProvider(source));
		});
	}

	private void OnRecordingFinished()
	{
		isRecording = false;
		RecordBtn.Content = S("● 녹음", "● Rec");
		RecordBtn.Foreground = new SolidColorBrush(Color.FromRgb(byte.MaxValue, 85, 85));
		LoopbackBtn.IsEnabled = true;
		SyncPlayRecordToggle.IsEnabled = true;
	}

	private void StopRecording()
	{
		bool valueOrDefault = SyncPlayRecordToggle.IsChecked == true;
		StopMicMonitor();
		if (loopbackMode)
		{
			loopbackCapture?.StopRecording();
			waveIn?.StopRecording();
		}
		else
		{
			waveIn?.StopRecording();
		}
		if (valueOrDefault && outputDevice != null)
		{
			if (outputDevice.PlaybackState == PlaybackState.Playing)
			{
				outputDevice.Pause();
				progressTimer.Stop();
			}
			PlayPauseButton.Content = "▶";
		}
	}

	private void AddFolderButton_Click(object sender, RoutedEventArgs e)
	{
		OpenFolderDialog openFolderDialog = new OpenFolderDialog();
		if (openFolderDialog.ShowDialog() == true && !libraryFolders.Contains(openFolderDialog.FolderName))
		{
			AddLibraryFolder(openFolderDialog.FolderName);
			UpdateRegisteredFoldersPanel();
			SaveSettings();
		}
	}

	private void OnLibraryFolderChanged(object sender, FileSystemEventArgs e)
	{
		if (isRecording)
		{
			return;
		}
		base.Dispatcher.BeginInvoke((Action)delegate
		{
			if (libraryRefreshTimer == null)
			{
				libraryRefreshTimer = new DispatcherTimer
				{
					Interval = TimeSpan.FromSeconds(1.0)
				};
				libraryRefreshTimer.Tick += delegate
				{
					libraryRefreshTimer?.Stop();
					libraryRefreshTimer = null;
					LibraryRefreshBtn_Click(this, new RoutedEventArgs());
				};
			}
			libraryRefreshTimer.Stop();
			libraryRefreshTimer.Start();
		});
	}

	private void LibraryRefreshBtn_Click(object sender, RoutedEventArgs e)
	{
		libraryFiles.Clear();
		filteredLibraryFiles.Clear();
		LibraryBox.Items.Clear();
		List<string> list = libraryFolders.ToList();
		libraryFolders.Clear();
		foreach (string item in list)
		{
			if (Directory.Exists(item))
			{
				AddLibraryFolder(item);
			}
		}
		UpdateRegisteredFoldersPanel();
	}

	private void UpdateRegisteredFoldersPanel()
	{
		RegisteredFoldersList.Children.Clear();
		foreach (string item in libraryFolders.ToList())
		{
			DockPanel dockPanel = new DockPanel
			{
				Margin = new Thickness(0.0, 2.0, 0.0, 2.0)
			};
			Button button = new Button
			{
				Content = "×",
				Width = 20.0,
				Height = 18.0,
				FontSize = 11.0,
				Tag = item,
				Style = (Style)base.Resources["DarkButton"],
				Margin = new Thickness(0.0, 0.0, 4.0, 0.0),
				VerticalAlignment = VerticalAlignment.Center
			};
			button.Click += delegate(object s, RoutedEventArgs e)
			{
				string f = (string)((Button)s).Tag;
				libraryFolders.Remove(f);
				foreach (FileSystemWatcher item2 in libraryWatchers.Where((FileSystemWatcher w) => w.Path.Equals(f, StringComparison.OrdinalIgnoreCase)).ToList())
				{
					item2.Dispose();
					libraryWatchers.Remove(item2);
				}
				libraryFiles.RemoveAll((string x) => x.StartsWith(f + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
				filteredLibraryFiles.RemoveAll((string x) => x.StartsWith(f + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
				LibraryBox.Items.Clear();
				foreach (string filteredLibraryFile in filteredLibraryFiles)
				{
					LibraryBox.Items.Add(System.IO.Path.GetFileName(filteredLibraryFile));
				}
				UpdateRegisteredFoldersPanel();
				SaveSettings();
			};
			DockPanel.SetDock(button, Dock.Left);
			TextBlock element = new TextBlock
			{
				Text = item,
				FontSize = 10.0,
				Foreground = new SolidColorBrush(Color.FromRgb(153, 153, 153)),
				VerticalAlignment = VerticalAlignment.Center,
				TextTrimming = TextTrimming.CharacterEllipsis,
				ToolTip = item
			};
			dockPanel.Children.Add(button);
			dockPanel.Children.Add(element);
			RegisteredFoldersList.Children.Add(dockPanel);
		}
	}

	private void AddLibraryFolder(string folder)
	{
		libraryFolders.Add(folder);
		if (!libraryWatchers.Any((FileSystemWatcher w) => w.Path.Equals(folder, StringComparison.OrdinalIgnoreCase)))
		{
			FileSystemWatcher fileSystemWatcher = new FileSystemWatcher(folder)
			{
				NotifyFilter = (NotifyFilters.FileName | NotifyFilters.Size),
				Filter = "*.*",
				IncludeSubdirectories = true,
				EnableRaisingEvents = true
			};
			fileSystemWatcher.Created += OnLibraryFolderChanged;
			fileSystemWatcher.Deleted += OnLibraryFolderChanged;
			fileSystemWatcher.Renamed += OnLibraryFolderChanged;
			libraryWatchers.Add(fileSystemWatcher);
		}
		string[] exts = new string[6] { ".mp3", ".wav", ".flac", ".ogg", ".m4a", ".aac" };
		string value = LibrarySearchBox?.Text?.Trim() ?? "";
		foreach (string item in from f in Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
			where exts.Contains(System.IO.Path.GetExtension(f).ToLower())
			orderby f
			select f)
		{
			if (!libraryFiles.Contains(item))
			{
				libraryFiles.Add(item);
				if (string.IsNullOrEmpty(value) || System.IO.Path.GetFileName(item).Contains(value, StringComparison.OrdinalIgnoreCase))
				{
					filteredLibraryFiles.Add(item);
					LibraryBox.Items.Add(System.IO.Path.GetFileName(item));
				}
			}
		}
	}

	private void FilterLibrary(string query)
	{
		LibraryBox.Items.Clear();
		filteredLibraryFiles.Clear();
		IEnumerable<string> enumerable;
		if (!string.IsNullOrEmpty(query))
		{
			enumerable = libraryFiles.Where((string f) => System.IO.Path.GetFileName(f).Contains(query, StringComparison.OrdinalIgnoreCase));
		}
		else
		{
			IEnumerable<string> enumerable2 = libraryFiles;
			enumerable = enumerable2;
		}
		foreach (string item in enumerable)
		{
			filteredLibraryFiles.Add(item);
			LibraryBox.Items.Add(System.IO.Path.GetFileName(item));
		}
	}

	private void LibrarySearchBox_TextChanged(object sender, TextChangedEventArgs e)
	{
		FilterLibrary(LibrarySearchBox.Text.Trim());
	}

	private void LibraryBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
	{
		DependencyObject dependencyObject = e.OriginalSource as DependencyObject;
		while (dependencyObject != null && !(dependencyObject is ListBoxItem))
		{
			dependencyObject = VisualTreeHelper.GetParent(dependencyObject);
		}
		if (!(dependencyObject is ListBoxItem))
		{
			return;
		}
		int selectedIndex = LibraryBox.SelectedIndex;
		if (selectedIndex >= 0)
		{
			List<string> list = ((filteredLibraryFiles.Count > 0) ? filteredLibraryFiles : libraryFiles);
			if (selectedIndex < list.Count)
			{
				AddToPlaylist(list[selectedIndex]);
			}
		}
	}

	private void LibraryBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (e.ClickCount >= 2)
		{
			libraryDragStart = null;
			return;
		}
		libraryDragStart = e.GetPosition(null);
		DependencyObject dependencyObject = e.OriginalSource as DependencyObject;
		while (dependencyObject != null && !(dependencyObject is ListBoxItem))
		{
			dependencyObject = VisualTreeHelper.GetParent(dependencyObject);
		}
		if (dependencyObject is ListBoxItem { IsSelected: not false })
		{
			libPreserveSelection = true;
			libDragFiles = (from string name in LibraryBox.SelectedItems
				select filteredLibraryFiles.FirstOrDefault((string f) => System.IO.Path.GetFileName(f) == name) into f
				where f != null
				select (f)).ToArray();
			e.Handled = true;
		}
		else
		{
			libPreserveSelection = false;
			libDragFiles = Array.Empty<string>();
		}
	}

	private void LibraryBox_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
	{
		if (libPreserveSelection && libraryDragStart.HasValue)
		{
			DependencyObject dependencyObject = e.OriginalSource as DependencyObject;
			while (dependencyObject != null && !(dependencyObject is ListBoxItem))
			{
				dependencyObject = VisualTreeHelper.GetParent(dependencyObject);
			}
			if (dependencyObject is ListBoxItem listBoxItem)
			{
				LibraryBox.SelectedItem = listBoxItem.Content;
			}
		}
		libPreserveSelection = false;
		libraryDragStart = null;
	}

	private void LibraryBox_PreviewMouseMove(object sender, MouseEventArgs e)
	{
		if (e.LeftButton != MouseButtonState.Pressed || !libraryDragStart.HasValue)
		{
			return;
		}
		Point position = e.GetPosition(null);
		if (!(Math.Abs(position.X - libraryDragStart.Value.X) > SystemParameters.MinimumHorizontalDragDistance) && !(Math.Abs(position.Y - libraryDragStart.Value.Y) > SystemParameters.MinimumVerticalDragDistance))
		{
			return;
		}
		libraryDragStart = null;
		string[] array = (from string name in LibraryBox.SelectedItems
			select filteredLibraryFiles.FirstOrDefault((string f) => System.IO.Path.GetFileName(f) == name) into f
			where f != null
			select f).ToArray();
		if (array.Length != 0)
		{
			DataObject data = new DataObject(DataFormats.FileDrop, array);
			DragDrop.DoDragDrop(LibraryBox, data, DragDropEffects.Copy);
		}
	}

	private void OpenFileButton_Click(object sender, RoutedEventArgs e)
	{
		OpenFileDialog openFileDialog = new OpenFileDialog
		{
			Filter = "음악 파일|*.mp3;*.wav;*.flac;*.ogg;*.m4a;*.aac|모든 파일|*.*",
			Multiselect = true
		};
		if (openFileDialog.ShowDialog() == true)
		{
			string[] fileNames = openFileDialog.FileNames;
			foreach (string filePath in fileNames)
			{
				AddToPlaylist(filePath);
			}
			if (currentIndex < 0 && playlist.Count > 0)
			{
				PlayTrack(0);
			}
		}
	}

	private void AddToPlaylist(string filePath)
	{
		if (!playlist.Contains(filePath))
		{
			AddToPlaylistInternal(filePath);
		}
	}

	private void AddToPlaylistInternal(string filePath)
	{
		playlist.Add(filePath);
		PlaylistBox.Items.Add(System.IO.Path.GetFileName(filePath));
	}

	private void PlaylistBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
	{
		DependencyObject dependencyObject = e.OriginalSource as DependencyObject;
		while (dependencyObject != null && !(dependencyObject is ListBoxItem))
		{
			dependencyObject = VisualTreeHelper.GetParent(dependencyObject);
		}
		if (dependencyObject is ListBoxItem && PlaylistBox.SelectedIndex >= 0)
		{
			PlayTrack(PlaylistBox.SelectedIndex);
		}
	}

	private void PlaylistBox_DragOver(object sender, DragEventArgs e)
	{
		if (e.Data.GetDataPresent(typeof(int)))
		{
			e.Effects = DragDropEffects.Move;
		}
		else if (e.Data.GetDataPresent(DataFormats.FileDrop))
		{
			e.Effects = DragDropEffects.Copy;
		}
		else
		{
			e.Effects = DragDropEffects.None;
		}
		e.Handled = true;
	}

	private void PlaylistBox_DragStart(object sender, MouseButtonEventArgs e)
	{
		DependencyObject dependencyObject = e.OriginalSource as DependencyObject;
		while (dependencyObject != null && !(dependencyObject is ListBoxItem))
		{
			dependencyObject = VisualTreeHelper.GetParent(dependencyObject);
		}
		if (dependencyObject is ListBoxItem listBoxItem)
		{
			playlistDragStartPos = e.GetPosition(null);
			playlistDragSourceIdx = PlaylistBox.Items.IndexOf(listBoxItem.Content);
		}
		else
		{
			playlistDragStartPos = null;
			playlistDragSourceIdx = -1;
		}
	}

	private void PlaylistBox_DragMove(object sender, MouseEventArgs e)
	{
		if (e.LeftButton == MouseButtonState.Pressed && playlistDragStartPos.HasValue && playlistDragSourceIdx >= 0)
		{
			Point position = e.GetPosition(null);
			if (Math.Abs(position.X - playlistDragStartPos.Value.X) > SystemParameters.MinimumHorizontalDragDistance || Math.Abs(position.Y - playlistDragStartPos.Value.Y) > SystemParameters.MinimumVerticalDragDistance)
			{
				playlistDragStartPos = null;
				int num = playlistDragSourceIdx;
				playlistDragSourceIdx = -1;
				DragDrop.DoDragDrop(PlaylistBox, num, DragDropEffects.Move);
			}
		}
	}

	private void PlaylistBox_Drop(object sender, DragEventArgs e)
	{
		if (e.Data.GetDataPresent(typeof(int)))
		{
			int num = (int)e.Data.GetData(typeof(int));
			int playlistDropIndex = GetPlaylistDropIndex(e);
			if (num != playlistDropIndex)
			{
				ReorderPlaylist(num, playlistDropIndex);
			}
		}
		else
		{
			if (!e.Data.GetDataPresent(DataFormats.FileDrop))
			{
				return;
			}
			string[] source = (string[])e.Data.GetData(DataFormats.FileDrop);
			string[] exts = new string[6] { ".mp3", ".wav", ".flac", ".ogg", ".m4a", ".aac" };
			bool flag = false;
			foreach (string item in source.Where((string f) => exts.Contains(System.IO.Path.GetExtension(f).ToLower())))
			{
				if (!playlist.Contains(item))
				{
					AddToPlaylistInternal(item);
					flag = true;
				}
			}
			if (flag && currentIndex < 0 && playlist.Count > 0)
			{
				PlayTrack(0);
			}
		}
	}

	private int GetPlaylistDropIndex(DragEventArgs e)
	{
		Point position = e.GetPosition(PlaylistBox);
		for (int i = 0; i < PlaylistBox.Items.Count; i++)
		{
			if (PlaylistBox.ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem listBoxItem)
			{
				double y = listBoxItem.TransformToAncestor(PlaylistBox).Transform(new Point(0.0, 0.0)).Y;
				if (position.Y < y + listBoxItem.ActualHeight / 2.0)
				{
					return i;
				}
			}
		}
		return Math.Max(0, PlaylistBox.Items.Count - 1);
	}

	private void ReorderPlaylist(int src, int dst)
	{
		string item = playlist[src];
		object insertItem = PlaylistBox.Items[src];
		playlist.RemoveAt(src);
		PlaylistBox.Items.RemoveAt(src);
		if (src < dst)
		{
			dst--;
		}
		dst = Math.Min(dst, playlist.Count);
		playlist.Insert(dst, item);
		PlaylistBox.Items.Insert(dst, insertItem);
		if (currentIndex == src)
		{
			currentIndex = dst;
		}
		else if (src < currentIndex && dst >= currentIndex)
		{
			currentIndex--;
		}
		else if (src > currentIndex && dst <= currentIndex)
		{
			currentIndex++;
		}
		PlaylistBox.SelectedIndex = currentIndex;
		UpdatePlaylistHighlight();
	}

	private void PlaylistBox_KeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key != Key.Delete || PlaylistBox.SelectedItems.Count == 0)
		{
			return;
		}
		foreach (int item in (from object item in PlaylistBox.SelectedItems
			select PlaylistBox.Items.IndexOf(item) into i
			where i >= 0
			orderby i descending
			select i).ToList())
		{
			if (item < playlist.Count)
			{
				if (item == currentIndex)
				{
					StopPlayback();
					NowPlayingText.Text = S("재생 중인 파일 없음", "No file playing");
					PlayPauseButton.Content = "▶";
					ProgressSlider.Value = 0.0;
					CurrentTimeText.Text = "0:00";
					TotalTimeText.Text = "0:00";
					currentIndex = -1;
					currentLyricsPath = "";
					UpdateLyricsDisplay();
				}
				else if (item < currentIndex)
				{
					currentIndex--;
				}
				playlist.RemoveAt(item);
				PlaylistBox.Items.RemoveAt(item);
			}
		}
		UpdatePlaylistHighlight();
	}

	private void PlayTrack(int index)
	{
		if (index < 0 || index >= playlist.Count)
		{
			return;
		}
		StopPlayback();
		currentIndex = index;
		abPointA = -1.0;
		abPointB = -1.0;
		UpdateAbInfo();
		try
		{
			audioFileReader = new AudioFileReader(playlist[index]);
			soundTouchStream = new SoundTouchWaveStream(audioFileReader);
			soundTouchStream.PitchSemiTones = (float)PitchSlider.Value;
			soundTouchStream.TempoChange = (float)TempoSlider.Value;
			IWaveProvider source = soundTouchStream;
			monitorBoost = new AudioMonitorBoostProvider(source, (float)VolumeSlider.Value);
			monitorBoost.LevelUpdated += OnInstLevel;
			if (!string.IsNullOrEmpty(_outputDeviceId))
			{
				try
				{
					MMDevice device = new MMDeviceEnumerator().GetDevice(_outputDeviceId);
					outputDevice = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: false, 200);
				}
				catch
				{
					outputDevice = null;
				}
			}
			if (outputDevice == null)
			{
				outputDevice = new WaveOutEvent
				{
					Volume = 1f
				};
			}
			outputDevice.Init(monitorBoost);
			outputDevice.Play();
			NowPlayingText.Text = System.IO.Path.GetFileName(playlist[index]);
			PlayPauseButton.Content = "⏸";
			ProgressSlider.Maximum = audioFileReader.TotalTime.TotalSeconds;
			TotalTimeText.Text = FormatTime(audioFileReader.TotalTime);
			PlaylistBox.SelectedIndex = index;
			progressTimer.Start();
			UpdatePlaylistHighlight();
			currentLyricsPath = playlist[index];
			UpdateLyricsDisplay();
		}
		catch (Exception ex)
		{
			MessageBox.Show("재생 오류: " + ex.Message);
		}
	}

	private void StopPlayback()
	{
		progressTimer.Stop();
		if (outputDevice != null)
		{
			outputDevice.Stop();
			outputDevice.Dispose();
			outputDevice = null;
		}
		soundTouchStream?.Dispose();
		soundTouchStream = null;
		audioFileReader?.Dispose();
		audioFileReader = null;
	}

	private void ApplyPitchTempo()
	{
		if (soundTouchStream != null)
		{
			soundTouchStream.PitchSemiTones = (float)PitchSlider.Value;
			soundTouchStream.TempoChange = (float)TempoSlider.Value;
		}
	}

	private void HandleNaturalEnd()
	{
		int num = currentIndex;
		StopPlayback();
		PlayPauseButton.Content = "▶";
		ProgressSlider.Value = 0.0;
		CurrentTimeText.Text = "0:00";
		if (repeatOne)
		{
			PlayTrack(num);
		}
		else if (shuffleMode && playlist.Count > 1)
		{
			int num2;
			do
			{
				num2 = rng.Next(playlist.Count);
			}
			while (num2 == num);
			playHistory.Push(num);
			PlayTrack(num2);
		}
		else if (num < playlist.Count - 1)
		{
			PlayTrack(num + 1);
		}
		else if (repeatAll && playlist.Count > 0)
		{
			PlayTrack(0);
		}
	}

	private void ProgressTimer_Tick(object? sender, EventArgs e)
	{
		if (audioFileReader == null || isSeeking)
		{
			return;
		}
		IWavePlayer? wavePlayer = outputDevice;
		if (wavePlayer != null && wavePlayer.PlaybackState == PlaybackState.Stopped)
		{
			HandleNaturalEnd();
			return;
		}
		double totalSeconds = audioFileReader.CurrentTime.TotalSeconds;
		ProgressSlider.Value = totalSeconds;
		CurrentTimeText.Text = FormatTime(audioFileReader.CurrentTime);
		if (abPointA >= 0.0 && abPointB > abPointA && totalSeconds >= abPointB)
		{
			audioFileReader.CurrentTime = TimeSpan.FromSeconds(abPointA);
		}
	}

	private void ProgressSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e)
	{
		if (!(e.OriginalSource is Thumb))
		{
			isSeeking = true;
			double num = Math.Clamp(e.GetPosition(ProgressSlider).X / ProgressSlider.ActualWidth, 0.0, 1.0);
			ProgressSlider.Value = ProgressSlider.Minimum + (ProgressSlider.Maximum - ProgressSlider.Minimum) * num;
			if (audioFileReader != null)
			{
				audioFileReader.CurrentTime = TimeSpan.FromSeconds(ProgressSlider.Value);
			}
			ProgressSlider.CaptureMouse();
			e.Handled = true;
		}
	}

	private void ProgressSlider_PreviewMouseMove(object sender, MouseEventArgs e)
	{
		if (isSeeking)
		{
			double num = Math.Clamp(e.GetPosition(ProgressSlider).X / ProgressSlider.ActualWidth, 0.0, 1.0);
			ProgressSlider.Value = ProgressSlider.Minimum + (ProgressSlider.Maximum - ProgressSlider.Minimum) * num;
			if (audioFileReader != null)
			{
				audioFileReader.CurrentTime = TimeSpan.FromSeconds(ProgressSlider.Value);
			}
		}
	}

	private void ProgressSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
	{
		if (isSeeking)
		{
			isSeeking = false;
			ProgressSlider.ReleaseMouseCapture();
		}
	}

	private void ProgressSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (isSeeking)
		{
			CurrentTimeText.Text = FormatTime(TimeSpan.FromSeconds(e.NewValue));
		}
	}

	private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
	{
		if (outputDevice == null)
		{
			if (playlist.Count > 0)
			{
				PlayTrack((currentIndex >= 0) ? currentIndex : 0);
			}
		}
		else if (outputDevice.PlaybackState == PlaybackState.Playing)
		{
			outputDevice.Pause();
			PlayPauseButton.Content = "▶";
			progressTimer.Stop();
		}
		else
		{
			outputDevice.Play();
			PlayPauseButton.Content = "⏸";
			progressTimer.Start();
		}
	}

	private void StopButton_Click(object sender, RoutedEventArgs e)
	{
		StopPlayback();
		PlayPauseButton.Content = "▶";
		ProgressSlider.Value = 0.0;
		CurrentTimeText.Text = "0:00";
	}

	private void PrevButton_Click(object sender, RoutedEventArgs e)
	{
		if (playlist.Count != 0)
		{
			if (shuffleMode && playHistory.Count > 0)
			{
				PlayTrack(playHistory.Pop());
			}
			else if (currentIndex > 0)
			{
				PlayTrack(currentIndex - 1);
			}
			else if (repeatAll)
			{
				PlayTrack(playlist.Count - 1);
			}
		}
	}

	private void NextButton_Click(object sender, RoutedEventArgs e)
	{
		if (playlist.Count == 0)
		{
			return;
		}
		if (shuffleMode && playlist.Count > 1)
		{
			int num;
			do
			{
				num = rng.Next(playlist.Count);
			}
			while (num == currentIndex);
			playHistory.Push(currentIndex);
			PlayTrack(num);
		}
		else if (currentIndex < playlist.Count - 1)
		{
			PlayTrack(currentIndex + 1);
		}
		else if (repeatAll)
		{
			PlayTrack(0);
		}
	}

	private void VolumeSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e)
	{
		if (!(e.OriginalSource is Thumb))
		{
			Point position = e.GetPosition(VolumeSlider);
			VolumeSlider.Value = Math.Clamp(position.X / VolumeSlider.ActualWidth * 1.5, 0.0, 1.5);
			isVolumeDragging = true;
			VolumeSlider.CaptureMouse();
			e.Handled = true;
		}
	}

	private void VolumeSlider_PreviewMouseMove(object sender, MouseEventArgs e)
	{
		if (isVolumeDragging)
		{
			Point position = e.GetPosition(VolumeSlider);
			VolumeSlider.Value = Math.Clamp(position.X / VolumeSlider.ActualWidth * 1.5, 0.0, 1.5);
		}
	}

	private void VolumeSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
	{
		if (isVolumeDragging)
		{
			isVolumeDragging = false;
			VolumeSlider.ReleaseMouseCapture();
		}
	}

	private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		double newValue = e.NewValue;
		if (VolumeText != null)
		{
			VolumeText.Text = $"{(int)(newValue * 100.0)}%";
			VolumeText.Foreground = ((newValue > 1.0) ? new SolidColorBrush(Color.FromRgb(byte.MaxValue, 102, 102)) : new SolidColorBrush(Color.FromRgb(170, 170, 170)));
		}
		if (monitorBoost != null)
		{
			monitorBoost.Volume = (float)newValue;
		}
	}

	private void PitchSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (PitchText != null)
		{
			PitchText.Text = ((int)e.NewValue).ToString("+0;-0;0");
		}
		ApplyPitchTempo();
	}

	private void PitchMinusBtn_Click(object sender, RoutedEventArgs e)
	{
		if (PitchSlider.Value > PitchSlider.Minimum)
		{
			PitchSlider.Value -= 1.0;
		}
	}

	private void PitchPlusBtn_Click(object sender, RoutedEventArgs e)
	{
		if (PitchSlider.Value < PitchSlider.Maximum)
		{
			PitchSlider.Value += 1.0;
		}
	}

	private void PitchResetBtn_Click(object sender, RoutedEventArgs e)
	{
		PitchSlider.Value = 0.0;
	}

	private void TempoSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (TempoText != null)
		{
			TempoText.Text = $"{100 + (int)e.NewValue}%";
		}
		ApplyPitchTempo();
	}

	private void TempoMinusBtn_Click(object sender, RoutedEventArgs e)
	{
		if (TempoSlider.Value > TempoSlider.Minimum)
		{
			TempoSlider.Value -= 1.0;
		}
	}

	private void TempoPlusBtn_Click(object sender, RoutedEventArgs e)
	{
		if (TempoSlider.Value < TempoSlider.Maximum)
		{
			TempoSlider.Value += 1.0;
		}
	}

	private void TempoResetBtn_Click(object sender, RoutedEventArgs e)
	{
		TempoSlider.Value = 0.0;
	}

	private void UpdateLyricsDisplay()
	{
		if (LyricsBox == null)
		{
			return;
		}
		lyricsEditMode = false;
		LyricsBox.IsReadOnly = true;
		EditLyricsBtn.Content = "편집";
		SaveLyricsBtn.IsEnabled = false;
		if (string.IsNullOrEmpty(currentLyricsPath))
		{
			LyricsBox.Text = "";
			return;
		}
		try
		{
			using TagLib.File file = TagLib.File.Create(new SharedReadFileAbstraction(currentLyricsPath));
			string lyrics = file.Tag.Lyrics;
			LyricsBox.Text = (string.IsNullOrEmpty(lyrics) ? "가사 없음\n\n편집 버튼을 눌러 가사를 추가하세요." : lyrics);
		}
		catch
		{
			LyricsBox.Text = "가사를 불러올 수 없습니다.";
		}
	}

	private void EditLyricsBtn_Click(object sender, RoutedEventArgs e)
	{
		lyricsEditMode = !lyricsEditMode;
		if (lyricsEditMode)
		{
			if (LyricsBox.Text == "가사 없음\n\n편집 버튼을 눌러 가사를 추가하세요." || LyricsBox.Text == "가사를 불러올 수 없습니다.")
			{
				LyricsBox.Text = "";
			}
			LyricsBox.IsReadOnly = false;
			LyricsBox.Focus();
			EditLyricsBtn.Content = "취소";
			SaveLyricsBtn.IsEnabled = !string.IsNullOrEmpty(currentLyricsPath);
		}
		else
		{
			UpdateLyricsDisplay();
		}
	}

	private void SaveLyricsBtn_Click(object sender, RoutedEventArgs e)
	{
		if (string.IsNullOrEmpty(currentLyricsPath))
		{
			return;
		}
		try
		{
			IWavePlayer? wavePlayer = outputDevice;
			bool flag = wavePlayer != null && wavePlayer.PlaybackState == PlaybackState.Playing;
			double num = audioFileReader?.CurrentTime.TotalSeconds ?? 0.0;
			int num2 = currentIndex;
			StopPlayback();
			using (TagLib.File file = TagLib.File.Create(currentLyricsPath))
			{
				file.Tag.Lyrics = LyricsBox.Text;
				file.Save();
			}
			lyricsEditMode = false;
			LyricsBox.IsReadOnly = true;
			EditLyricsBtn.Content = "편집";
			SaveLyricsBtn.IsEnabled = false;
			if (num2 >= 0)
			{
				PlayTrack(num2);
				if (audioFileReader != null && num > 0.0)
				{
					audioFileReader.CurrentTime = TimeSpan.FromSeconds(num);
				}
				if (!flag)
				{
					outputDevice?.Pause();
					PlayPauseButton.Content = "▶";
					progressTimer.Stop();
				}
			}
		}
		catch (Exception ex)
		{
			MessageBox.Show("가사 저장 실패: " + ex.Message);
		}
	}

	private void DecreaseLyricsFontBtn_Click(object sender, RoutedEventArgs e)
	{
		if (lyricsFontSize > 8)
		{
			lyricsFontSize -= 2;
			LyricsBox.FontSize = lyricsFontSize;
			SaveSettings();
		}
	}

	private void ResetLyricsFontBtn_Click(object sender, RoutedEventArgs e)
	{
		lyricsFontSize = 14;
		LyricsBox.FontSize = lyricsFontSize;
		SaveSettings();
	}

	private void IncreaseLyricsFontBtn_Click(object sender, RoutedEventArgs e)
	{
		if (lyricsFontSize < 48)
		{
			lyricsFontSize += 2;
			LyricsBox.FontSize = lyricsFontSize;
			SaveSettings();
		}
	}

	private void LyricsBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
	{
		if (Keyboard.Modifiers == ModifierKeys.Control)
		{
			if (e.Delta > 0)
			{
				IncreaseLyricsFontBtn_Click(sender, new RoutedEventArgs());
			}
			else
			{
				DecreaseLyricsFontBtn_Click(sender, new RoutedEventArgs());
			}
			e.Handled = true;
		}
	}

	private void UpdatePlaylistHighlight()
	{
		base.Dispatcher.InvokeAsync(delegate
		{
			for (int i = 0; i < PlaylistBox.Items.Count; i++)
			{
				if (PlaylistBox.ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem listBoxItem)
				{
					listBoxItem.Foreground = ((i == currentIndex) ? new SolidColorBrush(Color.FromRgb(74, 158, byte.MaxValue)) : Brushes.White);
					listBoxItem.FontWeight = ((i == currentIndex) ? FontWeights.SemiBold : FontWeights.Normal);
				}
			}
		}, DispatcherPriority.Loaded);
	}

	private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
	{
		if (Keyboard.FocusedElement is System.Windows.Controls.TextBox || Keyboard.FocusedElement is ListBoxItem)
		{
			return;
		}
		switch (e.Key)
		{
		case Key.Space:
			PlayPauseButton_Click(this, new RoutedEventArgs());
			e.Handled = true;
			break;
		case Key.Left:
			if (Keyboard.Modifiers == ModifierKeys.None)
			{
				PrevButton_Click(this, new RoutedEventArgs());
				e.Handled = true;
			}
			break;
		case Key.Right:
			if (Keyboard.Modifiers == ModifierKeys.None)
			{
				NextButton_Click(this, new RoutedEventArgs());
				e.Handled = true;
			}
			break;
		case Key.W:
			PitchPlusBtn_Click(this, new RoutedEventArgs());
			e.Handled = true;
			break;
		case Key.S:
			PitchMinusBtn_Click(this, new RoutedEventArgs());
			e.Handled = true;
			break;
		case Key.D:
			TempoPlusBtn_Click(this, new RoutedEventArgs());
			e.Handled = true;
			break;
		case Key.A:
			TempoMinusBtn_Click(this, new RoutedEventArgs());
			e.Handled = true;
			break;
		case Key.Q:
			AbSetABtn_Click(this, new RoutedEventArgs());
			e.Handled = true;
			break;
		case Key.E:
			AbSetBBtn_Click(this, new RoutedEventArgs());
			e.Handled = true;
			break;
		case Key.R:
			AbClearBtn_Click(this, new RoutedEventArgs());
			e.Handled = true;
			break;
		case Key.Z:
			PitchResetBtn_Click(this, new RoutedEventArgs());
			e.Handled = true;
			break;
		case Key.C:
			TempoResetBtn_Click(this, new RoutedEventArgs());
			e.Handled = true;
			break;
		case Key.X:
			PitchSlider.Value = 0.0;
			TempoSlider.Value = 0.0;
			e.Handled = true;
			break;
		}
	}

	private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
	{
		if (Keyboard.Modifiers == ModifierKeys.Control)
		{
			return;
		}
		for (DependencyObject dependencyObject = e.OriginalSource as DependencyObject; dependencyObject != null; dependencyObject = VisualTreeHelper.GetParent(dependencyObject))
		{
			if (dependencyObject is ListBox || dependencyObject is ScrollViewer)
			{
				return;
			}
		}
		VolumeSlider.Value = Math.Clamp(VolumeSlider.Value + ((e.Delta > 0) ? 0.05 : (-0.05)), 0.0, 1.5);
		e.Handled = true;
	}

	private static string FormatTime(TimeSpan ts)
	{
		if (ts.TotalHours >= 1.0)
		{
			return $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}";
		}
		return $"{ts.Minutes}:{ts.Seconds:D2}";
	}

	protected override void OnClosed(EventArgs e)
	{
		SaveSettings();
		StopPlayback();
		if (isRecording)
		{
			StopRecording();
		}
		StopMicMonitor();
		foreach (FileSystemWatcher libraryWatcher in libraryWatchers)
		{
			libraryWatcher.Dispose();
		}
		libraryWatchers.Clear();
		base.OnClosed(e);
	}

	private async void YtDownloadBtn_Click(object sender, RoutedEventArgs e)
	{
		if (isYtDownloading)
		{
			return;
		}
		string text = YtUrlBox.Text.Trim();
		if (string.IsNullOrEmpty(text))
		{
			MessageBox.Show(S("URL을 입력해주세요.", "Please enter a URL."));
			return;
		}
		if (libraryFolders.Count == 0)
		{
			MessageBox.Show(S("보관함에 폴더를 먼저 등록해주세요.", "Please add a folder to the library first."));
			return;
		}
		string ytdlp = FindYtDlp();
		if (ytdlp == null)
		{
			MessageBox.Show(S("yt-dlp.exe를 찾을 수 없습니다.\n앱 폴더 또는 PATH에 yt-dlp.exe를 넣어주세요.", "yt-dlp.exe not found.\nPlace yt-dlp.exe in the app folder or PATH."));
			return;
		}
		string savePath = libraryFolders[0];
		string fmt = (YtFormatCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "m4a";
		isYtDownloading = true;
		YtDownloadBtn.IsEnabled = false;
		YtDownloadBtn.Content = S("다운로드 중...", "Downloading...");
		try
		{
			string[] audioExts = new string[7] { ".mp3", ".wav", ".flac", ".ogg", ".m4a", ".aac", ".opus" };
			HashSet<string> before = new HashSet<string>(from f in Directory.EnumerateFiles(savePath, "*.*", SearchOption.TopDirectoryOnly)
				where audioExts.Contains(System.IO.Path.GetExtension(f).ToLower())
				select f);
			string args = BuildYtDlpArgs(text, fmt, savePath);
			int exitCode = await Task.Run(() => RunYtDlp(ytdlp, args));
			if (exitCode != 0)
			{
				YtDownloadBtn.Content = S("yt-dlp 업데이트 중...", "Updating yt-dlp...");
				bool updated = await Task.Run(() => RunYtDlp(ytdlp, "-U") == 0);
				if (updated)
				{
					YtDownloadBtn.Content = S("다시 시도 중...", "Retrying...");
					exitCode = await Task.Run(() => RunYtDlp(ytdlp, args));
				}
				if (exitCode != 0)
				{
					MessageBox.Show(S("다운로드 실패. URL을 확인해주세요.\n(yt-dlp 자동 업데이트 후에도 실패했습니다.)", "Download failed. Please check the URL.\n(Failed even after auto-updating yt-dlp.)"));
					return;
				}
			}
			List<string> list = (from f in Directory.EnumerateFiles(savePath, "*.*", SearchOption.TopDirectoryOnly)
				where !before.Contains(f) && audioExts.Contains(System.IO.Path.GetExtension(f).ToLower())
				select f).ToList();
			LibraryRefreshBtn_Click(this, new RoutedEventArgs());
			downloadedFiles.AddRange(list.Where(f => !downloadedFiles.Contains(f)));
			if (list.Count > 0)
			{
				AddToPlaylist(list[0]);
				PlayTrack(playlist.Count - 1);
				YtUrlBox.Clear();
			}
		}
		catch (Exception ex)
		{
			MessageBox.Show(S("오류: " + ex.Message, "Error: " + ex.Message));
		}
		finally
		{
			isYtDownloading = false;
			YtDownloadBtn.IsEnabled = true;
			YtDownloadBtn.Content = S("↓ 다운로드", "↓ Download");
		}
	}

	private static int RunYtDlp(string ytdlpPath, string args)
	{
		try
		{
			using Process process = Process.Start(new ProcessStartInfo(ytdlpPath, args)
			{
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true
			});
			process.ErrorDataReceived += delegate
			{
			};
			process.BeginErrorReadLine();
			process.StandardOutput.ReadToEnd();
			process.WaitForExit();
			return process.ExitCode;
		}
		catch
		{
			return -1;
		}
	}

	private static string? FindYtDlp()
	{
		string text = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "yt-dlp.exe");
		if (System.IO.File.Exists(text))
		{
			return text;
		}
		string[] array = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator);
		for (int i = 0; i < array.Length; i++)
		{
			string text2 = System.IO.Path.Combine(array[i].Trim(), "yt-dlp.exe");
			if (System.IO.File.Exists(text2))
			{
				return text2;
			}
		}
		return null;
	}

	private static string BuildYtDlpArgs(string url, string fmt, string savePath)
	{
		string text = System.IO.Path.Combine(savePath, "%(title)s.%(ext)s");
		string text2 = ((fmt == "m4a") ? "bestaudio[ext=m4a]/bestaudio[acodec=aac]/bestaudio" : ((!(fmt == "opus")) ? "bestaudio" : "bestaudio[ext=webm]/bestaudio[acodec=opus]/bestaudio"));
		string text3 = text2;
		List<string> list = new List<string>
		{
			"-x",
			"--audio-format " + fmt,
			(fmt == "mp3") ? "--audio-quality 320k" : "--audio-quality 0",
			"--format \"" + text3 + "\"",
			"-o \"" + text + "\"",
			"--no-playlist",
			"\"" + url + "\""
		};
		string[] array = new string[3] { "C:\\Program Files\\KMPlayer 64X\\LAVFilters64", "C:\\ffmpeg\\bin", "C:\\Program Files\\ffmpeg\\bin" };
		foreach (string text4 in array)
		{
			if (System.IO.File.Exists(System.IO.Path.Combine(text4, "ffmpeg.exe")))
			{
				list.Insert(0, "--ffmpeg-location \"" + text4 + "\"");
				break;
			}
		}
		return string.Join(" ", list);
	}

	private void OnInstLevel(float rms)
	{
		_instRms = rms;
		_instLastTick = Stopwatch.GetTimestamp();
	}

	private static float CalcRms16(byte[] buf, int count)
	{
		if (count < 2)
		{
			return 0f;
		}
		double num = 0.0;
		int num2 = count / 2;
		for (int i = 0; i < count - 1; i += 2)
		{
			float num3 = (float)BitConverter.ToInt16(buf, i) / 32768f;
			num += (double)(num3 * num3);
		}
		return (float)Math.Sqrt(num / (double)num2);
	}

	private void VisualTimer_Tick(object? sender, EventArgs e)
	{
		long timestamp = Stopwatch.GetTimestamp();
		double num = Stopwatch.Frequency;
		if (_instLastTick == 0L || (double)(timestamp - _instLastTick) / num > 0.3)
		{
			_instRms = 0f;
		}
		if (_micLastTick == 0L || (double)(timestamp - _micLastTick) / num > 0.3)
		{
			_micRms = 0f;
		}
		_instPeak = Math.Max(_instRms, _instPeak * 0.9f);
		_micPeak = Math.Max(_micRms, _micPeak * 0.9f);
		UpdateVuMeter(InstVuTrack, InstVuBar, InstDbText, _instPeak);
		UpdateVuMeter(MicVuTrack, MicVuBar, MicDbText, _micPeak);
		double length = InstVuTrack.ActualWidth * 0.8;
		double length2 = MicVuTrack.ActualWidth * 0.8;
		Canvas.SetLeft(InstThresholdLine, length);
		Canvas.SetLeft(MicThresholdLine, length2);
	}

	private static void UpdateVuMeter(Border track, Rectangle bar, TextBlock label, float rms)
	{
		double val = (((double)rms > 1E-06) ? (20.0 * Math.Log10(rms)) : (-60.0));
		val = Math.Max(-60.0, Math.Min(0.0, val));
		double num = (val + 60.0) / 60.0;
		double val2 = num * track.ActualWidth;
		bar.Width = Math.Max(0.0, val2);
		byte r;
		byte g;
		byte b;
		if (num < 0.7)
		{
			r = 126;
			g = 87;
			b = 194;
		}
		else if (num < 0.9)
		{
			r = byte.MaxValue;
			g = 193;
			b = 7;
		}
		else
		{
			r = 244;
			g = 67;
			b = 54;
		}
		bar.Fill = new SolidColorBrush(Color.FromRgb(r, g, b));
		label.Text = ((val <= -59.9) ? "-∞ dB" : $"{val:F0} dB");
		label.Foreground = ((num >= 0.9) ? new SolidColorBrush(Color.FromRgb(244, 67, 54)) : new SolidColorBrush(Color.FromRgb(74, 74, 74)));
	}

	private void PlaylistBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
	}

}
