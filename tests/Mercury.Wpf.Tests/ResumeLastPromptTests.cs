using System.IO;
using System.Windows;

namespace Mercury.Wpf.Tests;

[Collection("WpfSta")]
public class ResumeLastPromptTests
{
    [Fact]
    public void DecliningResumeLast_DoesNotKeepLastJobAsHeaderCurrent()
    {
        WpfSta.Run(() =>
        {
            WpfSta.EnsureApp();
            var root = Path.Combine(Path.GetTempPath(), "mercury-decline-resume-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            MainViewModel? vm = null;
            try
            {
                var paths = new AppPaths(root);
                var src = Path.Combine(root, "src");
                var dest = Path.Combine(root, "dest");
                Directory.CreateDirectory(src);
                Directory.CreateDirectory(dest);

                var last = new Job
                {
                    Name = "anchor",
                    SourcePath = src,
                    DestinationPath = dest,
                    Status = JobStatus.Incomplete,
                    SourceFiles = 26013,
                    DestFiles = 26013,
                    BytesCopied = 1,
                    ResultMessage = "Incomplete — 12 issue(s). Open the log for details."
                };
                var queued = new Job
                {
                    Name = "next",
                    SourcePath = Path.Combine(root, "other"),
                    DestinationPath = dest,
                    Status = JobStatus.Pending
                };

                using (var journal = JobJournal.Create(paths.JobDirectory(last.Id), last))
                {
                    JobHeartbeat.Write(journal, last.Id, 100, @"Warren Truss\clip.mkv");
                }

                AppSettingsStore.SaveLastJobId(paths, last.Id);
                QueueStore.Save(paths, [last, queued]);

                vm = new MainViewModel(paths);

                Assert.True(vm.ShowProgressDetail, "last-job snapshot is applied before the resume prompt");
                Assert.Equal(100, vm.JobPercent, 2);
                Assert.Equal(@"Warren Truss\clip.mkv", vm.CurrentFile);
                Assert.Equal("26013/26013", vm.JobStats.Files.Value);
                Assert.Equal("1 of 2", vm.JobStats.Job.Value);
                Assert.Contains("Incomplete", vm.ResultBanner, StringComparison.Ordinal);
                Assert.True(vm.ShowOpenLog);
                Assert.Equal("Resume", vm.StartButtonLabel);

                vm.DeclineDirtyResume(last.Id);

                Assert.Equal(QueueStatusHighlight.IdleLabel, vm.HeaderStatusLabel);
                Assert.Equal(QueueStatusHighlight.IdleLabel, vm.HeaderStatusChip.TileStatus);
                Assert.Equal("", vm.CurrentFile);
                Assert.Equal(0, vm.JobPercent);
                Assert.False(vm.ShowProgressDetail);
                Assert.True(string.IsNullOrEmpty(vm.ResultBanner));
                Assert.False(vm.ShowOpenLog);
                Assert.False(vm.JobStats.Files.HasValue);
                Assert.False(vm.JobStats.Job.HasValue);
                Assert.Equal("Start", vm.StartButtonLabel);
                Assert.Equal(2, vm.QueueJobs.Count);
                Assert.True(vm.CanResumeLast);
                Assert.Null(JobHeartbeat.FindDirty(paths));
            }
            finally
            {
                vm?.Dispose();
                try
                {
                    Directory.Delete(root, true);
                }
                catch
                {
                    // leftover
                }
            }
        });
    }

    [Fact]
    public void OfferDirtyResume_StaleZeroPercent_ClearsWithoutPrompt()
    {
        WpfSta.Run(() =>
        {
            WpfSta.EnsureApp();
            var root = Path.Combine(Path.GetTempPath(), "mercury-stale-zero-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            MainWindow? window = null;
            MainViewModel? vm = null;
            try
            {
                var paths = new AppPaths(root);
                var leftover = new Job
                {
                    Name = "Anchor Span",
                    SourcePath = Path.Combine(root, "Anchor Span"),
                    DestinationPath = Path.Combine(root, "EngA"),
                    Status = JobStatus.Incomplete
                };
                using (var journal = JobJournal.Create(paths.JobDirectory(leftover.Id), leftover))
                {
                    JobHeartbeat.Write(journal, leftover, 0, null);
                }

                AppSettingsStore.SaveLastJobId(paths, leftover.Id);
                QueueStore.Save(paths, [leftover]);
                vm = new MainViewModel(paths);
                Assert.NotNull(JobHeartbeat.FindDirty(paths));
                window = new MainWindow(vm)
                {
                    Width = 900,
                    Height = 600,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -4000,
                    Top = 0
                };
                window.Show();
                WpfSta.Flush();
                Assert.Null(JobHeartbeat.FindDirty(paths));
                Assert.Contains(
                    leftover.SourcePath,
                    JobHeartbeat.UnscheduledStopMessage(
                        JobHeartbeat.Create(leftover.Id, 0, null, leftover.SourcePath, leftover.DestinationPath),
                        leftover),
                    StringComparison.Ordinal);
            }
            finally
            {
                try { window?.Close(); } catch { /* window already disposed the VM */ }
                if (window is null)
                {
                    vm?.Dispose();
                }

                try { Directory.Delete(root, true); } catch { /* leftover */ }
            }
        });
    }
}
