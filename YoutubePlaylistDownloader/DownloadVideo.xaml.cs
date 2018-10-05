﻿using MoreLinq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using YoutubeExplode;
using YoutubeExplode.Models;
using YoutubeExplode.Models.MediaStreams;
using YoutubePlaylistDownloader.Objects;

namespace YoutubePlaylistDownloader
{
    /// <summary>
    /// Interaction logic for DownloadVideo.xaml
    /// </summary>
    public partial class DownloadVideo : UserControl, IDisposable
    {
        private Video Video;
        private string FileType;
        private int DownloadedCount;
        private List<Process> ffmpegList;
        private CancellationTokenSource cts;
        private VideoQuality Quality;
        private string Bitrate;
        private bool AudioOnly, PreferHighestFPS;
        private List<Tuple<string, string>> NotDownloaded;
        const int megaBytes = 1 << 20;

        public DownloadVideo(Video video, bool convert, VideoQuality quality = VideoQuality.High720, string fileType = "mp3", string bitrate = null,
            bool audioOnly = false, bool preferHighestFPS = false)
        {
            InitializeComponent();
            GlobalConsts.HideSettingsButton();
            GlobalConsts.HideAboutButton();
            GlobalConsts.HideHomeButton();
            GlobalConsts.HideSubscriptionsButton();
            GlobalConsts.HideHelpButton();

            cts = new CancellationTokenSource();
            ffmpegList = new List<Process>();
            NotDownloaded = new List<Tuple<string, string>>();
            Video = video;
            FileType = fileType;
            AudioOnly = audioOnly;
            PreferHighestFPS = preferHighestFPS;

            DownloadedCount = 0;
            Quality = quality;
            if (bitrate != null)
                Bitrate = $"-b:a {bitrate}k";
            else
                Bitrate = string.Empty;

            if (convert || audioOnly)
                StartDownloadingWithConverting(cts.Token).ConfigureAwait(false);
            else
                StartDownloading(cts.Token).ConfigureAwait(false);

        }

        public async Task StartDownloadingWithConverting(CancellationToken token)
        {

            var client = GlobalConsts.YoutubeClient;
            try
            {
                await Dispatcher.InvokeAsync(() => Update(0, Video));

                var streamInfoSet = await client.GetVideoMediaStreamInfosAsync(Video.Id);
                var cleanFileName = GlobalConsts.CleanFileName(Video.Title).Replace("$", "S");
                var bestQuality = streamInfoSet.Audio.MaxBy(x => x.AudioEncoding);
                var fileLoc = $"{GlobalConsts.TempFolderPath}{cleanFileName}";

                if (AudioOnly)
                    FileType = bestQuality.Container.GetFileExtension();

                var outputFileLoc = $"{GlobalConsts.TempFolderPath}{cleanFileName}.{FileType}";
                var copyFileLoc = $"{GlobalConsts.SaveDirectory}\\{cleanFileName}.{FileType}";

                using (var stream = new ProgressStream(File.Create(fileLoc)))
                {
                    Stopwatch sw = new Stopwatch();
                    TimeSpan ts = new TimeSpan(0);
                    string downloadSpeedText = (string)FindResource("DownloadSpeed");
                    int seconds = 0;
                    stream.BytesWritten += async (sender, args) =>
                    {
                        try
                        {
                            var delta = sw.Elapsed - ts;
                            ts = sw.Elapsed;
                            var speedInBytes = args.BytesMoved / delta.TotalSeconds;
                            var speedInMB = Math.Round(speedInBytes / megaBytes, 3);

                            var precent = Convert.ToInt32(args.StreamLength * 100 / bestQuality.Size);
                            await Dispatcher.InvokeAsync(() =>
                            {
                                CurrentDownloadProgressBar.Value = precent;
                                CurrentDownloadProgressBarTextBlock.Text = $"{precent}%";
                                if (seconds == sw.Elapsed.Seconds)
                                {
                                    DownloadSpeedTextBlock.Text = string.Concat(downloadSpeedText, speedInMB, " MB\\s");
                                    DownloadSpeedTextBlock.Visibility = Visibility.Visible;
                                    seconds += 1;
                                }
                            });
                        }
                        catch (OperationCanceledException)
                        {

                        }
                        catch (Exception ex)
                        {
                            await GlobalConsts.Log(ex.ToString(), "BytesWrittenEventHandler at ProgressStream in DownloadVideo");
                        }
                    };
                    sw.Start();
                    await client.DownloadMediaStreamAsync(bestQuality, stream, cancellationToken: token);
                    sw.Stop();
                }
                if (!AudioOnly)
                {

                    var ffmpeg = new Process()
                    {
                        EnableRaisingEvents = true,
                        StartInfo = new ProcessStartInfo()
                        {
                            FileName = $"{GlobalConsts.CurrentDir}\\ffmpeg.exe",
                            Arguments = $"-i \"{fileLoc}\" -vn -y {Bitrate} \"{outputFileLoc}\"",
                            CreateNoWindow = true,
                            UseShellExecute = false
                        }
                    };

                    token.ThrowIfCancellationRequested();
                    ffmpeg.Exited += async (x, y) =>
                    {
                        ffmpegList.Remove(ffmpeg);
                        await GlobalConsts.TagFile(Video, 0, outputFileLoc);

                        File.Copy(outputFileLoc, copyFileLoc, true);
                        File.Delete(outputFileLoc);
                        File.Delete(fileLoc);
                    };
                    ffmpeg.Start();
                    ffmpegList.Add(ffmpeg);
                }
                else
                {
                    File.Copy(fileLoc, copyFileLoc, true);
                    File.Delete(fileLoc);
                    try
                    {
                        await GlobalConsts.TagFile(Video, 0, copyFileLoc);
                    }
                    catch { }
                }

                DownloadedCount++;

            }
            catch (OperationCanceledException)
            {
                goto exit;
            }
            catch (Exception ex)
            {
                NotDownloaded.Add(new Tuple<string, string>(Video.Title, ex.Message));
            }

            exit:

            if (NotDownloaded.Any())
                await GlobalConsts.ShowMessage($"{FindResource("CouldntDownload")}", string.Concat($"{FindResource("ListOfNotDownloadedVideos")}\n", string.Join("\n", NotDownloaded.Select(x => string.Concat(x.Item1, " Reason: ", x.Item2)))));


            while (ffmpegList.Count > 0)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    HeadlineTextBlock.Text = (string)FindResource("AllDone");
                    CurrentDownloadProgressBar.IsIndeterminate = true;
                    ConvertingTextBlock.Visibility = Visibility.Visible;
                    ConvertingTextBlock.Text = $"{FindResource("StillConverting")} {ffmpegList.Count} {FindResource("files")}";
                    CurrentDownloadProgressBarTextBlock.Visibility = Visibility.Collapsed;
                    DownloadSpeedTextBlock.Visibility = Visibility.Collapsed;
                });
                await Task.Delay(1000);
            }

            CurrentDownloadGrid.Visibility = Visibility.Collapsed;
            ConvertingTextBlock.Visibility = Visibility.Collapsed;
        }

        public async Task StartDownloading(CancellationToken token)
        {
            var client = GlobalConsts.YoutubeClient;
            await Dispatcher.InvokeAsync(() => Update(0, Video));
            try
            {
                var streamInfoSet = await client.GetVideoMediaStreamInfosAsync(Video.Id);
                MediaStreamInfo bestQuality, bestAudio = null;
                var videoList = streamInfoSet.Video.OrderByDescending(x => x.VideoQuality == Quality);

                if (PreferHighestFPS)
                    videoList = videoList.ThenByDescending(x => x.Framerate).ThenByDescending(x => x.VideoQuality > Quality).ThenByDescending(x => x.VideoQuality);
                else
                    videoList = videoList.ThenByDescending(x => x.VideoQuality > Quality).ThenByDescending(x => x.VideoQuality);

                bestQuality = videoList.FirstOrDefault();
                bestAudio = streamInfoSet.Audio.OrderByDescending(x => x.AudioEncoding).FirstOrDefault();
                var cleanVideoName = GlobalConsts.CleanFileName(Video.Title);
                var fileLoc = $"{GlobalConsts.TempFolderPath}{cleanVideoName}";
                var outputFileLoc = $"{GlobalConsts.TempFolderPath}{cleanVideoName}.mkv";
                var copyFileLoc = $"{GlobalConsts.SaveDirectory}\\{cleanVideoName}.mkv";

                string audioLoc = null;
                if (bestAudio != null)
                    audioLoc = $"{GlobalConsts.TempFolderPath}{cleanVideoName}.{bestAudio.Container.GetFileExtension()}";

                using (var stream = new ProgressStream(File.Create(fileLoc)))
                {
                    Stopwatch sw = new Stopwatch();
                    TimeSpan ts = new TimeSpan(0);
                    int seconds = 0;
                    string downloadSpeedText = (string)FindResource("DownloadSpeed");
                    stream.BytesWritten += async (sender, args) =>
                    {
                        try
                        {
                            var delta = sw.Elapsed - ts;
                            ts = sw.Elapsed;
                            var speedInBytes = args.BytesMoved / delta.TotalSeconds;
                            var speedInMB = Math.Round(speedInBytes / megaBytes, 3);

                            var precent = Convert.ToInt32(args.StreamLength * 100 / bestQuality.Size);
                            await Dispatcher.InvokeAsync(() =>
                            {
                                CurrentDownloadProgressBar.Value = precent;
                                CurrentDownloadProgressBarTextBlock.Text = $"{precent}%";
                                if (seconds == sw.Elapsed.Seconds)
                                {
                                    DownloadSpeedTextBlock.Text = string.Concat(downloadSpeedText, speedInMB, " MB\\s");
                                    DownloadSpeedTextBlock.Visibility = Visibility.Visible;
                                    seconds += 1;
                                }
                            });
                        }
                        catch (OperationCanceledException)
                        {

                        }
                        catch (Exception ex)
                        {
                            await GlobalConsts.Log(ex.ToString(), "BytesWrittenEventHandler at ProgressStream in DownloadVideo");
                        }
                    };
                    sw.Start();
                    var videoTask = client.DownloadMediaStreamAsync(bestQuality, stream, cancellationToken: token);

                    using (var audioStream = File.Create(audioLoc))
                    {
                        var audioTask = client.DownloadMediaStreamAsync(bestAudio, audioStream);
                        await Task.WhenAll(videoTask, audioTask);
                        sw.Stop();
                    }
                }
                var ffmpeg = new Process()
                {
                    EnableRaisingEvents = true,
                    StartInfo = new ProcessStartInfo()
                    {
                        FileName = $"{GlobalConsts.CurrentDir}\\ffmpeg.exe",
                        Arguments = $"-i \"{fileLoc}\" -i \"{audioLoc}\" -y -c copy \"{outputFileLoc}\"",
                        CreateNoWindow = true,
                        UseShellExecute = false,
                    }
                };

                token.ThrowIfCancellationRequested();
                ffmpeg.Exited += (x, y) =>
                {
                    ffmpegList.Remove(ffmpeg);
                    File.Copy(outputFileLoc, copyFileLoc, true);
                    File.Delete(outputFileLoc);
                    File.Delete(audioLoc);
                    File.Delete(fileLoc);
                };
                ffmpeg.Start();
                ffmpegList.Add(ffmpeg);
                DownloadedCount++;


            }
            catch (OperationCanceledException)
            {
                goto exit;
            }
            catch (Exception ex)
            {
                NotDownloaded.Add(new Tuple<string, string>(Video.Title, ex.Message));
            }

            exit:

            if (NotDownloaded.Any())
                await GlobalConsts.ShowMessage($"{FindResource("CouldntDownload")}", string.Concat($"{FindResource("ListOfNotDownloadedVideos")}\n", string.Join("\n", NotDownloaded.Select(x => string.Concat(x.Item1, " Reason: ", x.Item2)))));

            while (ffmpegList.Count > 0)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    HeadlineTextBlock.Text = (string)FindResource("AllDone");
                    CurrentDownloadProgressBar.IsIndeterminate = true;
                    ConvertingTextBlock.Text = $"{FindResource("StillConverting")} {ffmpegList.Count} {FindResource("files")}";
                    ConvertingTextBlock.Visibility = Visibility.Visible;
                    DownloadSpeedTextBlock.Visibility = Visibility.Collapsed;
                    CurrentDownloadProgressBarTextBlock.Visibility = Visibility.Collapsed;
                });
                await Task.Delay(1000);
            }

            CurrentDownloadGrid.Visibility = Visibility.Collapsed;
            ConvertingTextBlock.Visibility = Visibility.Collapsed;
        }

        private void Update(int precent, Video video)
        {
            CurrentDownloadProgressBar.Value = precent;
            HeadlineTextBlock.Text = (string)FindResource("CurrentlyDownlading") + video.Title;
            CurrentDownloadProgressBarTextBlock.Text = $"{precent}%";
        }

        private async void Exit_Click(object sender, RoutedEventArgs e)
        {
            cts.Cancel(true);
            if (ffmpegList.Count > 0)
            {
                var yesno = await GlobalConsts.ShowYesNoDialog($"{FindResource("StillConverting")}", $"{FindResource("StillConverting")} {ffmpegList.Count(x => !x.HasExited)} {FindResource("files")} {FindResource("AreYouSureExit")}");
                if (yesno == MahApps.Metro.Controls.Dialogs.MessageDialogResult.Negative)
                    return;
            }
            ffmpegList.ForEach(x => { try { x.Kill(); } catch { } });
            GlobalConsts.LoadPage(new MainPage());
        }

        #region IDisposable Support
        private bool disposedValue = false;
        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    cts.Dispose();
                    ffmpegList.Clear();

                }

                Video = null;
                ffmpegList = null;
                disposedValue = true;
            }
        }
        public void Dispose()
        {
            Dispose(true);
        }
        #endregion
    }
}