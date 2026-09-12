// Prospero Explorer
// Copyright (C) 2026 SvenGDK

using ProsperoExplorer.Shell;
using SharpProspero.Application;
using SharpProspero.Audio;
using SharpProspero.Graphics;
using SharpProspero.Interop;
using SharpProspero.Interop.Pad;
using SharpProspero.Media;
using SharpProspero.Platform;
using SharpProspero.Storage;
using SharpProspero.Text;
using SharpProspero.Threading;
using SharpProspero.Ui;
using System;
using System.Collections.Generic;

// The element that shows a picture, named apart from anything else called an image so the buffer it is
// fed from and the control that draws it never read as the same thing.
using PictureElement = SharpProspero.Ui.Image;

namespace ProsperoExplorer.Media;

/// <summary>
/// Plays one media file: video with its sound, or sound on its own. The picture is drawn into a buffer
/// this page owns and shown through an <see cref="SharpProspero.Ui.Image"/>, so the page is built out of
/// elements like every other one and the shell still owns the layout.
/// </summary>
/// <remarks>
/// Two facts about the hardware shape this page. The audio output holds a small queue the page can see
/// into, so a frame tops it up without ever waiting on it: the queue depth, not a guess, decides how far
/// ahead the sound runs, and the buttons keep answering. The position and, for a video, the picture
/// follow the sound the output reports it has played rather than the point the decoder has read ahead to,
/// so neither runs in front of what is heard. And the output takes one fixed rate, so decoded samples are
/// resampled to that rate rather than the output being opened at whatever rate the file happens to carry.
/// </remarks>
internal sealed class MediaScreen : ExplorerScreen
{
    // The rate the output is opened at. It takes this and one other and refuses everything else.
    private const uint OutputSampleRate = 48000;

    // Samples per block, per channel. 512 frames is about eleven milliseconds of sound.
    private const uint OutputGrain = 512;

    // How many blocks the output queue holds. At this grain each block is about eleven milliseconds, so
    // four is roughly forty milliseconds of buffered sound: enough to ride out a frame that runs long
    // without the sound leading the picture in any way a viewer would notice.
    private const uint QueueDepth = 4;

    // Decoded sound waits here between the decoder and the output. It is kept to about a quarter second
    // so a seek is heard almost at once and the buffered sound never grows large; the output's own queue,
    // not this, decides how far ahead playback runs.
    private const int MaxPendingSamples = (int)(OutputSampleRate / 4) * 2;

    // How many decoded audio frames are collected per frame of the application.
    private const int MaxAudioFramesPerTick = 8;

    // How many decoded pictures may be presented in one frame. More than one is only reached when the
    // application fell behind and the sound has run past several pictures, which are then caught up on.
    private const int MaxVideoFramesPerTick = 4;

    // How often the idle timer is pushed back while a file plays and the pad is untouched.
    private const double KeepAwakeIntervalSeconds = 1.0;

    // The samples decoded here are all held in memory at once, and the copies made to bring them to the
    // output's format hold as much again, so a source is refused past these ceilings the way a picture is.
    // The file is bounded first, per format, so the decode itself is kept within reach: sixteen-bit sound
    // decodes about one to one, while the compact form expands several times over. The decoded sound is
    // then bounded again as a final check before the copies are made.
    private const long MaxWavFileBytes = 48L * 1024 * 1024;
    private const long MaxVagFileBytes = 12L * 1024 * 1024;
    private const long MaxDecodedSampleBytes = 48L * 1024 * 1024;

    // Reading tags means reading the file, so a large one is left untagged rather than holding the
    // opening up for the seconds that would take.
    private const long TagReadLimitBytes = 12L * 1024 * 1024;

    private const int SeekStepSeconds = 10;
    private const int VolumeStep = 5;

    // The picture area for a file with no video, and the strip the elapsed time and the controls are
    // drawn in. The strip is kept clear of the picture so it can be redrawn every frame without
    // painting over pixels the decoder only hands out once.
    private const int SoundOnlyHeight = 420;
    private const int OverlayStripHeight = 96;
    private const int MinCanvasHeight = 120;
    private const int DefaultCanvasWidth = 1280;
    private const int DefaultCanvasHeight = 480;

    // Line steps of the sound-only panel: a glyph is eight pixels tall before the scale is applied.
    private const int TitleStep = 40;
    private const int TagStep = 24;
    private const int TimeStep = 40;
    private const int LevelHeight = 16;

    private readonly string _path;
    private readonly string _name;
    private readonly Queue<short> _pending = new();

    private StackPanel? _root;
    private PictureElement? _picture;
    private PixelBuffer? _canvas;
    private Label? _headline;
    private Label? _note;
    private KeyValueRow? _tagRow;
    private ProgressBar? _bar;
    private KeyValueRow? _timeRow;
    private KeyValueRow? _stateRow;

    private BackgroundOperation<MediaSource>? _opening;
    private MediaPlayer? _player;
    private AudioQueueDevice? _audio;
    private short[] _block = [];
    private bool _audioRefused;

    private short[]? _clip;
    private int _clipCursor;
    private long _clipDurationMilliseconds;

    // The clock that follows the sound the output has actually played. _headMillis is the media time of
    // the sample at the front of _pending - the next to be handed over - once the first frame has set it;
    // subtracting the sound still waiting in the output's queue gives the time being heard right now.
    private double _headMillis;
    private bool _audioAnchored;
    private long _queuedFrames;

    // The sub-sample read position carried past a frame's end into the next, so resampling keeps its
    // pitch and timing continuous across the frame boundaries rather than resetting at each one.
    private double _resamplePhase;

    // The media time of the last picture handed to the canvas, and a wall-clock reading that stands in as
    // the position when a file carries no sound to follow.
    private double _shownVideoMillis;
    private double _wallClockMillis;
    private double _awakeAccumulator;

    private MediaTags _tags = MediaTags.Empty;
    private string _tagSummary = "";
    private string _sourceNote = "";
    private PlaybackState _state = PlaybackState.Opening;

    private bool _openStarted;
    private bool _autoPlayRequested;
    private bool _resumeOnReturn;
    private bool _closed;
    private bool _settingsChanged;
    private bool _hasVideo;
    private bool _repaintAll = true;
    private int _openingPercent;
    private float _level;

    private bool _loop;
    private bool _overlay;
    private int _volume;

    /// <summary>Opens the page for the file at <paramref name="mediaPath"/>.</summary>
    public MediaScreen(ExplorerShell shell, string mediaPath) : base(shell)
    {
        _path = mediaPath;
        _name = PathUtil.GetFileName(mediaPath);
        _loop = shell.Settings.MediaLoop;
        _overlay = shell.Settings.MediaShowOverlay;
        _volume = Math.Clamp(shell.Settings.MediaVolume, 0, 100);
    }

    /// <inheritdoc />
    public override string Title => _name;

    /// <inheritdoc />
    public override string Hint => "Cross pauses, Circle goes back.";

    /// <inheritdoc />
    protected override UiElement BuildRoot()
    {
        EnsureCanvas(DefaultCanvasWidth, DefaultCanvasHeight);

        _picture = new PictureElement(_canvas!.AsSurface());
        _headline = new Label(_name) { Scale = 3 };
        _note = new Label { Scale = 2, TextColor = Shell.Theme.TextMuted, Visible = false };
        _tagRow = new KeyValueRow("Track") { Visible = false };
        _bar = new ProgressBar();
        _timeRow = new KeyValueRow("Position", "0:00");
        _stateRow = new KeyValueRow("Playback", "opening");

        _root = new StackPanel()
            .Add(_picture)
            .Add(_headline)
            .Add(_note)
            .Add(_tagRow)
            .Add(_bar)
            .Add(_timeRow)
            .Add(_stateRow);
        return _root;
    }

    /// <inheritdoc />
    public override void OnShown()
    {
        if (!_openStarted)
        {
            BeginOpen(Shell.Settings.MediaAutoPlay);
            return;
        }

        if (!_resumeOnReturn)
            return;
        _resumeOnReturn = false;
        ResumePlayback(announce: false);
    }

    /// <inheritdoc />
    public override void OnHidden()
    {
        PersistSettings();

        // Nothing advances this page while another one is in front, so the sound would stop of its own
        // accord after the block already queued. Pausing makes that deliberate and keeps the position.
        if (_state != PlaybackState.Playing)
            return;
        PausePlayback(announce: false);
        _resumeOnReturn = _state == PlaybackState.Paused;
    }

    /// <inheritdoc />
    public override void Tick(FrameContext context)
    {
        try
        {
            FitCanvas();
            HandleInput(context);

            // Anything that changed the shape of the picture area - a resize, the overlay going on or
            // off - clears the buffer here, before this frame's picture is drawn into it rather than
            // after, which would take that picture away again for a frame.
            ClearCanvasIfAsked();

            AdvancePlayback(context.DeltaSeconds);
            PaintCanvas();
            UpdateRows();
        }
        catch (Exception error)
        {
            // A failure inside playback ends the playback, not the application.
            StopEverything();
            _state = PlaybackState.Failed;
            _sourceNote = ExplorerShell.Describe(error);
            _repaintAll = true;
            Shell.ReportFailure("Playback stopped", error);
        }
    }

    /// <inheritdoc />
    protected override void OnDispose()
    {
        _closed = true;
        PersistSettings();
        AbandonOpening();
        StopEverything();

        // The press that closes a page is read inside the same call that draws it, so the tree is drawn
        // once more after this runs. The element is emptied first, or that draw reads the freed pixels.
        _picture?.SetContent(default);
        _canvas?.Dispose();
        _canvas = null;
    }

    // Opening.

    private void BeginOpen(bool autoPlay)
    {
        _openStarted = true;
        _autoPlayRequested = autoPlay;
        _openingPercent = 0;
        _state = PlaybackState.Opening;

        string path = _path;
        bool loop = _loop;
        _opening = new BackgroundOperation<MediaSource>(() => LoadSource(path, autoPlay, loop), "media-open");

        // Opening a source runs on the player's own thread and can take seconds, so it is waited on from
        // the frame loop behind the system's progress box rather than inside a frame.
        Shell.Dialogs.RunWithProgress($"Opening {_name}", ReportOpening, FinishOpen);
    }

    private bool ReportOpening(MessageDialog dialog)
    {
        BackgroundOperation<MediaSource>? work = _opening;
        if (work is null || work.IsComplete)
        {
            dialog.SetProgress(100);
            return true;
        }

        // There is nothing to measure - the reading happens inside the player - so the bar only shows
        // that the work is still going, and stops short of the end until it actually finishes.
        if (_openingPercent < 95)
            _openingPercent++;
        dialog.SetProgress(_openingPercent);
        return false;
    }

    private void FinishOpen()
    {
        BackgroundOperation<MediaSource>? work = _opening;
        _opening = null;
        if (work is null)
            return;

        if (_closed)
        {
            // The page went away while the file was being opened. Release what the work produced.
            if (!work.Failed)
                work.Result.Player?.Dispose();
            return;
        }

        if (work.Failed)
        {
            Exception error = work.Error!;
            _state = PlaybackState.Failed;
            _sourceNote = ExplorerShell.Describe(error);
            _repaintAll = true;
            Shell.ReportFailure($"{_name} would not open", error, important: true);
            return;
        }

        MediaSource source = work.Result;
        _player = source.Player;
        _clip = source.Clip;
        _clipDurationMilliseconds = source.DurationMilliseconds;
        _tags = source.Tags;
        _tagSummary = SummariseTags(source.Tags);
        _sourceNote = source.Note;
        _clipCursor = 0;
        _state = _autoPlayRequested ? PlaybackState.Playing : PlaybackState.Paused;
        _repaintAll = true;

        Shell.Status(source.Note.Length > 0 ? source.Note : $"Playing {_name}.");
    }

    // Runs on a background thread, so it touches nothing the frame loop owns.
    private static MediaSource LoadSource(string path, bool autoPlay, bool loop)
    {
        var source = new MediaSource { Tags = ReadTags(path) };
        string extension = PathUtil.GetExtension(path).ToLowerInvariant();

        // The player takes a container it recognises and refuses the rest. For the two forms this SDK
        // decodes itself, the samples are decoded here and played through the output directly.
        if (extension is ".wav" or ".vag")
        {
            // Refuse before the read, then again after decoding, so neither the file nor the samples it
            // expands into can exhaust the heap; the message names the size and the limit like a picture.
            // The file limit is per format because the compact form expands several times as it decodes.
            long fileBytes = FileSystem.GetFileSize(path);
            long maxFileBytes = extension == ".wav" ? MaxWavFileBytes : MaxVagFileBytes;
            if (fileBytes > maxFileBytes)
                throw new NotSupportedException(
                    $"{PathUtil.GetFileName(path)} is {TextFormat.ByteSize(fileBytes)}. Its sound is read and decoded whole into memory, so this stops at {TextFormat.ByteSize(maxFileBytes)}.");

            PcmAudio decoded = extension == ".wav" ? WavAudio.Load(path) : VagAudio.Load(path);
            // The decode is resampled to the output rate and spread to stereo before it plays, so the
            // size that must fit in memory is the projected output, not the raw decode. Bound both, so a
            // small file that expands (a low-rate mono clip becoming 48 kHz stereo) is caught before the
            // larger buffer is allocated.
            long decodedBytes = (long)decoded.Samples.Length * sizeof(short);
            long frames = decoded.Channels > 0 ? (long)decoded.Samples.Length / decoded.Channels : decoded.Samples.Length;
            long outFrames = decoded.SampleRate > 0 ? frames * OutputSampleRate / (uint)decoded.SampleRate : frames;
            long projectedBytes = outFrames * 2 * sizeof(short);
            long peakBytes = Math.Max(decodedBytes, projectedBytes);
            if (peakBytes > MaxDecodedSampleBytes)
                throw new NotSupportedException(
                    $"{PathUtil.GetFileName(path)} decodes to {TextFormat.ByteSize(peakBytes)} of sound to play, so this stops at {TextFormat.ByteSize(MaxDecodedSampleBytes)}.");

            PcmAudio ready = ToOutputFormat(decoded);
            source.Clip = ready.Samples;
            source.DurationMilliseconds = ready.DurationMilliseconds;
            source.Note = "The player does not take this file, so its samples are decoded and played here.";
            return source;
        }

        MediaPlayer player = MediaPlayer.Open(path);
        try
        {
            player.SetLooping(loop);

            // Starting waits for the source to be read, which is the slow part; pausing straight after
            // keeps that wait off the frame loop even when playback is not meant to begin at once.
            player.Start();
            if (!autoPlay)
                player.Pause();
        }
        catch (Exception)
        {
            player.Dispose();
            throw;
        }

        source.Player = player;
        return source;
    }

    private static PcmAudio ToOutputFormat(PcmAudio decoded)
    {
        PcmAudio ready = decoded;
        if (ready.SampleRate != (int)OutputSampleRate)
            ready = AudioClip.Resample(ready, (int)OutputSampleRate);
        if (ready.Channels > 2)
            ready = AudioClip.ToMono(ready);
        if (ready.Channels == 1)
            ready = AudioClip.ToStereo(ready);
        return ready;
    }

    private static MediaTags ReadTags(string path)
    {
        try
        {
            if (FileSystem.GetFileSize(path) > TagReadLimitBytes)
                return MediaTags.Empty;
            return MediaMetadata.Read(FileSystem.ReadAllBytes(path));
        }
        catch (Exception)
        {
            // Tags are decoration. A file that will not give them up still plays.
            return MediaTags.Empty;
        }
    }

    private static string SummariseTags(MediaTags tags)
    {
        if (tags.IsEmpty)
            return "";

        var parts = new List<string>(5);
        Append(parts, tags.Title);
        Append(parts, tags.Artist);
        Append(parts, tags.Album);
        Append(parts, tags.Year);
        Append(parts, tags.Genre);
        return string.Join(" - ", parts);

        static void Append(List<string> into, string value)
        {
            if (value.Length > 0)
                into.Add(value);
        }
    }

    // Playback.

    private void AdvancePlayback(double deltaSeconds)
    {
        if (_state is PlaybackState.Opening or PlaybackState.Failed)
            return;

        if (_state == PlaybackState.Playing)
        {
            // Advanced whether or not there is sound, so the picture of a silent video still has a clock
            // to pace itself against; it is only read as the position when no sound is there to follow.
            _wallClockMillis += deltaSeconds * 1000.0;
            HoldSystemAwake(deltaSeconds);
        }

        _queuedFrames = QueuedFrames();

        if (_player is not null)
            AdvancePlayer(_player);
        else if (_clip is not null)
            AdvanceClip(_clip);
    }

    // Pushes the idle timer back a couple of times a second while a file plays, since the pad is often
    // untouched then and the console would otherwise dim and drop into rest in the middle of playback.
    private void HoldSystemAwake(double deltaSeconds)
    {
        _awakeAccumulator += deltaSeconds;
        if (_awakeAccumulator < KeepAwakeIntervalSeconds)
            return;
        _awakeAccumulator = 0;
        try
        {
            SystemControl.KeepAwake();
        }
        catch (ProsperoException)
        {
            // Nothing depends on it; the console falls back to its own idle behaviour.
        }
    }

    private void AdvancePlayer(MediaPlayer player)
    {
        if (_state == PlaybackState.Playing && !player.IsActive)
        {
            // The player has no more to give, but decoded sound the collector already pulled may still
            // be waiting to be handed to the device. Drain it before calling playback finished, so the
            // last quarter-second is heard rather than dropped.
            if (_pending.Count > 0)
            {
                PushPending();
                return;
            }
            _state = PlaybackState.Finished;
            _queuedFrames = QueuedFrames();
            _repaintAll = true;
            Shell.Status("Playback finished.");
            return;
        }

        if (_state == PlaybackState.Playing)
        {
            int collected = 0;
            while (collected < MaxAudioFramesPerTick
                && _pending.Count < MaxPendingSamples
                && player.TryGetAudioFrame(out AudioFrame frame))
            {
                collected++;
                Collect(frame);
            }

            PushPending();
        }

        // Re-read what the output holds after this frame's push, then show the picture the sound has
        // reached. The picture follows the sound rather than the decoder, so it never runs ahead of it.
        _queuedFrames = QueuedFrames();
        PresentVideo(player);
    }

    // Presents the decoded pictures whose time the sound has reached, keeping the newest one that is due,
    // so the picture tracks what is heard. A picture is valid only until the next is asked for, so each is
    // drawn into the page's own buffer as it is taken rather than held for later.
    private void PresentVideo(MediaPlayer player)
    {
        double clock = PlayoutMillis();
        int drawn = 0;
        while (drawn < MaxVideoFramesPerTick
            && clock >= _shownVideoMillis
            && player.TryGetVideoFrame(out VideoFrame picture))
        {
            drawn++;
            if (picture.VisibleWidth > 0 && picture.VisibleHeight > 0)
                DrawPicture(picture);
            _shownVideoMillis = picture.TimeStamp;

            // Stop once a picture at or past the sound has been shown: it is the current one, and taking
            // the next would run the picture ahead of what is heard.
            if (picture.TimeStamp >= clock)
                break;
        }
    }

    // The output takes one fixed rate, so the frame's samples are read at the ratio between its rate and
    // that one, blending between neighbouring samples the way the clip path does rather than snapping to
    // the nearest - which upsampling to the output rate would otherwise make audible. The sub-sample read
    // position is carried across frame boundaries so the pitch and timing stay true.
    //
    // The frame says how many channels it interleaves but not what each one carries, so a track with more
    // than two takes its leading pair - the front left and right of every layout that is written - rather
    // than a sum across channels whose placement is not stated. Reading the stride wrong is what matters
    // here: taking a six-channel frame as one mono stream plays it six times too fast.
    private void Collect(AudioFrame frame)
    {
        int channels = frame.ChannelCount > 0 ? frame.ChannelCount : 1;
        int available = frame.Samples.Length / channels;
        if (available <= 0)
            return;

        // The front of an empty queue is this frame, so its time anchors the clock the position and the
        // picture read from; after an underrun or a seek this resynchronises to the sound being decoded.
        if (_pending.Count == 0)
        {
            _headMillis = frame.TimeStamp;
            _audioAnchored = true;
        }

        double step = frame.SampleRate <= 0 ? 1.0 : (double)frame.SampleRate / OutputSampleRate;
        int peak = 0;
        double at = _resamplePhase;
        for (; at < available; at += step)
        {
            int i0 = (int)at;
            int i1 = i0 + 1 < available ? i0 + 1 : i0;
            double frac = at - i0;
            int baseIndex0 = i0 * channels;
            int baseIndex1 = i1 * channels;

            short left = Interpolate(frame.Samples[baseIndex0], frame.Samples[baseIndex1], frac);
            short right = channels >= 2
                ? Interpolate(frame.Samples[baseIndex0 + 1], frame.Samples[baseIndex1 + 1], frac)
                : left;
            _pending.Enqueue(left);
            _pending.Enqueue(right);

            int magnitude = left < 0 ? -left : left;
            if (magnitude > peak)
                peak = magnitude;
        }

        // Whatever of the step fell past this frame's last sample begins the next frame, so no sub-sample
        // of phase is dropped at the boundary.
        _resamplePhase = at - available;
        if (_resamplePhase < 0)
            _resamplePhase = 0;

        Observe(peak);
    }

    private static short Interpolate(short a, short b, double frac)
        => (short)Math.Clamp(Math.Round(a + (b - a) * frac), short.MinValue, short.MaxValue);

    private void PushPending()
    {
        AudioQueueDevice? device = OpenAudio();
        if (device is null)
        {
            // Nothing can play it, so the queue is dropped rather than left to grow.
            _pending.Clear();
            return;
        }

        int block = _block.Length;
        if (block == 0)
            return;

        // Fill the output up to what it will take without waiting. Reading the room once and pushing no
        // more than that many blocks keeps every push non-blocking, so the frame is never held up.
        uint free = device.FreeBlocks;
        for (uint pushed = 0; pushed < free && _pending.Count >= block; pushed++)
        {
            for (int i = 0; i < block; i++)
                _block[i] = _pending.Dequeue();
            device.TryOutput(_block);

            // The front of the queue moves on by one block, so the clock the position reads from advances
            // with what was handed over rather than with what was decoded.
            _headMillis += OutputGrain * 1000.0 / OutputSampleRate;
        }
    }

    private void AdvanceClip(short[] clip)
    {
        if (_state != PlaybackState.Playing)
            return;

        // A clip with no samples has no end for the cursor to reach, so repeat cannot carry it round and
        // the fill below would never come up with a block. It is finished as soon as it starts.
        if (clip.Length == 0)
        {
            _state = PlaybackState.Finished;
            _repaintAll = true;
            Shell.Status("This file holds no sound to play.");
            return;
        }

        AudioQueueDevice? device = OpenAudio();
        if (device is null)
        {
            _state = PlaybackState.Failed;
            return;
        }

        int block = _block.Length;
        if (block == 0)
            return;

        // As with the player path, hand over only what the output will take now, so the clip plays at the
        // output's own rate without a push ever waiting.
        uint free = device.FreeBlocks;
        for (uint pushed = 0; pushed < free; pushed++)
        {
            int filled = 0;
            while (filled < block)
            {
                if (_clipCursor >= clip.Length)
                {
                    if (!_loop)
                        break;
                    _clipCursor = 0;
                }

                int take = Math.Min(block - filled, clip.Length - _clipCursor);
                Array.Copy(clip, _clipCursor, _block, filled, take);
                _clipCursor += take;
                filled += take;
            }

            if (filled == 0)
            {
                _state = PlaybackState.Finished;
                _repaintAll = true;
                Shell.Status("Playback finished.");
                break;
            }

            if (filled < block)
                Array.Clear(_block, filled, block - filled);

            Observe(PeakOf(_block, filled));
            device.TryOutput(_block);
        }

        _queuedFrames = QueuedFrames();
    }

    // How many sample frames the output still holds unplayed. Zero when there is no output yet or the
    // reading fails, which leaves the clock to fall back rather than stopping the playback.
    private long QueuedFrames()
    {
        if (_audio is null)
            return 0;
        try
        {
            return (long)_audio.QueuedBlocks * OutputGrain;
        }
        catch (ProsperoException)
        {
            return 0;
        }
    }

    // The media time the output is playing right now: the front of what is buffered, less the sound still
    // waiting in the output's queue. With no working output to measure, the elapsed wall-clock time stands
    // in so a silent video still has a clock to pace its picture against.
    private double PlayoutMillis()
        => _audioAnchored && _audio is not null
            ? Math.Max(0, _headMillis - (_queuedFrames * 1000.0 / OutputSampleRate))
            : Math.Max(0, _wallClockMillis);

    private AudioQueueDevice? OpenAudio()
    {
        if (_audio is not null || _audioRefused)
            return _audio;

        try
        {
            _audio = AudioQueueDevice.OpenStereo(OutputGrain, OutputSampleRate, QueueDepth);
            _block = new short[_audio.SamplesPerBlock];
            ApplyVolume();
        }
        catch (ProsperoException error)
        {
            // A video with no sound is still worth watching, so this is reported and not retried.
            _audioRefused = true;
            Shell.ReportFailure("The audio output would not open", error);
        }

        return _audio;
    }

    private void Observe(int peak)
    {
        float level = Math.Min(1f, peak / 32768f);
        _level = level > _level ? level : _level * 0.88f;
    }

    private static int PeakOf(short[] samples, int count)
    {
        int peak = 0;
        for (int i = 0; i < count; i++)
        {
            int magnitude = samples[i] < 0 ? -samples[i] : samples[i];
            if (magnitude > peak)
                peak = magnitude;
        }
        return peak;
    }

    // Input.

    private void HandleInput(FrameContext context)
    {
        // An overlay is up: the page underneath must not act on the same press that is driving it.
        if (Shell.Dialogs.IsBusy || _state == PlaybackState.Opening)
            return;

        if (context.Pressed(ScePadButton.Cross))
            TogglePlayback();
        if (context.Pressed(ScePadButton.Left))
            Seek(-SeekStepSeconds);
        if (context.Pressed(ScePadButton.Right))
            Seek(SeekStepSeconds);
        if (context.Pressed(ScePadButton.Up))
            ChangeVolume(VolumeStep);
        if (context.Pressed(ScePadButton.Down))
            ChangeVolume(-VolumeStep);
        if (context.Pressed(ScePadButton.Triangle))
            ToggleLoop();
        if (context.Pressed(ScePadButton.Square))
            ToggleOverlay();
    }

    private void TogglePlayback()
    {
        switch (_state)
        {
            case PlaybackState.Playing:
                PausePlayback(announce: true);
                break;
            case PlaybackState.Paused:
                ResumePlayback(announce: true);
                break;
            case PlaybackState.Finished:
                Replay();
                break;
            default:
                Shell.Status($"There is nothing to play: {_sourceNote}");
                break;
        }
    }

    private void PausePlayback(bool announce)
    {
        if (_player is not null)
        {
            try
            {
                _player.Pause();
            }
            catch (ProsperoException error)
            {
                Shell.ReportFailure("Pausing failed", error);
                return;
            }
        }

        _state = PlaybackState.Paused;
        if (announce)
            Shell.Status("Paused.");
    }

    private void ResumePlayback(bool announce)
    {
        if (_state is PlaybackState.Opening or PlaybackState.Failed or PlaybackState.Finished)
            return;

        if (_player is not null)
        {
            try
            {
                _player.Resume();
            }
            catch (ProsperoException error)
            {
                Shell.ReportFailure("Resuming failed", error);
                return;
            }
        }

        _state = PlaybackState.Playing;
        if (announce)
            Shell.Status("Playing.");
    }

    private void Replay()
    {
        if (_clip is not null)
        {
            _clipCursor = 0;
            _state = PlaybackState.Playing;
            Shell.Status("Playing from the start.");
            return;
        }

        // A player that has run to the end refuses the calls that would rewind it, so the file is
        // opened again instead of being restarted.
        StopEverything();
        _hasVideo = false;
        _repaintAll = true;
        BeginOpen(autoPlay: true);
    }

    private void Seek(int seconds)
    {
        if (_state is PlaybackState.Opening or PlaybackState.Failed)
            return;

        if (_player is not null)
        {
            // Move from where the sound is heard, which is what the position shows, not from the point
            // the decoder has read ahead to.
            ulong position = (ulong)Math.Max(0L, Position);
            ulong step = (ulong)Math.Abs(seconds) * 1000;
            ulong target = seconds < 0 ? (position > step ? position - step : 0) : position + step;
            try
            {
                _player.JumpTo(target);
            }
            catch (ProsperoException error)
            {
                Shell.ReportFailure("Seeking failed", error);
                return;
            }

            // Drop what was buffered from before the jump and resynchronise the clocks to the new point,
            // so the position and the picture follow the sound from where it now resumes. The frames
            // already handed to the audio port cannot be recalled, so about a device-queue's worth of
            // pre-seek sound (~40 ms) still plays out; clearing the counted queue keeps the reported
            // position from dipping below the target on those frames until the clock re-anchors.
            _pending.Clear();
            _resamplePhase = 0;
            _audioAnchored = false;
            _queuedFrames = 0;
            _shownVideoMillis = 0;
            _wallClockMillis = target;
            Shell.Status($"Moved to {TextFormat.Duration(target / 1000.0)}.");
            return;
        }

        if (_clip is null)
            return;

        long frames = _clip.Length / 2;
        long at = Math.Clamp((_clipCursor / 2) + ((long)seconds * OutputSampleRate), 0, frames);
        _clipCursor = (int)(at * 2);
        if (_state == PlaybackState.Finished && at < frames)
            _state = PlaybackState.Paused;
        Shell.Status($"Moved to {TextFormat.Duration((double)at / OutputSampleRate)}.");
    }

    private void ChangeVolume(int delta)
    {
        int updated = Math.Clamp(_volume + delta, 0, 100);
        if (updated == _volume)
        {
            Shell.Status(delta > 0 ? "The volume is already at the top." : "The sound is already off.");
            return;
        }

        _volume = updated;
        Shell.Settings.MediaVolume = updated;
        _settingsChanged = true;
        ApplyVolume();
        Shell.Status($"Volume {updated}.");
    }

    private void ApplyVolume()
    {
        if (_audio is null)
            return;
        // The output scales every block it plays by this, silent at zero and unattenuated at one.
        _audio.Gain = _volume / 100f;
    }

    private void ToggleLoop()
    {
        bool wanted = !_loop;
        if (_player is not null)
        {
            try
            {
                _player.SetLooping(wanted);
            }
            catch (ProsperoException error)
            {
                Shell.ReportFailure("Repeat could not be changed", error);
                return;
            }
        }

        _loop = wanted;
        Shell.Settings.MediaLoop = wanted;
        _settingsChanged = true;
        Shell.Status(wanted ? "Repeat on." : "Repeat off.");
    }

    private void ToggleOverlay()
    {
        _overlay = !_overlay;
        Shell.Settings.MediaShowOverlay = _overlay;
        _settingsChanged = true;

        // The picture is fitted to a different height now, so what the last fit left behind has to go.
        _repaintAll = true;
        Shell.Status(_overlay ? "Overlay shown." : "Overlay hidden.");
    }

    // Drawing.

    private void FitCanvas()
    {
        StackPanel? root = _root;
        if (root is null || _canvas is null || root.Bounds.Width <= 0)
            return;

        // The picture takes what the rows under it leave. Measuring the whole tree and taking the
        // picture's own height back out gives that without repeating the shell's margins here. The
        // theme is the one the shell lays the tree out with, so the two agree.
        UiTheme theme = Screen.Theme;
        int width = root.Bounds.Width;
        int rows = root.Measure(width, theme) - _canvas.Height;
        int available = root.Bounds.Height - rows;
        EnsureCanvas(width, _hasVideo ? available : Math.Min(available, SoundOnlyHeight));
    }

    private void EnsureCanvas(int width, int height)
    {
        width = Math.Clamp(width, 64, 4096);
        height = Math.Clamp(height, MinCanvasHeight, 4096);
        if (_canvas is not null && _canvas.Width == width && _canvas.Height == height)
            return;

        var replacement = new PixelBuffer(width, height);
        _canvas?.Dispose();
        _canvas = replacement;
        _picture?.SetContent(_canvas.AsSurface());
        _repaintAll = true;
    }

    private void DrawPicture(VideoFrame frame)
    {
        if (_canvas is null)
            return;

        _hasVideo = true;
        Surface canvas = _canvas.AsSurface();
        int height = PictureHeight(canvas.Height);
        canvas.FillRect(0, 0, canvas.Width, height, Color.Black);

        (int x, int y, int width, int fitted) = Fit(frame.VisibleWidth, frame.VisibleHeight, canvas.Width, height);
        frame.RenderTo(canvas, x, y, width, fitted);
    }

    private void ClearCanvasIfAsked()
    {
        if (!_repaintAll || _canvas is null)
            return;
        _repaintAll = false;
        _canvas.Clear(Shell.Theme.Background);
    }

    private void PaintCanvas()
    {
        if (_canvas is null)
            return;

        Surface canvas = _canvas.AsSurface();
        if (!_hasVideo)
            PaintSoundPanel(canvas);
        if (_overlay)
            PaintOverlay(canvas);
    }

    // A file with no picture gets a panel of its own: what it is, what it says it is, how far in it is,
    // and how loud it is right now.
    private void PaintSoundPanel(Surface canvas)
    {
        UiTheme theme = Shell.Theme;
        int height = PictureHeight(canvas.Height);
        canvas.FillRect(0, 0, canvas.Width, height, theme.Panel);
        canvas.DrawRect(0, 0, canvas.Width, height, theme.Border);

        string title = _tags.Title.Length > 0 ? _tags.Title : _name;
        string performer = Join(_tags.Artist, _tags.Album);
        string detail = Join(_tags.Year, _tags.Genre);

        // The title, the tag lines that have something to say, the time and the level meter, as one
        // block centred in the panel.
        int stack = TitleStep + TimeStep + LevelHeight
            + (performer.Length > 0 ? TagStep : 0)
            + (detail.Length > 0 ? TagStep : 0);
        int y = Math.Max(8, (height - stack) / 2);

        canvas.DrawTextCentered(title, y, 4, theme.Text);
        y += TitleStep;
        if (performer.Length > 0)
        {
            canvas.DrawTextCentered(performer, y, 2, theme.TextMuted);
            y += TagStep;
        }
        if (detail.Length > 0)
        {
            canvas.DrawTextCentered(detail, y, 2, theme.TextMuted);
            y += TagStep;
        }

        canvas.DrawTextCentered(ElapsedText(), y, 3, theme.Text);
        y += TimeStep;

        int barWidth = Math.Max(0, canvas.Width - 200);
        if (barWidth <= 0 || y + LevelHeight > height)
            return;
        int barX = (canvas.Width - barWidth) / 2;
        canvas.FillRect(barX, y, barWidth, LevelHeight, theme.Border);
        canvas.FillRect(barX, y, (int)(barWidth * _level), LevelHeight, theme.Accent);
    }

    private void PaintOverlay(Surface canvas)
    {
        UiTheme theme = Shell.Theme;
        int top = canvas.Height - OverlayStripHeight;
        if (top <= 0)
            return;

        canvas.FillRect(0, top, canvas.Width, OverlayStripHeight, theme.Panel);
        canvas.HLine(0, top, canvas.Width, theme.Border);
        canvas.DrawTextOutlined(ElapsedText(), 24, top + 18, 3, Color.White, Color.Black);
        canvas.DrawTextOutlined(
            "Cross pause   Left/Right 10s   Up/Down volume   Triangle repeat   Square overlay",
            24, top + 58, 2, theme.TextMuted, Color.Black);
    }

    private int PictureHeight(int canvasHeight)
        => _overlay ? Math.Max(1, canvasHeight - OverlayStripHeight) : canvasHeight;

    private void UpdateRows()
    {
        if (_root is null)
            return;

        _note!.Text = _sourceNote;
        _note.Visible = _sourceNote.Length > 0;
        _tagRow!.Value = _tagSummary;
        _tagRow.Visible = _tagSummary.Length > 0;
        _timeRow!.Value = TimeText();
        _stateRow!.Value = StateText();

        // The bar follows the position when the length is known. The player reports where it is but not
        // how long the file runs, so for anything it opens the bar shows the output level instead.
        _bar!.Value = _clipDurationMilliseconds > 0
            ? (float)((double)Position / _clipDurationMilliseconds)
            : _level;
    }

    private long Position
    {
        get
        {
            if (_player is not null)
                return (long)PlayoutMillis();
            if (_clip is not null)
            {
                // The cursor is where the clip has been handed to the output; the sound still queued has
                // not been heard yet, so it is taken back out to leave what is actually playing.
                long played = Math.Max(0L, (_clipCursor / 2) - _queuedFrames);
                return played * 1000 / OutputSampleRate;
            }
            return 0;
        }
    }

    private string ElapsedText()
    {
        double elapsed = Position / 1000.0;
        return _clipDurationMilliseconds > 0
            ? $"{TextFormat.Duration(elapsed)} / {TextFormat.Duration(_clipDurationMilliseconds / 1000.0)}"
            : TextFormat.Duration(elapsed);
    }

    private string TimeText()
        => _clipDurationMilliseconds > 0 ? ElapsedText() : $"{ElapsedText()} (length not reported)";

    private string StateText()
    {
        string state = _state switch
        {
            PlaybackState.Opening => "opening",
            PlaybackState.Playing => "playing",
            PlaybackState.Paused => "paused",
            PlaybackState.Finished => "finished",
            _ => "stopped",
        };
        string sound = _audioRefused ? ", no sound output" : "";
        return $"{state}, volume {_volume}, repeat {(_loop ? "on" : "off")}, overlay {(_overlay ? "on" : "off")}{sound}";
    }

    private static string Join(string left, string right)
    {
        if (left.Length == 0)
            return right;
        return right.Length == 0 ? left : $"{left} - {right}";
    }

    // The largest fit of a source of the given size inside the destination, keeping its shape, centred.
    private static (int X, int Y, int Width, int Height) Fit(int sourceWidth, int sourceHeight, int destWidth, int destHeight)
    {
        float scale = MathF.Min((float)destWidth / sourceWidth, (float)destHeight / sourceHeight);
        int width = Math.Max(1, (int)(sourceWidth * scale));
        int height = Math.Max(1, (int)(sourceHeight * scale));
        return ((destWidth - width) / 2, (destHeight - height) / 2, width, height);
    }

    // Shutting down.

    private void PersistSettings()
    {
        if (!_settingsChanged)
            return;
        _settingsChanged = false;
        if (!Shell.Settings.Save())
            Shell.Status("The settings could not be written.");
    }

    private void AbandonOpening()
    {
        BackgroundOperation<MediaSource>? work = _opening;
        _opening = null;
        if (work is null)
            return;

        // The work cannot be called off, and what it produces holds decoder threads, so it is waited on
        // briefly and released. Waiting only happens when the page closes while a file is opening.
        if (work.Wait(TimeSpan.FromSeconds(2)) && !work.Failed)
            work.Result.Player?.Dispose();
    }

    private void StopEverything()
    {
        _pending.Clear();
        _clip = null;
        _clipCursor = 0;
        _clipDurationMilliseconds = 0;
        _level = 0f;

        _headMillis = 0;
        _audioAnchored = false;
        _queuedFrames = 0;
        _resamplePhase = 0;
        _shownVideoMillis = 0;
        _wallClockMillis = 0;
        _awakeAccumulator = 0;

        _player?.Dispose();
        _player = null;
        _audio?.Dispose();
        _audio = null;
        _block = [];
        _audioRefused = false;
    }

    /// <summary>Where the page is between opening a file and running out of it.</summary>
    private enum PlaybackState
    {
        Opening,
        Playing,
        Paused,
        Finished,
        Failed,
    }

    /// <summary>What opening a file produced: a player, or samples decoded here, plus what it says about itself.</summary>
    private sealed class MediaSource
    {
        /// <summary>The player, when the file is one it takes.</summary>
        public MediaPlayer? Player { get; set; }

        /// <summary>Interleaved stereo samples at the output's rate, when the file was decoded here.</summary>
        public short[]? Clip { get; set; }

        /// <summary>How long the decoded samples run, or zero when the length is not known.</summary>
        public long DurationMilliseconds { get; set; }

        /// <summary>The tags the file carries.</summary>
        public MediaTags Tags { get; set; } = MediaTags.Empty;

        /// <summary>What to tell the user about how the file is being played, or an empty string.</summary>
        public string Note { get; set; } = "";
    }
}
